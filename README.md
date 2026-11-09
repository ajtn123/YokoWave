# YokoWave · 波枢

[节点玩法](docs/pipelines.md)提供数值端口、波形处理、多设备输出、泵参数、后台计时和可保存的节点画布。所有玩法均为可编辑节点图模板，共用同一后台运行时、设备独占及停止入口。

面向 YOKONEX 使用场景重新设计的跨平台设备工作台。使用 **.NET 10 + Avalonia 12**，首版聚焦多设备管理、双通道控制和波形管理。

支持本地模拟及 **Windows 原生 BLE 连接**：YOKONEX 电击器、震动器、灌肠机，郊狼 V2／V3、负鼠，以及爪印 V1.1／灵猫输入设备。各型号使用独立协议驱动和特征白名单，设备能力限制玩法与波形选择。支持矩阵和限制见 [蓝牙说明](docs/bluetooth.md)。

## 运行

安装 .NET 10 SDK，在项目根目录执行：

```sh
dotnet restore YokoWave.slnx
dotnet run --project src/YokoWave.Desktop
```

Windows、macOS 和 Linux 共用同一个桌面入口。真实 BLE 后端目前支持 Windows 10 2004 及以上；macOS、Linux 可运行模拟模式，尚未接入原生蓝牙、也未进行实机验证。暂不支持 Android 与 iOS。

支持通过 WebSocket 连接另一台 YokoWave 共享的蓝牙设备，Windows、macOS 和 Linux 控制端可与本地设备同时使用。服务端作为独立玩法，提供 `gatt.*` 原始读写／通知透传及 `control.*` 高层播放、强度、程序、泵、状态和设备命令；租约类型统一为 `gatt` / `control`。设置、接口和使用示例见 [WebSocket 说明](docs/websocket.md)。运行需要 .NET 10 及 ASP.NET Core 10 运行时，SDK 已包含。

## 使用

1. 在「偏好设置 → 设备来源」选择「本地模拟」或「蓝牙」，点击保存。切换来源会停止并断开现有设备。首次启动使用模拟模式。
2. 在「设备工作台」点击「发现设备」，再连接一台或多台设备。实机需要开机并断开其他控制应用；连接时按协议关闭输出并初始化状态，初始化失败则释放连接。不同协议的确认能力见蓝牙说明。
   设备页管理连接、断开和 WebSocket 来源，连接按钮使用系统强调色，断开按钮使用警示色。自动连接默认关闭；开启后重连手动连接过的设备，手动断开会移除该设备的自动连接记录。重连保持零输出，不恢复玩法。
3. 在「节点玩法」从波形、滑动、声控、禁锢、运动、睡眠剥夺、泵控制、设备原生程序或 WebSocket 服务端模板创建实例。左侧只显示已连接设备，按玩法分组；将兼容的「未分配设备」加入当前玩法。模板均为可编辑节点图，同一模板可创建多个实例。设备在停止时也只归属一个玩法，需先从原分组移除才能改派；运行时禁止修改归属。断连隐藏卡片但保留图中的设备引用。右键玩法列表可复制、保存、删除或另存为模板。
4. 数值节点提供强度，每个输出／控制节点独立保存上限，通道分别限幅后合并发送。两路均为 0% 时也可开始，运行中可调整强度与上限。单通道设备忽略 B 通道。定时开关、时长滑块和读数位于同一行，方向键每次调整 1 秒；开始后滑块变为逐渐缩短的进度条，到期仅停止该玩法。滑块最大时长在偏好设置中配置（1 至 120 分钟）；关闭定时则持续运行，直到停止。玩法运行时锁定自身定时设置。
   选中波形或播放列表节点可运行中切换波形，保留设备、强度及会话计时，新波形从起点播放。所有电击和震动型号共用统一波形，播放时按协议转换。
   波形玩法提供自定义播放列表，可添加、移除、上下排序，最多 100 个不同波形。顺序与随机模式从首项开始，仅播放列表中的波形，A/B 共用列表波形；运行时只能切换列表内波形。单曲循环与仅播放当前支持独立 A/B 波形。列表保存在所属玩法中，停止后可编辑。
5. 当前玩法的停止按钮只停止自身目标；意外断连或输出故障也只影响所属玩法，其他玩法继续运行。切换页面、查看设备或选择玩法不会停止已有会话。点击「全部停止」、按 `Esc`、切换设备来源或关闭应用会停止所有玩法；失联设备的物理输出无法通过软件继续确认。
6. 在「波形资料库」新建、复制、编辑、删除自定义波形，或通过 JSON 文件 / 文本导入导出。支持正弦、三角、脉冲、阶梯、自定义采样点及完整采样序列。默认波形可复制，删除按钮始终禁用。
7. 在「帮助」查看停止与重连行为、设备连接与扫描排查、通道强度、定时、玩法与目标分配、波形和本地记录说明。侧栏与窄窗口导航均提供帮助入口。

离线电刺激还支持 A/B 独立波形、顺序/单曲/随机播放、暂停与继续、随机/递增强度、按住临时增量、温柔模式，以及滑动控制的松手归零、轨迹循环和保持参数。轨迹可保存为自定义波形；兼容二代固件可读取版本并切换灯光。操作说明与平台边界见 [离线电刺激控制](docs/estim.md)。

声控支持本地 WAV／MP3／FLAC、默认麦克风及 Windows 系统播放声音，提供音量／节奏映射、灵敏度和环境校准。禁锢按偏离基准角度的幅度渐增输出；运动模式在持续静止后输出，可设置动作目标；睡眠剥夺提供固定、随机和活动重置间隔。数据缺失或过期时感应输出归零，随机波形仅使用本地列表。各功能、参数和实机验证边界见 [声控与感应玩法](docs/live-modes.md)。

