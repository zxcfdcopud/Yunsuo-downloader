# 视频嗅探下载器助手（Chromium 浏览器扩展）

配套 `F:\下载器工程` 桌面下载器的 **Manifest V3** 浏览器扩展，支持 **Chrome / Edge（Chromium 116+）**。
它负责在浏览器侧探测页面视频地址（HLS / DASH / MP4 等），并通过本机 WebSocket 把下载任务一键发送给桌面下载器。

- 纯原生 JS，**无第三方库、无构建步骤、无打包器、无 npm 依赖**
- 全部数据只在本机流转（插件 ↔ `127.0.0.1`），不访问任何外部服务器
- UI 全中文，深色/浅色跟随系统 `prefers-color-scheme`

---

## 1. 目录结构

```
extension/
  manifest.json           扩展清单（MV3）
  background.js           service worker：webRequest 嗅探、按 tabId 缓存、WebSocket 客户端、角标
  content/
    inject.js             MAIN world（document_start）：hook fetch/XHR/MSE，就地解析 m3u8/mpd
    content.js            ISOLATED world：读 DOM video/source、og 元信息、时长、cookie
    content.css           页面内轻提示样式（已发送/排队等）
  popup/
    popup.html/.css/.js   工具栏弹窗：媒体列表、发送、复制直链、清空、连接状态
  options/
    options.html/.css/.js 设置页：地址端口、令牌、重连、通知、角标、自动发送
  icons/                  icon16 / icon32 / icon48 / icon128（合法 PNG）
  tests/                  可选自检脚本（Node 运行，不参与扩展运行）
    smoke-test.js         background.js 自检（缓存/协议/重连/排队）
    sniff-test.js         inject.js + content.js 自检（嗅探与解析）
  README.md               本文件
```

---

## 2. 安装（加载未打包扩展）

### Chrome
1. 打开 `chrome://extensions/`
2. 右上角打开 **开发者模式**
3. 点击 **加载已解压的扩展程序**，选择目录 `F:\下载器工程\extension`
4. 建议把「视频嗅探下载器助手」固定到工具栏（拼图图标 → 图钉）

### Edge
1. 打开 `edge://extensions/`
2. 左下角打开 **开发人员模式**
3. 点击 **加载解压缩的扩展**，选择 `F:\下载器工程\extension`

> 修改代码后，回到扩展页点击该扩展的 **刷新（↻）** 即可生效；`background.js` 的改动需要刷新后重新打开 popup。

---

## 3. 配置令牌（token）

1. 先启动桌面下载器，确认已开启**浏览器插件服务**（默认监听 `ws://127.0.0.1:16888/media`）。
2. 在下载器中打开 **设置 → 浏览器插件**，复制 **令牌**。
3. 点击扩展工具栏图标 → **设置**（或右键扩展 → 选项），在「访问令牌」中粘贴并 **保存设置**。
4. 若忘了填或填错，连接会被下载器以关闭码 **4001** 拒绝，插件会显示：
   > 令牌不匹配，请在下载器 设置 → 浏览器插件 中复制令牌
   此时回到设置页粘贴正确令牌并保存（或点「测试连接」）即可。

端口与路径同样可在设置页修改（主机 / 端口 / 路径 / 令牌）。

---

## 4. 使用说明

1. 打开含视频的网页并 **播放视频**（部分站点需要播几秒，播放器才会请求真实分片）。
2. 工具栏图标出现 **角标数字** ＝ 当前标签页探测到的媒体数量。
3. 点击图标打开列表：
   - 每条显示：类型标签（HLS / DASH / MP4 / WebM / FLV / MKV / 音频）、站点、清晰度、是否加密、大小、时长、来源（网络嗅探 / 播放器请求 / 页面元素 / 后台解析）
   - **发送到下载器**：发送单条
   - **全部发送**：发送列表中全部可下载媒体
   - **复制直链**：复制全部直链到剪贴板；单条卡片上也有「复制直链」
   - **浏览器下载**：非 HLS 直链的兜底方案（调用浏览器自带下载器）
   - **清空列表**：清空当前标签页的探测结果
