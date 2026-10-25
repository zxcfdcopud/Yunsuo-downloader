# 浏览器插件 ⇄ 桌面 App 通信协议

版本：1.0  
传输：WebSocket（RFC 6455），仅回环地址  
地址：`ws://127.0.0.1:16888/media`（端口与令牌都可在 App 的「设置 → 浏览器插件」修改）

服务端是 App 自己用 `TcpListener` 实现的 WebSocket（`Core/Services/MediaServer.cs`），
刻意不使用 `HttpListener`——它在非 localhost 前缀下需要管理员权限注册 URL ACL。
只监听 `127.0.0.1`，外部网络无法访问。

---

## 1. 安全模型

App 首次运行会生成一个随机令牌（`settings.json` 的 `mediaServerToken`）。

1. 插件连上后**必须**先发 `hello`，其中带 `token`。
2. App 比对令牌：
   * 一致 → 回 `hello-ack`，该连接标记为已认证，后续消息才被处理；
   * 不一致 → 以关闭码 **4001** 关闭连接，并附带中文原因。
3. 未认证连接发来的其它消息一律忽略并记日志。

令牌可在 App 里「重新生成」，也可以留空（留空表示不校验，**不建议**）。

---

## 2. 消息格式

所有消息都是 UTF-8 JSON 文本帧，必须带 `type` 字段。

### 2.1 插件 → App

#### `hello`（连接后第一条）
```json
{
  "type": "hello",
  "client": "browser-extension",
  "version": "1.0.0",
  "browser": "chrome",
  "token": "<App 里显示的令牌>"
}
```

#### `add`（投递下载任务）
```json
{
  "type": "add",
  "tasks": [
    {
      "url": "https://example.com/index.m3u8",
      "kind": "hls",
      "title": "视频标题",
      "filename": "建议文件名（不带扩展名，可选）",
      "referer": "https://example.com/watch/123",
      "userAgent": "Mozilla/5.0 ...",
      "cookies": "a=b; c=d",
      "headers": { "Referer": "...", "User-Agent": "...", "Origin": "..." },
      "duration": 123,
      "thumbnail": "https://example.com/cover.jpg",
      "site": "bilibili",
      "quality": "1080p",
      "isHls": true,
      "encrypted": false,
      "codec": "hvc1",
      "hevc": true,
      "totalBytes": 278728675,
      "audioUrl": "https://.../audio.mp4",
      "needsMerge": true
    }
  ]
}
```

字段说明：

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `url` | string | 是 | 媒体地址（直链 / m3u8 / mpd / 视频页） |
| `kind` | string | 否 | `video` \| `hls` \| `file`，默认按 `isHls` 推断 |
| `title` | string | 否 | 展示用标题；为空时用解析结果或 URL |
| `filename` | string | 否 | 建议文件名，**优先于**解析器从 URL 猜出的名字 |
| `referer` | string | 否 | 防盗链需要 |
| `userAgent` | string | 否 | 覆盖默认 UA |
| `cookies` | string | 否 | 会员/登录内容需要 |
| `headers` | object | 否 | 其它请求头（`Origin` 建议放这里） |
| `duration` | number | 否 | 秒 |
| `thumbnail` | string | 否 | 封面图 |
| `site` | string | 否 | `bilibili`/`youtube`/`douyin`/`kuaishou`/`xiaohongshu`/`other` |
| `quality` | string | 否 | 例如 `1080p`，仅用于展示 |
| `isHls` | bool | 否 | 是否 HLS |
| `encrypted` | bool | 否 | 是否检测到 `#EXT-X-KEY` 等加密标记（只标记，不破解） |
| `codec` | string | 否 | 视频编码短名：`hvc1` / `hev1` / `avc1` / `vp9` / `av01`。HEVC 在部分播放器放不出来，App 会提示 |
| `codecFull` | string | 否 | 完整 codec 串（如 `hvc1.1.6.L93.B0`），仅诊断用 |
| `hevc` | bool | 否 | 是否 HEVC/H.265（`codec` 的便捷标志位） |
| `totalBytes` | number | 否 | **文件真实总长**。抖音等站点用 Range 分段拉同一个 mp4 时，单段 `Content-Length` 只是分片大小，必须用 `Content-Range` 的总长回填，否则界面会显示成"只有几 MB" |
| `audioOnly` | bool | 否 | 这一条是**独立音轨**（抖音/快手常把音视频分开给）。此时 `kind` 一律为 `file` |
| `audioUrl` | string | 否 | 与视频配套的音频地址（视频条目上才有）。App 下载完视频后用它合并 |
| `audioCodec` | string | 否 | 音频编码，仅诊断用 |
| `needsMerge` | bool | 否 | 视频轨下载完需要把 `audioUrl` 合进来（ffmpeg `-c copy`） |

