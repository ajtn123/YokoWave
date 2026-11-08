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
   设备卡片内提供连接与断开，分别使用系统强调色和警示色；选择实体设备可查看状态和固件信息。
3. 左侧将玩法与设备合并显示：添加「波形玩法」或「滑动玩法」后选中分组，将「未分配设备」加入当前玩法。每种类型可创建多个实例，创建后类型固定，右侧显示相应操作面板。设备在停止时也只归属一个玩法，需先从原分组移除才能改派；运行时禁止修改归属。蓝牙连接与玩法分配是独立操作。强度、上限、定时、波形与播放策略由玩法保存，点击「保存玩法」保存预设。
4. 设置该玩法的 A / B 强度及上限。两路均为 0% 时也可开始，运行中可调整强度与上限。单通道设备忽略 B 通道。定时开关、时长滑块和读数位于同一行，方向键每次调整 1 秒；开始后滑块变为逐渐缩短的进度条，到期仅停止该玩法。全局滑块最大时长在偏好设置中配置（1 至 120 分钟）；关闭定时则持续运行，直到停止。玩法运行时锁定自身定时设置。
   工作台的波形下拉框支持运行中切换：保留设备、强度、会话计时及剩余时长，新波形从起点播放。真实电击器拒绝电机波形并保留当前波形。
   波形玩法提供自定义播放列表，可添加、移除、上下排序，最多 100 个不同波形。顺序与随机模式从首项开始，仅播放列表中的波形，A/B 共用列表波形；运行时只能切换列表内波形。单曲循环与仅播放当前支持独立 A/B 波形。列表保存在所属玩法中，停止后可编辑。
5. 当前玩法的停止按钮只停止自身目标；意外断连或输出故障也只影响所属玩法，其他玩法继续运行。切换页面、查看设备或选择玩法不会停止已有会话。点击「全部停止」、按 `Esc`、切换设备来源或关闭应用会停止所有玩法；失联设备的物理输出无法通过软件继续确认。
6. 在「波形资料库」新建、复制、编辑、删除自定义波形，或通过 JSON 文件 / 文本导入导出。支持正弦、三角、脉冲、阶梯、自定义采样点及完整采样序列。默认波形可复制，删除按钮始终禁用。
7. 在「帮助」查看停止与重连行为、设备连接与扫描排查、通道强度、定时、玩法与目标分配、波形和本地记录说明。侧栏与窄窗口导航均提供帮助入口。

离线电刺激还支持 A/B 独立波形、顺序/单曲/随机播放、暂停与继续、随机/递增强度、按住临时增量、温柔模式，以及滑动控制的松手归零、轨迹循环和保持参数。轨迹可保存为自定义波形；兼容二代固件可读取版本并切换灯光。操作说明与平台边界见 [离线电刺激控制](docs/estim.md)。

默认库包含原应用接口返回的全部 22 个系统波形：12 个电击波形、10 个电机波形，共 1,379 帧。数据内置于应用，无需登录或联网；波形只显示名称、参数和预览。完整数据格式见 [默认波形说明](docs/default-waveforms.md)。

启动和重新连接不会恢复输出。波形预览展示配置曲线，「已发送强度」展示受限后的指令百分比。电击采样中的频率、脉宽和通道强度分别编码；100% 强度映射到文档中的 276 级，百分比不表示电流或其他校准物理量。接入边界与诊断方式见 [蓝牙说明](docs/bluetooth.md)。

## 外观与数据

在「偏好设置 / Settings → 界面语言 / Interface language」切换 `English` / `简体中文`，立即生效并自动保存。首次启动按系统界面语言选择：中文使用 `zh-CN`，其他语言使用 `en`。

翻译使用 [`en.json`](src/YokoWave.Core/Locales/en.json) 和 [`zh-CN.json`](src/YokoWave.Core/Locales/zh-CN.json)，不使用 `.resx`。页面、默认波形、状态、应用错误和文件选择器标题支持运行时更新；用户填写的名称保持原样。实现与维护方式见 [本地化说明](docs/localization.md)。

默认跟随系统明暗主题。FluentTheme 在平台提供支持时自动采用并更新系统强调色；平台未提供时使用 Avalonia 的默认强调色。按钮、导航、状态和 A 通道图表均使用主题资源；B 通道与错误状态使用独立的语义颜色。

所有应用颜色集中定义在 [`src/YokoWave.App/Styles/Palette.axaml`](src/YokoWave.App/Styles/Palette.axaml)。视图和绘图控件通过动态资源获取颜色，运行时切换主题无需重启。系统主题行为参考 [Avalonia Theme variants](https://docs.avaloniaui.net/docs/how-to/theme-switching-how-to) 和 [FluentTheme 文档](https://docs.avaloniaui.net/docs/styling/themes)。

字号、间距、内边距、圆角与布局尺寸集中在 [`Styles/Metrics.axaml`](src/YokoWave.App/Styles/Metrics.axaml)。页面通过共享样式类组合标题、表单、卡片及操作行，修改通用外观无需逐页调整数值。样式分工见 [架构说明](docs/architecture.md)。

应用图标的矢量源为 `src/YokoWave.App/Assets/YokoWave.svg`；同目录的多尺寸 `YokoWave.ico` 同时用于桌面窗口和 Windows EXE，普通构建即可嵌入图标。

数据目录默认为 `LocalApplicationData/YokoWave/`，准确路径显示在设置页，也可用 `YOKOWAVE_DATA_DIR` 指定。`workspace.json` 只存版本与基础累计统计，全局设置存入 `settings.json`，自定义波形存入 `presets.json`，玩法参数、目标标识和播放列表顺序存入 `modes.json`；这些文件均为紧凑 JSON。玩法预设会恢复，但不会自动连接设备或启动输出。会话逐条写入 `sessions.jsonl`，每个文件最大 128 KiB，仅保留一个滚动旧文件 `sessions.1.jsonl`。历史列表显示两份日志中仍保留的记录，累计统计不会随滚动减少。开发阶段不迁移旧格式，读取到 Schema 1–3 时重置应用数据文件，不删除其他文件。

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
