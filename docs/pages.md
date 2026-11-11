# 官网与 GitHub Pages

[官网](https://ajtn123.github.io/YokoWave/) 提供项目介绍、最新桌面版下载和浏览器模块入口；[恐龙游戏](https://ajtn123.github.io/YokoWave/modules/dinosaur/) 可不连接设备游玩。站点不提供设备控制服务端，设备连接仍由用户运行的 YokoWave 处理。HTTPS 页面连接远程服务端使用浏览器信任的 `wss://`；本机 HTTP 预览可用于 `ws://` 连接。

## 文件与生成

- `site/`：首页 HTML、样式、页面协调对象和模块目录组件。
- `modules/<id>/`：各模块的静态页面、脚本、样式与 `module.json` 元数据。
- `modules/shared/`：系统明暗主题、控件语义样式和 JSON 词典加载器。
- `locales/site/` 与 `locales/<id>/`：各自的 en / zh-CN / ja / ko 词典。模块拥有自身名称和简介。
- `scripts/build-pages.ps1`：组装 `artifacts/pages/`，生成 `site/modules.json`。默认中文首页及模块链接在构建时预渲染，禁用 JavaScript 时仍可阅读首页并打开模块。运行时跟随浏览器语言，也可用 `?lang=zh-CN` 等参数选择；切换后模块链接和返回首页链接保留语言。

下载按钮只指向 `releases/latest`，不保存版本号、包名、功能数量或平台包清单。发布新桌面版无需更新或重新部署首页。

输出只包含首页、已注册模块、共享静态资源及它们的词典，不复制仓库根目录、桌面源码、研究目录、凭据或用户数据。图标从应用 SVG 复制；社交预览从 `docs/assets/` 复制。构建输出限定在仓库的 `artifacts/` 子目录内，每次重建会清空指定输出目录；公共源目录不允许符号链接。

## 新增模块

在 `modules/<id>/` 添加 `index.html` 和模块资源，再添加 `module.json`：

```json
{
  "id": "example",
  "titleKey": "Example.Title",
  "descriptionKey": "Example.Summary"
}
```

ID 必须与目录名一致，使用小写字母、数字和连字符。在 `locales/<id>/` 的四份词典中提供对应名称与简介，并运行 `./scripts/sort-locales.ps1`。公共模块目录只存放可公开的静态文件，不放凭据、开发依赖或用户数据。构建自动发现所有含 `index.html` 的模块目录，验证元数据与四语标题／简介，生成首页目录；无需编辑首页或维护第二份模块列表。

模块可引用 `../shared/theme.css`，并创建 `new Localization("example", "Example.Title")` 加载自己的词典。模块索引与资源全部使用相对路径，兼容项目 Pages 的 `/YokoWave/` 前缀。返回首页使用 `../../`，图标使用 `../../assets/YokoWave.svg`。

## 本地验证

```sh
pwsh -File scripts/build-pages.ps1
python -m http.server 8000 --bind 127.0.0.1 --directory artifacts/pages
node --test tests/dinosaur/*.test.js tests/site/*.test.js
```

访问 `http://127.0.0.1:8000/`。检查语言切换、窄屏布局、模块跳转及返回；游戏验证可在未连接设备、输出关闭的状态完成。测试还检查所有模块被发现、词典键与占位符、预渲染、相对链接及输出目录边界。

## 自动部署

`.github/workflows/pages.yml` 独立于桌面 build / release 工作流。主分支的站点、模块、词典、站点组装脚本或相关图标更新时自动组装并部署，也支持手动运行。仓库 Pages 的 Source 使用 **GitHub Actions**；部署环境为 `github-pages`。

构建 job 只有仓库读取权限，部署 job 使用 `pages: write` 和 `id-token: write`。并发部署只保留最新提交。Pages 工作流不运行桌面构建或浏览器模块测试；浏览器回归按需手动执行，桌面 build / release 同样不打包或测试模块。