4. HLS 主播放列表（master playlist）会自动展开清晰度列表；`#EXT-X-KEY`（`METHOD != NONE`）会标记为 **加密**。
5. 未连接下载器时发送的任务会进入 **待发送队列**（持久化在 `chrome.storage.session`），连接成功后自动补发，弹窗顶部会提示排队数量。
6. 状态区显示：未连接 / 连接中 / 握手中 / **已连接（含下载器版本与下载目录）** / **令牌错误**。

### 探测原理（简述）
- **网络层**：`chrome.webRequest` 的 `onBeforeRequest` / `onBeforeSendHeaders` / `onHeadersReceived` 监听全站请求，按扩展名（`.m3u8/.mpd/.mp4/.webm/.flv/.mkv/.ts/.m4s/.mp3/.m4a`…）与响应头 `content-type`（`video/*`、`audio/*`、`application/vnd.apple.mpegurl`、`application/x-mpegURL`、`application/dash+xml`）判定媒体，同时采集 `Referer / Cookie / Origin / User-Agent`。
- **播放器层**：MAIN world 注入脚本在 `document_start` hook `window.fetch`、`XMLHttpRequest.open/send/setRequestHeader` 与 `MediaSource.addSourceBuffer`，捕获 MSE（`blob:`）播放链路的真实请求，并**就地复用响应体**解析 m3u8/mpd（不额外发请求）。
- **DOM 层**：ISOLATED world 读取 `<video>` / `<source>` 的 `src`、`currentSrc`、`data-src`、`poster`、`duration`，以及 `og:title`、`og:image`、`<title>`、JSON-LD 时长。

---

## 5. 支持的站点

**专门适配（站点标识 + 站点标签）**

| 站点 | 站点域（含常见 CDN） |
| --- | --- |
| 哔哩哔哩 | bilibili.com、b23.tv、bilivideo.com、hdslb.com |
| YouTube | youtube.com、youtu.be、googlevideo.com、ytimg.com |
| 抖音 | douyin.com、iesdouyin.com、douyinvod.com、snssdk.com |
| 快手 | kuaishou.com、gifshow.com、kwaicdn.com、yximgs.com |
| 小红书 | xiaohongshu.com、xhscdn.com、xhslink.com |

**任意其他网站**：依靠通用嗅探规则（扩展名 + content-type）工作，站点标识为 `other`。

---

## 6. 与桌面下载器的通信协议

默认地址：`ws://127.0.0.1:16888/media`（端口/路径/token 均可在插件设置中修改）。

### 连接后必须先发 hello

```json
{ "type": "hello", "client": "browser-extension", "version": "1.0.0", "browser": "chrome", "token": "" }
```

`browser` 取值为 `chrome | edge | opera | brave | chromium`。

成功回应（插件据此显示「已连接 + 版本」，并记录下载目录）：

```json
{ "type": "hello-ack", "appVersion": "1.0.0", "downloadDir": "F:\\下载器工程\\downloads", "settings": {} }
```

**令牌不匹配**：下载器以关闭码 **4001** 关闭连接 → 插件进入「令牌错误」状态，弹窗横幅提示，并且**不再自动重连**（改设置或点「重试连接」后恢复）。

### 发送任务

```json
{
  "type": "add",
  "tasks": [
    {
      "url": "https://.../index.m3u8",
      "kind": "hls",
      "title": "视频标题",
      "filename": "建议文件名（不带扩展名，可选）",
      "referer": "页面 URL",
      "userAgent": "navigator.userAgent",
      "cookies": "cookie 字符串（; 连接，可为空）",
      "headers": { "Referer": "...", "User-Agent": "...", "Origin": "..." },
      "duration": 123,
      "thumbnail": "封面 URL",
      "site": "bilibili|youtube|douyin|kuaishou|xiaohongshu|other",
      "quality": "1080p",
      "isHls": true,
      "encrypted": false
    }
  ]
}
```

