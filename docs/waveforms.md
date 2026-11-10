# 统一波形格式

默认波形、自定义波形、滑动录制和 JSON 导入导出都使用 `FormatVersion: 1`，保存通用采样参数及可选来源元数据。设备协议转换只在播放时执行，不修改资产和图模板。用户编辑步骤位于应用内「帮助」。

## 数据格式

顶层为波形数组。每条波形包含 `Id`、回退名称 `Name`、`FormatVersion`、`Shape`、`PeriodSeconds`、`Frequency`，以及自定义插值点 `Points` 或采样序列 `Samples`。可选 `Names` 保存多语言名称；`Frequency` 是生成曲线的频率，单位 Hz。采样序列的帧间隔为 `PeriodSeconds / Samples.Length`，每帧保持到下一帧。

| 采样字段 | 范围 | 含义 |
| --- | --- | --- |
| `Level` | 0–1 | 相对于输入强度的包络 |
| `FrequencyHz` | 1–100 Hz | 脉冲频率 |
| `PulseWidth` | 0–1 | 相对脉宽／波形幅度，不代表跨设备校准的物理量 |
| `Channels` | 可省略，1–3 个 −1–1 数值 | 各源通道的有符号倍率；省略表示本帧对已定义通道的倍率为 1，数组中未提供的通道为 0 |

源通道数量取采样序列中 `Channels` 数组的最大长度；整条波形没有通道数组时只定义 A，生成曲线也只定义 A。节点分别显示已定义通道的幅度端口，频率与脉宽共用。没有独立通道数据的波形可以将 A 端口连接到多个目标。

`Level`、`FrequencyHz`、`PulseWidth` 必须显式存在于采样 JSON 中。未知版本或缺少必要字段直接拒绝，不把旧协议字段默认为零。所有数值必须有限，单条最多 512 个采样或 256 个插值点，周期为 0.2–60 秒。采样上限由 `WaveformAsset.MaximumSamples` 定义，编辑器与导入校验共用，Schema 保持一致。

```json
[
  {
    "FormatVersion": 1,
    "Id": "edca5dc0-2a7b-491c-a0a6-3bd6913b35ca",
    "Name": "Custom",
    "Shape": "Sampled",
    "PeriodSeconds": 0.2,
    "Frequency": 20,
    "Samples": [
      { "Level": 1, "FrequencyHz": 20, "PulseWidth": 0.4, "Channels": [1, 0.5, -0.5] },
      { "Level": 0.5, "FrequencyHz": 40, "PulseWidth": 0.8, "Channels": [1, 1, 0] }
    ]
  }
]
```

生成曲线也渲染成同一种采样：曲线值进入 `Level`，`PulseWidth` 为 1。预览幅度为强度包络 × 相对脉宽 × 最大通道倍率绝对值，不把预览值再次乘进强度。波形只显示名称、参数和预览。

## JSON Schema