默认库保留原应用接口的全部 22 个系统波形（1,379 帧），另加入郊狼公开协议的呼吸／潮汐示例（140 个 25 ms 样本），共 24 个。24 条均已转换为统一格式，可在所有已接入的电击／震动型号播放；有符号通道保留在文件中，单向设备播放时使用绝对值。设备不支持的频率、方向和额外通道按能力降级，实际效果不保证等价。所有数据内置，无需登录或联网；波形只显示名称、参数和预览。完整数据格式见 [默认波形说明](docs/default-waveforms.md)。

启动和重新连接不会恢复输出。波形预览展示配置曲线，「已发送强度」展示受限后的指令百分比。强度按各型号量程编码，百分比不表示电流或其他校准物理量。三通道震动器提供独立 C 通道数值输入，可连接任意波形或音频特征。泵控制使用方向、抽水泵开关与两路独立时长；持续模式续发一秒指令，定时模式发送剩余秒数，暂停或停止关闭两个泵。接入边界与诊断方式见 [蓝牙说明](docs/bluetooth.md)。

设备原生程序玩法支持两代电击器固定程序、二代电击器频率序列、马达固定程序和郊狼 V2 原始 X/Y/Z 参数。设备页右侧详情卡片按型号显示硬件选项，包括辅助马达、郊狼 V3 相对强度、通道上限和 DG 平衡；按设备保存。输出节点通道使用按设备生成的下拉选项。实体设备的协议面板提供计步、传感器流、爪印全部触发器／肩灯／校准、灵猫方向及归零、负鼠屏幕同步等操作；能力、参数与 MTU 限制见 [协议覆盖表](docs/bluetooth.md#设备与协议覆盖)。新增功能已通过替身测试，仍待实机验证。

## 外观与数据

在「偏好设置 / Settings → 界面语言 / Interface language」切换 `English` / `简体中文`，立即生效并自动保存。首次启动按系统界面语言选择：中文使用 `zh-CN`，其他语言使用 `en`。

翻译使用 [`en.json`](src/YokoWave.Core/Locales/en.json) 和 [`zh-CN.json`](src/YokoWave.Core/Locales/zh-CN.json)，不使用 `.resx`。页面、默认波形、状态、应用错误和文件选择器标题支持运行时更新；用户填写的名称保持原样。实现与维护方式见 [本地化说明](docs/localization.md)。

默认跟随系统明暗主题。FluentTheme 在平台提供支持时自动采用并更新系统强调色；平台未提供时使用 Avalonia 的默认强调色。按钮、导航、状态和 A 通道图表均使用主题资源；B 通道与错误状态使用独立的语义颜色。

所有应用颜色集中定义在 [`src/YokoWave.App/Styles/Palette.axaml`](src/YokoWave.App/Styles/Palette.axaml)。视图和绘图控件通过动态资源获取颜色，运行时切换主题无需重启。系统主题行为参考 [Avalonia Theme variants](https://docs.avaloniaui.net/docs/how-to/theme-switching-how-to) 和 [FluentTheme 文档](https://docs.avaloniaui.net/docs/styling/themes)。

字号、间距、内边距、圆角与布局尺寸集中在 [`Styles/Metrics.axaml`](src/YokoWave.App/Styles/Metrics.axaml)。页面通过共享样式类组合标题、表单、卡片及操作行，修改通用外观无需逐页调整数值。样式分工见 [架构说明](docs/architecture.md)。

应用图标的矢量源为 `src/YokoWave.App/Assets/YokoWave.svg`；同目录的多尺寸 `YokoWave.ico` 同时用于桌面窗口和 Windows EXE，普通构建即可嵌入图标。

数据目录默认为 `LocalApplicationData/YokoWave/`，准确路径显示在设置页，也可用 `YOKOWAVE_DATA_DIR` 指定。`workspace.json` 只存版本与基础累计统计，全局设置、自动连接设备标识与设备硬件选项存入 `settings.json`，自定义波形存入 `waveforms.json`，图实例、参数、目标标识和播放列表顺序存入 `pipelines.json`，用户模板存入 `pipeline-presets.json`；这些文件均为紧凑 JSON。图会恢复，连接由设备页的自动连接开关控制；不会自动打开音频输入或启动玩法。会话逐条写入 `sessions.jsonl`，每个文件最大 128 KiB，仅保留一个滚动旧文件 `sessions.1.jsonl`。历史列表显示两份日志中仍保留的记录，累计统计不会随滚动减少。设置页的「清除本地数据」经确认后先停止玩法、断开设备，再清除应用数据并重建默认状态，保留内置波形及目录中的其他文件。开发阶段不迁移旧数据格式，Schema 不匹配时重置应用数据文件。

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
src/YokoWave.Pipelines  图编译、数值处理、模板与后台会话
src/YokoWave.Remote     WebSocket 服务端、GATT／control 客户端、RPC 与独占租约
src/YokoWave.App        共享 Avalonia 界面、视图模型、动态主题和绘图控件
  Views/Pages          设备工作台、波形资料库、会话记录、偏好设置
  Views/Components     波形编辑器、JSON 导入导出
src/YokoWave.Desktop    Windows / macOS / Linux 桌面入口
tests/YokoWave.Tests    核心与 Headless 界面集成测试
docs/architecture.md   设计边界与后续硬件接入方式
```

Git 仅管理新应用源码、测试和文档。`reference/`、APK、提取文件、反编译目录和本地分析目录均被忽略，不包含在提交中。
