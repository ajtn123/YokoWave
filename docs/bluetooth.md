# 蓝牙设备接入

目前使用 Windows BLE / GATT 原生 API，最低目标为 Windows 10 2004。平台适配通过 `IBluetoothAdapter` / `IBluetoothConnection` 隔离；Core 不引用 Windows SDK。macOS、Linux 仍使用模拟模式，选择蓝牙模式会明确报告后端尚未实现。

## 设备与协议

| 广播名／服务 | 设备 | 已实现能力 |
| --- | --- | --- |
| `YYC-DJ` | 旧版 V1 | 单通道配置；参数相同时合并为 AB 控制 |
| `YCY-DJ`、`YYC-DJV2`、`YYC-DJ-V2` | 新版 V2 | 实时模式，一个包包含 A / B 配置 |
| `D-LAB ESTIM01` | 郊狼 V2 | AB 强度、X/Y/Z 波形、电量与强度读取 |
| `47L121…` | 郊狼 V3 | AB 强度、四帧波形、硬件上限、平衡参数、电量、B1 回报 |
| `47L127…` | 负鼠 V1 | AB 震动强度、波形、电量、按键回报 |
| `47L1203…` | 爪印 V1.1 | 按键、加速度、XYZ、外部电压原始读数及事件回报 |
| `47L124…` | 灵猫 V1 | 有符号 kPa 压力、压力归零、电量 |
| 服务 `FF40` | YOKONEX 震动器 | 型号与实际通道探测、最多 ABC 三路强度、电量 |
| 服务 `FFB0` | YOKONEX 灌肠机 | AES 指令、蠕动泵正反转、抽水泵开关、原始压力、电量 |

新增型号均处于**协议与 GATT 替身测试阶段，未经本项目实机验证**。用户已有郊狼 V3，按用户安排稍后做实机测试。爪印 V1.0 `47L120100` 显示需升级且禁用连接；未知 DG-LAB 前缀不猜测协议。负鼠／灵猫若不能被扫描，厂商文档要求连按五次电源键进入可发现状态。

广播名和产品代际不应混为一谈。YOKONEX 电击器两种线协议共用 FF30／FF31／FF32；震动器使用 FF40／FF41／FF42，灌肠机使用 FFB0／FFB1／FFB2。DG-LAB V3、负鼠、爪印和灵猫使用 180C／150A／150B，电量在 180A／1500（爪印除外）。郊狼 V2 使用 `955Axxxx-0FE2-F5AA-A094-84B8D4F3E8AD` UUID，180B 控制服务内的 1504 强度、1506 A 波形、1505 B 波形，180A／1500 电量。V2 官方表的符号名称与通道描述相反，本实现遵循通道描述，实际 A/B 对应仍需 V2 实机确认。

连接时核对每个型号的服务、特征与权限，订阅所需通知，保留所有服务句柄；可选电量服务缺失不影响控制连接。优先使用特征支持的无响应写入，仅支持响应写入时使用对应模式。