[单条波形 Schema](../schemas/waveform.schema.json) 与 [波形数组 Schema](../schemas/waveforms.schema.json) 使用 [JSON Schema Draft 2020-12](https://json-schema.org/draft/2020-12/schema)。单条 Schema 校验对象字段、版本、形状及采样范围，并按 `Custom`／`Sampled` 要求对应数组；数组 Schema 用于 `waveforms.json`、内置数据和导入导出文件。引用使用相对地址，可在本地离线解析。

单条对象可添加 `"$schema": "./schemas/waveform.schema.json"`，路径相对于该 JSON 文件。应用的顶层格式保持数组，数组无法添加 `$schema` 属性，应在编辑器中关联 `schemas/waveforms.schema.json`。`Names` 与 `Source` 是可选数据；`ChannelCount` 是运行时派生值，不写入 JSON。导入还会检查 1–100 条限制和 ID 唯一性，这两项由应用处理。

## 来源与信息保留

波形的显示名称直接存入资产的 `Names`，例如 `{"en":"Extrusion","zh-CN":"挤压"}`，当前语言及其父文化均不存在时回退 `Name`。名称不放在应用词典中，不添加设备名前缀，也不另存一份原始标签。内置名称使用来源标签，允许拼写修正。YOKONEX 使用相同格式，来源数值标识以字符串保留在 `Source.Key`。

有 `Source` 时，资料库、下拉选项和节点预览以小字显示 `Provider · Collection`，如 `dglab-kit · coyote`。编辑多语言波形时只修改打开编辑器时所用语言的名称，其他语言不变；切换界面语言保留当前输入。新建波形可以仅使用 `Name`。

可选 `Source` 对象使用 `WaveformSource` 类型，包含以下字段：

| 字段 | 内容 |
| --- | --- |
| `Provider`、`Collection` | 必填，来源和系列。例如 `dglab-kit` / `coyote` |
| `Key` | 可选，来源中的原始标识。内置库均提供，例如 `EXTRUSTION` 或 YOKONEX 的 `4857` |
| `Repository`、`Revision`、`Path` | 可选，来源仓库 URL、版本／提交与文件路径 |
| `License` | 可选，来源许可名称 |

来源元数据不执行网络请求，不保存协议包，不参与设备编码。未知 `Source` 字段在导入时拒绝，避免默默丢掉不认识的来源信息。复制、重命名、编辑及保存后保留来源；新建／清空编辑器不继承上一条波形的元数据。`Source` 表示最初来源，不表示编辑后的采样仍与来源完全相同。

新增郊狼和负鼠数据保留了原始枚举标识、双语名称、顺序、完整采样和周期。每个原始 100 ms 帧包含四个不同位置的频率／幅度值，依次转换为四个 25 ms 采样，没有插值、截断或降采样。频率由压缩周期转换为 Hz，幅度除以 100 存入 `PulseWidth`，`Level` 为 1。Hz 使用浮点数保存，但这 44 条数据的全部 1,208 个原始协议帧均可逐字节重新编码还原；不需要缓存原始协议数据。原始 TypeScript 的排版和代码语法不属于波形资产。

英文名称 `Extrustion` 修正为 `Extrusion`，原始枚举标识仍为 `EXTRUSTION`；不重复保存拼写修正前的标签。YOKONEX 同步采用 `Names` 与 `Source`，全部稳定 ID 与已有采样不变。另行核对本地系统波形来源后，12 条电击波形的频率与来源一致，没有实际的 0 Hz 到 1 Hz 替换。电机波形中的 20 Hz 是通用格式的默认参数，来源仅定义三路电机值。

## 默认数据及转换来源

三个嵌入资源 `yokonex-waveforms.json`、`coyote-waveforms.json`、`ovc-waveforms.json` 与普通导入导出使用完全相同的波形结构，可直接通过普通波形导入器读取。内置条目均提供 `Names.en`、`Names.zh-CN` 及 `Source`；来源不参与设备编码。YOKONEX 保留原稳定 ID；郊狼与负鼠的标识由来源系列及枚举名确定，不依赖显示名称或排列位置。

| 来源 | 数量／采样 | 转换 |
| --- | --- | --- |
| 原应用电击系统波形 | 12 条 | `Level=1`，频率 PL 按 Hz 保存，TL 除以 100 作为相对脉宽 |
| 原应用电机 f1 系统波形 | 10 条 | `Level=1`、`PulseWidth=1`，三路有符号值除以 20 保存为 `Channels`；源数据没有脉冲频率，使用默认 20 Hz |
| dglab-kit 郊狼波形 | 24 条，3,052 个采样 | 压缩周期解码为毫秒后转换为 `1000 / period` Hz；幅度除以 100，`Level=1`；每组 100 ms 展开为四个 25 ms 采样 |
| dglab-kit 负鼠波形 | 20 条，1,780 个采样 | 与郊狼原始波形格式相同，保存全部四个采样的频率和幅度；实际震动输出忽略频率 |

YOKONEX 原系统数据共 22 条（1,379 帧），未收录用户分享波形及 f2 分类波形。郊狼和负鼠收录 [dglab-kit coyote.ts](https://github.com/dungeonlab-open/dglab-kit/blob/3e7ed447e387c2fa52f2c27353c27ff295b0bdeb/src/waveform/coyote.ts) 与 [ovc.ts](https://github.com/dungeonlab-open/dglab-kit/blob/3e7ed447e387c2fa52f2c27353c27ff295b0bdeb/src/waveform/ovc.ts) 的全部波形，未截断或降采样。最长序列为 336 个采样。总计 66 条、6,211 个采样，均不可删除或覆盖，可复制后编辑。转换数据及标签的来源许可见[第三方数据声明](../THIRD-PARTY-NOTICES.md)。

回归测试将全部郊狼和负鼠采样按原时间顺序重新编码，分别使用上游原始帧及“原始标识、双语名称（含允许的拼写修正）、帧数量、协议帧”的 SHA-256 校验完整内容；同时验证名称和来源的导入导出、本地存储、Schema、语言切换、最长序列的复制编辑，以及每个已支持输出驱动对整个内置库的播放转换。

YOKONEX 频率和脉冲时间单位依据厂商 [V1／V2 蓝牙文档](https://github.com/YCY-YOKONEX/YCY-YOKONEX-OpenSource/tree/main/Bluetooth)。波形数据内置，无需登录或联网，应用不读取本地参考账号凭据。

## 播放时转换

- YOKONEX 电击器：基础强度 × `Level`，Hz 取整到协议频率；相对脉宽 × 通道倍率绝对值映射到 0–100 µs。
- 郊狼 V2：Hz 转换为周期并编码 X／Y，相对脉宽映射为推荐的 Z≤20；单次读取当前采样。
- 郊狼 V3：每包四个 25 ms 采样，频率转换为压缩周期，幅度映射到 0–100。四帧包络使用包内峰值设置强度，再对各帧幅度归一，避免重复乘包络或超过输出节点上限。
- YOKONEX 震动器／负鼠：相对脉宽与通道倍率作为震动幅度，忽略脉冲频率。负鼠同样保留每包四个幅度采样。
- 当前输出协议只支持非负幅度，因此有符号通道取绝对值；符号仍完整保存在文件中，设备没有的通道忽略。方向和电击频率效果无法由单向震动设备复现。
- 所有转换继续受玩法强度、上限、暂停和停止约束。单次播放及定时结束之后的四帧预取位置补零。旧设备的时间分辨率和参数量化更粗，跨设备播放不代表物理效果等价。
- 泵继续使用方向／开关控制；爪印、灵猫是输入设备，不作为波形播放目标。

## 存储

`waveforms.json` 仅保存统一格式的自定义波形，玩法只引用波形 ID；不保存按设备转换的副本。本地存储使用紧凑 JSON，导出文件可以缩进便于编辑。

文件所有权、版本重置与损坏保护见[本地存储](storage.md)。参考资料和凭据不属于波形资产或发布内容。
