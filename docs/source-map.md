# 源码地图

项目使用文件范围命名空间，目录与命名空间对应。入口与依赖见[架构](architecture.md)。

```text
src/
  YokoWave.Core/          设备、能力、协议、统一资产和工作区存储
    Bluetooth/           协议识别、驱动、编码、通知与 GATT 抽象
    Data/                规范化内置波形
    Localization/        JSON 词典、本地化消息与异常
  YokoWave.Pipelines/     纯 .NET 图定义、编译、处理器和会话
  YokoWave.Remote/        WebSocket RPC、租约、设备／事件客户端与资源节点
    DgLab/V4/            DG-LAB V4 配对、设备目录、任务处理与中继
  YokoWave.App/           Avalonia 界面
    ViewModels/          设备、设置、波形、历史及共享编辑模型
      Pipelines/         图工作区、节点编辑器、历史与设备分组
    Views/
      Pages/             设备、波形、历史、设置、帮助及确认窗口
      Components/        设备卡片、硬件／协议表单、序列及波形编辑
      Pipelines/         画布、参数面板、计时、设备分组与连线交互
    Controls/            触控面板、按住增强、波形绘图
    Styles/              颜色、尺寸、排版和共享语义样式
    Assets/              SVG 图标与窗口／EXE 使用的 ICO
  YokoWave.Desktop/       桌面入口与平台实现
    Bluetooth/           Windows BLE、GATT 发现和链路
    Audio/               MiniAudio 输入与媒体播放
    Diagnostics/         原生蓝牙／音频探针
tests/YokoWave.Tests/    按领域组织的 xUnit 和 Headless 测试
  Support/               图工厂、传输替身、隔离目录及 UI 操作
tests/dinosaur/          Node 内置测试运行器验证浏览器模块
modules/dinosaur/        HTML、JS、CSS 恐龙游戏，调用现有 control 服务端
locales/
  app/                   应用的 en.json / zh-CN.json，嵌入 Core
  dinosaur/              恐龙游戏的 en.json / zh-CN.json，同源加载
schemas/                 单波形与波形数组的 JSON Schema
scripts/                 风格、验证及覆盖率脚本
```

## 修改入口

下列文件表达职责边界，不是需要同步修改的固定清单；沿实际调用关系追踪实现。

