# 实现文档

这里记录当前代码的结构和契约。运行、验证及交付流程见 [CONTRIBUTING.md](../CONTRIBUTING.md)；用户操作说明位于应用内「帮助」。

| 主题 | 内容 |
| --- | --- |
| [架构](architecture.md) | 项目依赖、职责、所有权与停止流程 |
| [源码地图](source-map.md) | 目录、关键类型及修改入口 |
| [节点管线](pipelines.md) | 图定义、注册、编译、数值求值、模板与后台会话 |
| [输入](inputs.md) | 音频、传感器、有效期、模拟来源和资源生命周期 |
| [蓝牙](bluetooth.md) | 能力与协议覆盖、GATT、确认、驱动与诊断 |
| [WebSocket](websocket.md) | 服务端／客户端结构、RPC、租约、时钟与 API |
| [WebSocket 事件](websocket-events.md) | 客户端声明、触发／数值事件、节点输入与调用示例 |
| [DG-LAB V4](dglab-v4.md) | App 配对、设备来源、被控图、中继与任务语义 |
| [恐龙游戏](dinosaur.md) | 静态浏览器模块、运行步骤及碰撞输出 |
| [波形](waveforms.md) | 统一格式、Schema、默认库与设备转换 |
| [存储](storage.md) | 文件所有权、写入顺序、损坏保护与重置 |
| [界面与本地化](localization.md) | JSON 词典、动态文本、系统主题、共享资源及帮助页 |
| [代码风格](code-style.md) | 放置、命名、语言特性、异步清理与 XAML |
| [测试](testing.md) | 测试领域、共享准备、覆盖率与验证边界 |

实现以源码及行为测试为准；新增功能应同时更新其所属文档和用户帮助，避免在多个位置维护完整操作手册。
