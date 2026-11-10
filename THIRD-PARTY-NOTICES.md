# 第三方数据

## dglab-kit 波形

以下文件由 [dungeonlab-open/dglab-kit](https://github.com/dungeonlab-open/dglab-kit/tree/3e7ed447e387c2fa52f2c27353c27ff295b0bdeb) 的波形数据转换而来，保留上游 GNU GPL v3 许可，完整文本见 [licenses/dglab-kit-GPL-3.0.txt](licenses/dglab-kit-GPL-3.0.txt)。

- `src/YokoWave.Core/Data/coyote-waveforms.json`：来自 `src/waveform/coyote.ts` 的全部 24 条波形。
- `src/YokoWave.Core/Data/ovc-waveforms.json`：来自 `src/waveform/ovc.ts` 的全部 20 条波形。
- 两个波形 JSON 中的 `Names` 基于对应的上游标签。

转换修改：将每个 100 ms 协议帧展开为四个 25 ms 通用采样；压缩频率转换为 Hz，幅度除以 100 保存为相对脉宽，强度包络为 1；添加稳定标识及来源元数据。双语名称保存在资产的 `Names` 中，不加设备名前缀；英文 “Extrustion” 修正为 “Extrusion”。`Source` 保留原始枚举标识、仓库、提交、路径及许可名称，导入导出及编辑副本继续保留这些信息。原始 TypeScript 和协议包不作为运行时资产保存。
