# YokoWave 架构

## 依赖与职责

`Desktop → App → Remote → Pipelines → Core`；各层可直接引用所需的下层模型。Core 包含设备、统一波形、能力、协议驱动和存储；Pipelines 包含图定义、注册表、编译器、处理器与后台会话；Remote 包含 RPC、租约、GATT／control 设备和服务端资源；App 只承载编辑与展示；Desktop 提供原生蓝牙和音频。

Nodify.Avalonia 2.0.0 只出现在 App，图文件不保存控件模型。组合优先于继承：配置是不可变数据，处理器每次启动重新创建；仅在多个实现确实共享行为时使用接口。没有按玩法枚举分派的另一套执行系统，模板只生成普通节点图。

`MainViewModel` 组合 `DevicesPaneViewModel`、`SettingsViewModel`、`WaveformLibraryViewModel`、`HistoryViewModel` 和 `PipelinesWorkspaceViewModel`，负责导航、保存与关闭。页面直接绑定所属 VM，不通过根 VM 转发属性。`PipelineDeviceGroupsViewModel` 独立维护已连接设备与玩法分组的界面投影，通过选择／分配委托触发操作，刷新时保留现有分组与设备卡片实例。每个图由 `PipelineEditorViewModel` 管理草稿、选择与独立会话；`PipelineEditHistory` 管理有界撤销／重做快照，`PipelineGraphClipboard` 提取所选片段并在粘贴时重映射节点、连线、布局与模板端口。普通参数编辑保留节点与字段实例，历史恢复和粘贴才重建图。节点编辑器按职责单独成文件。设备只保存连接与实时读数，图拥有输出设置。

## 图执行

`PipelineCompiler` 解析注册配置、展开嵌入子图、验证端口种类与范围、检测环、排序节点，并检查同设备写入冲突、能力与硬件选项。编译结果保存独立配置和波形快照。

`PipelineRuntime` 每帧按拓扑同步求值一次。数值信号含一个标量或四个 25 ms 样本、有效期、模拟来源与输入序号。处理器保留来源／有效期；目标端口执行单位范围和离散数值校验。已连接但无效的输入不会退回非零默认值。原生程序与控制帧的结构化元数据使用单独的 intent 端口，频率、强度等仍为数值。

`IRunParticipant` 管理音频、传感器和网络 I/O；平台音频只提供特征。`IInteractiveProcessor` 接收边界队列中的数值、波形、滑动、增强、校准、泵和命令操作；`ISuspendableProcessor` 清理暂停时不应保留的输入状态。`DeviceOutputCoordinator` 将通道独立限幅后合并为设备帧。

`PipelineSession` 负责后台单调计时、原子设备申请、资源启动、约 100 ms 的求值／提交、暂停、错误和停止收尾。每次启动创建新的 `PipelineOutputDispatcher`，负责先提交命令、并行发送独立设备帧、去重定时泵命令，并仅统计成功发送的实际强度峰值；同设备由控制器串行化。停止可以取消尚未完成的音频启动；启动失败且清理成功后不保留输入、帧、节点状态或倒计时。提交、暂停和停止有明确的异步边界。停止先撤销网络输出，再归零、停流与释放资源；失败收集原始和清理错误，未确认设备继续保留占用。

## 设备与远程

`DeviceController` 对本地、模拟和远程传输提供一致的整台设备独占，负责操作锁、占用、写入和关闭顺序。`DeviceConnection` 在设备锁内接收状态，校验读数、合并遥测并维护采集时间和运动序号。暂停保留占用，运行期不允许改派。`BluetoothProtocolDriverFactory` 按集中定义的能力创建九类公开协议驱动。统一波形在播放时转换为设备格式，不在资产中存储设备包。

`BluetoothDeviceTransport` 负责发现和路由，每条连接由 `BluetoothDeviceSession` 拥有原生链路、协议驱动、初始化门、GATT 租约切换及清理。通知在驱动和远端租约之间路由，驱动代次阻止旧后台命令越过切换边界；原生链接释放失败时保留驱动关闭错误与释放错误。Windows 的适配器和连接分别实现扫描与单链路 GATT 生命周期。

GATT 客户端把远程连接交给相同协议工厂；control 客户端作为设备传输发送统一输出帧与类型化命令。两者复用认证、心跳、租约顺序与服务端时钟逻辑。GATT 服务端节点独占并导出设备，远端运行协议驱动；control 服务端节点通过缓冲传输把远端请求送回本地图，继续经过数值处理、上限和输出协调。远程播放请求同样编译成图，不另设播放引擎。详情见 [WebSocket](websocket.md)。

`RemoteExportSession` 管理导出预留、租约授权、过期与关闭；每个 GATT 租约组合独立的 `RemoteGattSession`，持有已打开的属性描述、设备标识映射、读写请求和通知订阅。GATT 通知不获取操作锁，以允许底层在写入过程中同步通知。

## 存储与界面

`workspace.json` 只保存版本／累计统计，`settings.json` 保存全局设置，`waveforms.json` 保存统一自定义波形，`pipelines.json` 保存实例，`pipeline-presets.json` 保存用户模板。会话使用 128 KiB、单旧文件的紧凑 JSON Lines。开发阶段 schema 不匹配时只重置已知应用文件；损坏数据保留并阻止覆盖。

`WaveformAsset` 表达统一波形资产，界面的 `WaveformItem` 包含资产与本地化展示属性；`SavedPipelineTemplate` 表达保存的节点模板。`WaveformExchange` 独立负责波形文件／文本导入导出与校验，`WorkspaceStore` 只负责本地工作区文件的读取、验证和写入。

颜色集中在 Palette，尺寸／间距／圆角集中在 Metrics，共享语义样式用于各页面。字体继承系统无衬线，主题与可用强调色跟随系统。en／zh-CN 使用 JSON 本地化，切换语言不重建图或丢弃输入。帮助页集中说明操作和限制；设置仅保留配置及直接说明。

完整节点与模板行为见 [节点玩法](pipelines.md)，设备能力见 [蓝牙协议](bluetooth.md)。
