<p align="center">
  <img src="src/YokoWave.App/Assets/YokoWave.svg" width="96" height="96" alt="YokoWave logo" />
</p>

# YokoWave · 波枢

[![Test](https://github.com/ajtn123/YokoWave/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/ajtn123/YokoWave/actions/workflows/build.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](global.json)
[![Avalonia 12](https://img.shields.io/badge/Avalonia-12-8B44AC)](src/YokoWave.App/YokoWave.App.csproj)
![Status: development](https://img.shields.io/badge/status-development-yellow)

基于 .NET 与 Avalonia 的跨平台设备控制应用，支持 YOKONEX 和 DG-LAB 设备。用节点图组合波形、触控、音频、传感器与设备输出；同一模板可创建多个实例，分别控制不同设备。

- **设备管理**：扫描、连接、自动重连、硬件设置及按型号提供的协议操作。
- **节点玩法**：波形、滑动、声控、禁锢、运动、睡眠剥夺、泵和原生程序模板；支持自定义图与子图模板。
- **波形管理**：24 条内置波形、自定义播放列表、采样编辑和统一格式导入导出。
- **远程设备**：WebSocket GATT 透传、高层控制与 [DG-LAB V4](docs/dglab-v4.md) App 配对；支持被控节点及内置 V4 中继，可与本地设备同时使用。
- **浏览器模块**：[恐龙游戏](docs/dinosaur.md)，碰撞时通过现有 WebSocket 服务端输出短脉冲。
- **界面**：English / 简体中文，跟随系统字体、明暗主题及可用的强调色。

## 当前支持

| 平台 | 本地模拟 / WebSocket | 原生蓝牙 | 音频 |
| --- | --- | --- | --- |
| Windows 10 2004+ | 支持 | 支持 | 媒体文件、麦克风、系统播放声音 |
| macOS / Linux | 支持 | 尚未实现 | 媒体文件、麦克风后端已实现，待平台实测 |
| Android / iOS | 尚未提供入口 | — | — |

已接入 YOKONEX 电击器 V1/V2、震动器、灌肠机，郊狼 V2/V3、负鼠、爪印 V1.1 和灵猫。协议替身测试覆盖全部已接入驱动；实机验证范围与能力限制见[蓝牙实现](docs/bluetooth.md)。跨设备使用同一波形不表示物理输出等价。

## 快速开始

安装 **.NET 10 SDK** 后执行：

```sh
git clone https://github.com/ajtn123/YokoWave.git
cd YokoWave
dotnet restore YokoWave.slnx
dotnet run --project src/YokoWave.Desktop
```

1. 首次启动使用本地模拟。使用实机时，在「偏好设置 → 设备来源」选择蓝牙并保存。
2. 在「设备工作台」发现并连接设备。
3. 在「节点玩法」选择模板、添加玩法，将设备加入分组，检查输出节点的设备和通道绑定。
4. 调整数值输入及各输出节点上限后点击开始。零强度也可开始；「全部停止」或 `Esc` 停止所有玩法。

完整操作、快捷键、远程连接与排查方法位于应用内「帮助」。保存的玩法不会自动运行；重新连接从关闭输出开始。

## 文档入口

| 读者 / 用途 | 入口 |
| --- | --- |
| 使用应用 | 应用内「帮助」用户手册 |
| 开发、验证与交付 | [CONTRIBUTING.md](CONTRIBUTING.md) |
| 模块、源码位置与实现细节 | [docs 索引](docs/README.md) |
| 自动化代理 | [AGENTS.md](AGENTS.md) |

项目处于开发阶段，旧开发数据格式会重置。用户数据路径可在设置页查看；存储和损坏保护规则见[本地存储](docs/storage.md)。
