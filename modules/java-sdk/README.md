# YokoWave Java SDK

独立 Java 25 库，不引用 Minecraft、.NET 或 UI。使用 JDK WebSocket，唯一运行依赖为 Gson 2.10.1。包名：`io.github.ajtn123.yokowave.sdk`。

```sh
cd modules/java-sdk
bash gradlew build
bash gradlew publishToMavenLocal
```

其他 Gradle 项目可 `includeBuild('/path/to/modules/java-sdk')`，或从本地 Maven 引用：

```groovy
repositories { mavenLocal(); mavenCentral() }
dependencies { implementation 'io.github.ajtn123.yokowave:yokowave-sdk:1.0.0' }
```

## YokoWave

```java
try (var client = new YokoWaveClient(address, token)) {
    client.acquire(YokoWaveClient.LeaseType.EVENT, List.of());
    client.register("my-game", List.of(
        new YokoWaveClient.EventDefinition("collision", "trigger", "Collision", null, null, 1000),
        new YokoWaveClient.EventDefinition("speed", "number", "Speed", 0d, 100d, 1000)));
    client.emit("collision", null);
    client.emit("speed", 12d);
}
```

`devices()` 返回设备目录；`acquire(CONTROL, List.of(id))` 后，用 `call("control.play", parameters)` 等调用[高层接口](../../docs/websocket.md)，`Json.object(...)` 帮助构建参数。GATT 使用 GATT 租约及 `gatt.*`，通知由 `onNotification` 接收；SDK 不实现设备驱动。

库负责认证、递增 ID／序号、单调时钟截止时间、自动续期和命名空间隔离。每连接同一时间只保留一个租约，`release()` 后可重新申请。`close()` 对 control 先停止再释放，保留停止与释放错误，最终关闭网络。事件租约不申请设备。

## DG-LAB V4

```java
try (var client = new DgLabClient("ws://192.168.1.10:9998/v4")) {
    String pairingUrl = client.pairingUrl(); // 编码为二维码交给 App
    client.maintain(); // 定期调用，查询新接入 App；刚连接时目录可能为空
    var devices = client.devices();
    if (!devices.isEmpty()) {
        var device = devices.getFirst();
        client.request(device.appId(), "device.op.clear", Json.object("s", device.slotId())).get();
        client.operate(device, 7, 0, Json.value(0), 0, true).get();
        client.operate(device, 7, 1, Json.value(0), 0, true).get();
    }
}
```

SDK 作为控制方连接外部中继，App 根据配对 URL 接入。目录支持快照、增删和深层补丁，旧查询不覆盖新状态。`Device` 提供在线、量程和 App 上限／静音状态；`request` 调用 V4 RPC，`operate` 封装设备操作，`onEvent` 提供通知／动作。V4 `close()` 只关闭网络，调用者先清任务和归零；Minecraft 适配器负责该顺序。

调用属于阻塞 I/O，勿放在 UI／游戏线程。DG 操作的 future 在任务结束时完成；默认 RPC 超时 5 秒，`request` 可显式指定 `Duration`；`operate` 按有限任务时长延长等待，最长两小时。通知回调在接收线程执行，不可阻塞或同步发起 RPC。连接最多 64 个待响应请求，JSON 文本最多 1 MiB（UTF-8）、深度 32，支持分片文本、WS 和 JDK 默认信任库认可的 WSS。不自动重连或重放。远端错误为 `RpcException`，检查 `code()`；传输失败后关闭并创建新客户端。

独立 [SDK CI](../../.github/workflows/java-sdk.yml) 以 `java-sdk-v<版本>` 标签发布库、源码和 Javadoc JAR，版本来自 `sdk_version`，不触发桌面版发布。尚未配置远程 Maven 仓库。
