# 蓝牙协议与驱动

## 平台和链路

Windows BLE / GATT 后端最低支持 Windows 10 2004。Core 的 `IBluetoothAdapter` / `IBluetoothConnection` 隔离平台 API；macOS / Linux 尚无原生蓝牙后端，可使用模拟或 WebSocket 来源。

`BluetoothDeviceProfiles` 识别广播名／服务并集中定义能力，`BluetoothProtocolDriverFactory` 选择驱动。`BluetoothDeviceTransport` 管理发现和路由，`BluetoothDeviceSession` 管理一条原生链接、驱动、初始化门和 GATT 透传切换。Windows 的 `WindowsGattResolver` 负责服务／特征发现和权限校验，`WindowsBluetoothConnection` 负责单链路读写、通知与释放。

## 设备与协议覆盖

| 广播名／服务 | 设备 | 已实现功能 |
| --- | --- | --- |
| `YYC-DJ` | YOKONEX 电击器 V1 | A/B 实时参数、1–16 固定程序、辅助马达及三种模式、计步启停／归零／暂停／继续、运动流、接触／强度／模式／电量／步数／六轴查询与错误回报 |
| `YCY-DJ`、`YYC-DJV2`、`YYC-DJ-V2` | YOKONEX 电击器 V2 | V1 通用功能、双通道实时包、固定程序包、每通道 1–100 项频率／脉宽序列、兼容固件版本与灯光 |
| `D-LAB ESTIM01` | 郊狼 V2 | A/B 强度读写及通知、两路 X/Y/Z 脉冲读写、电量读取／通知 |
| `47L121…` | 郊狼 V3 | B0 四帧波形、强度保持／增加／减少／绝对设置、B1 回复、BF 独立上限和频率／幅度平衡、电量 |
| `47L127…` | 负鼠 | B0 双路震动、B3 强度与单路保持、50 按键流、D0 按键位集、B2 屏幕同步、电量 |
| `47L1203…` | 爪印 V1.1 | 实时数据、随机／概率／电压／按键与运动触发、状态／事件、参数重置、角度校准及范围、肩灯 |
| `47L124…` | 灵猫 | 压力流、有符号压力、归零、屏幕方向、电量 |
| 服务 `FF40` | YOKONEX 震动器 | 型号与各通道程序数量、固定程序与通道掩码、最多 ABC 三路实时强度、电量 |
| 服务 `FFB0` | YOKONEX 灌肠机 | AES、蠕动泵正反转／独立时长、水泵开关／独立时长、全局暂停、状态／压力／电量 |

爪印 V1.0 `47L120100` 旧固件没有可用公开指令，显示需升级并禁用连接；未知 DG-LAB 前缀不猜测协议。未公开的升级格式、已废弃 BE 指令及服务端业务接口不在此覆盖范围。负鼠与灵猫的厂商资料要求连按五次电源键进入可发现状态。

## 指令与通知

| 协议 | 发送 | 接收／读取 |
| --- | --- | --- |
| 电击 V1 | 35 11 输出／固定／自定义；35 12 马达；35 13 计步；35 14 运动流；35 71 查询 | 35 71 类型 01–06；55 错误 |
| 电击 V2 | 35 11 模式 01 固定、02 实时、03 序列；V1 通用辅助指令；兼容固件 36 13 版本、35 15 灯光、35 71 07 灯光查询 | V1 通用通知及兼容固件扩展 |
| 马达 | 35 10 信息；35 11 固定程序／掩码；35 12 三路强度 | 35 10 型号／版本／数量；35 13 电量 |
| 泵 | AES 命令 01 蠕动泵、02 水泵、03 暂停、04 状态、05 电量 | AES 回复 01 状态、02 压力、03 电量 |
| 郊狼 V2 | 1504 强度；1506 A 脉冲；1505 B 脉冲 | 1504 强度读／通知；1506／1505 脉冲读；1500 电量 |
| 郊狼 V3 | B0 强度与四帧波形；BF 上限／平衡 | B1 序号与强度；1500 电量 |
| 负鼠 | 50 按键流；B0 波形；B3 强度／FF 保持；B2 屏幕 | B3 强度；D0 按键位／序号；1500 电量 |
| 爪印 V1.1 | 50 的禁用／实时／随机／概率／电压／按键与运动；5F 重置；60 校准；70 肩灯 | 51 状态；5A 开始、5B 取消、5C 参数；D0 读数；F1 校准范围 |
| 灵猫 | 50 压力流；66 归零／屏幕方向 | D0 压力；1500 电量 |

负鼠与灵猫固定使用文档定义的指示字节 01，不套用爪印颜色表。编码器、驱动与命令类型位于 Core/Bluetooth，能力、UI 筛选和后端验证共用同一配置。

## 编码规则与边界

统一波形只在发送时转换，规则见[波形](waveforms.md)。波形源通道与硬件插口通过图独立路由，不假定 C 永远跟随 A。传感器不播放波形，泵使用离散控制。

