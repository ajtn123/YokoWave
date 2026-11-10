# 测试

`tests/YokoWave.Tests` 使用 xUnit v3。开发流程见 [CONTRIBUTING.md](../CONTRIBUTING.md)，生产模块位置见[源码地图](source-map.md)。目录与命名空间按行为领域组织，同一功能的纯逻辑、协议和界面检查分别放到对应领域。

| 目录 | 验证范围 |
| --- | --- |
| `Audio` | 播放增益、静音、非有限采样值、输入特征与播放音量隔离 |
| `Bluetooth` | 协议编码、能力与范围、GATT 初始化、通知解析、确认、错误与连接释放 |
| `Devices` | 通道限制、设备独占、并发、停止与失败回滚、自动连接、硬件设置 |
| `Waveforms` | 生成、统一格式与 Schema、导入边界、默认波形及设备编码兼容性 |
| `Pipelines` | 图编译、数值与来源传播、输入有效期、通道路由、模板、处理器状态和会话生命周期 |
| `Remote` | 本地回环 WebSocket 的认证、RPC 校验、租约、GATT 透传、高层控制与故障收尾 |
| `Storage` | 文件划分、往返、损坏保护、旧开发格式重置、模板清除和 UTF-8 日志滚动 |
| `Localization` | 翻译键与占位符、语言回退、通知和本地化异常 |
| `Ui` | Headless 布局、真实指针输入、绑定、自动保存、编辑历史、分组、计时、语言和主题 |

## 共享准备与隔离

`Support` 只存放测试需要的准备和替身，不承载产品规则：

- `TestFolder` 为每个文件测试创建独立目录并在释放时删除；`TestPaths` 统一定位仓库和产物目录。
- `AsyncAssert.EventuallyAsync` 使用单调时钟和有上限的等待；`UiActions` 在等待和指针操作时处理 UI 队列。能等待任务、写入完成或事件时直接等待，不加入固定延时。
- `Support/Pipelines` 提供小型图工厂、通道波形和可阻塞的输出替身。测试仍在自身代码中声明要证明的连线、参数与结果。
- `Support/Bluetooth` 区分协议 GATT 替身与并发传输替身，不用一个通用替身模拟所有设备生命周期。
- `Support/Remote` 管理真实本地 WebSocket 主机和客户端，端口由系统分配；准备失败与释放均清理资源并保留清理错误。
- `Support/Ui` 统一启动 Headless 应用、绑定日志和截图。每个 UI 测试使用 `UiTestState` 恢复语言、主题及直接修改的应用资源。窗口在 `finally` 中关闭。

程序集禁用测试并行：Headless 应用、语言目录和日志接收器属于进程共享状态。核心并发行为通过测试内部的独立任务和可等待信号验证。租约续期及跨网络数据老化检查需要实际经过对应时间；普通后台完成检查使用状态等待。

## 运行

PowerShell 7 下运行完整检查：

```powershell
./scripts/verify.ps1
# 依赖已缓存且网络不可用：
./scripts/verify.ps1 -Offline
```

该脚本依次还原、检查代码风格、构建和运行测试，不执行 publish。已有 Release 构建时可直接运行或按领域筛选：

```powershell
dotnet tests/YokoWave.Tests/bin/Release/net10.0/YokoWave.Tests.dll -nocolor
dotnet tests/YokoWave.Tests/bin/Release/net10.0/YokoWave.Tests.dll -namespace 'YokoWave.Tests.Pipelines' -nocolor
```

截图写入 `artifacts/screenshots/`，完整验证的 xUnit 报告写入 `artifacts/test-results.xml`。截图辅助诊断，布局、绑定、状态和渲染变化仍通过断言检查；大图测试验证全部节点和连线被呈现，不将单次运行的时间或内存快照当作性能基准。

浏览器模块使用 Node 内置测试运行器，独立于 .NET 检查，不安装 npm 包：

```sh
node --test tests/dinosaur/*.test.js
```

`tests/dinosaur` 验证固定步长物理、碰撞一次性触发、暂停、有限波形、通道路由、RPC 顺序、租约续期、取消及超时清理，并核对根目录各组词典。Remote 的单次碰撞脉冲测试通过实际回环服务端验证客户端不发送停止请求时仍归零且保留租约。浏览器交互、响应式布局和实际协议联调另行检查，不对真实硬件发送非零测试输出。

## 覆盖率与边界

先完成还原和构建，再运行：

```powershell
./scripts/test-coverage.ps1
```

脚本从还原结果定位 `Microsoft.NET.Test.Sdk` 带入的覆盖率收集器，通过 VSTest 收集 Cobertura，避免与项目选择的 Microsoft Testing Platform 命令参数混用。每次运行创建独立的 `artifacts/coverage/<id>/`，包含 TRX、Cobertura 和 `summary.json`，汇总 Core、Pipelines、Remote、App 的行与分支覆盖率。收集器缺失、测试失败或未生成报告均使脚本失败。

整理或删除测试时，核对原有测试行为和覆盖报告；覆盖率只辅助发现缺口，不代替错误路径、边界、所有权和资源清理断言。新增回归围绕可观察行为，不为每个私有方法建立镜像测试。

蓝牙测试使用协议替身，远程测试使用本地回环，UI 使用 Headless；这些检查不等于实机验证。当前测试程序集未覆盖 Desktop 的原生 Windows BLE、音频捕获和窗口合成后端，需要单独的原生启动检查及实机测试。