- `kind` 取值只会是 `video` | `hls` | `file`（`m3u8 → hls`；`mp4/webm/flv/mkv → video`；`mpd/音频/其它 → file`）。
- `filename` 仅在能生成建议名时出现；`duration` 为秒。
- 下载器回复：

```json
{ "type": "add-ack", "accepted": 1, "rejected": 0, "message": "..." }
```

### 其它消息

| 方向 | 消息 | 说明 |
| --- | --- | --- |
| 插件 → App | `{"type":"ping"}` | 每 20 秒心跳 |
| App → 插件 | `{"type":"pong"}` | 心跳应答（超 60 秒未收到则重建连接） |
| App → 插件 | `{"type":"notify","level":"info\|success\|error","message":"..."}` | 插件用 `chrome.notifications` 弹通知 |
| App → 插件 | `{"type":"config","downloadDir":"..."}` | 保存并展示下载目录 |
| App → 插件 | `{"type":"error","message":"..."}` | 展示错误并弹通知 |

---

## 7. 权限说明

| 权限 | 用途 |
| --- | --- |
| `webRequest` + `host_permissions: <all_urls>` | 嗅探全站媒体请求与响应头（只读观察，不修改请求） |
| `storage` | `local` 存设置；`session` 存按标签页的探测缓存与待发送队列 |
| `tabs` | 获取当前标签页、按 tabId 隔离缓存、清理关闭/跳转的标签页 |
| `activeTab` / `scripting` | 对当前标签页按需重新扫描（content script 未注入时手动注入） |
| `notifications` | 展示下载器推送的通知与错误提示 |
| `downloads` | 「浏览器下载」兜底按钮（非 HLS 直链） |
| `alarms` | service worker 被回收后定时恢复连接与保活 |

---

## 8. 已知限制

1. **MV3 service worker 会被回收**：WebSocket 连接可能被浏览器中断，插件用 `chrome.alarms`（30 秒周期）与指数退避自动重连（1s→2s→4s→8s→16s→30s 上限，带抖动），连接状态与队列持久化在 `chrome.storage.session`，SW 重启后自动恢复；重连期间点击发送的任务会排队而不是丢失。
2. **MSE / `blob:` 播放**：页面自身用 `blob:` 喂 MSE 时（YouTube、B 站等常见），DOM 里的 `src` 无法直接下载。插件只能等播放器真正发出分片请求后，从网络层拿到真实 m3u8/mpd 地址；若播放器始终未请求（例如只有静态封面），列表里会出现「检测到 MSE / blob: 播放」的提示条目。
3. **DRM / 加密流**：`#EXT-X-KEY` 加密（AES-128）会标记为「加密」；Widevine/PlayReady 等 DRM（`mpd` 中的 `ContentProtection`）无法下载，插件只做标记不做绕过。
4. **需要鉴权的地址**：插件转发 `Referer / User-Agent / Cookie(请求头或 document.cookie 兜底)`，但部分站点使用一次性签名 URL、`HttpOnly` Cookie 或短时效 token，可能导致下载器侧 403/404。
5. **分片不做独立任务**：`.ts` / `.m4s` 分片会被记录（用于诊断）但默认隐藏、且不会作为独立任务发送（避免把一条 HLS 拆成几百个任务）。
6. **后台解析兜底有限**：MAIN world 拿不到播放列表响应体时，插件会在后台用扩展身份 `fetch` 解析。扩展请求无法设置 `Referer`（浏览器禁止的头），对严格校验 Referer 的站点解析可能失败——此时仍然会把 URL 交给下载器，由下载器自行解析。
7. **`chrome://`、扩展商店、`file://` 等页面**不支持注入与嗅探；`file://` 需要额外在扩展详情页手动开启「允许访问文件网址」。
8. **跨域 iframe 内的媒体**：可以靠网络层嗅探到，但无法读取其 DOM 元信息（标题/时长）。
9. **角标数字**为当前标签页的非分片媒体数；同一媒体重复请求会按 URL 去重。
10. 端口被占用 / 下载器未启动时，插件会持续退避重连并显示「未连接」，属正常现象。

