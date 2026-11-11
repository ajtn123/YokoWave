# WebSocket 事件

事件是 `yokowave.v2` 的第三种端点／租约，与 `gatt`、`control` 使用同一认证、监听器和时钟规则。客户端先声明可发送的事件，再发送事件；本地图决定事件如何影响输出。基础 RPC、TLS、消息限制和租约规则见 [WebSocket](websocket.md)。

## 节点与职责

- `network.event.server`：启动事件端点，输出 `events` 类型的事件流；不要求或占用设备。
- `network.event.input`：连接事件流，选择一个客户端和事件，输出数值端口 `trigger`、`value`、`count`。一个流可连接多个输入节点，也能暴露为子图接口。
- `EventServerParticipant`：拥有监听器、订阅和捕获；`RemoteEventSession`：验证租约、声明与发送；`RemoteEventHub`：缓存目录并提供有界消息队列；`RemoteEventProcessor`：同步求值，不访问网络。
- `RemoteEventClient`：可复用的 .NET 客户端，连接时自动认证、申请事件租约并声明目录，连接成功后才允许发送；后台续期，释放时等待在途操作。

「WebSocket 事件服务端」模板只生成服务端和事件输入两枚普通节点。启动后，把端点和令牌交给客户端；客户端声明目录后，参数面板可选择事件，也可以提前填写稳定的客户端／事件 ID。选择修改自动保存，可在运行中切换。目录仅在本次工作区内缓存，重启应用后由客户端重新声明；节点选择保存在图中，凭据不落盘。

## 声明与发送

顺序为 `system.authenticate` → `system.hello` → `lease.acquire` → `event.register` → 若干 `event.emit`。事件端点不提供设备目录或 `gatt.*` / `control.*` 调用；设备端点也不接受事件租约。

| 方法 | 参数／结果 |
| --- | --- |
| `lease.acquire` | `{type:"event", deviceIds:[]}`，同一连接最多一个租约；返回普通 `RemoteLease` |
| `event.register` | 公共租约参数加 `registration:{clientId, events, simulated?}`；每个租约只能成功声明一次；成功返回 `true` |
| `event.emit` | 公共租约参数加 `eventId`，数值事件还必须有 `value`，触发事件不得有 `value`；返回 `{sequence}`，表示端点接受该消息时分配的序号 |
| `lease.renew` / `lease.release` | 与设备租约一致；到期、释放或断连时移除活动注册，发送 `lease.revoked` |

公共租约参数为 `lease`、严格递增的 `sequence` 和服务端单调时钟中的 `deadline`。`event.register` 与 `event.emit` 必须使用带 `id` 的请求。返回序号表示已接受，不表示某个节点或硬件已经处理；不自动重试或重放事件。

声明示例（在 `event.register` 的参数中）：

```json
{
  "lease": "00000000-0000-0000-0000-000000000001",
  "sequence": 1,
  "deadline": 12500,
  "registration": {
    "clientId": "dinosaur",
    "events": [
      {"id": "collision", "kind": "trigger", "name": "Collision", "validityMilliseconds": 1000},
      {"id": "score", "kind": "number", "name": "Score", "minimum": 0, "maximum": 100, "validityMilliseconds": 1000}
    ],
    "simulated": false
  }
}
```

`clientId` 和 `id` 区分大小写，均为 1–80 个 ASCII 字母、数字、`_`、`-` 或 `.`。每份目录包含 1–128 个唯一事件；可选 `name` 为 1–120 个非空白字符，界面显示该名称，路由仍用 ID。同一端点内，活动客户端 ID 不能重复；断连后同一 ID 可重新连接，必须重新声明完整目录。未声明或越界的发送被拒绝，不修改输入。

`kind` 为 `trigger` 或 `number`。数值的默认范围为 −1,000,000 至 1,000,000，可改为有限的 −10¹² 至 10¹² 内闭区间。范围用于校验，不自动归一化；连接强度端口前，用范围映射或增益转换成 0–1。`validityMilliseconds` 默认 1000，允许 100–10000，以服务端收到事件时计时。`simulated` 默认 false；为 true 时，全部派生数值带模拟标记，不能驱动真实设备。

## 输入语义与生命周期

| 输出 | 行为 |
| --- | --- |
| `trigger` | 每次触发事件输出一个高电平帧；连续高电平之间留一个零帧。数值有更新时也给出更新脉冲，同次捕获的更新合并为一次 |
| `value` | 触发事件仅在高电平帧输出 1，其余帧缺失；数值事件保持最近值直到有效期，不插值 |
| `count` | 本次注册中该输入节点已处理的事件数；数值更新即使在同次捕获中合并也逐条计数；输出有效期跟随最近处理事件 |

