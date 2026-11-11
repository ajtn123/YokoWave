# 界面与 JSON 本地化

## 词典与动态文本

根目录 `locales/` 按使用方划分词典：`app/` 属于桌面应用，`site/` 属于官网，`dinosaur/` 属于恐龙游戏。每组包含 `en.json`、`zh-CN.json`、`ja.json`、`ko.json`，不把模块文案混入应用词典。官网与模块共用 `modules/shared/localization.js`，模块名与简介从模块自身词典读取。

Core 项目直接将 `locales/app/*.json` 嵌入程序集，通过 `System.Text.Json` 读取，不使用 ResX 或运行时文件复制。`JsonLocalizer` 实现属性通知，`LocExtension` 将 XAML 的资源键绑定到该服务。浏览器模块从同源 `locales/<module>/` 加载自己的词典；已知 RPC 错误使用模块翻译，未知错误显示本地化错误前缀及服务端资源键。

首次启动按系统界面语言选择：中文映射为 `zh-CN`，日语为 `ja`，韩语为 `ko`，其他语言回退为 `en`；区域后缀不影响选择。浏览器模块遵循相同规则。`SettingsViewModel` 切换语言后立即更新现有绑定并保存设置；页面、节点、设备连接与用户输入保持原对象。语言菜单显示各语言的本名。用户输入的名称不自动翻译；波形可携带自己的多语言名称。原生对话框按钮和外部异常由平台决定。

缺少当前语言的键时回退英文，英文也缺少则显示 `[Key]`。词典读取拒绝重复键和非字符串值，不在运行时下载翻译。

```xml
<TextBlock Text="{l:Loc Wave.Save}" />
```

```csharp
var label = L10n.Get("Wave.Save");
var countLabel = L10n.Format("Device.LinkedCount", count);
```

占位符为 `{0}`、`{1}` 等，四份词典的键和参数必须一致。修改词典后执行 `./scripts/sort-locales.ps1`，它会按 Ordinal 键序排序整个 `locales/`，保持 UTF-8、两空格缩进和行尾换行，并拒绝重复键及非字符串值；大小写不同的键分别保留。`check-style.ps1` / `verify.ps1` 验证格式，`check-style.ps1 -Fix` 同时执行排序。

`LocalizedMessage` 保存键与参数；已有状态及本地化异常在显示时取当前语言，同时保留原始错误。会话原因保存稳定资源键，旧的本地化原因可从任一词典解析，未知的用户原因保留原文。

波形名称存入各波形 JSON 的 `Names` 映射，内置资源提供 `en` / `zh-CN`，不使用 `DefaultWave.*` 词典键。`WaveformAsset.GetName` 按当前文化、父文化、回退 `Name` 的顺序查找；资料库、下拉选项和预览共用资产名称。源标签不加设备名前缀，可保留拼写修正；`Source.Provider · Source.Collection` 在界面作为小字来源行。新建波形可仅有 `Name`。编辑已有多语言波形时，只更新打开编辑器时所用语言的名称；随后切换应用语言不会将未提交输入写入另一语言。

## 页面与共享组件

页面直接绑定所属 ViewModel。设备卡片、连接列表、硬件详情、协议表单、序列操作、节点计时和已发送条复用独立组件。字段按设备能力和通道掩码生成，配置通过对应表单编辑。节点参数更新保留字段实例；语言变化只刷新展示属性。

V4 配对二维码由 QRCoder 在内存中生成，`PairingCodeView` 显示二维码、链接与配对步骤；语言变化复用已有图像，断开来源时清除并释放图像。

XAML 使用语义样式和动态资源：

- `Styles/Palette.axaml`：主题色和语义画刷。
- `Styles/Metrics.axaml`：字号、尺寸、间距、内边距与圆角。
- `Typography` / `Layout`：文本层次和布局语义。
- `Controls` / `Components` / `Pipelines` / `ItemTemplates`：控件、复用组件及图编辑器样式。

默认跟随系统明暗主题；平台提供时 FluentTheme 更新强调色，否则使用 Avalonia 默认值。字体继承系统无衬线，不引入字体包；上下文菜单继承窗口字体。自绘控件缓存几何和画笔，并在参数、尺寸及主题资源变化时失效。图标源为 Assets/YokoWave.svg，ICO 用于窗口及 Windows EXE。

浏览器模块在自己的 CSS 中集中定义颜色、间距和圆角变量，使用系统字体与 `prefers-color-scheme`，不依赖桌面样式资源。

## 用户手册

用户手册是 `Views/Pages/HelpView.axaml`，正文统一使用四份词典中的 `Help.*`。快速开始直接显示，其余主题可展开阅读；主题顺序按连接、图编辑、输出与定时、波形、输入、协议、远程、停止及数据排查组织。

帮助只描述用户可执行的步骤、状态和限制，不列源码类型或协议包字段。实现说明保留在 docs，开发命令保留在 CONTRIBUTING。修改用户行为时同步更新帮助的四种语言；切换语言不重置已展开主题。

## 验证

词典测试核对键、非空值、占位参数、回退和异常；Headless 测试验证语言／主题切换后的绑定、布局和编辑状态。运行命令见[开发指南](../CONTRIBUTING.md#验证)及[测试说明](testing.md)。