| 行为 | 主要位置 |
| --- | --- |
| 应用启动、平台工厂 | [Desktop/Program.cs](../src/YokoWave.Desktop/Program.cs)、[App.axaml.cs](../src/YokoWave.App/App.axaml.cs) |
| 页面组合、保存与关闭 | [MainViewModel](../src/YokoWave.App/ViewModels/MainViewModel.cs)、[MainView](../src/YokoWave.App/Views/MainView.axaml) |
| 设备锁、占用和输出状态 | [DeviceController](../src/YokoWave.Core/DeviceController.cs)、[DeviceConnection](../src/YokoWave.Core/DeviceConnection.cs)、[DeviceOperationLocks](../src/YokoWave.Core/DeviceOperationLocks.cs) |
| 来源路由与重连 | [RoutedDeviceTransport](../src/YokoWave.Core/RoutedDeviceTransport.cs)、[DeviceAutoConnector](../src/YokoWave.Core/DeviceAutoConnector.cs)、[DevicesPaneViewModel](../src/YokoWave.App/ViewModels/DevicesPaneViewModel.cs) |
| 协议识别和驱动选择 | [BluetoothDeviceProfile](../src/YokoWave.Core/Bluetooth/BluetoothDeviceProfile.cs)、[DeviceCapabilities](../src/YokoWave.Core/Bluetooth/DeviceCapabilities.cs)、[BluetoothProtocolDriverFactory](../src/YokoWave.Core/Bluetooth/BluetoothProtocolDriverFactory.cs) |
| 蓝牙单链路生命周期 | [BluetoothDeviceSession](../src/YokoWave.Core/Bluetooth/BluetoothDeviceSession.cs)、[WindowsBluetoothConnection](../src/YokoWave.Desktop/Bluetooth/WindowsBluetoothConnection.cs) |
| 广播合并与名称解析 | [BluetoothDiscoveryCatalog](../src/YokoWave.Core/Bluetooth/BluetoothDiscoveryCatalog.cs)、[WindowsBluetoothDiscovery](../src/YokoWave.Desktop/Bluetooth/WindowsBluetoothDiscovery.cs)、[WindowsBluetoothNameResolver](../src/YokoWave.Desktop/Bluetooth/WindowsBluetoothNameResolver.cs) |
| 图契约和注册 | [PipelineDocument](../src/YokoWave.Pipelines/PipelineDocument.cs)、[NodeTypeRegistry](../src/YokoWave.Pipelines/NodeTypeRegistry.cs)、[FeatureNodes](../src/YokoWave.Pipelines/FeatureNodes.cs) |
| 图编译、求值和提交 | [PipelineCompiler](../src/YokoWave.Pipelines/PipelineCompiler.cs)、[PipelineRuntime](../src/YokoWave.Pipelines/PipelineRuntime.cs)、[PipelineSession](../src/YokoWave.Pipelines/PipelineSession.cs)、[PipelineOutputDispatcher](../src/YokoWave.Pipelines/PipelineOutputDispatcher.cs) |
| 内置与网络模板 | [PipelineTemplates](../src/YokoWave.Pipelines/PipelineTemplates.cs)、[RemotePipelineNodes](../src/YokoWave.Remote/RemotePipelineNodes.cs) |
| 图编辑、分组和历史 | [PipelineEditorViewModel](../src/YokoWave.App/ViewModels/Pipelines/PipelineEditorViewModel.cs)、[PipelineDeviceGroupsViewModel](../src/YokoWave.App/ViewModels/Pipelines/PipelineDeviceGroupsViewModel.cs)、[PipelineEditHistory](../src/YokoWave.App/ViewModels/Pipelines/PipelineEditHistory.cs) |
| RPC 和租约 | [WebSocketRpcPeer](../src/YokoWave.Remote/WebSocketRpcPeer.cs)、[RemoteServerSession](../src/YokoWave.Remote/RemoteServerSession.cs)、[RemoteExportSession](../src/YokoWave.Remote/RemoteExportSession.cs)、[RemoteClientConnection](../src/YokoWave.Remote/RemoteClientConnection.cs)、[RemoteLeaseLink](../src/YokoWave.Remote/RemoteLeaseLink.cs) |
| 远程事件 | [RemoteEventSession](../src/YokoWave.Remote/RemoteEventSession.cs) 管理租约与声明，[RemoteEventHub](../src/YokoWave.Remote/RemoteEventHub.cs) 缓冲事件与缓存目录，[RemoteEventClient](../src/YokoWave.Remote/RemoteEventClient.cs) 声明并发送，[RemoteEventNodes](../src/YokoWave.Remote/RemoteEventNodes.cs) 注册节点，[RemoteEventProcessor](../src/YokoWave.Remote/RemoteEventProcessor.cs) 同步求值；见[事件契约](websocket-events.md) |
| DG-LAB V4 | [DgLabV4Controller](../src/YokoWave.Remote/DgLab/V4/DgLabV4Controller.cs)、[DgLabV4Transport](../src/YokoWave.Remote/DgLab/V4/DgLabV4Transport.cs)、[DgLabV4AppRouter](../src/YokoWave.Remote/DgLab/V4/DgLabV4AppRouter.cs)、[DgLabV4Scheduler](../src/YokoWave.Remote/DgLab/V4/DgLabV4Scheduler.cs)、[DgLabV4Relay](../src/YokoWave.Remote/DgLab/V4/DgLabV4Relay.cs)；见 [V4 契约与职责](dglab-v4.md) |
| 波形资产、生成和交换 | [WaveformAsset](../src/YokoWave.Core/WaveformAsset.cs)、[WaveformGenerator](../src/YokoWave.Core/WaveformGenerator.cs)、[WaveformExchange](../src/YokoWave.Core/WaveformExchange.cs) |
| 本地数据 | [WorkspaceStore](../src/YokoWave.Core/WorkspaceStore.cs)、[SessionJournal](../src/YokoWave.Core/SessionJournal.cs)、[PipelineStore](../src/YokoWave.Pipelines/PipelineStore.cs) |
| 用户手册 | [HelpView.axaml](../src/YokoWave.App/Views/Pages/HelpView.axaml)，正文在 [en.json](../locales/app/en.json) / [zh-CN.json](../locales/app/zh-CN.json) 的 `Help.*` |
| 恐龙游戏 | [modules/dinosaur](../modules/dinosaur/)，物理、绘制、输出和界面分别组合；见[运行与接口](dinosaur.md) |
| 官网与模块发布 | [site](../site/)、[modules/shared](../modules/shared/)、[build-pages.ps1](../scripts/build-pages.ps1)；模块目录由元数据自动发现，见 [GitHub Pages](pages.md) |

## 界面资源

`Palette.axaml` 只负责颜色，`Metrics.axaml` 负责尺寸、间距和圆角。`Typography`、`Layout`、`Controls`、`Components`、`Pipelines` 与 `ItemTemplates` 按职责组合共享样式；页面和自绘控件消费动态资源。SVG 是图标源，ICO 由桌面项目的 `ApplicationIcon` 嵌入 EXE，并用于窗口图标。

测试目录与替身职责见[测试说明](testing.md)。新增文件放入拥有该行为的模块，不建立无具体职责的 Utils / Common 层。
