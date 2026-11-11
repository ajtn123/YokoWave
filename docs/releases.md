# 发行与打包

用户下载与运行步骤见 [README](../README.md)，发布者操作见 [CONTRIBUTING](../CONTRIBUTING.md#按需发布)。

## 工作流与版本

`.github/workflows/release.yml` 只接受已存在的稳定版本标签 `vMAJOR.MINOR.PATCH`。版本检查要求 `Directory.Build.props` 与标签一致，且对应的 `docs/releases/<版本>.md` 存在；检验、测试、打包与发布均使用标签解析到的同一提交。手动运行也必须提供现有标签。

测试复用 `.github/workflows/build.yml`，在 Windows 和 Linux 安装 .NET 10，执行风格检查（包括所有本地化 JSON 的排序）、构建和 xUnit 测试。build / release 不安装 Node、不运行浏览器模块测试，也不制作模块静态包。普通 push / PR 不 publish。打包矩阵的六个 job 分别运行在 Windows、Linux、macOS 宿主，允许同系统跨架构构建。

版本源为 `Directory.Build.props`；程序集、应用侧栏／设置、包名和 macOS 元数据从同一版本生成，界面词典不保存版本号。

## 包结构

`scripts/package-release.ps1` 运行自包含、不裁剪、非单文件的 `dotnet publish`，验证主程序、.NET 核心、Kestrel 与音频原生库均存在。打包前递归移除所有 `.pdb` 符号文件（包括依赖自带文件，扩展名大小写不敏感）；macOS 在移除后才签名。保留所有运行依赖，附带 `LICENSE`、`THIRD-PARTY-NOTICES.md` 与 `README.txt`，不带研究目录、用户数据或凭据。

- Windows：`YokoWave-v<版本>-win-{x64,arm64}.zip`，目录内运行 `YokoWave.exe`。
- Linux：`YokoWave-v<版本>-linux-{x64,arm64}.tar.gz`，保留执行权限，运行 `./YokoWave`。
- macOS：`YokoWave-v<版本>-osx-{x64,arm64}.tar.gz`，包含 `YokoWave.app`。

macOS 由 `scripts/package-macos.ps1` 创建 bundle：使用现有 ICO 内的 PNG 通过系统 `sips` / `iconutil` 生成 ICNS，使用 `Packaging/Info.plist` 模板写入版本、入口与图标，从根目录词典生成 English / 简体中文 / 日本語 / 한국어麦克风权限说明，最后进行临时签名和签名验证。不使用开发者证书，也不执行公证。

## 发布与失败处理

每个打包 job 只上传自身压缩包，最终 job 使用 `scripts/publish-release.ps1` 检查六个桌面包，然后创建草稿、上传六个附件、公开 release。附件全部准备就绪后才写 GitHub，不生成额外校验文件。Release notes 使用正文、列表或二级以下标题；脚本拒绝重复发布标题的一级标题。

草稿重试可替换同名附件；包含未知附件的草稿会阻止发布，避免把未审查内容一起公开。已公开的版本不会覆盖，修复应使用新版本。工作流同一标签串行执行，不取消正在发布的版本。

本地打包使用独立的 `artifacts/release/work/` 目录，拒绝覆盖已有同名压缩包。构建临时文件和包均由 `.gitignore` 排除。