协议依据：[厂商开源蓝牙协议目录](https://github.com/YCY-YOKONEX/YCY-YOKONEX-OpenSource/tree/main/Bluetooth)、[V1 文档](https://github.com/YCY-YOKONEX/YCY-YOKONEX-OpenSource/blob/main/Bluetooth/%E7%94%B5%E5%87%BB%E5%99%A8%E4%B8%80%E4%BB%A3.pdf)、[V2 文档](https://github.com/YCY-YOKONEX/YCY-YOKONEX-OpenSource/blob/main/Bluetooth/%E7%94%B5%E5%87%BB%E5%99%A8%E4%BA%8C%E4%BB%A3.pdf)。Windows API 使用方式见 [Microsoft GATT Client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)。实现是独立编写的 C# 编码器；参考 APK、提取源码和原始 PDF 保留在 Git 忽略的研究目录。

## YOKONEX 电击器输出与状态

- 连接先订阅通知，发送关闭双通道指令，再查询电量及 A / B 状态。只有设备确认两路均关闭后，连接才算完成；3 秒内未确认则再次尝试关闭并释放连接。
- UI 的通道强度是 0–100%，经过所属玩法上限限制后映射至协议 0–276 级，使用大端强度字段及逐字节累加校验。设备不保存独立配置。百分比不代表实际电流。
- 默认电击波形的 PL / TL 分别进入频率及脉宽字段，强度独立设置；不能把脉宽再次乘进强度。生成器波形通过幅度包络调节强度，脉宽固定为 100，频率来自编辑器。
- A / B 错相分别采样。旧版可分别发送两路配置，新版使用双通道实时包。电机采样被拒绝，不会发送给电击器。
- A/B 可选择独立波形。温柔模式将协议强度满量程限制为 180，普通模式保持 276；零强度仍发送关闭指令。自动增量、临时增量和滑动控制的输出仍通过同一上限入口。
- 二代设备可主动读取硬件/固件及灯光状态；校验、长度与状态范围均需有效。只有版本确认后才能切换灯光，且必须等待状态确认。此功能不访问网络，未对无版本回复的设备开放。
- 电量、电极接触与输出开启状态来自通知，读取前显示未知；无效长度、范围或校验的通知被忽略。写入采用无响应模式，成功表示系统接受写入，不等同于测得物理输出。
- 感应玩法使用 `35 14 01 4A` 开启角度上报，停止时发送 `35 14 00 49`。`35 71 06` 包中的有符号加速度数据换算为倾斜量，`35 71 05` 计步通知作为可选活动读数；长度、校验、零向量及新鲜度均需有效。没有新鲜传感器数据时归零，不根据经过时间伪造读数。该输入链路已用 GATT 替身验证，尚待对应实机确认。
- 控制命令串行执行，同一设备同时只允许一个运行中的玩法写入；暂停期间保留占用。停止或计时结束只关闭该玩法目标，页面、查看设备与选中玩法切换不会中断其他玩法。全部停止、Esc、切换设备来源及关闭应用会停止所有玩法。设备拒绝指令后禁止继续发送非零输出，但仍允许关闭指令；同一故障只上报一次。
- 意外断连会释放连接并停止所属玩法的其他目标，不影响独立运行的其他玩法，不会自动恢复输出。已经失联的设备无法继续接收关闭指令，也无法确认其物理输出。
- Windows 写入使用 [WriteValueWithResultAsync](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic.writevaluewithresultasync)，记录系统状态与 ATT 协议错误；按协商 MTU 检查包长，并串行执行原生写入和资源释放。异常保留内部堆栈，参数异常会额外输出 HRESULT、包长、权限及 MTU。
- 无设备归属或任一目标未连接时禁用开始按钮；两路 0% 仍可开始。停止未确认会保留玩法占用；用户主动断开时，即使停止写入失败，也继续释放旧 GATT 连接并报告未确认状态，以便重新连接。重连不会恢复旧会话。

## 郊狼、负鼠与输入设备

郊狼 V3 的 20 字节 B0 每 100 ms 下发，包含每路四个 25 ms 周期／幅度样本。强度按 0–200 编码，原生波形的幅度不再次乘进强度。普通播放使用序号 0 和绝对强度，玩法持续控制设备；硬件按键修改的强度会在下一次下发时被玩法值覆盖。连接与停止时先写 BF，将两路硬件上限设为零，再发送带 1–15 循环序号的归零 B0；只有收到相同序号且两路均为零的 B1 才完成该次停止。运行时 BF 使用玩法上限和平衡参数，仅在值改变时写入，避免每帧重复写入断电保存参数。平衡参数由玩法保存，停止后才能编辑。

郊狼 V2 的强度以 22 位 AB 数值封装为小端三字节，量程 0–2047；周期转换为 X／Y，幅度映射到推荐的 Z≤20。归零后读取强度或等待强度通知确认。V2／V3 可以使用 `DgLab` 原生样本及生成波形，拒绝 YOKONEX `Electrical` 原始编码；反向同样拒绝。兼容检查在开始申请设备前、动态切换和传输层分别执行。播放列表按每个目标设备检查，可混合各目标均支持的生成波形与原生波形；已有不兼容列表保留内容并禁用启动，不静默删除条目。

负鼠使用独立的 B3 强度与 B0 震动幅度，不使用郊狼电击频率字段。初始化开启按键通知，连接／停止写入零强度与零幅度。该协议没有保证“已为零时也回复”的停止确认，写入成功只代表系统接受指令。B2 屏幕同步的公开长度存在 24／20 字节冲突，本实现暂不发送 B2，也不截断指令。

爪印 V1.1 连接后立即发送 17 字节 50 指令，开启物理数据上报，读取 51 电量及 D0 按键／运动／外部电压数据；可接收 5A／5B／5C 事件。灵猫通过 50 开启压力数据，D0 的 8–9 字节按有符号小端数除以 100 读取 kPa，支持公开的 13 字节 66 压力归零指令。两类设备均按连接分别维护读数，界面标注最近更新时间，断开时关闭流。未知单位保留原始值。尚未实现传感器到玩法的联动规则、爪印触发器编辑或 F1 阈值配置。

## YOKONEX 震动器与灌肠机

震动器通过 35 10 查询型号与各路模式数量，只启用实际存在的电机，最多三路。35 12 的每路范围 0–20；原生电机值按玩法 ABC 强度比例缩放，C 使用 A 采样，声控时跟随左输入。公开协议不支持负值，因此库内反向波形被筛除。初始化及停止发送全零；型号回复可以确认协议与通道，不能作为物理停止确认。固定内置模式选择尚未提供，当前使用实时强度控制。

灌肠机使用厂商公开的固定互操作密钥和 AES-128 ECB 加密 16 字节指令，未用字节随机填充。泵控制玩法独立提供蠕动泵方向和抽水泵开关，没有虚构流量百分比。运行时为每个泵续发一秒时长指令，暂停／停止使用全局暂停指令并查询状态，等待两个泵关闭的回报；连接同样先停止并查询。压力通知为两个无单位原始数值，界面不标成 kPa。固件时长行为、实际方向与压力标定仍待实机确认。

## 协议来源

- [YOKONEX 蓝牙文档目录](https://github.com/YCY-YOKONEX/YCY-YOKONEX-OpenSource/tree/main/Bluetooth)：两代电击器、跳蛋／飞机杯和灌肠机 PDF，参考提交 `9345b7e`。
- [郊狼 V2](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/coyote/v2/README_CN.md)、[郊狼 V3](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/coyote/v3/README_CN.md)、[波形示例](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/coyote/v3/example_cn.md)。
- [负鼠](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/opossum-vibrate-controller/README_CN.md)、[爪印](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/paw-prints/README_CN.md)、[灵猫](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/civet-edging-sensor/README_CN.md)：厂商协议参考提交 `045720d5`。
- [DG-LAB-Client 设备实现与限制](https://github.com/CrimsonSeraph/DG-LAB-Client/blob/main/docs/devices.md)：参考多连接、型号识别及协议歧义，提交 `740d490e`。没有移入其源码，C# 编码器与驱动独立实现。

参考仓库浅克隆及原始 PDF 均位于被忽略的 `.research/`，不加入 Git。

## 使用与诊断

设置页选择「蓝牙」并保存，再到工作台连接设备。首次启动使用模拟模式；已保存的蓝牙来源会在下次启动恢复，但不会自动连接或播放。

仅扫描、输出广播总数及相关候选：

```sh
dotnet run --project src/YokoWave.Desktop -- --bluetooth-probe
```

发现且仅发现一台兼容设备时，连接、关闭输出、等待设备确认、读取状态，再断开：

```sh
dotnet run --project src/YokoWave.Desktop -- --bluetooth-probe --connect
```

诊断模式不会发送非零强度。多台设备需通过界面选择，不自动猜选。扫描未发现设备时，先检查开机、距离、系统蓝牙和其他应用是否仍持有连接。

可按协议筛选，仅当筛选后恰有一台设备时连接：

```sh
dotnet run --project src/YokoWave.Desktop -- --bluetooth-probe --connect --profile CoyoteV3
```

可选协议名称见 `BluetoothProtocol`；诊断输出包含名称、协议、信号、GATT 权限和系统错误。传感器探测不发送输出指令。

用本地音频重复验证五次静音媒体初始化、归零、零强度声控会话及停止：

```sh
dotnet run --project src/YokoWave.Desktop -- --bluetooth-probe --connect --zero-audio /path/to/audio.wav
```

此检查将扬声器音量和 A/B 强度固定为 0%，不使用应用保存的强度配置。

Windows 桌面目标会自动使用 `net10.0-windows10.0.19041.0`；非 Windows RID 仍使用通用 `net10.0`。需要在 Windows 上构建通用模拟入口时可设置 `-p:YokoWaveWindowsBluetooth=false`。这只是构建选择，不会给非 Windows 平台增加原生蓝牙能力。

## 验证记录

2026-11-07：在 Windows 上实际发现并连接 `YCY-DJ`，读取到电量 82%、A / B 电极未接入，且设备明确回报两路输出关闭，之后主动断开。未发送非零强度，因此尚未验证实际刺激输出、强度标定或长时间硬件播放。旧版 `YYC-DJ` 编码器通过协议测试，尚无旧版实机验证。

用户随后反馈实机运行可用。后续定时、动态波形切换与存储调整通过模拟传输和 GATT 替身测试，开发验证没有自动发送非零硬件输出。

2026-11-07：针对 Windows GATT 参数异常，实机确认 `YCY-DJ` 的 FF31 权限为 `WriteWithoutResponse`、FF32 为 `Notify`，协商 MTU 为 512。修改后的写入路径完成五轮静音媒体与零强度会话，设备持续回报两路关闭。原始间歇性异常未在这些检查中复现，不能据此确认其唯一根因。

自动测试使用假的 GATT 链路核对精确控制包、关闭确认、校验与范围、连接失败释放、故障锁定、后台通知到 UI 的调度、设备来源保存与恢复，以及断连只停止所属玩法的目标、其他玩法继续输出。测试中的非零强度仅写入内存链路。

2026-11-07：新增协议验证涵盖郊狼 V2 位字段及示例、V3 四个 25 ms 样本与停止序号匹配、泵公开 AES 测试向量和时长字节序、三通道电机与方向限制、传感器有符号值及截断包、初始化失败释放、停止失败保留占用、多连接隔离、玩法兼容性、界面本地化及配置保存。真实扫描收到广播但未发现郊狼 V3；用户选择稍后进行实机验证，没有对新增设备发送过非零硬件输出。
