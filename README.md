# YokoWave · 波枢

面向 YOKONEX 使用场景重新设计的跨平台设备工作台。使用 **.NET 10 + Avalonia 12**，首版聚焦多设备管理、双通道控制和波形管理。

**当前版本是可运行的本地模拟版本。** 设备、电量、信号和输出均为模拟数据；尚未实现真实蓝牙扫描、硬件 GATT 通信、远程控制及原应用后端。模拟器不会向任何硬件发送指令。

## 运行

安装 .NET 10 SDK，在项目根目录执行：

```sh
dotnet restore YokoWave.slnx
dotnet run --project src/YokoWave.Desktop
```

Windows、macOS 和 Linux 共用同一个桌面入口。当前已在 Windows 完成编译、核心测试与 Avalonia Headless 界面渲染；macOS、Linux 尚未进行实机验证。Android、iOS 不在本次交付范围内。

## 使用

1. 点击「发现设备」刷新本地模拟设备，再连接一台或多台设备。
2. 选择当前设备，设置输出上限，调节 A / B 通道强度。单通道设备始终忽略 B 通道。
3. 选择波形和会话时长，点击「开始会话」。开启「多设备联动」后，会话控制已连接且勾选的设备；每台设备保留各自的输出上限。
4. 点击「全部停止」或按 `Esc` 立即归零。切换页面、切换设备、修改连接、定时结束和关闭应用也会停止会话。
5. 在「波形资料库」新建、复制、编辑、删除自定义波形，或通过 JSON 文件 / 文本导入导出。支持正弦、三角、脉冲、阶梯及自定义采样点。

启动和重新连接不会恢复输出。波形预览展示配置曲线，实时输出数值展示模拟器收到的归一化指令。强度单位为应用内百分比，不代表硬件电流、压力或其他物理量。

## 外观与数据

在「偏好设置 / Settings → 界面语言 / Interface language」切换 `English` / `简体中文`，立即生效并自动保存。首次启动按系统界面语言选择：中文使用 `zh-CN`，其他语言使用 `en`。

翻译使用 [`en.json`](src/YokoWave.Core/Locales/en.json) 和 [`zh-CN.json`](src/YokoWave.Core/Locales/zh-CN.json)，不使用 `.resx`。页面、内置波形、状态、应用错误和文件选择器标题支持运行时更新；用户填写的名称和描述保持原样。实现与维护方式见 [本地化说明](docs/localization.md)。

默认跟随系统明暗主题。FluentTheme 在平台提供支持时自动采用并更新系统强调色；平台未提供时使用 Avalonia 的默认强调色。按钮、导航、状态和 A 通道图表均使用主题资源；B 通道与错误状态使用独立的语义颜色。

所有应用颜色集中定义在 [`src/YokoWave.App/Styles/Palette.axaml`](src/YokoWave.App/Styles/Palette.axaml)。视图和绘图控件通过动态资源获取颜色，运行时切换主题无需重启。系统主题行为参考 [Avalonia Theme variants](https://docs.avaloniaui.net/docs/how-to/theme-switching-how-to) 和 [FluentTheme 文档](https://docs.avaloniaui.net/docs/styling/themes)。

自定义波形、偏好和最多 500 条会话记录保存到 `LocalApplicationData/YokoWave/workspace.json`；准确路径显示在设置页。可用 `YOKOWAVE_DATA_DIR` 环境变量指定其他目录。写入先创建同目录临时文件，再替换原文件；数据损坏时保留原文件并阻止覆盖。应用不访问原应用服务。

## 验证与打包

```sh
dotnet build YokoWave.slnx -c Release
dotnet run --project tests/YokoWave.Tests -c Release
```

测试使用 xUnit v3 进程内运行器，包含真实鼠标点击、滑块绑定、页面切换、明暗主题、强调色和语言切换检查。界面截图输出至 `artifacts/screenshots/`。

`dotnet run --project src/YokoWave.Desktop -- --smoke-test` 可执行原生窗口启动检查，发现模拟设备后自动关闭并保存本地数据。可结合 `YOKOWAVE_DATA_DIR` 使用隔离的数据目录。

PowerShell 7 用户可执行：

```powershell
./scripts/verify.ps1
# 已缓存依赖且网络不可用时：
./scripts/verify.ps1 -Offline
```

按目标系统发布需要安装 .NET 运行时的应用：

```sh
dotnet publish src/YokoWave.Desktop -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64
dotnet publish src/YokoWave.Desktop -c Release -r osx-arm64 --self-contained false -o artifacts/publish/osx-arm64
dotnet publish src/YokoWave.Desktop -c Release -r linux-x64 --self-contained false -o artifacts/publish/linux-x64
```

需要独立运行包时改用 `--self-contained true`，并确保可以还原对应平台的 .NET 运行时包。发布输出需要保留同目录的依赖和原生库；本项目尚未提供安装器、代码签名和公证。

## 项目结构

```text
src/YokoWave.Core       设备模型、传输抽象、输出约束、波形、会话与存储
src/YokoWave.App        共享 Avalonia 界面、视图模型、动态主题和绘图控件
src/YokoWave.Desktop    Windows / macOS / Linux 桌面入口
tests/YokoWave.Tests    核心与 Headless 界面集成测试
docs/architecture.md   设计边界与后续硬件接入方式
```

Git 仅管理新应用源码、测试和文档。`reference/`、APK、提取文件、反编译目录和本地分析目录均被忽略，不包含在提交中。
