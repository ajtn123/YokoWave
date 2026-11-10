# DG-LAB V4

实现遵循官方 [dglab-websocket-server main](https://github.com/dungeonlab-open/dglab-websocket-server/tree/main) 的中继契约和 [dglab-kit V4 说明](https://github.com/dungeonlab-open/dglab-kit/blob/main/README_CN.md)。V3 WebSocket 不在此接入范围内；V4 消息中的 V2／V3 波形数据均支持。

## 接入入口

| 入口 | 行为 |
| --- | --- |
| 设备页远程来源「DG-LAB V4」 | 作为控制方连接中继，生成 App 配对二维码；多个 App 的多个设备进入普通设备路由，可绑定现有输出和传感器节点 |
| 「DG-LAB V4 被控设备」模板 | 将本地郊狼或负鼠暴露为 App 插槽；远程任务经过图中的数值端口、处理节点和各输出节点上限 |
| 「DG-LAB V4 传感器共享」模板 | 暴露本地灵猫压力传感器，不生成输出节点 |
| 「DG-LAB V4 中继」模板 | 仅启动路由监听器，不需要设备；为控制方与多个 App 转发 V4 消息 |

设备来源使用普通远程来源的新增、连接和移除操作，不需要令牌。连接后即显示二维码，等待 App 接入时无需反复扫描。添加来源时可指定二维码中的可达主机地址；该值只替换主机名，保留端口和路径。手机访问电脑中继时，填写可访问的局域网地址。

被控节点填写中继 URL 和控制方连接得到的 ID。同一玩法内 URL 与控制方 ID 相同的节点共享一条 App 连接，每个节点使用自己的稳定节点 ID 作为 slotId。设备只允许本地、非模拟的已支持型号；节点实际引用的设备由现有会话独占。图可在网络输入与设备输出之间插入普通处理节点。

中继默认 `127.0.0.1:9998/v4`，路径精确匹配。表单可设置监听地址、端口、路径、JSON 心跳间隔、空闲超时、原生探活间隔、允许未回应次数和 PFX 证书。原生探活默认 10 秒，允许 3 次未回应；.NET WebSocket 的 pong 等待上限为二者乘积。心跳与空闲计时独立，空闲时长使用单调时钟。端口 0 由系统分配。证书密码只在内存，不写入节点文件。跨设备连接使用可达地址；WSS 证书遵循平台信任验证。中继实现没有认证字段，配对 ID 决定路由。

## 设备与状态

| V4 类型 | 设备 | 通道／输入 |
| --- | --- | --- |
| `COYOTE_020` | 郊狼 V2 | A / B 输出，强度范围映射为 0–2047 |
| `COYOTE_030` | 郊狼 V3 | A / B 输出，强度范围映射为 0–200 |
| `OVC_1` | 负鼠 | A / B 输出，强度范围映射为 0–200 |
| `BMTR_1` | 灵猫 | 压力输入 |

连接标识包含来源、连接代次、App 和插槽，避免不同 App 的同名插槽冲突。新连接不会按名称接管旧设备。未知型号保留在目录中，但不能输出。

`devices.get` 更新描述及成员；`devices.snapshot` 替换该 App 全量设备；`devices.patch` 增删成员；`slots.patch` 深合并属性与插槽状态。查询记录发出时的目录修订号，较晚返回的旧查询不能覆盖更新事件。事件先完整验证再提交，重复插槽或非法快照不会部分覆盖目录。目录保留扩展字段；单个 App 的无效设备事件只移除该 App 的设备，不关闭其他 App 的连接。断开 App 删除其插槽，控制方断开则清空本次连接的目录。

设备来源提供统一输出与状态查询。硬件原生程序、平衡参数和厂商命令不属于公开 V4 操作，不因物理型号支持而显示为 V4 能力。App 的通道静音和最大强度约束会进一步压低发送值。

设备详情的「DG-LAB App 状态」以只读行显示已定义的标签、通道状态、模式、更新时间／值、标记颜色、连接状态、冷启动系数、强度上限、舒适上限与自适应参数、灵猫边控状态。没有上报的字段省略，未知扩展字段仍留在目录；不会将缺失测量伪装成零。V4 没有修改这些 App 专属参数的公开 RPC，不能将读取状态理解为远程设置接口。YokoWave 被控端只上报自身已知的状态，输出仍由图中普通节点决定。

压力与反馈各自记录采集时间；电量、连接或其他状态更新不刷新旧测量。压力相同但 `updateTime` 变化仍计为新采样。被控灵猫节点会启用采集，按实际采集时间发送更新，并在停止时关闭采集；缺失／过期输入由现有管线归零等待。动作 `custom.action` 的 0–9 编号进入设备传感器事件端口；同一 App 的每次动作有独立递增序号，即使编号和采集时间相同也可区分。传感器节点保留该序号，普通状态合并不改变它；每个图时钟读取最新动作，不保存两次求值之间的完整事件队列。被控节点的「动作」与「触发」数值输入在触发上升沿发送反馈。

## 消息与任务

V4 不使用 JSON-RPC。中继分配 `hello.clientId`；App 以 `?tid=<控制方 ID>` 接入，同时接受上游中继支持的 `targetId` 参数。二者同时存在时 `targetId` 优先，显式空值表示控制方；其他查询参数不改变角色。YokoWave 生成的配对地址使用 `tid`。控制方发给指定 App 的消息包含 `clientId`，App 上行消息由中继添加其真实 ID。跨控制方路由会被拒绝。

内部请求为 `{t:"req", reqId, m, data?}`，响应为 `{t:"resp", reqId, result}` 或 `error`。支持 `ping`、`devices.get`、`device.op`、`device.op.clear`；未知方法、无效参数及重复的在途请求 ID 返回协议错误。错误码在界面本地化。任务响应在任务结束时返回，等待长任务不会阻塞其他请求、清理或网络接收。

中继的 `bad_request` 等非终止错误不取消其他在途请求。任意业务负载与不完整 RPC 不会断开所有 App；能匹配到请求的非法响应只拒绝该请求，无效设备目录事件只移除上报方的目录。收到 `idle_timeout` 或被控端收到 `controller_disconnected` 即终止本地连接并通知所属会话停止、归零和释放设备，不依赖中继随后及时发送关闭帧。

| device.op 的 t | 行为 |
| --- | --- |
| 0 | 波形任务，接受十六进制或数值帧，ver 2／3、可选 seq |
| 3 | 相对增加／减少基础强度，一次性任务 |
| 4 | 临时强度任务，持续期间叠加，任务结束移除其临时值 |
| 7 | 将基础强度设为 0；仅接受 v = 0 |

参数 `s` 指定插槽，`c` 为 0／1，`p` 为 0／1／2，`d` 为毫秒，`im` 替换同插槽、通道和类型的任务。每个通道／类型／优先级独立 FIFO；基础强度与活跃临时值相加后限幅，波形取活跃任务中最高优先级。波形帧为 100 ms，V3 每帧包含四个频率与四个幅度值；V2 三字节按现有 BLE 编码解包为统一采样。seq 是兼容输入，不改变图的时钟。

波形和临时强度的时长在帧内按 25 ms 采样截断，不把不足 100 ms 的末段扩展为完整帧。队列已满时，`im` 仍可替换同类任务；先验证新任务，失败保留原有队列。

`device.op.clear` 可省略参数清理全部任务，或用 `{s}`／`{s,c}` 缩小范围；不能只指定 c，显式 null、非整数通道和空插槽参数均被拒绝。清理移除持续任务，基础强度由 t = 7 归零。任务完成原因包括 `completed`、`cleared`、`replaced`、`cancelled`。暂停、停止与断连取消未结束任务，实际设备归零由 `PipelineSession` 负责；暂停保留占用并发布静音状态，仍允许清理和零重置。请求接受、插槽移除与取消使用同一同步边界，已取消命令不会在恢复后重放。

设备来源将普通输出转换成可替换的短波形／临时强度任务，临时任务最长保持 400 ms；停止先清任务再分别重置两通道。任务完成回包表示调度结果，不能当作设备测量或硬件停止确认。

`DgLabV4Controller` 为 `devices.get`、App 级 ping、四类操作及三种清理范围提供可复用的强类型方法。有限操作的默认等待时间考虑任务时长；无时长临时任务默认等待到完成、清理或断连。调用者可设置等待上限或取消等待，取消已经发出的等待不等同于清理远端任务；需要停止任务时调用 Clear。已取消的令牌或非法等待参数在发送前拒绝。

实现边界：单条 JSON 消息最多 1 MiB、深度 32；每个连接最多 256 个普通在途请求，另留 1 个即时替换及 4 个清理请求位置，每个插槽最多 256 个任务，每个 App 最多 128 个插槽；一条波形最多 500 帧，显式持续时间最多两小时。中继最多 256 条连接，支持 JSON 心跳、原生 ping/pong、空闲关闭及关闭码 4000／4001／4002。它遵循上游对无效 JSON／未知帧的忽略行为，接受文本或二进制帧中的 UTF-8 JSON，再以文本帧转发，并保留消息的任意 JSON 负载。

## 职责与验证

| 对象 | 所有权与职责 |
| --- | --- |
| `DgLabV4Peer` / `DgLabV4Controller` | 消息与响应／强类型操作封装；普通设备来源复用同一操作封装 |
| `DgLabV4DeviceCatalog` / `DgLabV4Slot` | 原子目录与修订／型号、量程及已上报状态转换；型号定义集中于 `DgLabV4Models` |
| `DgLabV4AppSession` / `DgLabV4AppPool` | 单个网络连接／按运行与端点引用计数共享，注册失败同样释放引用 |
| `DgLabV4AppRouter` / `DgLabV4AppSlot` | 同步请求与取消／插槽信箱、状态快照和真正的增量补丁 |
| `DgLabV4Scheduler` / `DgLabV4ScheduledTask` | FIFO、优先级与合并／波形、临时强度、一次性强度命令各自的行为和时长 |

处理器不执行 I/O，资源参与者负责连接与清理。中继只转发，不执行设备任务。全部玩法仍使用一套 `PipelineSession`。

### 公开契约核对

2026-11-10 核对上游 main：中继 [`bf6fdd1`](https://github.com/dungeonlab-open/dglab-websocket-server/tree/bf6fdd1625d8a76eafc97c38c660c6a3fd71c1d1)，SDK [`3e7ed44`](https://github.com/dungeonlab-open/dglab-kit/tree/3e7ed447e387c2fa52f2c27353c27ff295b0bdeb)。以下覆盖公开 V4 消息与操作；App 内部功能不能仅凭其状态字段推断为远程 RPC。

| 契约 | 实现入口 | 回归验证 |
| --- | --- | --- |
| 配对、`tid` / `targetId`、多 App 路由与来源隔离 | `DgLabV4Relay` / `DgLabV4Peer` | `DgLabV4NetworkTests`：参数优先级、空参数、未知控制方与跨控制方拒绝 |
| hello、接入／断开、心跳、ping/pong、空闲关闭及关闭码 | `DgLabV4Relay` / `DgLabV4Peer` | `DgLabV4NetworkTests`：心跳、空闲、终止通知无需关闭帧；`DgLabV4PipelineTests`：通知后输出归零并释放占用 |
| `devices.get`、App 级 `ping` | `DgLabV4Controller` / `DgLabV4AppRouter` | `DgLabV4ControllerTests`：实际回环调用 |
| `device.op` 的 0、3、4、7；A / B；三种优先级、FIFO、`d`、`im`、四种完成原因 | `DgLabV4Controller` / `DgLabV4Scheduler` | `DgLabV4ProtocolTests`、`DgLabV4ControllerTests`、`DgLabV4PipelineTests`：独立调度、替换、取消、帧内时长及长任务响应 |
| V2 / V3 数值与十六进制波形、`ver` / `seq` | `DgLabV4Pulse` / `DgLabV4Operation` | `DgLabV4ProtocolTests`、`DgLabV4ControllerTests`：双格式与双版本，非法输入拒绝 |
| `device.op.clear` 全部／插槽／通道 | `DgLabV4Controller` / `DgLabV4AppRouter` | `DgLabV4ControllerTests`、`DgLabV4NetworkTests`：范围校验、暂停与队列满时仍可清理 |
| `devices.snapshot`、`devices.patch`、`slots.patch` | `DgLabV4DeviceCatalog` / `DgLabV4AppSlot` | `DgLabV4ProtocolTests`、`DgLabV4NetworkTests`：替换、深合并、旧查询、原子更新与无效条目隔离 |
| `custom.action` 0–9 | `DgLabV4Transport` / `DgLabV4InputProcessor` | `DgLabV4NetworkTests`：范围和重复动作序号；`PipelineRuntimeTests`：事件时间与序号合并／失效；`DgLabV4PipelineTests`：反馈上行 |
| 郊狼 V2 / V3、负鼠、灵猫的公开状态 | `DgLabV4Models` / `DgLabV4Slot` / `DgLabDeviceStateViewModel` | `DgLabV4ProtocolTests`、`DgLabV4UiTests`：型号能力、量程、测量新鲜度、只读字段与中英文显示 |
| WS / WSS、路径、文本／二进制 JSON 透传、异常与资源清理 | `JsonWebSocket` / `DgLabV4Relay` / App 资源参与者 | `DgLabV4NetworkTests`：证书信任、任意 JSON、畸形业务帧及非终止错误；`DgLabV4PipelineTests`：监听器生命周期与两端限幅 |

另已用官方 `dglab-kit 1.0.5` 经真实回环中继完成设备发现、相对强度、临时强度、波形、清理、零重置和反馈互通；接收端为无硬件测试管线。公开契约已有对应实现和测试；官方手机 App 的强度尺度、优先级组合和真实物理输出仍需联调验证。