---

## 9. 故障排查

| 现象 | 处理 |
| --- | --- |
| 弹窗一直「未连接」 | 确认下载器已启动并开启浏览器插件服务；端口与插件设置一致；本机防火墙未拦截 `127.0.0.1` |
| 「令牌错误」/ 连接被立即关闭（4001） | 到下载器 **设置 → 浏览器插件** 复制令牌，粘贴到插件设置页保存 |
| 列表为空 | 先播放视频；点击「刷新探测」；部分站点需要切换到具体视频页而不是首页 |
| 只有 MSE 提示条目 | 播放器尚未请求分片，继续播放几秒后重试；或该视频受 DRM 保护 |
| 发送后下载器报 403 | 该站点需要 Referer/Cookie 或地址已过期，重新播放视频后再发送 |
| 修改代码后不生效 | 到扩展页点击刷新；`background.js` 改动后请重新打开 popup |
| 想彻底重置 | 扩展页「移除」后重新「加载已解压的扩展程序」 |

---

## 10. 图标

`icons/` 下 4 个尺寸均为合法 PNG（RGBA）：

| 文件 | 尺寸 |
| --- | --- |
| icon16.png | 16×16 |
| icon32.png | 32×32 |
| icon48.png | 48×48 |
| icon128.png | 128×128 |

重新生成（需 Pillow）：绘制 8 倍超采样的圆角渐变方块 + 白色播放三角与下载箭头，再用 LANCZOS 缩放导出。

---

## 11. 隐私说明

- 插件不与任何外部服务器通信；WebSocket 只连接本机 `127.0.0.1`（地址可配置）。
- 采集的页面元信息（标题、封面、时长、Cookie、UA）仅用于构造下载任务并保存在本地 `chrome.storage.session`，标签页关闭即清理。
- 不使用 `eval`、不远程加载代码（符合 MV3 与商店政策）。

---

## 12. 自检（可选）

`tests/` 下两个脚本用 Node 运行（**不需要 npm 安装任何依赖**，也不属于扩展运行时代码，可随时删除）：

```powershell
cd F:\下载器工程\extension
node tests/smoke-test.js   # background.js：m3u8 解析、归类、站点识别、分标签缓存、
                           # 角标、webRequest 链路、后台兜底解析、WebSocket 握手与
                           # hello/add 契约字段、add-ack/notify/config、4001 令牌错误、
                           # 排队与自动补发、跳转/关闭清理（100 项断言）
node tests/sniff-test.js   # inject.js + content.js：fetch/XHR hook、m3u8 主列表与加密解析、
                           # MSE 探测、probe 应答、DOM 扫描、og 元信息、上报结构（41 项断言）
```

两个脚本都会打印 `断言 N 项，失败 0 项` 并以退出码 0 结束表示通过。

对扩展本身的最小验证命令：

```powershell
# 1) manifest.json 合法
python -c "import json;json.load(open(r'F:\下载器工程\extension\manifest.json',encoding='utf-8'))"
# 2) 所有脚本语法合法
node --check background.js
node --check content/inject.js
node --check content/content.js
node --check popup/popup.js
node --check options/options.js
# 3) 图标是合法 PNG（需要 Pillow；本机 `E:\python3.10.5\python.exe` 未装 Pillow 时，
#    可用带 Pillow 的 python 运行，或跳过此项——4 个图标已生成并校验通过）
python -c "from PIL import Image;print([Image.open(r'F:\下载器工程\extension\icons\icon%d.png'%s).format for s in (16,32,48,128)])"
```
