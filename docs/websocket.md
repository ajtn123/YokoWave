# WebSocket 设备共享与事件

## 实现与入口

用户配置步骤位于应用内「帮助」，这里定义网络边界与接口契约。

本文描述 YokoWave 的 `yokowave.v2` 协议。DG-LAB App 使用独立的 [V4 协议](dglab-v4.md)，由另一组设备来源和节点接入，不使用这里的认证或租约。

`RemotePipelineNodes` 提供 GATT、control 和 event 资源节点。GATT 节点导出原始读写／通知，远端使用同一协议工厂运行驱动；control 节点将远端输出缓冲到本地图，再经数值处理、限幅和设备路由提交；event 节点接收客户端声明的触发和数值事件。每类端点只允许对应租约。输入设备可由 GATT 导出，或通过 control 读取状态和执行命令。事件端点不申请设备，详见[事件契约与示例](websocket-events.md)。

默认监听 127.0.0.1:9000，路径为 /ws/{资源节点 ID，32 位无连字符}。多个节点可共享相同地址／端口／证书的监听器，各自保持认证与设备范围。非回环监听使用客户端可达地址；PFX 证书须由客户端信任，不跳过证书校验。

`WebSocketGattAdapter` / `WebSocketGattConnection` 实现链路抽象，`WebSocketControlTransport` 实现统一设备传输，经 `RoutedDeviceTransport` 与本地及模拟来源共存。`RemoteEventClient` 声明并发送事件。三者复用 `RemoteClientConnection` 的认证与 `RemoteLeaseLink` 的租约续期、顺序号和服务端单调时钟。`RemoteServerSession` 为设备导出和事件会话提供共同的监听／认证边界，事件接收不创建另一套玩法执行器。BLE 主机后端为 Windows，控制端可使用其他桌面平台。

