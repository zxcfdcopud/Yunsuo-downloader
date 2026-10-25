# YunsuoDownloader 测试页

一个**零依赖**的本地测试页面，用来快速验证 YunsuoDownloader 浏览器扩展的
「嗅探 → 发送」链路，以及桌面下载器的本机 WebSocket 契约。

## 目录结构

```
test-page/
  index.html    测试页（在浏览器里打开）
  serve.mjs     零依赖 Node 静态服务器（正确设置媒体 content-type）
  run.bat       一键启动（Windows）
  media/        测试媒体：HLS 主列表 + 变体 + 加密变体 + ts + mp4 + webm + mp3
```

## 为什么需要本地服务器

扩展是靠 `chrome.webRequest` 的响应头（`content-type`）+ 扩展名来判定媒体的。
直接双击 `index.html`（`file://`）一方面扩展不一定注入，另一方面浏览器对
`file://` 的请求头行为也不一致。所以用 `serve.mjs` 起一个本地服务器，让
`.m3u8` 返回 `application/vnd.apple.mpegurl`、`.ts` 返回 `video/mp2t` 等，
才能真实触发扩展的嗅探逻辑。

## 启动

```powershell
# 方式一：双击 run.bat
# 方式二：命令行
cd F:\下载器工程\test-page
node serve.mjs 8000
```

然后浏览器打开 `http://127.0.0.1:8000/`（建议先加载 `F:\下载器工程\extension` 扩展）。

## 怎么用

### 第 1 节 —— 触发嗅探（观察扩展弹窗/角标）
每个按钮都会让页面出现一个真实媒体请求或视频元素：

| 按钮 | 验证点 |
| --- | --- |
| 插入 MP4 / WebM | DOM 层 `<video src>` 扫描 |
| 注入 data-src 视频 | 读取 `data-src` 属性（播放器常见） |
| 注入 source 列表 | `<source>` 标签扫描 |
| fetch 主列表 | MAIN world 的 `fetch` hook + HLS 主列表解析（展开清晰度、标加密） |
| XHR 主列表 | `XMLHttpRequest` hook，采集 Referer/UA/Cookie 头 |
| 模拟 MSE 播放 | `MediaSource` + `blob:`（MSE 提示条目） |
| 请求直链 mp4 | `webRequest` 嗅探真实响应头 |
| 清空测试元素 | 重置页面，便于重复测试 |

点完后看扩展工具栏**角标数字**和弹窗列表是否对应更新。

### 第 2 节 —— 连接本机下载器并发送
绕过扩展弹窗，直接连 `ws://127.0.0.1:16888/media`（端口/令牌与 App
「设置 → 浏览器插件」一致）：

1. 填地址（默认 `ws://127.0.0.1:16888/media`）与令牌（App 没设令牌则留空）。
2. 点「连接并握手」：页面会发 `hello`，收到 `welcome` → `hello-ack`，状态显示 App 版本与下载目录。
3. 选一条任务点「发送 add 任务」：页面发 `add`，下载器回 `add-ack`。
4. 想验证令牌错误：在 App 里设一个令牌，这里填**错的**，连接后会被以 `4001` 关闭，状态显示「令牌错误(4001)」。

## 排查

- 第 2 节一直「未连接」：确认桌面下载器已启动且开启「浏览器插件」服务，端口一致，防火墙没拦 `127.0.0.1`。
- 第 1 节角标不更新：先播放/点击按钮触发真实请求；确认扩展已加载且与本页同源（本机 `127.0.0.1`）。
- 加密 HLS 变体下载失败：属于站点签名/密钥正常限制，测试页目的是验证「嗅探与标记」，下载结果取决于下载器与密钥可达性。
