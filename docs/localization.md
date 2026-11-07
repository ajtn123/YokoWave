# JSON 本地化

支持 `en` 和 `zh-CN`。翻译文件为 `src/YokoWave.Core/Locales/en.json` 与 `zh-CN.json`，作为嵌入资源随应用发布，无需部署外部词典，也不生成 `.resx` 或 XML 翻译资源。

[Avalonia 本地化文档](https://docs.avaloniaui.net/docs/app-development/localizing) 使用 ResX 示例，并说明运行时切换可使用 `INotifyPropertyChanged` 服务。本项目采用同一绑定更新机制，词典读取由 `System.Text.Json` 完成。

## 使用

设置页的语言选择器立即刷新现有视图，并将语言代码保存到 `workspace.json`。首次启动根据系统界面语言选择；中文语言映射为 `zh-CN`，其他语言回退到 `en`。旧版工作区没有语言字段时仍可正常读取。

切换语言更新页面、导航、按钮、提示、默认波形、应用错误及状态记录，不重建工作台，不重置设备连接与输出配置。自定义波形名称和其他用户内容不会被翻译。操作系统原生对话框自身的按钮及外部异常详情由相应平台提供。

会话结束原因保存为稳定的资源键，显示时再翻译。读取旧版中文或英文原因文本时会匹配词典并翻译；未知的自定义原因保留原文。新记录包含波形 ID，便于切换语言后显示内置波形名称；历史自定义名称保持原样。

## 添加文案

1. 在两份 JSON 中添加相同键名，使用功能性文字。
2. XAML 使用 `{l:Loc Wave.Save}`，C# 使用 `L10n.Get("Wave.Save")`。
3. 有参数的文本使用 `{0}`、`{1}` 等格式，并调用 `L10n.Format("Device.LinkedCount", count)`。两种语言的占位参数必须一致。
4. 执行 `scripts/verify.ps1`，检查词典一致性、格式参数以及实际界面的切换和保存行为。

词典缺少当前语言的键时回退英文；英文也缺少时显示 `[Key]`，便于发现遗漏。加载嵌入词典时拒绝重复键和非字符串值。应用没有运行时下载翻译文件的行为。

## 实现位置

- `JsonLocalizer`：词典读取、语言选择、格式化、回退和属性通知。
- `LocExtension`：将 XAML 资源键绑定到本地化服务。
- `MainViewModel`：语言设置保存及动态属性刷新。
- `LocalizedMessage`：保留消息键和参数，使已有状态及动态记录随语言更新。
- `LocalizedErrors`：应用校验和设备错误的本地化信息，保留原异常类型关系。

JSON 只存储翻译文字。界面仍使用 Avalonia XAML，颜色仍集中在 `Styles/Palette.axaml`，语言切换不会改变系统主题或强调色。

默认波形数据的 `nameKey` 对应两份词典中的 `DefaultWave.*.Name`。增加默认波形时必须同步添加中英文名称，测试会校验每个数据条目的翻译键。