上述客户端及协议契约属于 [C# SDK](../modules/csharp-sdk/README.md)，命名空间为 `YokoWave.Sdk`。`RemoteControlClient` 供外部软件直接调用播放、输出、状态和命令；`YokoWave.Remote` 保留服务端与管线集成。客户端包只依赖 Core，不要求 ASP.NET Core 或管线运行时。

图、服务端参数和设备引用保存在 pipelines.json，端点地址／名称／连接种类保存在 settings.json；令牌和证书密码仅在内存。WebSocket 来源需手动接入，已接入来源中的设备可参与自动重连；启动不自动监听或恢复玩法输出。服务端 Remote 依赖 .NET 10 的 Microsoft.AspNetCore.App；SDK 仅要求 .NET 10 基础运行时。

## 协议及租约

- WebSocket 子协议：`yokowave.v2`。文本 JSON-RPC 2.0，UTF-8，可分片，单条最大 1 MiB，深度最大 32，不接收批量 RPC 数组。
- 请求 `id` 是正整数或十进制正整数字符串，在同一连接上严格递增，不可重复。响应返回原始 `id`。设备控制及事件声明／发送必须使用请求，服务端不执行没有 `id` 的通知。
- 首先调用 `system.authenticate`，参数 `{ "token": "..." }`；10 秒内未认证则关闭连接。随后 `system.hello` 返回版本、服务端玩法 ID、租约超时、消息上限和单调时钟 `clockMilliseconds`。枚举使用 camelCase 字符串，二进制数据使用 Base64。
- `devices.list` 返回设备数组；`devices.describe` 使用 `deviceId`。设备 ID 是服务端玩法内的匿名标识；客户端将端点 ID、玩法 ID、设备 ID 组合为本地标识。设备包含协议、能力、通道掩码、电量、信号及模拟标记。
- `lease.acquire` 参数为 `{ "deviceIds": ["..."], "type": "gatt" }`、`"control"`，或事件端点上的 `{ "deviceIds": [], "type": "event" }`。返回 `{ "id", "type", "deviceIds", "leaseMilliseconds" }`。设备租约最多 32 台目标，设备不能出现在其他租约中；事件租约没有设备，同一连接最多一个。
- `lease.renew` 和 `lease.release` 参数为 `{ "lease": "UUID" }`，只能由取得租约的连接调用。续期返回当前服务端 `clockMilliseconds`。自带客户端按超时的三分之一续期，租约撤销通知为 `lease.revoked`。
- 每个 `gatt.*` / `control.*` / `event.*` 请求都包含 `lease`、严格递增的 `sequence`，以及服务端单调时钟中的 `deadline`（毫秒）。执行前拒绝过期或超过当前时钟 1 秒的截止时间。客户端用 hello／续期时钟加本地单调经过时间估算，不依赖两台电脑的系统时间；自带客户端使用 500 ms 截止时间。

播放与手动输出请求先校验完整参数、波形采样、目标能力及图定义，再修改硬件选项或替换当前播放；参数错误返回 `-32602` 并保留当前播放。顺序／随机播放只接受列表内的实时波形切换，在全部目标校验通过后才提交。传感器可接收统一设备连接流程的零输出请求，非零输出仍被拒绝。

示例请求：

```json
{"jsonrpc":"2.0","id":"7","method":"control.output","params":{"lease":"00000000-0000-0000-0000-000000000001","sequence":1,"deadline":12500,"frame":{"channelA":0,"channelB":0,"frequency":20,"outputLimit":30}}}
```

响应为 `result` 或 `error: { code, message }`；`message` 是应用的本地化键，不发送调用堆栈、请求内容或令牌。`-32600` 无效 RPC／重复请求 ID，`-32601` 未知方法，`-32602` 参数错误；`-32001` 认证失败，`-32002` 设备或事件客户端标识已占用，`-32003` 租约／序号／截止时间错误，`-32004` 设备不可用，`-32005` 事件缓冲已满，其他执行失败为 `-32000`。

## GATT 接口

`gatt` 租约停止主机协议驱动的输出和自动维护，将已发现且受协议允许的 GATT 特征交给控制端。每个方法额外包含租约内的 `deviceId`。

| 方法 | 参数／结果 |
| --- | --- |
| `gatt.describe` | 返回 `device`、`attributes`、`maximumWriteLength`；特征描述含 `address`、`read`、`write`、`writeWithoutResponse`、`notify` |
| `gatt.read` | `address: { service, characteristic }`；返回 Base64 或 `null` |
| `gatt.write` | `write: { address, data, mode, delayAfterMilliseconds }`；mode 为 `auto`、`withResponse`、`withoutResponse` |
| `gatt.batch` | `writes` 为 1–16 条有序写入；整批先验证，再串行执行 |
| `gatt.subscribe` / `gatt.unsubscribe` | `address`；只允许设备已发现的通知特征 |

每次写入长度为 1 到真实连接的 `maximumWriteLength`，不伪造 MTU；一批写入的累计延时最多 100 ms。已验证批次不意味着硬件事务：中途写入失败时，已完成的包不会撤回。通知为 `gatt.notification`，参数 `{ lease, deviceId, notification: { address, data }, clockMilliseconds }`。自带客户端忽略超过 500 ms 的旧通知。

主机不同时解析透传通知或发送协议维护包。归零、租约结束和断连时，主机通过工厂创建新的协议驱动并重新初始化，避免旧强度缓存影响停止行为；失败保留错误与占用，等待再次停止或断开。GATT 允许原始协议指令，不能用百分比上限约束其内容；服务端面板的输出上限仅适用于 `control`。

## 高层控制接口

`control` 租约在主机运行会话引擎，使用统一波形、限幅、设备能力验证和停止逻辑。除状态和单设备命令外，操作作用于租约全部目标；为不同设备建立独立租约可分别控制。高层输出上限取请求上限和服务端上限的较小值。

| 方法 | 参数／行为 |
| --- | --- |
| `control.output` | `frame` 为 Core `OutputFrame` 的 camelCase JSON；按 100 ms 周期保持该帧，支持三通道、采样窗口、温柔模式、泵、原生程序及硬件选项；也可使用下列程序接口 |
| `control.play` | `playback: { program, waveform?, channelA?, channelB?, channelC?, durationSeconds? }`；`program` 为 `RemotePlaybackSettings`，`kind` 支持 `waveform`、`nativeProgram`、`pump`；配置含 order、playlist、channelBWaveform、outputLimit、strength、offsetChannelB、gentle、pump、nativeProgram、hardware、balances，播放请求会编译为普通管线图 |
| `control.playlist` | 同上，要求 waveform 类型和非空 `program.playlist`；支持顺序、随机、单曲及独立 A/B 配置 |
| `control.native` | 同上，要求 nativeProgram 类型；原生程序参数和目标必须兼容，固定程序的幅度／上限约束同本地玩法 |
| `control.pump` | 同上，要求 pump 类型；方向、开关、两路时长放在 `program.pump` |
| `control.strength` | `channelA`、`channelB`、可选 `channelC`；调整当前会话或直接输出帧，保留较低的请求上限 |
| `control.waveform` | `waveform` 为统一 `WaveformAsset`；运行中切换，播放列表模式仅允许列表内波形 |
| `control.pause` / `control.resume` | 归零暂停／继续，暂停保留租约及播放位置，仍需续期 |
| `control.stop` | 归零并结束播放，保留租约；释放设备请调用 lease.release |
| `control.command` | `deviceId`、`command`；按设备能力执行下表的强类型命令 |
| `control.status` | `deviceId`；返回遥测、电量、运动、接触和灯光状态 |
| `control.subscribe` / `control.unsubscribe` | `deviceId`；按 100 ms 采样，仅在状态变化时推送 `control.status` 通知 `{ lease, status }` |

状态中的 `telemetryClockMilliseconds` 与 `motionClockMilliseconds` 分别表示遥测和运动读数的采集时刻，使用服务端单调时钟，可能因采集早于服务端启动而为负值；缺失表示没有有效采集时间。客户端将其换算为输入年龄，状态刷新不会延长旧读数的有效期，新采样恢复有效输入。`motion.capturedAt` 不用于跨主机的新鲜度判断。

遥测同时携带独立压力或反馈时间时，`pressureClockMilliseconds` / `eventClockMilliseconds` 表达各字段的采集时刻，客户端重建本地时间。这样转发 V4 设备后，反馈和电量更新仍不会延长旧压力数据的有效期。

声控、滑动及运动等有实时输入的图在控制端运行，通过选定的 GATT 或 control 连接发送输出；高层 API 也可由第三方程序消费自己的输入并发送统一帧。高层服务端不远程打开麦克风或读取客户端文件。

`control.command` 的 `type` 与 Core 类型对应；字段和范围遵循 [蓝牙协议覆盖表](bluetooth.md#设备与协议覆盖)：

服务端申请设备时会关闭输入流和爪印独立触发器。高层客户端需要输入读数时，发送 `{ "type": "stream", "enabled": true }`；停止、租约结束或全部停止时再次关闭。GATT 客户端由其协议驱动启用输入。

| type | Core 类型 |
| --- | --- |
| query | QueryDevice |
| motion | ConfigureMotionStream |
| steps | ControlStepCounter |
| stream | ConfigureSensorStream |
| indicator | ConfigureIndicator |
| shoulder | ConfigureShoulderLight |
| calibrate | CalibratePawAngles |
| resetPaw | ResetPawParameters |
| pressure | ConfigurePressureSensor |
| display | SynchronizeDisplay |
| paw | ConfigurePaw |
| light | ConfigureLight |

Paw 的 `trigger.type` 支持 `none`、`live`、`random`、`probability`、`voltage`、`buttonMotion`。例如灵猫命令为 `{ "type": "pressure", "resetPressure": true, "orientation": "flipped" }`，灯光命令为 `{ "type": "light", "enabled": false }`。

## JavaScript 调用示例

以下代码在浏览器中连接一个已运行的服务端，仅发送零强度；填写面板的地址和令牌。生产客户端应在连接关闭时处理自身状态，并保持一个租约操作队列。

```js
const socket = new WebSocket(serverUrl, "yokowave.v2");
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = reject;
});
let requestId = 0;
const pending = new Map();
socket.onmessage = event => {
  const message = JSON.parse(event.data);
  if (!message.id) return; // 可在这里处理状态及租约撤销通知
  const waiter = pending.get(String(message.id));
  if (!waiter) return;
  pending.delete(String(message.id));
  clearTimeout(waiter.timeout);
  if (message.error) waiter.reject(message.error);
  else waiter.resolve(message.result);
};
function rpc(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = String(++requestId);
    const timeout = setTimeout(() => {
      pending.delete(id);
      socket.close(); // 超时不重试设备指令
      reject(new Error("timeout"));
    }, 10000);
    pending.set(id, { resolve, reject, timeout });
    socket.send(JSON.stringify({ jsonrpc: "2.0", id, method, params }));
  });
}
await rpc("system.authenticate", { token });
const hello = await rpc("system.hello");
let clock = hello.clockMilliseconds;
let receivedAt = performance.now();
const devices = await rpc("devices.list");
const output = devices.find(device => device.kind !== "sensor");
const lease = await rpc("lease.acquire", { deviceIds: [output.id], type: "control" });
let sequence = 0;
const heartbeat = setInterval(async () => {
  try {
    const reply = await rpc("lease.renew", { lease: lease.id });
    clock = reply.clockMilliseconds;
    receivedAt = performance.now();
  } catch {
    clearInterval(heartbeat);
    socket.close();
  }
}, lease.leaseMilliseconds / 3);
function control(method, params = {}) {
  return rpc(method, {
    ...params,
    lease: lease.id,
    sequence: ++sequence,
    deadline: Math.floor(clock + performance.now() - receivedAt + 500)
  });
}
try {
  await control("control.output", {
    frame: { channelA: 0, channelB: 0, frequency: 20, outputLimit: 30 }
  });
  await control("control.status", { deviceId: output.id });
  await control("control.stop");
  await rpc("lease.release", { lease: lease.id });
} finally {
  clearInterval(heartbeat);
  socket.close();
}
```

## 停止与验证范围

[`modules/dinosaur`](../modules/dinosaur/) 提供无需构建的浏览器调用示例：使用 control 租约，在碰撞时提交有限、单次播放的统一波形。启动与操作见[恐龙游戏](dinosaur.md)。

断连、租约到期、服务端定时结束、本地停止及全部停止都尝试归零；输入流和原生程序也执行协议停止。每台设备的失败独立收集，原始错误和清理错误均保留，停止未确认时不移交设备。请求超时或取消后关闭连接，不自动重试控制请求。物理设备失联时，软件不能确认硬件实际输出。

自动测试通过真实回环 WebSocket 连接和 GATT 替身验证九种协议的连接／归零／通知、鉴权、租约类型隔离、截止时间、重复 ID、限幅、播放列表、暂停继续、原生程序、泵、输入设备命令、共享端口、定时停止、混合来源以及无凭据持久化。事件测试覆盖声明、跨客户端隔离、触发排队、数值过期、断连归零、暂停清空、模拟来源和子图分流；Headless 验证中英文选择界面、自动保存及模板复用。未进行远程非零输出的实机验证。