- **电击 V1/V2**：验证包长、加和校验和字段范围。保留原始六轴；零加速度不生成有效倾角。兼容 V2 固件确认版本后开放灯光，并等待状态确认。
- **V2 缓冲序列**：每通道 1–100 项、每项 100 ms、频率 1–100 Hz、脉宽 0–100。按协商容量在条目边界分包，不提前重复启动未完成包；会话结束前裁剪条目。
- **固定程序**：电击器编号 1–16，0 为应用内关闭选项。马达程序编号受设备回报数量限制；协议只有开关，非零通道和原生节点上限须为 100%，缓存只在完整发送成功后更新。
- **郊狼 V2**：原始 X=0–31、Y=0–1023、Z=0–31；普通波形转换使用建议的 Z≤20。归零后读取强度或等待通知确认。
- **郊狼 V3**：相对更新按最近强度计算，序号 1–15 循环并等待对应 B1；保持使用序号 0。BF 仅在值变化时发送。连接／停止先设 BF 上限为零，再发带序号的零 B0，并等待对应零 B1。
- **负鼠**：未修改通道发 FF 保持，后续输出重新应用图值。收到 B3 时在容量允许下同步 24 字节 B2；需要 MTU≥27，不截断包。
- **泵**：时长 0–65535 秒，0 为持续；持续模式续发一秒指令，定时发送剩余秒数。方向／开关／时长改变只重置该路计时，暂停冻结活动时间。停止发送全局暂停并查询两个泵均关闭。压力保留原始数值，不伪标物理单位。
- **爪印**：最多六项概率事件，总值超过 200 时从末项扣除。动态时间 0 禁用、1–60 为秒、61–120 为分钟；立即增／减量独立。指示灯 1–6 为黄红紫蓝青绿，肩灯另有 0 灭、7 白。校准先等 3 秒再采集 10 秒；范围除 2、向零取整并裁剪到有符号字节后可填入草稿，仍需显式发送。24 个事件独立维护，取消不虚构参数归零。
- **灵猫**：有符号小端压力除以 100 得 kPa；归零与屏幕方向可合并发送。

硬件选项按设备标识保存于 settings.json，在设备详情卡片编辑，运行／暂停时只读；协议命令草稿不是硬件选项持久化。辅助马达与运动输入不能并用。具体存储见[存储](storage.md)。

## 确认、并发与错误

每条连接的驱动维护独立缓存和确认状态，命令、输出和后台屏幕同步共用同一设备门。停止分别尝试辅助马达与主输出，保留全部错误。马达型号回复只确认协议／通道，不保证物理停止；负鼠也不保证零值重复回复。原生写入成功只说明系统接受指令。

Windows 使用 `WriteValueWithResultAsync`，根据实际特征权限选择写入模式并验证容量。异常保留 HRESULT、包长、权限及 ATT 状态。原生写入与释放串行，初始化失败释放链路；同时保留驱动关闭错误与原生释放错误。

连接内保留广播元数据，后续扫描消失不影响已有连接和 GATT 导出。GATT 批次先整体校验长度、权限、容量和延时，再串行发送；中途失败不是可回滚事务。

停止失败保留占用，主动断开仍尝试释放 GATT 并报告错误。重连从关闭输出开始。租约切换暂停主机驱动和维护包，收回链路后创建新驱动重新初始化，见 [WebSocket](websocket.md)。

## 诊断入口

以下命令只在显式进行原生诊断时运行。连接探针仅在筛选后恰有一台兼容设备时连接、归零、读取状态再断开；不发送非零强度。

```sh
# 扫描
dotnet run --project src/YokoWave.Desktop -- --bluetooth-probe
# 连接、归零和断开，可按 BluetoothProtocol 名称筛选
dotnet run --project src/YokoWave.Desktop -- --bluetooth-probe --connect --profile CoyoteV3
# 静音媒体、零强度声控会话和停止的重复检查
dotnet run --project src/YokoWave.Desktop -- --bluetooth-probe --connect --zero-audio /path/to/audio.wav
# 不连接蓝牙的媒体输入探针
dotnet run --project src/YokoWave.Desktop -- --audio-probe /path/to/audio.wav
```

实现位于 Desktop/Diagnostics/DiagnosticProbes.cs。常规验证见 [CONTRIBUTING](../CONTRIBUTING.md#验证)。

## 来源与验证范围

- [YOKONEX 文档](https://github.com/YCY-YOKONEX/YCY-YOKONEX-OpenSource/tree/main/Bluetooth)：两代电击器、马达和泵 PDF，参考提交 `9345b7e`。
- [郊狼 V2](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/coyote/v2/README_CN.md)、[V3](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/coyote/v3/README_CN.md)、[波形示例](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/coyote/v3/example_cn.md)。
- [负鼠](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/opossum-vibrate-controller/README_CN.md)、[爪印](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/paw-prints/README_CN.md)、[灵猫](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/civet-edging-sensor/README_CN.md)，参考提交 `045720d5`。
- [DG-LAB-Client](https://github.com/CrimsonSeraph/DG-LAB-Client/blob/main/docs/devices.md)，参考型号限制与协议歧义，提交 `740d490e`；未移入其源码。

九类驱动均有编码、初始化、通知、停止及失败清理的替身测试。Windows 的 YCY-DJ 已验证扫描、连接、电量／接触读数、零输出确认与断开，FF31 为 WriteWithoutResponse、FF32 为 Notify，所测连接 MTU 为 512。其他型号和非零物理输出仍待相应实机验证。自动测试不等于硬件标定，见[测试](testing.md)。

参考克隆、PDF、APK 与本地分析均留在忽略目录，不随应用发布或提交。
