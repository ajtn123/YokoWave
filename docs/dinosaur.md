# 恐龙游戏

源码位于 [`modules/dinosaur/`](../modules/dinosaur/)，只有 HTML、原生 JS 和 CSS，不需要 npm 依赖或编译，不附带静态服务器或通用浏览器 SDK。翻译在 [`locales/dinosaur/`](../locales/dinosaur/)，图标复用应用 SVG，主题和词典加载器与官网共用。

## 运行

直接打开[在线游戏](https://ajtn123.github.io/YokoWave/modules/dinosaur/)，或在仓库根目录组装静态站点后运行 Python 自带的 HTTP 服务：

```sh
pwsh -File scripts/build-pages.ps1
python -m http.server 8000 --bind 127.0.0.1 --directory artifacts/pages
```

Windows 也可将 `python` 换为 `py`。打开 [http://127.0.0.1:8000/modules/dinosaur/](http://127.0.0.1:8000/modules/dinosaur/)，点击左上角图标返回官网首页。也可使用已有的静态 HTTP 工具，以 `artifacts/pages` 作为站点根目录；不要直接通过 `file://` 打开，ES 模块和词典加载需要 HTTP。静态组装与自动发布见 [GitHub Pages](pages.md)。

空格、上方向键或点按画布跳跃，按住下方向键或下蹲按钮下蹲。P 暂停／继续，Esc 暂停并停止输出。每次碰撞结束一局，可重新开始；只在浏览器本地保存最高分。游戏可在未连接设备时运行。

## 设备输出

1. 在 YokoWave 设备页连接设备，创建 WebSocket Control 服务端玩法，将服务端节点路由至目标设备输出，并设置输出节点上限。
2. 开始该玩法，将服务端节点显示的完整 `ws://…/ws/节点ID` 地址和令牌填入游戏。不能使用 GATT 服务端。
3. 连接后选择服务端导出的设备与通道。仅列出支持数值波形输出的电刺激／震动设备，通道按实际掩码生成。
4. 设置强度（初始 0）、频率（1–100 Hz）和时长（200–2000 ms），手动勾选「启用碰撞输出」。修改设置前先关闭输出。

每次碰撞仅发送一次脉冲；跳跃不输出。强度 0 不发送播放请求。服务端仍执行自身的限幅与设备能力检查；返回接受请求不代表测量到实际硬件输出。

暂停、切走页面、窗口失焦、停止输出或断开会关闭输出并释放租约。恢复游戏或重新连接不会自动启用输出。令牌仅保留在内存，成功连接后清空输入框，不写入 URL、localStorage 或文件。HTTPS 页面连接远程服务端时使用受浏览器信任的 `wss://`，不要跳过证书校验。

## 实现边界

- `game.js`：固定步长物理、障碍、碰撞和计分，不执行设备 I/O。
- `renderer.js`：Canvas 像素形状与缩放，颜色来自 `styles.css` 的共享变量。
- `output.js`：针对游戏的原生 WebSocket 调用，认证、单设备 control 租约、串行控制、续期与单调时钟。
- `output-panel.js`：连接和脉冲表单、错误及清理入口。
- `../shared/localization.js` / `app.js`：共享词典加载器、模块输入与页面生命周期；语言通过链接的 `lang` 参数传递，未指定时跟随浏览器。

碰撞使用 `control.play` 提交统一采样波形，`order: once`、周期为脉冲时长、其他通道为零，并设置至少 1 秒的服务端定时上界。服务端通过既有 PipelineSession 播放与归零，不依靠浏览器定时器发送最终零帧；输出按会话周期更新，实际时长包含调度与协议延迟。控制带递增序号和服务端时钟中的 500 ms 截止时间；请求超时后断开，不重试控制或恢复输出。

协议契约见 [WebSocket](websocket.md)。验证命令见[测试说明](testing.md)。