### Range 分段与"一个视频只探测到一段"

抖音/快手的播放器常用 `Range` 分段拉**同一个** mp4。扩展的处理约定：

* **同 URL 不同 Range** → 合并成**一条**条目（是同一个文件），
  并用 `Content-Range: bytes a-b/TOTAL` 里的 `TOTAL` 回填 `totalBytes`；
* **同视频不同清晰度** → 是**不同**条目（URL 不同），不能合并；
* **音视频分离轨** → 是**不同**条目，音频那条标 `audioOnly: true`，
  视频那条挂 `audioUrl` + `needsMerge: true`。

> ⚠ 发给 App 的 `headers` 里**绝不能带 `Range`**：带上它 App 就只会下到那一段。

### Cookie 自动采集

扩展带 `cookies` 权限后，会按**页面域**从 `chrome.cookies` 读取 Cookie 并随任务发送
（媒体 CDN 的请求头里通常没有 Cookie，Cookie 挂在站点主域上）。

App 收到后会：
1. 本次任务直接用它做 Referer/Cookie 请求；
2. 顺手按域名存进本地缓存（DPAPI 加密），
   之后用户在程序里**直接粘贴链接**也能复用这份登录态 —— 不需要再手动填 Cookie。

> 这是"不用手填 Cookie"的实现方式：用户在浏览器里正常登录过一次站点即可。
> 设置里可以关掉（`captureBrowserCookies`）。

> 兼容写法：也可以直接给顶层 `url`（不带 `tasks`），App 会当成单个任务处理。

#### `ping`
```json
{ "type": "ping" }
```

### 2.2 App → 插件

#### `hello-ack`
```json
{
  "type": "hello-ack",
  "appVersion": "1.0.0",
  "downloadDir": "C:\\Users\\me\\Downloads\\YunsuoDownloader",
  "settings": {
    "maxConcurrentDownloads": 3,
    "connectionsPerDownload": 16,
    "preferredVideoQuality": "best",
    "autoConvertHlsToMp4": true
  }
}
```

#### `add-ack`
```json
{ "type": "add-ack", "accepted": 1, "rejected": 0, "message": "已接收 1 个任务" }
```

#### `pong`
```json
{ "type": "pong" }
```

#### `notify`（App 主动推，插件用 `chrome.notifications` 展示）
```json
{ "type": "notify", "level": "success", "message": "下载完成：xxx.mp4" }
```
`level` ∈ `info` | `success` | `error`

#### `config`（设置变化时推给插件）
```json
{ "type": "config", "downloadDir": "C:\\Users\\me\\Downloads\\YunsuoDownloader" }
```

#### `welcome`（连接建立后立刻推，无需等待 hello）
```json
{ "type": "welcome", "app": "YunsuoDownloader", "serverTime": "2026-10-17T14:56:38" }
```

---

## 3. 时序

```
插件                                           App (MediaServer + ExtensionBridge)
 │  TCP 连接 + HTTP Upgrade                        │
 │───────────────────────────────────────────────►│  101 Switching Protocols
 │◄───────────────────────────────────────────────│  {"type":"welcome",...}
 │  {"type":"hello","token":"..."}                │
 │───────────────────────────────────────────────►│  校验令牌
 │◄───────────────────────────────────────────────│  {"type":"hello-ack",...}
 │                                                │   或 关闭码 4001
 │  {"type":"ping"}   （每 20s 心跳）              │
 │───────────────────────────────────────────────►│
 │◄───────────────────────────────────────────────│  {"type":"pong"}
 │  {"type":"add","tasks":[...]}                  │
 │───────────────────────────────────────────────►│  解析 → 选引擎 → 排队
 │◄───────────────────────────────────────────────│  {"type":"add-ack",...}
 │◄───────────────────────────────────────────────│  {"type":"notify",...}（下载完成后）
```

---

## 4. 自己写一个客户端

不限于浏览器插件——任何能连 WebSocket 的程序都能用这套协议投递任务。
最小可用的 C# 示例（App 端 CLI 自检里就是这么做的，见 `Cli/MiniWebSocketClient.cs`）：

1. TCP 连 `127.0.0.1:16888`；
2. 发 `GET /media HTTP/1.1` + `Upgrade: websocket` + `Sec-WebSocket-Key`；
3. 读 `101` 响应；
4. 发 `hello`（注意：**客户端帧必须加掩码**，服务端帧不加）；
5. 发 `add`。

协议细节的回归测试在 `ysd selftest` 的第 6 项里（握手 / hello / ping / add /
真实下载完成 / 令牌错误 4001）。
