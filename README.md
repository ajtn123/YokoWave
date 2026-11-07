# YokoWave · 波枢

面向 YOKONEX 使用场景重新设计的跨平台设备工作台。使用 **.NET 10 + Avalonia 12**，首版聚焦多设备管理、双通道控制和波形管理。

支持本地模拟及 **Windows 原生 BLE 电击器连接**。识别 `YYC-DJ`、`YCY-DJ`、`YYC-DJV2` 和 `YYC-DJ-V2`，按广播名选择对应线协议。已使用 `YCY-DJ` 实机验证扫描、连接、双通道关闭确认、电量与电极状态读取，并获用户实机可用反馈；强度标定和长时间运行尚未验证。未实现远程控制或原应用后端。

## 运行

安装 .NET 10 SDK，在项目根目录执行：

```sh
dotnet restore YokoWave.slnx
dotnet run --project src/YokoWave.Desktop
```

Windows、macOS 和 Linux 共用同一个桌面入口。真实 BLE 后端目前支持 Windows 10 2004 及以上；macOS、Linux 可运行模拟模式，尚未接入原生蓝牙、也未进行实机验证。Android、iOS 不在本次交付范围内。

## 使用

1. 在「偏好设置 → 设备来源」选择「本地模拟」或「YOKONEX 蓝牙」，点击保存。切换来源会停止并断开现有设备。首次启动使用模拟模式。
2. 点击「发现设备」，再连接一台或多台设备。实机需要开机并断开其他控制应用；连接后先关闭双通道，并等待设备确认，超时则断开。
   卡片内的「参与联动」用于选择联动会话的目标设备，不会自动连接；独立控制模式只使用当前设备。连接按钮使用系统强调色，断开按钮使用警示色。
3. 选择当前设备，设置输出上限。A / B 通道均为 0% 时也可开始会话，并在运行中调节强度。单通道设备始终忽略 B 通道。
4. 定时开关、时长滑块和时间读数位于同一行。开启定时后拖动滑块选择时长，按方向键可精确调整 1 秒；开始后滑块变为逐渐缩短的剩余进度条，到期自动停止。滑块上限在「偏好设置 → 定时时长上限」配置（1 至 120 分钟）。关闭定时则持续运行，直到主动停止。运行后定时设置锁定。开启「多设备联动」后，会话控制已连接且勾选的设备；每台设备保留各自的输出上限。
   工作台的波形下拉框支持运行中切换：保留设备、强度、会话计时及剩余时长，新波形从起点播放。真实电击器拒绝电机波形并保留当前波形。
5. 点击「全部停止」或按 `Esc` 发送关闭指令。切换页面、切换设备、修改连接、定时结束和关闭应用也会停止会话。意外断连会停止其他联动设备；断连设备的物理输出无法通过软件继续确认。
6. 在「波形资料库」新建、复制、编辑、删除自定义波形，或通过 JSON 文件 / 文本导入导出。支持正弦、三角、脉冲、阶梯、自定义采样点及完整采样序列。默认波形可复制，删除按钮始终禁用。
7. 在「帮助」查看停止与重连行为、设备连接与扫描排查、通道强度、定时、联动、波形和本地记录说明。侧栏与窄窗口导航均提供帮助入口。

默认库包含原应用接口返回的全部 22 个系统波形：12 个电击波形、10 个电机波形，共 1,379 帧。数据内置于应用，无需登录或联网；波形只显示名称、参数和预览。完整数据格式见 [默认波形说明](docs/default-waveforms.md)。

启动和重新连接不会恢复输出。波形预览展示配置曲线，「已发送强度」展示受限后的指令百分比。电击采样中的频率、脉宽和通道强度分别编码；100% 强度映射到文档中的 276 级，百分比不表示电流或其他校准物理量。接入边界与诊断方式见 [蓝牙说明](docs/bluetooth.md)。

## 外观与数据

在「偏好设置 / Settings → 界面语言 / Interface language」切换 `English` / `简体中文`，立即生效并自动保存。首次启动按系统界面语言选择：中文使用 `zh-CN`，其他语言使用 `en`。

翻译使用 [`en.json`](src/YokoWave.Core/Locales/en.json) 和 [`zh-CN.json`](src/YokoWave.Core/Locales/zh-CN.json)，不使用 `.resx`。页面、默认波形、状态、应用错误和文件选择器标题支持运行时更新；用户填写的名称保持原样。实现与维护方式见 [本地化说明](docs/localization.md)。

默认跟随系统明暗主题。FluentTheme 在平台提供支持时自动采用并更新系统强调色；平台未提供时使用 Avalonia 的默认强调色。按钮、导航、状态和 A 通道图表均使用主题资源；B 通道与错误状态使用独立的语义颜色。

所有应用颜色集中定义在 [`src/YokoWave.App/Styles/Palette.axaml`](src/YokoWave.App/Styles/Palette.axaml)。视图和绘图控件通过动态资源获取颜色，运行时切换主题无需重启。系统主题行为参考 [Avalonia Theme variants](https://docs.avaloniaui.net/docs/how-to/theme-switching-how-to) 和 [FluentTheme 文档](https://docs.avaloniaui.net/docs/styling/themes)。

字号、间距、内边距、圆角与布局尺寸集中在 [`Styles/Metrics.axaml`](src/YokoWave.App/Styles/Metrics.axaml)。页面通过共享样式类组合标题、表单、卡片及操作行，修改通用外观无需逐页调整数值。样式分工见 [架构说明](docs/architecture.md)。

应用图标的矢量源为 `src/YokoWave.App/Assets/YokoWave.svg`；同目录的多尺寸 `YokoWave.ico` 同时用于桌面窗口和 Windows EXE，普通构建即可嵌入图标。

数据目录默认为 `LocalApplicationData/YokoWave/`，准确路径显示在设置页，也可用 `YOKOWAVE_DATA_DIR` 指定。`workspace.json` 只存累计次数与时长等基础统计，设置存入 `settings.json`，自定义波形存入 `presets.json`；这些存储文件均为紧凑 JSON。会话逐条写入 `sessions.jsonl`，每个文件最大 128 KiB，仅保留一个滚动旧文件 `sessions.1.jsonl`。历史列表显示两份日志中仍保留的记录，累计统计不会随滚动减少。开发阶段不迁移旧格式，启动读取到旧版数据时会重置。

## 开发验证

开发阶段只构建和测试，无需每次执行 `publish`。

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

## 按需发布

交付时可按目标系统发布需要安装 .NET 运行时的应用：

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
  Views/Pages          设备工作台、波形资料库、会话记录、偏好设置
  Views/Components     波形编辑器、JSON 导入导出
src/YokoWave.Desktop    Windows / macOS / Linux 桌面入口
tests/YokoWave.Tests    核心与 Headless 界面集成测试
docs/architecture.md   设计边界与后续硬件接入方式
```

Git 仅管理新应用源码、测试和文档。`reference/`、APK、提取文件、反编译目录和本地分析目录均被忽略，不包含在提交中。