管线约每 100 ms 捕获一次。触发消息排队，若处理前过期则丢弃；每个订阅／输入节点最多缓冲 256 条，服务端队列满时原子拒绝新发送并返回 `-32005`。输入节点自身待处理队列溢出属于输入故障，停止所属图。持续高频触发不保证在有效期内全部执行，应按用途设置有效期并控制发送频率。

未连接、未收到所选事件、有效期结束或客户端离线时，输入缺失，普通输出节点归零等待。释放、断连、租约到期、服务端停止会清掉对应排队消息；重新注册从新计数开始。暂停清空队列和输入状态、拒绝发送但保留租约及目录；继续后等待新事件。切换选择同样清除旧值与待处理触发。计数累积了模拟事件时，在重置前保留模拟标记。

使用 `trigger` 可连接节点的触发／重启输入，或经增益后作为一个节拍的强度；`value` 可连接范围映射、阈值、平滑、波形增益和输出节点。已有输出节点的通道能力、独立限幅和设备占用规则继续生效。事件本身不绑定设备。

## .NET 客户端

```csharp
await using var client = new RemoteEventClient(url, token, new("dinosaur",
    [new("collision", RemoteEventKind.Trigger, "Collision"),
     new("score", RemoteEventKind.Number, "Score", Minimum: 0, Maximum: 100)]));
await client.ConnectAsync(cancellationToken);
await client.EmitAsync("collision", cancellationToken: cancellationToken);
await client.EmitAsync("score", 42, cancellationToken);
// 保持 client 存活；应用结束时 DisposeAsync 释放租约和连接。
```

发生断连时 `IsConnected` 变为 false 并触发 `Disconnected`。可以再次调用 `ConnectAsync` 重新声明目录；失败发送不自动重试。已释放的客户端不可复用。

## 浏览器调用

无需客户端包；用浏览器原生 `WebSocket`。下面建立连接并保留 `emit`／`disconnect` 给游戏或其他页面使用，`url` 和 `token` 来自节点面板。页面应在连接失败／关闭后更新状态，不自动重发旧事件。

```js
async function connectEvents(url, token) {
  const socket = new WebSocket(url, "yokowave.v2");
  let requestId = 0, sequence = 0, heartbeat;
  const pending = new Map();
  socket.onmessage = ({ data }) => {
    const message = JSON.parse(data);
    if (message.method === "lease.revoked") socket.close();
    const waiter = pending.get(String(message.id));
    if (!waiter) return;
    pending.delete(String(message.id));
    clearTimeout(waiter.timeout);
    if (message.error) waiter.reject(message.error);
    else waiter.resolve(message.result);
  };
  socket.onclose = () => {
    clearInterval(heartbeat);
    for (const waiter of pending.values()) {
      clearTimeout(waiter.timeout);
      waiter.reject(new Error("Disconnected"));
    }
    pending.clear();
  };
  function rpc(method, params = {}) {
    return new Promise((resolve, reject) => {
      if (socket.readyState !== WebSocket.OPEN) return reject(new Error("Disconnected"));
      const id = String(++requestId);
      const timeout = setTimeout(() => socket.close(), 10000);
      pending.set(id, { resolve, reject, timeout });
      socket.send(JSON.stringify({ jsonrpc: "2.0", id, method, params }));
    });
  }
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = reject;
  });
  try {
    await rpc("system.authenticate", { token });
    const hello = await rpc("system.hello");
    let clock = hello.clockMilliseconds, receivedAt = performance.now();
    const lease = await rpc("lease.acquire", { type: "event", deviceIds: [] });
    function send(method, params) {
      return rpc(method, { ...params, lease: lease.id, sequence: ++sequence,
        deadline: Math.floor(clock + performance.now() - receivedAt + 500) });
    }
    await send("event.register", { registration: { clientId: "dinosaur", events: [
      { id: "collision", kind: "trigger", name: "Collision" },
      { id: "score", kind: "number", name: "Score", minimum: 0, maximum: 100 }
    ] } });
    heartbeat = setInterval(async () => {
      try {
        const reply = await rpc("lease.renew", { lease: lease.id });
        clock = reply.clockMilliseconds;
        receivedAt = performance.now();
      } catch { socket.close(); }
    }, lease.leaseMilliseconds / 3);
    return {
      emit: (eventId, value) => send("event.emit", { eventId, ...(value === undefined ? {} : { value }) }),
      disconnect: async () => {
        try { await rpc("lease.release", { lease: lease.id }); }
        finally { clearInterval(heartbeat); socket.close(); }
      }
    };
  } catch (error) {
    socket.close();
    throw error;
  }
}
const events = await connectEvents(url, token);
// 游戏碰撞时：await events.emit("collision");
// 分数更新时：await events.emit("score", 42);
// 结束连接时：await events.disconnect();
```

声明、发送、租约隔离、时间／模拟来源、子图分流、断连归零及暂停已通过回环／替身测试；界面与持久化经过 Headless 验证。
