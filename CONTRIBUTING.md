# 开发指南

源码职责见[源码地图](docs/source-map.md)，设计边界见[架构](docs/architecture.md)。自动化代理另须遵守 [AGENTS.md](AGENTS.md)。

## 环境与运行

- .NET 10 SDK；`global.json` 允许同一主版本内滚动到较新的 feature band。
- PowerShell 7 用于仓库检查脚本；直接使用 dotnet 命令不需要 PowerShell。
- Windows 原生 BLE 目标最低为 Windows 10 2004。macOS / Linux 构建通用桌面目标，可使用模拟与 WebSocket 设备；音频边界见[输入实现](docs/inputs.md#平台后端)。

```sh
git clone https://github.com/ajtn123/YokoWave.git
cd YokoWave
dotnet restore YokoWave.slnx
dotnet run --project src/YokoWave.Desktop
```

首次启动默认模拟，不会自动运行保存的图。开发时可隔离数据，避免改动日常工作区：

```powershell
$env:YOKOWAVE_DATA_DIR = Join-Path $PWD 'artifacts/dev-data'
dotnet run --project src/YokoWave.Desktop
```

`YOKOWAVE_DATA_DIR` 由应用读取；文件格式见[存储](docs/storage.md)。在 Windows 上强制构建通用入口可传 `-p:YokoWaveWindowsBluetooth=false`，该开关不会增加其他平台的原生蓝牙能力。

## 验证

```powershell
./scripts/verify.ps1
# 已缓存依赖且离线：
./scripts/verify.ps1 -Offline
```

该脚本依次还原、检查风格、构建 Release 并运行 xUnit，不执行 publish。日常修改也可分别执行：

```sh
dotnet build YokoWave.slnx -c Release
dotnet run --project tests/YokoWave.Tests -c Release
```

`scripts/check-style.ps1 -Fix` 修复支持自动修复的规则，之后审阅差异。构建启用代码风格分析并将 warning 视为错误；不要用批量压制绕过规则。手工审查成员位置、职责、命名及异常边界，具体见[代码风格](docs/code-style.md)。

按领域筛选测试、复用替身、截图及收集覆盖率见[测试说明](docs/testing.md)。`global.json` 选择 Microsoft Testing Platform；覆盖率脚本单独调用 VSTest，不将其参数混入 `dotnet test`。

原生启动检查不会代替 Headless 回归，可按需使用隔离数据：

```sh
dotnet run --project src/YokoWave.Desktop -- --smoke-test
```

蓝牙和音频诊断入口见[蓝牙诊断](docs/bluetooth.md#诊断入口)。这些入口操作真实设备／输入，不属于常规构建步骤。

## 修改功能

| 修改范围 | 入口与需要保持的边界 |
| --- | --- |
| 节点／玩法 | 在 Pipelines 定义配置、注册、处理器及必要的资源参与者；在 App 提供编辑 UI；模板只生成普通图。见[管线](docs/pipelines.md)。 |
| 设备／协议 | 集中定义能力、编码／解析和驱动，由工厂创建；验证发现、确认、失败释放及停止。见[蓝牙](docs/bluetooth.md)。 |
| 音频／传感器 | 平台层采集，核心只消费有时间与来源的特征；缺失归零，清理可等待。见[输入](docs/inputs.md)。 |
| 波形 | 修改统一资产和 Schema，不在资产中保存设备包；验证默认库和各驱动转换。见[波形](docs/waveforms.md)。 |
| 远程 | 同时更新 RPC 契约、服务端、客户端、节点和回环测试；只使用 `gatt` / `control` 租约。见 [WebSocket](docs/websocket.md)。 |
| 界面／文案 | 复用组件、语义样式和资源；同步 en / zh-CN，保留语言切换时的状态。见[界面与本地化](docs/localization.md)。 |
| 本地文件 | 保持文件所有权、紧凑格式、损坏保护和关闭前写入屏障。开发阶段不增加数据迁移。见[存储](docs/storage.md)。 |

测试验证可观察行为、边界和资源所有权。优先复用 `tests/YokoWave.Tests/Support`，用可等待信号、可控输入和有上限的状态等待；不为每个私有步骤增加镜像测试。硬件与网络新增路径明确区分替身验证、回环验证和实机验证。

## 提交与文档

提交应聚焦一个可审阅的结果，包含必要的调用、绑定、测试和文档更新。修改前检查工作区，不覆盖其他改动。重命名全链路更新，不保留无用途别名。PR 描述写明问题、最终行为、验证及实际限制；不记录探索过程或废弃方案。

文档分工：

- `README.md`：项目介绍、状态、快速开始与入口。
- `AGENTS.md`：代理必须遵守的工作和设计约束。
- `CONTRIBUTING.md`：开发、验证、提交和交付流程。
- `docs/`：当前实现、源码位置、数据与协议契约。
- 应用内帮助：操作步骤、限制、快捷键和故障排查，中英文同步。

不要提交 APK、反编译源码、原始参考资料、凭据、研究脚本或构建产物；本地分析留在被忽略目录。代理提交使用自身身份，不修改用户全局 Git 配置。

## 按需发布

开发验证不需要 publish。需要交付时，从桌面项目发布，保留输出目录中的依赖和原生库：

```sh
dotnet publish src/YokoWave.Desktop -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64
dotnet publish src/YokoWave.Desktop -c Release -r osx-arm64 --self-contained false -o artifacts/publish/osx-arm64
dotnet publish src/YokoWave.Desktop -c Release -r linux-x64 --self-contained false -o artifacts/publish/linux-x64
```

上述包需要 .NET 10 与 ASP.NET Core 10 运行时，SDK 已包含两者。自包含包使用 `--self-contained true` 并还原对应运行时。当前未提供安装器、代码签名或 macOS 公证。
