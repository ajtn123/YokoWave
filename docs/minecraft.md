# Minecraft 与 Java SDK

安装与连接见 [Minecraft 模块](../modules/minecraft/README.md)，库 API 见 [Java SDK](../modules/java-sdk/README.md)。

| 层 | 职责 |
| --- | --- |
| `modules/java-sdk` | 独立 Java 25 库：WebSocket、鉴权／租约／续期、V4 配对、请求和原子设备目录 |
| `modules/minecraft/common` | 游戏观测、事件映射、伤害脉冲、限幅、存储和单线程 I/O 生命周期；无 Minecraft 引用 |
| `modules/minecraft/client` | 官方映射下的共用原生界面、按键和本地玩家快照 |
| `fabric` / `forge` / `neoforge` | 加载器生命周期、按键注册和元数据；不重复协议或规则 |

加载器合入 SDK、common 和 ZXing。Forge 用 ART 2.0.18 重映射 Java 25 字节码，不修改字节码版本冒充旧 Java。SDK 和 Mod 各自使用 Gradle，与 .NET 构建和发布无关。

## 事件与输出

| ID | 类型／范围 | 来源 |
| --- | --- | --- |
| `damage` | number，0–1024 | 本地玩家生命与吸收量下降差值；客户端观测，并非服务端精确伤害事件 |
| `death` / `respawn` | trigger | 生命跨过零值 |
| `jump` | trigger | 从着地变为向上离地；也可能来自击退 |
| `health` | number，0–1 | 生命／最大生命 |
| `hunger` | number，0–20 | 饱食度 |
| `air` | number，−20–300 | 剩余空气，超出标准范围截断 |
| `armor` | number，0–100 | 护甲值 |
| `experience` | number，0–1,000,000 | 经验等级 |
| `speed` | number，0–1000 | 水平速度，方块／秒 |

变化事件每游戏 tick 观察，数值每五 tick 发送，有效期 1000 ms。未启用不发送，暂停／退出世界重置前一快照，不把载入当作受伤。不能观测其他玩家或精确伤害来源；吸收效果消退可能被观测为下降，其他 Mod 修改的状态可能截断。

同一游戏连接内替换玩家对象时，事件模式保留死亡到复活的观测，重设伤害与移动基线；control／V4 模式禁用输出，需要重新启用。死亡画面不作为手动暂停处理。

事件模式把声明后的数据交给节点图。control／V4 模式将伤害映射为 `min(强度上限, 伤害 × 每点伤害强度)`，只输出所选通道；频率 10–100 Hz、时长 100–2000 ms。control 使用单次统一波形，V4 使用有限临时强度与波形，并遵守 App 上限／静音。没有复制持久波形或管线系统。

游戏线程仅提交有界输入；工作线程串行执行 I/O。超过 500 ms 的待发送批次丢弃，队列满则禁用，停止／修改清空队列，断线不重放。V4 任务 future 不阻塞接收或后续清理；退出先清任务再归零。失联时无法确认硬件状态，有限任务也不代表停止确认。

全部设置有 UI，保存于 `config/yokowave.json`；令牌仅内存，损坏配置不覆盖，可移走后重启。词典位于根目录 `locales/minecraft`，四种语言构建时转为原生游戏语言资源。

## 验证范围

SDK 使用真实回环 WebSocket 测试认证、租约、续期、序号、停止／释放、V4 配对和操作；目录测试覆盖补丁、旧查询隔离和原子性。游戏逻辑测试观测、限幅、通道和配置损坏保护。加载器分别编译打包；游戏内及官方手机 App 联调仍需手工验证。

另已使用 Java SDK 连接真实 YokoWave 实现，验证事件认证／声明／发送／续期、高层设备发现／零强度播放／停止／释放再申请，以及 V4 中继握手／配对。输出端为仅接受零值的测试替身，没有向实机发送输出。

参考 [DG_Spigot](https://github.com/huzpsb/DG_Spigot) 和 [Dungeon-Minecraft](https://github.com/zly2006/Dungeon-Minecraft) 的接入方式，未复制其旧 V3 协议代码。
