# YokoWave C# SDK

`YokoWave.Sdk` 面向 .NET 10，提供与桌面应用共用的 WebSocket 客户端。仅依赖 `YokoWave.Core`，无需 Avalonia、管线项目或 ASP.NET Core 运行时。

`YokoWave.Remote` 保留服务端、DG V4 中继、被控端和管线节点，并引用本 SDK。客户端实现已搬入本目录，没有复制协议或创建第二套执行器。公共命名空间为 `YokoWave.Sdk` 与 `YokoWave.Sdk.DgLab.V4`。

## 引用与构建

源码中可直接 `ProjectReference` 此目录的 `YokoWave.Sdk.csproj`。运行根目录 `./scripts/package-sdk.ps1` 生成 SDK 与 Core 两个 NuGet 包，并验证独立消费者；把两者放在同一 NuGet 源：

```sh
dotnet add package YokoWave.Sdk --version 1.0.0 --source /path/to/artifacts/sdk
```

包内 Core 同时含有现有内置波形数据，保留原许可，并附 `THIRD-PARTY-NOTICES.md` 与许可全文。SDK 代码为 MIT。

## 高层控制

```csharp
using YokoWave.Core;
using YokoWave.Sdk;

await using var client = new RemoteControlClient(url, token);
await client.ConnectAsync();
var device = (await client.GetDevicesAsync()).First();
await client.AcquireAsync([device.Id]);
await client.OutputAsync(OutputFrame.Zero);
await client.ReleaseAsync();
```

`RemoteControlClient` 提供统一帧、波形／播放列表／原生程序／泵播放、强度与波形切换、暂停／继续／停止、设备命令、状态查询和订阅；`CallAsync<T>` 可调用其他 `control.*` 方法。使用本客户端枚举的原始设备 ID，不混用应用路由后的 ID。

同一客户端只保留一个 control 租约，可包含多台设备。I/O 串行，自动维护鉴权、租约续期、序号与单调时钟截止时间。`StopAsync` 归零并保留租约；`ReleaseAsync` 先停止再释放，可重新申请；`DisposeAsync` 等待在途调用、停止／释放并关闭连接。原始与清理错误都保留，释放失败关闭链路，不自动重放输出。状态回调运行在接收线程，UI 调用者自行调度，不要在回调内阻塞等待 RPC。

## 事件

```csharp
await using var events = new RemoteEventClient(url, token, new("my-game",
    [new("collision", RemoteEventKind.Trigger), new("speed", RemoteEventKind.Number, Minimum: 0, Maximum: 100)]));
await events.ConnectAsync();
await events.EmitAsync("collision");
await events.EmitAsync("speed", 12);
```

事件客户端验证声明和值，连接时先认证、申请无设备租约并声明目录。网络服务端和图决定事件如何影响输出。基础协议见 [WebSocket](../../docs/websocket.md) 与[事件契约](../../docs/websocket-events.md)。

## GATT 与 DG-LAB V4

- `WebSocketGattAdapter` 实现 Core 的 `IBluetoothAdapter`，枚举后可连接、读取、写入／批量写入及接收通知，也可交给 `BluetoothDeviceTransport` 和同一驱动工厂。
- `WebSocketControlTransport` 实现 Core 的设备传输接口，适用于需要 `DeviceController` 路由与输入新鲜度换算的软件；直接播放程序用 `RemoteControlClient`。
- `DgLabV4Transport` 连接外部 V4 中继，`ScanAsync` 建立连接后 `PairingUrl` 可供 App 扫码，后续 App 设备进入普通传输接口。`DgLabV4Peer`／`DgLabV4Controller` 提供低层通知和强类型的全部公开操作；其 peer 释放只关闭网络，直接调用者须先清任务、归零。

WS／WSS 使用 .NET 的证书信任校验。原始 GATT 写入不能受百分比上限约束；control 使用服务端和请求上限中较低者。失联时不能确认物理输出状态。

## 测试与发布

`tests/YokoWave.Sdk.Tests` 使用真实回环主机和既有 GATT 替身，测试直接控制、限幅、订阅、命名空间、停止、释放再申请与程序集边界。既有 Remote 测试继续覆盖事件、GATT、V4 和应用集成。

Java 与 C# SDK 共用 [SDKs 工作流](../../.github/workflows/sdk.yml)、`modules/sdk-version.txt` 和 `sdk-v<版本>` 标签；发布附件为三个 Java JAR 及两个 NuGet 包。桌面版和 Minecraft 发布仍独立，尚未配置外部 Maven／NuGet 服务器。
