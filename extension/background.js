/*
 * background.js —— MV3 service worker。
 *
 * 职责：
 *  1. 用 chrome.webRequest 嗅探全站媒体请求（m3u8 / mpd / mp4 / webm / flv / mkv / ts / m4s…）
 *     以及 content-type 为 video/*、audio/*、application/vnd.apple.mpegurl、application/dash+xml 的响应。
 *  2. 按 tabId 缓存探测结果（内存镜像 + chrome.storage.session 持久化），刷新角标数字。
 *  3. 维护与本机「下载器」桌面应用的 WebSocket 长连接：
 *     ws://127.0.0.1:16888/media —— hello / hello-ack / ping / pong / add / add-ack / notify / config。
 *     支持指数退避重连（最大 30s）、4001 令牌错误识别、service worker 被回收后自动恢复。
 *  4. 作为 popup / options / content script 之间的消息中枢。
 *
 * MV3 注意事项：
 *  - service worker 随时可能被销毁，因此连接状态、待发送队列、探测缓存全部落 chrome.storage.session，
 *    并用 chrome.alarms（30s 周期）在空闲被唤醒时检查/恢复连接。
 *  - 不使用 eval、不加载远程代码。
 */
'use strict';

// ---------------------------------------------------------------------------
// 常量
// ---------------------------------------------------------------------------

/** 插件版本，随 manifest.json 同步 */
const EXT_VERSION = '1.0.0';
/** 客户端标识，写入 hello.client */
const CLIENT_NAME = 'browser-extension';
/** 单个标签页最多保留的媒体条目 */
const MAX_ITEMS_PER_TAB = 200;
/** 单个标签页最多保留的 HLS 分片条目（分片不计入角标） */
const MAX_FRAGMENTS_PER_TAB = 120;
/** WebSocket 重连退避上限（毫秒） */
const MAX_BACKOFF_MS = 30000;
/** 心跳间隔（毫秒），契约要求 20 秒 */
const PING_INTERVAL_MS = 20000;
/** 超过该时间没收到 pong 认为连接已死（毫秒） */
const PONG_TIMEOUT_MS = 60000;
/** 保活 alarm 名称 */
const KEEPALIVE_ALARM = 'vsd-keepalive';
/** 缓存持久化防抖（毫秒） */
const PERSIST_DEBOUNCE_MS = 400;

/** 默认设置（token 由用户在插件设置里填写） */
const DEFAULT_SETTINGS = {
  host: '127.0.0.1',
  port: 16888,
  path: '/media',
  token: '',
  autoReconnect: true,
  notify: true,
  autoSend: false,
  showBadge: true
};

/** 媒体扩展名 -> 归类 */
const EXT_KIND = {
  m3u8: { kind: 'hls', type: 'hls', isHls: true },
  mpd: { kind: 'file', type: 'dash', isHls: false },
  mp4: { kind: 'video', type: 'mp4', isHls: false },
  m4v: { kind: 'video', type: 'mp4', isHls: false },
  mov: { kind: 'video', type: 'mov', isHls: false },
  webm: { kind: 'video', type: 'webm', isHls: false },
  mkv: { kind: 'video', type: 'mkv', isHls: false },
  flv: { kind: 'video', type: 'flv', isHls: false },
  avi: { kind: 'video', type: 'avi', isHls: false },
  '3gp': { kind: 'video', type: '3gp', isHls: false },
  ts: { kind: 'file', type: 'ts', isHls: false, fragment: true },
  m4s: { kind: 'file', type: 'm4s', isHls: false, fragment: true },
  // 音频扩展名保持 kind=file（协议里 kind 只有 video/hls/file），是否"纯音轨"由 audioOnly 表达
  mp3: { kind: 'file', type: 'audio', isHls: false },
  m4a: { kind: 'file', type: 'audio', isHls: false },
  aac: { kind: 'file', type: 'audio', isHls: false },
  flac: { kind: 'file', type: 'audio', isHls: false },
  ogg: { kind: 'file', type: 'audio', isHls: false },
  ogv: { kind: 'video', type: 'ogv', isHls: false },
  wav: { kind: 'file', type: 'audio', isHls: false },
  // 裸 HEVC/H.265 码流（少见，但有的站点会把 .h265/.hevc 直接挂出来）
  h265: { kind: 'video', type: 'mp4', isHls: false },
  hevc: { kind: 'video', type: 'mp4', isHls: false },
  '265': { kind: 'video', type: 'mp4', isHls: false }
};

/** 需要嗅探的 webRequest 资源类型 */
const SNIFF_TYPES = ['xmlhttprequest', 'media', 'object', 'other', 'main_frame', 'sub_frame', 'ping'];

// ---------------------------------------------------------------------------
// 内存状态
// ---------------------------------------------------------------------------

/** tabId -> { pageUrl, page, items: Map<id, item>, updatedAt } */
const MEM = new Map();
/** requestId -> 请求上下文（请求头等），用于在 onHeadersReceived 阶段补全信息 */
const REQ_CTX = new Map();
/** 已经尝试过在后台解析播放列表的 URL，避免重复请求 */
const PARSED_PLAYLISTS = new Set();
/** 连接状态内存镜像 */
let connState = {
  state: 'disconnected', // disconnected | connecting | handshaking | connected | token-error | init-error
  appVersion: '',
  downloadDir: '',
  message: '',
  closeCode: 0,
  updatedAt: 0
};
/** WebSocket 实例与定时器 */
let socket = null;
let socketToken = 0;
let reconnectAttempt = 0;
let reconnectTimer = null;
let pingTimer = null;
let lastPongAt = 0;
let tokenError = false;

// ---------------------------------------------------------------------------
// 通用工具
// ---------------------------------------------------------------------------

/** 简单字符串哈希（用于生成稳定 id） */
function hashString(str) {
  let h = 5381;
  const s = String(str);
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) + h + s.charCodeAt(i)) | 0;
  }
  return (h >>> 0).toString(36);
}

function safeHost(url) {
  try {
    return new URL(url).hostname.toLowerCase();
  } catch (e) {
    return '';
  }
}

function pathExt(url) {
  try {
    const u = new URL(url);
    const m = /\.([a-z0-9]{2,5})$/i.exec(u.pathname);
    return m ? m[1].toLowerCase() : '';
  } catch (e) {
    const m = /\.([a-z0-9]{2,5})(?:[?#]|$)/i.exec(String(url));
    return m ? m[1].toLowerCase() : '';
  }
}

/** 去掉 query/hash 后的地址，用于去重 */
function baseUrl(url) {
  try {
    const u = new URL(url);
    u.hash = '';
    return u.href;
  } catch (e) {
    return String(url).split('#')[0];
  }
}

/** 站内目录前缀，用于把分片关联到母 m3u8 */
function dirPrefix(url) {
  const idx = String(url).lastIndexOf('/');
  return idx > 0 ? String(url).slice(0, idx + 1) : String(url);
}

/** 站点识别 */
const SITE_RULES = [
  { site: 'bilibili', hosts: ['bilibili.com', 'b23.tv', 'bilivideo.com', 'bilivideo.cn', 'hdslb.com', 'acgvideo.com'] },
  { site: 'youtube', hosts: ['youtube.com', 'youtu.be', 'googlevideo.com', 'ytimg.com', 'youtube-nocookie.com'] },
  { site: 'douyin', hosts: ['douyin.com', 'iesdouyin.com', 'douyinvod.com', 'douyinpic.com', 'douyincdn.com', 'snssdk.com'] },
  { site: 'kuaishou', hosts: ['kuaishou.com', 'gifshow.com', 'kwaicdn.com', 'kwimgs.com', 'yximgs.com', 'kuaishoupay.com'] },
  { site: 'xiaohongshu', hosts: ['xiaohongshu.com', 'xhscdn.com', 'xhslink.com', 'xhs-video.com'] }
];

function detectSite(url) {
  const host = safeHost(url);
  if (!host) return 'other';
  for (const rule of SITE_RULES) {
    for (const h of rule.hosts) {
      if (host === h || host.endsWith('.' + h)) return rule.site;
    }
  }
  return 'other';
}

/** 由高度/带宽推断清晰度标签（若 m3u8 的 NAME 已含分辨率（如 "1080P 高清"）则优先采用） */
function qualityFromHeight(height, name) {
  const named = String(name || '').trim();
  if (named && /(\d{3,4})\s*[pP]/.test(named)) return named;
  const h = parseInt(height, 10) || 0;
  if (h >= 2160) return '2160p';
  if (h >= 1440) return '1440p';
  if (h >= 1080) return '1080p';
  if (h >= 720) return '720p';
  if (h >= 576) return '576p';
  if (h >= 480) return '480p';
  if (h >= 360) return '360p';
  if (h >= 240) return '240p';
  return named;
}

/** 从 URL 里猜清晰度（如 /1080p/、_720p.mp4） */
function qualityFromUrl(url) {
  const m = /(?:^|[^0-9])(2160|1440|1080|720|576|480|360|240)[pP](?:[^0-9]|$)/.exec(String(url));
  if (m) return m[1] + 'p';
  const m2 = /(?:^|[^0-9])(2160|1440|1080|720|576|480|360|240)(?:[^0-9]|$)/.exec(String(url).split('?')[0].split('/').slice(-3).join('/'));
  return m2 ? m2[1] + 'p' : '';
}

// ---------------------------------------------------------------------------
// 编码格式（codec）识别 —— HEVC / hvc1 / hev1 / h265
// ---------------------------------------------------------------------------

/**
 * codec 家族归一化表。
 * 抖音/西瓜等站点的 HEVC 视频常以 `hvc1`（或 `hev1`）标识出现在
 * content-type 的 codecs 参数、MSE 的 addSourceBuffer(mime)、以及部分 URL 的 query 里。
 */
const CODEC_FAMILY = {
  hvc1: 'hvc1', hev1: 'hev1', hvc2: 'hvc1', hev2: 'hev1', h265: 'hvc1', hevc: 'hvc1', '265': 'hvc1',
  avc1: 'avc1', avc3: 'avc1', h264: 'avc1', '264': 'avc1',
  av01: 'av01', av1: 'av01',
  vp09: 'vp09', vp9: 'vp09', vp08: 'vp09',
  mp4a: 'mp4a', opus: 'opus', 'ac-3': 'ac3', ec3: 'eac3', 'ec-3': 'eac3', flac: 'flac', alac: 'alac'
};

/** 是否 HEVC（H.265）家族 */
function isHevcCodec(codec) {
  return codec === 'hvc1' || codec === 'hev1';
}

/**
 * 从 content-type / codecs 串解析编码信息。
 *   'video/mp4; codecs="hvc1.1.6.L93.B0"' -> { codec:'hvc1', codecFull:'hvc1.1.6.L93.B0', hevc:true, mime:'video/mp4' }
 *   'hvc1.1.6.L93.B0'                      -> 同上（无 mime）
 * @returns {{codec:string,codecFull:string,hevc:boolean,mime:string}|null}
 */
function parseCodec(text) {
  const raw = String(text || '').trim();
  if (!raw) return null;
  const lower = raw.toLowerCase();

  let mime = '';
  if (lower.indexOf('/') >= 0) mime = lower.split(';')[0].trim();

  // 优先取 codecs="..." 参数，其次把整串当成 codecs
  let codecs = '';
  const m = /codecs?\s*=\s*"?([^";]+)"?/i.exec(raw);
  if (m) codecs = m[1];
  else if (mime && lower.indexOf(';') >= 0) codecs = lower.slice(lower.indexOf(';') + 1).replace(/^[^:]*:/, '');
  else if (!mime) codecs = lower;

  const first = String(codecs).split(',')[0].trim();
  if (!first) return mime ? { codec: '', codecFull: '', hevc: false, mime: mime } : null;

  const family = CODEC_FAMILY[first.split('.')[0]] || '';
  if (!family) return mime ? { codec: '', codecFull: '', hevc: false, mime: mime } : null;
  return { codec: family, codecFull: first, hevc: isHevcCodec(family), mime: mime };
}

/** 从 URL 的 query 里找编码标识（部分站点会写 codec=hvc1 / video_codec=hev1） */
function codecFromUrl(url) {
  const s = String(url || '');
  const q = s.indexOf('?') >= 0 ? s.slice(s.indexOf('?')) : '';
  if (!q) return null;
  const m = /[?&](?:codecs?|video_codec|audio_codec|codec_type|format)=([^&#]+)/i.exec(q);
  if (!m) return null;
  try {
    return parseCodec(decodeURIComponent(m[1]));
  } catch (e) {
    return parseCodec(m[1]);
  }
}

/**
 * 抖音等站点的清晰度不写在 URL 路径里，但会带 `br=`（码率，单位 kbps）。
 * 用它给一个"可区分"的标签，避免列表里几条清晰度看起来一模一样。
 */
function qualityFromBitrate(url) {
  const m = /[?&]br=(\d+)/i.exec(String(url || ''));
  if (!m) return '';
  const kbps = parseInt(m[1], 10) || 0;
  if (kbps <= 0) return '';
  if (kbps >= 6000) return '4K·' + kbps + 'kbps';
  if (kbps >= 3000) return '2K·' + kbps + 'kbps';
  if (kbps >= 1800) return '1080p·' + kbps + 'kbps';
  if (kbps >= 900) return '720p·' + kbps + 'kbps';
  if (kbps >= 400) return '540p·' + kbps + 'kbps';
  return '360p·' + kbps + 'kbps';
}

/**
 * URL 里的容器/轨道提示。
 * 抖音的媒体地址通常没有扩展名，形如
 *   https://v3-web.douyinvod.com/.../oQxxx/?...&mime_type=video_mp4&...
 *   https://v3-web.douyinvod.com/.../oQyyy/?...&mime_type=audio_mp4&...
 * 音视频分离时靠 mime_type 区分轨道，不能都当成视频。
 */
function urlHints(url) {
  const out = { video: false, audio: false, mime: '' };
  const s = String(url || '');
  const m = /[?&](?:mime_type|mime|media_type|format)=([^&#]+)/i.exec(s);
  if (!m) return out;
  let v = m[1];
  try { v = decodeURIComponent(v); } catch (e) { /* 忽略 */ }
  v = String(v).toLowerCase().replace(/[^a-z0-9/.]/g, '');
  out.mime = v;
  if (v.indexOf('audio') === 0) out.audio = true;
  else if (v.indexOf('video') === 0) out.video = true;
  return out;
}

/** content-type -> 归类 */
function classifyByContentType(ct) {
  const t = String(ct || '').toLowerCase().split(';')[0].trim();
  if (!t) return null;
  if (t === 'application/vnd.apple.mpegurl' || t === 'application/x-mpegurl' || t === 'audio/mpegurl' || t === 'audio/x-mpegurl') {
    return { kind: 'hls', type: 'hls', isHls: true };
  }
  if (t === 'application/dash+xml') return { kind: 'file', type: 'dash', isHls: false };
  if (t.indexOf('video/') === 0) {
    if (t.indexOf('mp2t') >= 0) return { kind: 'file', type: 'ts', isHls: false, fragment: true };
    if (t.indexOf('mp4') >= 0) return { kind: 'video', type: 'mp4', isHls: false };
    if (t.indexOf('webm') >= 0) return { kind: 'video', type: 'webm', isHls: false };
    if (t.indexOf('x-flv') >= 0) return { kind: 'video', type: 'flv', isHls: false };
    return { kind: 'video', type: 'video', isHls: false };
  }
  // 音视频分离的独立音轨：audio/mp4、audio/mp4; codecs="mp4a.40.2" 等
  // 注意：协议里 kind 只有 video/hls/file，音频轨保持 kind=file + type=audio + audioOnly
  if (t.indexOf('audio/') === 0) return { kind: 'file', type: 'audio', isHls: false, audioOnly: true };
  if (t === 'application/octet-stream' || t === 'binary/octet-stream') {
    // 仅当扩展名已知时才采纳（m3u8 有时以 octet-stream 返回，扩展名会先命中）
    return null;
  }
  return null;
}

/**
 * 是否符合嗅探条件（扩展名 / content-type / URL 里的 mime_type）。
 * 抖音地址无扩展名，且 CDN 有时只回 application/octet-stream，
 * 这时只能靠 mime_type=video_mp4 / audio_mp4 认出来。
 */
function classify(url, contentType) {
  const ext = pathExt(url);
  const byExt = ext && EXT_KIND[ext];
  const byCt = classifyByContentType(contentType);
  const hints = urlHints(url);

  let merged = null;
  if (byCt && byCt.isHls) merged = { kind: 'hls', type: 'hls', isHls: true };
  else if (byExt) merged = Object.assign({}, byExt);
  else if (byCt) merged = Object.assign({}, byCt);
  else if (hints.video || hints.audio) {
    merged = hints.audio && !hints.video
      ? { kind: 'file', type: 'audio', isHls: false, audioOnly: true }
      : { kind: 'video', type: 'mp4', isHls: false };
  }
  if (!merged) return null;

  // 音频优先：扩展名是 .mp4 但 content-type 是 audio/*（或 mime_type=audio_mp4）时按音轨处理
  const audioOnly = !!(byCt && byCt.audioOnly) || (hints.audio && !hints.video) || !!merged.audioOnly;
  if (audioOnly && !merged.isHls) {
    merged.audioOnly = true;
    merged.kind = 'file';
    merged.type = 'audio';
  } else if (audioOnly) {
    merged.audioOnly = true;
  }

  // 编码格式：content-type 的 codecs 参数优先，其次 URL 的 codec= 参数
  const codec = parseCodec(contentType) || codecFromUrl(url);
  if (codec && codec.codec) {
    merged.codec = codec.codec;
    merged.codecFull = codec.codecFull || '';
    if (codec.hevc) merged.hevc = true;
  }
  return merged;
}

/** 规范化文件名（去掉非法字符，供 App 使用） */
function sanitizeFilename(name) {
  let s = String(name || '').replace(/[\r\n\t]/g, ' ').trim();
  s = s.replace(/[\\/:*?"<>|]/g, '_');
  s = s.replace(/\s+/g, ' ');
  if (s.length > 120) s = s.slice(0, 120).trim();
  return s;
}

/** 生成建议文件名（不带扩展名） */
function buildFilename(item) {
  const parts = [];
  if (item.title) parts.push(item.title);
  if (item.quality && String(item.title || '').indexOf(item.quality) === -1) parts.push(item.quality);
  let name = sanitizeFilename(parts.join('_'));
  if (!name) {
    const ext = pathExt(item.url);
    const base = String(item.url).split('?')[0].split('/').pop() || 'media';
    name = sanitizeFilename(base.replace(new RegExp('\\.' + ext + '$', 'i'), ''));
  }
  return name || 'media_' + hashString(item.url);
}

// ---------------------------------------------------------------------------
// 设置读写
// ---------------------------------------------------------------------------

let settingsCache = Object.assign({}, DEFAULT_SETTINGS);

async function loadSettings() {
  try {
    const got = await chrome.storage.local.get('settings');
    settingsCache = Object.assign({}, DEFAULT_SETTINGS, got && got.settings ? got.settings : {});
  } catch (e) {
    settingsCache = Object.assign({}, DEFAULT_SETTINGS);
  }
  return settingsCache;
}

async function saveSettings(patch) {
  settingsCache = Object.assign({}, settingsCache, patch || {});
  // 端口/地址/token 变化时重置令牌错误状态并重连
  if (patch && (Object.prototype.hasOwnProperty.call(patch, 'port') ||
      Object.prototype.hasOwnProperty.call(patch, 'host') ||
      Object.prototype.hasOwnProperty.call(patch, 'path') ||
      Object.prototype.hasOwnProperty.call(patch, 'token'))) {
    tokenError = false;
    reconnectAttempt = 0;
    forceReconnect();
  }
  await chrome.storage.local.set({ settings: settingsCache });
  return settingsCache;
}

/** 拼接 WebSocket 地址 */
function wsUrl() {
  const host = (settingsCache.host || '127.0.0.1').trim();
  const port = parseInt(settingsCache.port, 10) || DEFAULT_SETTINGS.port;
  let path = String(settingsCache.path || '/media').trim();
  if (!path.startsWith('/')) path = '/' + path;
  return 'ws://' + host + ':' + port + path;
}

// ---------------------------------------------------------------------------
// 连接状态持久化与广播
// ---------------------------------------------------------------------------

async function persistConn() {
  connState.updatedAt = Date.now();
  try {
    await chrome.storage.session.set({ conn: connState });
  } catch (e) { /* 忽略 */ }
  broadcastToExtension({ cmd: 'conn-updated', conn: connState });
}

/** 向 popup / options 广播（没有监听者时会抛错，直接吞掉） */
function broadcastToExtension(message) {
  try {
    chrome.runtime.sendMessage(message, function () {
      void chrome.runtime.lastError;
    });
  } catch (e) { /* 忽略 */ }
}

/**
 * 是否真的打开了 popup / 扩展页面。
 * 探测到媒体时若没有 UI 在听，就不必广播（否则每条嗅探结果都会唤醒所有 content script 监听器）。
 */
async function hasUiContext() {
  try {
    if (!chrome.runtime.getContexts) return false;
    const contexts = await chrome.runtime.getContexts({ contextTypes: ['POPUP', 'TAB'] });
    return !!(contexts && contexts.length);
  } catch (e) {
    return false;
  }
}

/** 限频广播：探测结果可能瞬间出现几十条，合并成一次通知 */
let mediaBroadcastTimer = null;
const mediaBroadcastTabs = new Set();

function scheduleMediaBroadcast(tabId) {
  if (typeof tabId === 'number' && tabId >= 0) mediaBroadcastTabs.add(tabId);
  if (mediaBroadcastTimer) return;
  mediaBroadcastTimer = setTimeout(function () {
    mediaBroadcastTimer = null;
    const tabs = Array.from(mediaBroadcastTabs);
    mediaBroadcastTabs.clear();
    hasUiContext().then(function (alive) {
      if (!alive) return;
      broadcastToExtension({ cmd: 'media-updated', tabIds: tabs });
    }).catch(function () { /* 忽略 */ });
  }, 600);
}

function setConnState(state, patch) {
  connState = Object.assign({}, connState, patch || {}, { state: state, updatedAt: Date.now() });
  persistConn();
}

async function restoreConn() {
  try {
    const got = await chrome.storage.session.get('conn');
    if (got && got.conn) connState = Object.assign({}, connState, got.conn);
  } catch (e) { /* 忽略 */ }
  if (connState.state === 'connected') {
    // SW 被回收过，实际连接已断开
    connState.state = 'disconnected';
    connState.message = '后台已重启，正在重新连接…';
  }
  tokenError = connState.state === 'token-error';
}

// ---------------------------------------------------------------------------
// 媒体缓存（按 tabId）
// ---------------------------------------------------------------------------

class TabBucket {
  constructor(tabId) {
    this.tabId = tabId;
    this.pageUrl = '';
    this.page = {};
    this.items = new Map();
    this.updatedAt = Date.now();
    this.persistTimer = null;
  }
}

function getBucket(tabId, create) {
  let bucket = MEM.get(tabId);
  if (!bucket && create) {
    bucket = new TabBucket(tabId);
    MEM.set(tabId, bucket);
  }
  return bucket;
}

/** 从 storage.session 载入某标签页缓存（SW 被回收后恢复） */
async function loadBucket(tabId) {
  let bucket = getBucket(tabId, true);
  if (bucket.loaded) return bucket;
  try {
    const key = 'tab:' + tabId;
    const got = await chrome.storage.session.get(key);
    const data = got && got[key];
    if (data) {
      bucket.pageUrl = data.pageUrl || '';
      bucket.page = data.page || {};
      bucket.items = new Map();
      (data.items || []).forEach(function (it) {
        if (it && it.id) bucket.items.set(it.id, it);
      });
      bucket.updatedAt = data.updatedAt || Date.now();
    }
  } catch (e) { /* 忽略 */ }
  bucket.loaded = true;
  return bucket;
}

function schedulePersist(bucket) {
  if (bucket.persistTimer) return;
  bucket.persistTimer = setTimeout(function () {
    bucket.persistTimer = null;
    persistBucket(bucket);
  }, PERSIST_DEBOUNCE_MS);
}

async function persistBucket(bucket) {
  try {
    if (!bucket.items.size) {
      await chrome.storage.session.remove('tab:' + bucket.tabId);
      return;
    }
    const data = {
      pageUrl: bucket.pageUrl,
      page: bucket.page,
      updatedAt: bucket.updatedAt,
      items: Array.from(bucket.items.values())
    };
    const payload = {};
    payload['tab:' + bucket.tabId] = data;
    await chrome.storage.session.set(payload);
  } catch (e) { /* 忽略 */ }
}

/** 生成条目 id */
function itemId(item) {
  if (String(item.url).indexOf('blob:') === 0) {
    return 'blob|' + item.url + '|' + (item.via || '');
  }
  return baseUrl(item.url);
}

/**
 * 写入/合并一条媒体记录。
 * @param {number} tabId
 * @param {object} incoming 原始条目
 * @param {string} via 来源：webRequest / inject / dom
 */
async function recordMedia(tabId, incoming, via) {
  if (!tabId || tabId < 0 || !incoming || !incoming.url) return;
  const bucket = await loadBucket(tabId);
  const id = itemId(incoming);

  // 页面跳转后的陈旧数据保护
  if (incoming.pageUrl && bucket.pageUrl && incoming.pageUrl !== bucket.pageUrl) {
    const sameOrigin = (function () {
      try { return new URL(incoming.pageUrl).origin === new URL(bucket.pageUrl).origin; }
      catch (e) { return false; }
    })();
    // 注意：这里必须"原地"清空（不能删 MEM 里的 bucket），否则紧接着写入的这条记录会丢
    if (!sameOrigin) await resetBucketInPlace(tabId, bucket);
    bucket.pageUrl = incoming.pageUrl;
  }
  if (incoming.pageUrl && !bucket.pageUrl) bucket.pageUrl = incoming.pageUrl;

  // 媒体请求往往不带 Cookie（Cookie 挂在站点主域上），从页面域补一份，
  // 这样 App 侧不需要用户手动粘贴 Cookie 也能拿到登录态。
  if (!incoming.cookies && bucket.pageUrl) {
    if (!incoming.pageUrl) incoming.pageUrl = bucket.pageUrl;
    await fillCookiesFromPage(tabId, incoming);
  }

  const existing = bucket.items.get(id);
  const isFragment = !!(incoming.fragment || (EXT_KIND[pathExt(incoming.url)] || {}).fragment);

  if (existing) {
    mergeInto(existing, incoming, via, isFragment);
  } else {
    const item = Object.assign({}, incoming, {
      id: id,
      tabId: tabId,
      site: incoming.site || detectSite(incoming.pageUrl || incoming.url),
      firstSeen: Date.now(),
      sources: via ? [via] : []
    });
    item.lastSeen = Date.now();
    if (!item.quality) item.quality = qualityFromUrl(item.url);
    // 抖音等站点的清晰度不在 URL 里，退而用 br=（码率）标一档，至少能区分不同清晰度
    if (!item.quality && !item.audioOnly) item.quality = qualityFromBitrate(item.url);
    if (!item.title && bucket.page && bucket.page.title) item.title = bucket.page.title;
    if (!item.thumbnail && bucket.page && bucket.page.thumbnail) item.thumbnail = bucket.page.thumbnail;
    if (!item.duration && bucket.page && bucket.page.duration) item.duration = bucket.page.duration;
    if (!item.userAgent && bucket.page && bucket.page.userAgent) item.userAgent = bucket.page.userAgent;
    if (!item.referer) item.referer = bucket.pageUrl || (bucket.page && bucket.page.url) || '';
    if (!item.cookies && bucket.page && bucket.page.cookies) item.cookies = bucket.page.cookies;
    item.isFragment = !!isFragment;
    if (item.partial || item.isSegmentOfFile) item.rangeCount = 1;
    bucket.items.set(id, item);
  }

  // 母 m3u8 关联分片
  const stored = bucket.items.get(id);
  if (stored && stored.isFragment && !stored.parentUrl) {
    for (const other of bucket.items.values()) {
      if (!other.isFragment && other.isHls && dirPrefix(other.url) === dirPrefix(stored.url)) {
        stored.parentUrl = other.url;
        break;
      }
    }
  }

  // 音视频分离轨：把独立音频挂到同页面视频条目上（App 侧用 ffmpeg 合并）
  if (stored) linkAudioTracks(bucket);

  pruneBucket(bucket);
  bucket.updatedAt = Date.now();
  schedulePersist(bucket);
  updateBadge(tabId);
  scheduleMediaBroadcast(tabId);

  // 后台补一次播放列表解析（MAIN world 未能拿到响应体时）
  if (stored && !stored.isFragment && (stored.isHls || stored.type === 'dash') &&
      (!stored.variants || !stored.variants.length) && !PARSED_PLAYLISTS.has(stored.url)) {
    PARSED_PLAYLISTS.add(stored.url);
    parsePlaylistInBackground(tabId, stored).catch(function () { /* 忽略 */ });
  }
}

/** 合并新旧条目：新信息更"富"时覆盖，否则仅补空字段 */
function mergeInto(existing, incoming, via, isFragment) {
  // 这些布尔标记只会从 false 变成 true（例如后台解析后才发现 #EXT-X-KEY 加密）
  const boolFields = ['isHls', 'encrypted', 'isBlob', 'mse', 'isMaster', 'isMedia', 'isFragment',
    'hevc', 'audioOnly', 'partial', 'isSegmentOfFile', 'needsMerge'];
  const fields = ['kind', 'type', 'isHls', 'encrypted', 'contentType', 'size', 'status',
    'method', 'title', 'thumbnail', 'duration', 'quality', 'isBlob', 'mse', 'mime',
    'playlistType', 'parentUrl', 'pageUrl', 'referer', 'userAgent', 'cookies', 'variants',
    'variantCount', 'segmentCount', 'isMaster', 'isMedia', 'site', 'host',
    'codec', 'codecFull', 'totalBytes', 'byteStart', 'byteEnd', 'range', 'audioUrl', 'audioCodec',
    'hevc', 'audioOnly', 'partial', 'isSegmentOfFile', 'needsMerge'];
  for (const f of fields) {
    const v = incoming[f];
    if (v === undefined || v === null || v === '') continue;
    if (Array.isArray(v) && !v.length) continue;
    if (boolFields.indexOf(f) >= 0) {
      if (v) existing[f] = true;
      continue;
    }
    // 总长只增不减：Content-Range 的总长是权威值，不能被后面某个 200 响应的
    // content-length（可能只是其中一段/被 CDN 改写）覆盖成更小的数
    if (f === 'totalBytes') {
      const cur = parseInt(existing.totalBytes, 10) || 0;
      const next = parseInt(v, 10) || 0;
      if (next > cur) existing.totalBytes = next;
      continue;
    }
    if (f === 'size') {
      // 有 totalBytes 时 size 跟着总长走；否则只补空/取更大的值（避免分段把大小改小）
      const cur = parseInt(existing[f], 10) || 0;
      const next = parseInt(v, 10) || 0;
      const total = parseInt(existing.totalBytes, 10) || 0;
      if (total > 0) { existing.size = total; continue; }
      if (!cur || next > cur) existing.size = next;
      continue;
    }
    if (existing[f] === undefined || existing[f] === null || existing[f] === '' ||
        (Array.isArray(existing[f]) && !existing[f].length)) {
      existing[f] = v;
    } else if (f === 'variants' && Array.isArray(v) && v.length) {
      // 变体列表以"更多"为准
      if (!Array.isArray(existing.variants) || v.length > existing.variants.length) existing.variants = v;
    }
  }
  // 分段计数：同一条目的多次 Range 请求累加（用于诊断"播放器分了几段拉"）
  if (incoming.range || incoming.isSegmentOfFile || incoming.partial) {
    existing.rangeCount = (parseInt(existing.rangeCount, 10) || 0) + 1;
  }
  // 请求头做并集，保留最完整的一份
  if (incoming.headers && typeof incoming.headers === 'object') {
    existing.headers = Object.assign({}, existing.headers || {}, incoming.headers);
  }
  // Range 永远不留在转发头里
  if (existing.headers) {
    Object.keys(existing.headers).forEach(function (k) {
      if (String(k).toLowerCase() === 'range') delete existing.headers[k];
    });
  }
  // 同一 URL 的多段读取 = 同一个文件：永远不因为"分段"而变成分片条目
  if (existing.isSegmentOfFile) existing.isFragment = !!existing.fragment || !!existing.isFragment;
  if (isFragment) existing.isFragment = true;
  if (Array.isArray(existing.variants) && existing.variants.length) {
    existing.variantCount = existing.variants.length;
    const best = existing.variants.reduce(function (a, b) {
      const ah = a && a.height ? a.height : 0;
      const bh = b && b.height ? b.height : 0;
      return bh > ah ? b : a;
    }, existing.variants[0]);
    if (best && best.quality && !existing.quality) existing.quality = best.quality;
    if (!existing.encrypted) {
      existing.encrypted = existing.variants.some(function (x) { return x && x.encrypted; });
    }
  }
  if (via && Array.isArray(existing.sources) && existing.sources.indexOf(via) === -1) {
    existing.sources.push(via);
  }
  if (isFragment) existing.isFragment = true;
  // 先按视频记下、后来响应头才表明是独立音轨：归一到音频
  if (existing.audioOnly && !existing.isHls) {
    existing.kind = 'file';
    existing.type = 'audio';
  }
  // 有了总长就以总长为展示大小
  if (parseInt(existing.totalBytes, 10) > 0) existing.size = parseInt(existing.totalBytes, 10);
  existing.lastSeen = Date.now();
}

/**
 * 音视频分离轨关联：把独立的音频条目挂到同页面的视频条目上（item.audioUrl）。
 * 只在"同站点 + 视频还没有音轨"时关联，且同一音频只挂一次，
 * 避免同一页面多个视频互相抢音轨。
 */
function linkAudioTracks(bucket) {
  const videos = [];
  const audios = [];
  for (const it of bucket.items.values()) {
    if (it.isFragment || it.isBlob) continue;
    if (it.audioOnly) audios.push(it);
    else if (it.kind === 'video' && !it.isHls) videos.push(it);
  }
  if (!videos.length || !audios.length) return;
  const used = new Set();
  for (const v of videos) {
    if (v.audioUrl) { used.add(v.audioUrl); continue; }
    // 优先同 host，其次同站点
    let pick = audios.find(function (a) {
      return !used.has(a.url) && a.host && a.host === v.host;
    });
    if (!pick) {
      pick = audios.find(function (a) {
        return !used.has(a.url) && (!a.site || !v.site || a.site === v.site);
      });
    }
    if (!pick) continue;
    used.add(pick.url);
    v.audioUrl = pick.url;
    if (pick.codec) v.audioCodec = pick.codec;
    v.needsMerge = true;
    pick.linkedVideoUrl = v.url;
  }
  // 视频后来才出现时，允许重新挂（上面对已挂的只做 used 登记，不会重复挂）
}

/** 控制单标签页条目规模：优先淘汰旧分片，再淘汰最久未见条目 */
function pruneBucket(bucket) {
  const fragments = [];
  for (const it of bucket.items.values()) if (it.isFragment) fragments.push(it);
  if (fragments.length > MAX_FRAGMENTS_PER_TAB) {
    fragments.sort(function (a, b) { return (a.lastSeen || 0) - (b.lastSeen || 0); });
    const drop = fragments.length - MAX_FRAGMENTS_PER_TAB;
    for (let i = 0; i < drop; i++) bucket.items.delete(fragments[i].id);
  }
  if (bucket.items.size > MAX_ITEMS_PER_TAB) {
    const all = Array.from(bucket.items.values()).sort(function (a, b) {
      const af = a.isFragment ? 0 : 1;
      const bf = b.isFragment ? 0 : 1;
      if (af !== bf) return af - bf; // 先删分片
      return (a.lastSeen || 0) - (b.lastSeen || 0);
    });
    const drop = bucket.items.size - MAX_ITEMS_PER_TAB;
    for (let i = 0; i < drop; i++) bucket.items.delete(all[i].id);
  }
}

/** 清空某标签页缓存（popup 的「清空列表」、标签页关闭/跳转时使用） */
async function clearTab(tabId, options) {
  const opts = options || {};
  const bucket = MEM.get(tabId);
  if (bucket) {
    if (bucket.persistTimer) clearTimeout(bucket.persistTimer);
    bucket.items.clear();
    bucket.page = {};
    bucket.updatedAt = Date.now();
  }
  try {
    await chrome.storage.session.remove('tab:' + tabId);
  } catch (e) { /* 忽略 */ }
  MEM.delete(tabId);
  updateBadge(tabId);
  if (!opts.silent) broadcastToExtension({ cmd: 'media-updated', tabId: tabId, cleared: true });
}

/** 原地清空 bucket（保留 MEM 引用，避免正在写入的记录被丢弃） */
async function resetBucketInPlace(tabId, bucket) {
  if (bucket.persistTimer) {
    clearTimeout(bucket.persistTimer);
    bucket.persistTimer = null;
  }
  bucket.items.clear();
  bucket.page = {};
  bucket.updatedAt = Date.now();
  try {
    await chrome.storage.session.remove('tab:' + tabId);
  } catch (e) { /* 忽略 */ }
  updateBadge(tabId);
}

/** 更新角标（非分片条目数量） */
function updateBadge(tabId) {
  if (!settingsCache.showBadge) {
    safeBadge(tabId, '');
    return;
  }
  const bucket = MEM.get(tabId);
  if (!bucket) {
    safeBadge(tabId, '');
    return;
  }
  let count = 0;
  for (const it of bucket.items.values()) {
    if (it.isFragment) continue;
    if (it.isBlob && String(it.url).indexOf('blob:mse') === 0) continue;
    count++;
  }
  safeBadge(tabId, count > 0 ? (count > 99 ? '99+' : String(count)) : '');
}

function safeBadge(tabId, text) {
  try {
    const arg = { text: text };
    if (typeof tabId === 'number' && tabId >= 0) arg.tabId = tabId;
    chrome.action.setBadgeText(arg, function () { void chrome.runtime.lastError; });
  } catch (e) { /* 忽略 */ }
}

/** 读取某标签页的媒体列表（供 popup 使用） */
async function getTabMedia(tabId) {
  const bucket = await loadBucket(tabId);
  const items = Array.from(bucket.items.values()).sort(function (a, b) {
    // 主媒体优先（非分片），其次按高度/发现时间
    const af = a.isFragment ? 1 : 0;
    const bf = b.isFragment ? 1 : 0;
    if (af !== bf) return af - bf;
    const ah = bestHeight(a);
    const bh = bestHeight(b);
    if (ah !== bh) return bh - ah;
    return (a.firstSeen || 0) - (b.firstSeen || 0);
  });
  const hasHls = items.some(function (it) { return it.isHls && !it.isFragment; });
  return {
    tabId: tabId,
    pageUrl: bucket.pageUrl,
    page: bucket.page,
    hasHls: hasHls,
    items: items
  };
}

function bestHeight(item) {
  if (item.variants && item.variants.length) {
    return item.variants.reduce(function (max, v) { return Math.max(max, (v && v.height) || 0); }, 0);
  }
  if (item.height) return item.height;
  const q = qualityFromUrl(item.quality || '') || (item.quality || '');
  const m = /(\d{3,4})/.exec(String(q));
  return m ? parseInt(m[1], 10) : 0;
}

// ---------------------------------------------------------------------------
// 后台播放列表解析（best effort）
// ---------------------------------------------------------------------------

/** 解析 m3u8 属性行 */
function parseAttrs(text) {
  const out = {};
  const re = /([A-Za-z0-9-]+)=("[^"]*"|[^,]*)/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    let v = m[2];
    if (v.charAt(0) === '"') v = v.slice(1, -1);
    out[m[1].toUpperCase()] = v.trim();
  }
  return out;
}

/** 解析 m3u8 文本（与 content/inject.js 保持一致的输出结构） */
function parseM3U8Text(text, base) {
  const result = { isMaster: false, isMedia: false, variants: [], encrypted: false, keyMethod: '', segmentCount: 0 };
  if (!text || typeof text !== 'string') return result;
  const lines = text.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    if (!line) continue;
    if (line.indexOf('#EXT-X-KEY') === 0) {
      const km = /METHOD=([^,]+)/i.exec(line);
      if (km && !/^NONE$/i.test(km[1].trim())) {
        result.encrypted = true;
        result.keyMethod = km[1].trim();
      }
    } else if (line.indexOf('#EXT-X-STREAM-INF') === 0) {
      const attrs = parseAttrs(line.slice(line.indexOf(':') + 1));
      let uri = '';
      for (let j = i + 1; j < lines.length; j++) {
        const next = lines[j].trim();
        if (!next || next.charAt(0) === '#') continue;
        uri = next;
        break;
      }
      const rm = /(\d+)\s*[xX]\s*(\d+)/.exec(attrs.RESOLUTION || '');
      const height = rm ? parseInt(rm[2], 10) : 0;
      const width = rm ? parseInt(rm[1], 10) : 0;
      let abs = '';
      try { abs = new URL(uri || base, base).href; } catch (e) { abs = base; }
      result.variants.push({
        url: abs,
        bandwidth: parseInt(attrs.BANDWIDTH || attrs['AVERAGE-BANDWIDTH'] || '0', 10) || 0,
        resolution: attrs.RESOLUTION || '',
        width: width,
        height: height,
        name: attrs.NAME || '',
        codecs: attrs.CODECS || '',
        quality: qualityFromHeight(height, attrs.NAME || '')
      });
    } else if (line.indexOf('#EXTINF') === 0) {
      result.isMedia = true;
      result.segmentCount++;
    }
  }
  result.isMaster = result.variants.length > 0;
  result.variants.sort(function (a, b) { return (b.height - a.height) || (b.bandwidth - a.bandwidth); });
  return result;
}

/** 解析 mpd 文本 */
function parseMPDText(text) {
  const out = { variants: [], encrypted: false };
  if (!text) return out;
  try {
    if (/<ContentProtection/i.test(text)) out.encrypted = true;
    const re = /<Representation\b[^>]*>/gi;
    let m;
    while ((m = re.exec(text)) !== null) {
      const tag = m[0];
      const w = /\bwidth="(\d+)"/i.exec(tag);
      const h = /\bheight="(\d+)"/i.exec(tag);
      const bw = /\bbandwidth="(\d+)"/i.exec(tag);
      const height = h ? parseInt(h[1], 10) : 0;
      out.variants.push({
        url: '',
        bandwidth: bw ? parseInt(bw[1], 10) : 0,
        resolution: (w && h) ? (w[1] + 'x' + h[1]) : '',
        width: w ? parseInt(w[1], 10) : 0,
        height: height,
        name: '',
        codecs: '',
        quality: qualityFromHeight(height, '')
      });
    }
    out.variants.sort(function (a, b) { return (b.height - a.height) || (b.bandwidth - a.bandwidth); });
  } catch (e) { /* 忽略 */ }
  return out;
}

/**
 * 在后台 fetch 播放列表并解析（MAIN world 拿不到响应体时的兜底）。
 * 说明：扩展发起的请求不受页面 CORS 限制（依赖 host_permissions），
 * 但 Referer 属于禁止设置的请求头，部分站点会因此拒绝——失败即静默放弃。
 */
async function parsePlaylistInBackground(tabId, item) {
  if (!item || !item.url) return;
  if (String(item.url).indexOf('blob:') === 0) return;
  try {
    const resp = await fetch(item.url, {
      method: 'GET',
      credentials: 'include',
      cache: 'no-store',
      redirect: 'follow'
    });
    if (!resp.ok) return;
    const declaredLength = parseInt(resp.headers.get('content-length') || '0', 10) || 0;
    if (declaredLength > 4 * 1024 * 1024) return; // 明显不是播放列表，放弃
    const text = await resp.text();
    if (!text || text.length > 4 * 1024 * 1024) return;
    const playlist = item.isHls ? parseM3U8Text(text, item.url) : parseMPDText(text);
    if (!playlist.variants.length && !playlist.isMedia && !playlist.encrypted) return;
    await recordMedia(tabId, {
      url: item.url,
      pageUrl: item.pageUrl,
      variants: playlist.variants,
      variantCount: playlist.variants.length,
      isMaster: !!playlist.isMaster,
      isMedia: !!playlist.isMedia,
      segmentCount: playlist.segmentCount || 0,
      encrypted: !!playlist.encrypted,
      keyMethod: playlist.keyMethod || '',
      quality: (playlist.variants[0] && playlist.variants[0].quality) || item.quality,
      contentType: resp.headers.get('content-type') || item.contentType
    }, 'background-parse');
  } catch (e) {
    // 解析失败属于常态（需要 Referer/Cookie 的站点），静默忽略
  }
}

// ---------------------------------------------------------------------------
// webRequest 嗅探
// ---------------------------------------------------------------------------

/** 记录请求头上下文 */
function onBeforeSendHeaders(details) {
  try {
    if (!details || !details.url) return;
    const headers = {};
    let range = '';
    (details.requestHeaders || []).forEach(function (h) {
      if (!h || !h.name) return;
      const name = h.name.toLowerCase();
      if (name === 'referer' || name === 'cookie' || name === 'origin' ||
          name === 'user-agent' || name === 'authorization' || name === 'range') {
        headers[h.name] = h.value || '';
        // Range 不写进 headers（转发给 App 时会让下载器只拿到一段），
        // 但要单独记下来，用于判断"同一 URL 的分段读取"并回填总长。
        if (name === 'range') range = h.value || '';
      }
    });
    REQ_CTX.set(details.requestId, {
      tabId: details.tabId,
      url: details.url,
      method: details.method,
      headers: headers,
      range: range,
      at: Date.now()
    });
    // 控制上下文 Map 规模
    if (REQ_CTX.size > 2000) {
      const cutoff = Date.now() - 60000;
      for (const [key, value] of REQ_CTX) {
        if (value.at < cutoff) REQ_CTX.delete(key);
      }
    }
  } catch (e) { /* 忽略 */ }
}

/**
 * 解析 Content-Range：`bytes 0-999/12345` / `bytes 1000-1999/*`
 * @returns {{start:number,end:number,total:number}|null}
 */
function parseContentRange(value) {
  const m = /bytes\s+(\d+)-(\d+)\/(\d+|\*)/i.exec(String(value || ''));
  if (!m) return null;
  return {
    start: parseInt(m[1], 10) || 0,
    end: parseInt(m[2], 10) || 0,
    total: m[3] === '*' ? 0 : (parseInt(m[3], 10) || 0)
  };
}

/** 解析请求 Range：`bytes=0-999`（只取起点，用于判断是否是"从头开始的一段"） */
function parseRequestRange(value) {
  const m = /bytes\s*=\s*(\d+)-(\d*)/i.exec(String(value || ''));
  if (!m) return null;
  return { start: parseInt(m[1], 10) || 0, end: m[2] === '' ? 0 : (parseInt(m[2], 10) || 0) };
}

/** 请求发起阶段：命中扩展名即记录（此时还没有 content-type） */
function onBeforeRequest(details) {
  try {
    if (!details || !details.url || details.tabId < 0) return;
    const kind = classify(details.url, '');
    if (!kind) return;
    const ctx = REQ_CTX.get(details.requestId) || {};
    recordMedia(details.tabId, buildItem(details.url, kind, ctx, details), 'webRequest');
  } catch (e) { /* 忽略 */ }
}

/** 响应头阶段：content-type 命中或扩展名命中都记录，并补全大小/类型 */
function onHeadersReceived(details) {
  try {
    if (!details || !details.url || details.tabId < 0) return;
    const ctx = REQ_CTX.get(details.requestId) || {};
    let contentType = '';
    let contentLength = 0;
    let contentRange = null;
    (details.responseHeaders || []).forEach(function (h) {
      if (!h || !h.name) return;
      const name = h.name.toLowerCase();
      if (name === 'content-type') contentType = h.value || '';
      else if (name === 'content-length') contentLength = parseInt(h.value, 10) || 0;
      else if (name === 'content-range') contentRange = parseContentRange(h.value);
    });
    const kind = classify(details.url, contentType);
    if (!kind) {
      REQ_CTX.delete(details.requestId);
      return;
    }
    const item = buildItem(details.url, kind, ctx, details);
    item.contentType = contentType;
    item.size = contentLength;
    item.status = details.statusCode;

    // ---- Range / Content-Range：分段读取要合并成一条，并用总长回填 size ----
    const reqRange = ctx.range ? parseRequestRange(ctx.range) : null;
    const isPartial = details.statusCode === 206 || !!contentRange || !!reqRange;
    if (contentRange && contentRange.total > 0) {
      // 服务器给了总长：这才是文件真实大小，content-length 只是这一段的长度
      item.totalBytes = contentRange.total;
      item.size = contentRange.total;
      item.byteStart = contentRange.start;
      item.byteEnd = contentRange.end;
    } else if (reqRange) {
      item.byteStart = reqRange.start;
      if (reqRange.end) item.byteEnd = reqRange.end;
    }
    item.partial = isPartial;
    item.range = ctx.range || '';
    // 分段读取不是"分片文件"（不是 .ts/.m4s 那种独立片段），不能标记 isFragment
    if (isPartial && !item.isFragment) {
      item.isSegmentOfFile = true;
    }

    recordMedia(details.tabId, item, 'webRequest');
    REQ_CTX.delete(details.requestId);
  } catch (e) { /* 忽略 */ }
}

/**
 * 从页面域补齐 Cookie。
 *
 * 为什么需要：媒体请求（尤其是抖音/快手这类 CDN）往往**不带 Cookie**，
 * Cookie 挂在站点主域（douyin.com）上。以前只读请求头里的 Cookie，
 * 结果送过去的是空串，App 侧拿不到登录态，yt-dlp 就会报
 * "Fresh cookies (not necessarily logged in) are needed"。
 *
 * 这里按页面 URL 的域去 chrome.cookies 里取，拼成 "a=1; b=2" 的形式。
 * 取不到就原样返回（不影响没有 Cookie 也能下的站点）。
 */
async function fillCookiesFromPage(tabId, item) {
  try {
    if (!item || item.cookies) return item;            // 请求头里已经带了就不动
    if (!chrome.cookies || !chrome.cookies.getAll) return item;
    const pageUrl = item.pageUrl || item.referer || '';
    if (!/^https?:/i.test(pageUrl)) return item;

    const list = await chrome.cookies.getAll({ url: pageUrl });
    if (!list || !list.length) return item;

    const header = list
      .filter(function (c) { return c && c.name; })
      .map(function (c) { return c.name + '=' + (c.value || ''); })
      .join('; ');
    if (header) {
      item.cookies = header;
      item.cookiesFrom = 'page';
    }
  } catch (e) { /* 没有 cookies 权限或页面无 cookie：静默跳过 */ }
  return item;
}

/** 组装嗅探到的条目 */
function buildItem(url, kind, ctx, details) {
  const headers = {};
  const raw = (ctx && ctx.headers) || {};
  Object.keys(raw).forEach(function (k) {
    // Range 不转发给下载器：带上它 App 只会下到一段
    if (String(k).toLowerCase() === 'range') return;
    if (raw[k]) headers[k] = raw[k];
  });
  const cookieHeader = raw['Cookie'] || raw['cookie'] || '';
  const referer = raw['Referer'] || raw['referer'] || '';
  const codec = (kind && kind.codec) ? { codec: kind.codec, codecFull: kind.codecFull || '', hevc: !!kind.hevc }
    : (parseCodec(kind && kind.contentType) || codecFromUrl(url));
  const item = {
    url: url,
    kind: kind.kind,
    type: kind.type,
    isHls: !!kind.isHls,
    encrypted: false,
    fragment: !!kind.fragment,
    isFragment: !!kind.fragment,
    method: (ctx && ctx.method) || (details && details.method) || 'GET',
    host: safeHost(url),
    via: 'webRequest',
    headers: headers,
    cookies: cookieHeader,
    referer: referer,
    userAgent: raw['User-Agent'] || raw['user-agent'] || (typeof navigator !== 'undefined' ? navigator.userAgent : ''),
    quality: qualityFromUrl(url),
    // 页面地址优先取 Referer（媒体 CDN 域名与站点域名往往不同，靠它才能正确判断 site）
    pageUrl: /^https?:/i.test(referer) ? referer : '',
    discoveredAt: Date.now()
  };
  if (kind && kind.audioOnly) item.audioOnly = true;
  if (codec && codec.codec) {
    item.codec = codec.codec;
    item.codecFull = codec.codecFull || '';
    if (codec.hevc) item.hevc = true;
  }
  return item;
}

/** 注册 webRequest 监听（extraHeaders 不可用时降级） */
function registerWebRequest() {
  const filter = { urls: ['<all_urls>'], types: SNIFF_TYPES };

  try {
    chrome.webRequest.onBeforeRequest.addListener(onBeforeRequest, filter);
  } catch (e) {
    try { chrome.webRequest.onBeforeRequest.addListener(onBeforeRequest, { urls: ['<all_urls>'] }); }
    catch (e2) { /* 忽略 */ }
  }

  try {
    chrome.webRequest.onBeforeSendHeaders.addListener(
      onBeforeSendHeaders, filter, ['requestHeaders', 'extraHeaders']);
  } catch (e) {
    try {
      chrome.webRequest.onBeforeSendHeaders.addListener(onBeforeSendHeaders, filter, ['requestHeaders']);
    } catch (e2) { /* 忽略 */ }
  }

  try {
    chrome.webRequest.onHeadersReceived.addListener(
      onHeadersReceived, filter, ['responseHeaders', 'extraHeaders']);
  } catch (e) {
    try {
      chrome.webRequest.onHeadersReceived.addListener(onHeadersReceived, filter, ['responseHeaders']);
    } catch (e2) { /* 忽略 */ }
  }
}

// ---------------------------------------------------------------------------
// WebSocket 客户端（与桌面 App 通信）
// ---------------------------------------------------------------------------

/** 发送一条 JSON 消息；未连接返回 false */
function sendRaw(obj) {
  if (!socket || socket.readyState !== WebSocket.OPEN) return false;
  try {
    socket.send(JSON.stringify(obj));
    return true;
  } catch (e) {
    return false;
  }
}

function connect() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  if (tokenError) return; // 令牌错误时不自动重连，等用户改设置或点重试

  const url = wsUrl();
  const myToken = ++socketToken; // 用于忽略旧 socket 的回调
  setConnState('connecting', { message: '正在连接 ' + url, closeCode: 0 });

  let ws;
  try {
    ws = new WebSocket(url);
  } catch (e) {
    setConnState('disconnected', { message: '地址无效：' + url });
    scheduleReconnect();
    return;
  }
  socket = ws;

  ws.onopen = function () {
    if (myToken !== socketToken) return;
    setConnState('handshaking', { message: '已连接，正在握手…' });
    const hello = {
      type: 'hello',
      client: CLIENT_NAME,
      version: EXT_VERSION,
      browser: detectBrowserName(),
      token: settingsCache.token || ''
    };
    sendRaw(hello);
    startPing();
  };

  ws.onmessage = function (event) {
    if (myToken !== socketToken) return;
    let msg = null;
    try {
      msg = JSON.parse(event.data);
    } catch (e) {
      return;
    }
    if (!msg || !msg.type) return;
    handleAppMessage(msg);
  };

  ws.onerror = function () {
    // 具体错误信息由 onclose 处理
  };

  ws.onclose = function (event) {
    if (myToken !== socketToken) return;
    stopPing();
    socket = null;
    const code = event && event.code ? event.code : 0;
    lastPongAt = 0;

    if (code === 4001) {
      // 契约：令牌不匹配时 App 以 4001 关闭连接
      tokenError = true;
      setConnState('token-error', {
        closeCode: 4001,
        message: '令牌不匹配，请在下载器 设置→浏览器插件 中复制令牌'
      });
      notify('令牌不匹配', '请在下载器「设置 → 浏览器插件」中复制令牌，粘贴到本插件的设置页。', 'error');
      return;
    }

    setConnState('disconnected', {
      closeCode: code,
      message: settingsCache.autoReconnect ? '连接已断开，正在重连…' : '连接已断开'
    });
    scheduleReconnect();
  };
}

/** 强制重连（设置变更或用户手动点重试） */
function forceReconnect() {
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  stopPing();
  socketToken++; // 使旧 socket 回调失效
  if (socket) {
    try { socket.close(1000, 'client-reconnect'); } catch (e) { /* 忽略 */ }
  }
  socket = null;
  reconnectAttempt = 0;
  connect();
}

/** 指数退避重连：1s,2s,4s,8s,16s,30s（上限 30s）+ 抖动 */
function scheduleReconnect() {
  if (!settingsCache.autoReconnect) return;
  if (tokenError) return;
  if (reconnectTimer) return;

  reconnectAttempt = Math.min(reconnectAttempt + 1, 20);
  const base = Math.min(MAX_BACKOFF_MS, 1000 * Math.pow(2, Math.min(reconnectAttempt - 1, 5)));
  const jitter = Math.floor(Math.random() * 400);
  const delay = Math.min(MAX_BACKOFF_MS, base + jitter);

  reconnectTimer = setTimeout(function () {
    reconnectTimer = null;
    connect();
  }, delay);
}

function startPing() {
  stopPing();
  lastPongAt = Date.now();
  pingTimer = setInterval(function () {
    if (!socket || socket.readyState !== WebSocket.OPEN) return;
    if (lastPongAt && Date.now() - lastPongAt > PONG_TIMEOUT_MS) {
      // 心跳超时：连接处于半死状态，主动重建
      forceReconnect();
      return;
    }
    sendRaw({ type: 'ping' });
  }, PING_INTERVAL_MS);
}

function stopPing() {
  if (pingTimer) {
    clearInterval(pingTimer);
    pingTimer = null;
  }
}

/** 识别浏览器名，写入 hello.browser */
function detectBrowserName() {
  try {
    const ua = navigator.userAgent || '';
    if (/Edg\//.test(ua)) return 'edge';
    if (/OPR\//.test(ua)) return 'opera';
    if (/Brave/.test(ua)) return 'brave';
    if (/Chrome\//.test(ua)) return 'chrome';
    return 'chromium';
  } catch (e) {
    return 'chrome';
  }
}

/** 处理 App 下发/回复的消息 */
function handleAppMessage(msg) {
  switch (msg.type) {
    case 'hello-ack':
      reconnectAttempt = 0;
      tokenError = false;
      setConnState('connected', {
        appVersion: msg.appVersion || '',
        downloadDir: msg.downloadDir || '',
        message: '已连接下载器' + (msg.appVersion ? ' v' + msg.appVersion : ''),
        closeCode: 0
      });
      flushPendingTasks().catch(function () { /* 忽略 */ });
      break;

    case 'pong':
      lastPongAt = Date.now();
      break;

    case 'add-ack': {
      const accepted = msg.accepted || 0;
      const rejected = msg.rejected || 0;
      let text = '下载器已接收 ' + accepted + ' 个任务';
      if (rejected) text += '，拒绝 ' + rejected + ' 个';
      if (msg.message) text += '：' + msg.message;
      notify('任务已发送', text, rejected ? 'error' : 'success');
      broadcastToExtension({ cmd: 'add-ack', accepted: accepted, rejected: rejected, message: msg.message || '' });
      break;
    }

    case 'notify':
      // App 主动推的通知
      notify('下载器', msg.message || '', msg.level || 'info');
      broadcastToExtension({ cmd: 'app-notify', level: msg.level || 'info', message: msg.message || '' });
      break;

    case 'config':
      if (msg.downloadDir) {
        setConnState(connState.state, { downloadDir: msg.downloadDir });
      }
      break;

    case 'error':
      setConnState(connState.state, { message: msg.message || '下载器返回错误' });
      notify('下载器错误', msg.message || '未知错误', 'error');
      break;

    default:
      break;
  }
}

/** 桌面通知（带去重，避免刷屏） */
const notifyHistory = new Map();
function notify(title, message, level) {
  if (!settingsCache.notify) return;
  const key = title + '|' + message;
  const last = notifyHistory.get(key) || 0;
  if (Date.now() - last < 20000) return;
  notifyHistory.set(key, Date.now());
  if (notifyHistory.size > 50) {
    const cutoff = Date.now() - 120000;
    for (const [k, v] of notifyHistory) if (v < cutoff) notifyHistory.delete(k);
  }
  try {
    chrome.notifications.create('vsd-' + Date.now(), {
      type: 'basic',
      iconUrl: chrome.runtime.getURL('icons/icon128.png'),
      title: title,
      message: message || '',
      priority: level === 'error' ? 2 : 0
    }, function () { void chrome.runtime.lastError; });
  } catch (e) { /* 忽略 */ }
}

// ---------------------------------------------------------------------------
// 任务发送（契约字段严格对齐）
// ---------------------------------------------------------------------------

/** 组装待发送任务对象（字段名必须与桌面 App 契约一致） */
function buildSendTask(item) {
  const headers = {};
  const rawHeaders = item.headers || {};
  const pick = ['Referer', 'referer', 'User-Agent', 'user-agent', 'Origin', 'origin'];
  pick.forEach(function (k) {
    if (rawHeaders[k]) headers[k] = rawHeaders[k];
  });
  if (!headers['Referer'] && !headers['referer']) {
    const ref = item.referer || item.pageUrl || '';
    if (ref) headers['Referer'] = ref;
  }
  if (!headers['User-Agent'] && !headers['user-agent']) {
    const ua = item.userAgent || (typeof navigator !== 'undefined' ? navigator.userAgent : '');
    if (ua) headers['User-Agent'] = ua;
  }
  if (!headers['Origin'] && !headers['origin'] && item.pageUrl) {
    try { headers['Origin'] = new URL(item.pageUrl).origin; } catch (e) { /* 忽略 */ }
  }

  const task = {
    url: item.url,
    // 协议里 kind 只有 video/hls/file；纯音轨（audioOnly）按 file 直链处理
    kind: item.kind === 'hls' ? 'hls'
      : (item.kind === 'file' || item.audioOnly) ? 'file' : 'video',
    title: item.title || '',
    referer: item.referer || item.pageUrl || '',
    userAgent: item.userAgent || (typeof navigator !== 'undefined' ? navigator.userAgent : ''),
    cookies: item.cookies || '',
    headers: headers,
    duration: parseInt(item.duration, 10) || 0,
    thumbnail: item.thumbnail || '',
    site: item.site || 'other',
    quality: item.quality || '',
    isHls: !!item.isHls,
    encrypted: !!item.encrypted
  };

  // ---- 新增契约字段（见 docs/PROTOCOL.md）：编码格式 / 真实总长 / 音视频分离轨 ----
  if (item.codec) task.codec = item.codec;
  if (item.codecFull) task.codecFull = item.codecFull;
  if (item.hevc) task.hevc = true;
  const totalBytes = parseInt(item.totalBytes, 10) || 0;
  if (totalBytes > 0) task.totalBytes = totalBytes;
  if (item.audioOnly) task.audioOnly = true;
  if (item.audioUrl) {
    task.audioUrl = item.audioUrl;
    if (item.audioCodec) task.audioCodec = item.audioCodec;
  }
  if (item.needsMerge) task.needsMerge = true;

  const filename = item.filename || buildFilename(item);
  if (filename) task.filename = filename;
  return task;
}

/** 发送任务：未连接时进入待发送队列（storage.session 持久化，重连后自动补发） */
async function sendTasks(tabId, ids) {
  const bucket = await loadBucket(tabId);
  const targets = [];
  if (!ids || !ids.length) {
    for (const it of bucket.items.values()) {
      if (!it.isFragment && !(it.isBlob && String(it.url).indexOf('blob:mse') === 0)) targets.push(it);
    }
  } else {
    ids.forEach(function (id) {
      const it = bucket.items.get(id);
      if (it) targets.push(it);
    });
  }
  // 分片不能作为独立任务发送
  const filtered = targets.filter(function (it) {
    return !it.isFragment && !(it.isBlob && String(it.url).indexOf('blob:mse') === 0) && it.url;
  });
  if (!filtered.length) {
    return { ok: false, sent: 0, queued: 0, message: '没有可发送的媒体，请先播放视频或刷新页面重试' };
  }

  const tasks = filtered.map(buildSendTask);
  const connected = socket && socket.readyState === WebSocket.OPEN && connState.state === 'connected';

  if (connected && sendRaw({ type: 'add', tasks: tasks })) {
    return { ok: true, sent: tasks.length, queued: 0, message: '已发送 ' + tasks.length + ' 个任务到下载器' };
  }

  // 未连接：入队并唤醒连接
  await pushPending(tasks);
  if (!tokenError) connect();
  return {
    ok: true,
    sent: 0,
    queued: tasks.length,
    message: tokenError
      ? '令牌不匹配，任务已排队；请先在设置中填写正确令牌'
      : '下载器未连接，' + tasks.length + ' 个任务已排队，连接成功后自动发送'
  };
}

/** 待发送队列（持久化） */
async function pushPending(tasks) {
  try {
    const got = await chrome.storage.session.get('pending');
    const list = (got && got.pending) || [];
    const merged = list.concat(tasks).slice(-200);
    await chrome.storage.session.set({ pending: merged });
    broadcastToExtension({ cmd: 'pending-updated', count: merged.length });
  } catch (e) { /* 忽略 */ }
}

async function getPending() {
  try {
    const got = await chrome.storage.session.get('pending');
    return (got && got.pending) || [];
  } catch (e) {
    return [];
  }
}

/** 连接建立后补发排队任务（合并成一条 add 消息，避免刷屏 add-ack） */
async function flushPendingTasks() {
  const list = await getPending();
  if (!list.length) return;
  let remaining = list;
  let sent = 0;
  if (sendRaw({ type: 'add', tasks: list })) {
    remaining = [];
    sent = list.length;
  }
  await chrome.storage.session.set({ pending: remaining });
  broadcastToExtension({ cmd: 'pending-updated', count: remaining.length });
  if (sent) broadcastToExtension({ cmd: 'pending-flushed', sent: sent });
}

// ---------------------------------------------------------------------------
// 生命周期：alarm / 安装 / 启动
// ---------------------------------------------------------------------------

function ensureAlarm() {
  try {
    chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 0.5, delayInMinutes: 0.5 });
  } catch (e) { /* 忽略 */ }
}

async function bootstrap(reason) {
  await loadSettings();
  await restoreConn();
  ensureAlarm();
  const connected = socket && socket.readyState === WebSocket.OPEN;
  if (!connected && !tokenError) connect();
  if (reason === 'installed') {
    // 首次安装：清掉可能残留的角标
    safeBadge(-1, '');
  }
}

chrome.runtime.onInstalled.addListener(function () {
  bootstrap('installed').catch(function () { /* 忽略 */ });
});

chrome.runtime.onStartup.addListener(function () {
  bootstrap('startup').catch(function () { /* 忽略 */ });
});

chrome.alarms.onAlarm.addListener(function (alarm) {
  if (!alarm || alarm.name !== KEEPALIVE_ALARM) return;
  (async function () {
    if (!settingsCache.host) await loadSettings();
    if (socket && socket.readyState === WebSocket.OPEN) {
      if (lastPongAt && Date.now() - lastPongAt > PONG_TIMEOUT_MS) {
        forceReconnect();
      } else if (connState.state === 'connected') {
        sendRaw({ type: 'ping' });
      }
    } else if (!tokenError) {
      connect();
    }
    // 空闲时清理过期的请求上下文
    const cutoff = Date.now() - 120000;
    for (const [key, value] of REQ_CTX) {
      if (value.at < cutoff) REQ_CTX.delete(key);
    }
  })().catch(function () { /* 忽略 */ });
});

// 标签页关闭 -> 清理缓存
chrome.tabs.onRemoved.addListener(function (tabId) {
  clearTab(tabId, { silent: true }).catch(function () { /* 忽略 */ });
});

// 标签页跳转 -> 清理旧页面结果；加载完成 -> 请求重新扫描
chrome.tabs.onUpdated.addListener(function (tabId, changeInfo, tab) {
  if (changeInfo.url) {
    const bucket = MEM.get(tabId);
    if (bucket && bucket.pageUrl && bucket.pageUrl !== changeInfo.url) {
      clearTab(tabId, { silent: true }).catch(function () { /* 忽略 */ });
    }
  }
  if (changeInfo.status === 'complete' && tab && tab.url && /^https?:/i.test(tab.url)) {
    setTimeout(function () {
      try {
        chrome.tabs.sendMessage(tabId, { cmd: 'scan' }, function () { void chrome.runtime.lastError; });
      } catch (e) { /* 忽略 */ }
    }, 1200);
  }
});

// ---------------------------------------------------------------------------
// 消息路由（popup / options / content script）
// ---------------------------------------------------------------------------

chrome.runtime.onMessage.addListener(function (message, sender, sendResponse) {
  if (!message || !message.cmd) return;

  // ---- 来自 content script 的探测上报 ----
  if (message.cmd === 'report') {
    const tabId = sender && sender.tab ? sender.tab.id : -1;
    if (tabId < 0) {
      sendResponse({ ok: false });
      return true;
    }
    (async function () {
      const bucket = await loadBucket(tabId);
      const page = message.page || {};
      if (page.url && page.url !== bucket.pageUrl) bucket.pageUrl = page.url;
      if (!bucket.pageUrl) bucket.pageUrl = page.url || '';
      // 合并页面元信息（标题/封面/时长取到值就覆盖空值）
      ['title', 'thumbnail', 'duration', 'cookies', 'userAgent', 'referer'].forEach(function (k) {
        if (page[k]) bucket.page[k] = page[k];
      });

      const items = Array.isArray(message.items) ? message.items : [];
      for (const raw of items) {
        if (!raw || !raw.url) continue;
        const via = raw.via === 'mse' || raw.via === 'mse-probe' ? 'inject-mse'
          : (raw.via === 'dom' || raw.via === 'dom-link') ? 'dom' : 'inject';
        await ingestFromContent(tabId, bucket, raw, via);
      }
      bucket.updatedAt = Date.now();
      schedulePersist(bucket);
      updateBadge(tabId);

      // 自动发送（可选功能）
      if (settingsCache.autoSend) {
        const fresh = items.filter(function (x) { return x && x.url; }).map(function (x) { return itemId(x); });
        if (fresh.length) {
          const connected = socket && socket.readyState === WebSocket.OPEN && connState.state === 'connected';
          if (connected) sendTasks(tabId, fresh).catch(function () { /* 忽略 */ });
        }
      }
      scheduleMediaBroadcast(tabId);
      sendResponse({ ok: true, count: items.length });
    })().catch(function () {
      try { sendResponse({ ok: false }); } catch (e) { /* 忽略 */ }
    });
    return true;
  }

  // ---- 弹窗 / 设置页 ----
  switch (message.cmd) {
    case 'getState':
      (async function () {
        if (!settingsCache.host) await loadSettings();
        if (!socket || socket.readyState === WebSocket.CLOSED) {
          if (!tokenError) connect();
        }
        const pending = await getPending();
        sendResponse({
          ok: true,
          conn: connState,
          settings: settingsCache,
          wsUrl: wsUrl(),
          pendingCount: pending.length,
          extVersion: EXT_VERSION,
          clientName: CLIENT_NAME
        });
      })().catch(function () {
        try { sendResponse({ ok: false, conn: connState, settings: settingsCache }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'getTabMedia':
      (async function () {
        let tabId = message.tabId;
        if (typeof tabId !== 'number' || tabId < 0) {
          const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
          tabId = tabs && tabs[0] ? tabs[0].id : -1;
        }
        if (tabId < 0) {
          sendResponse({ ok: false, items: [], message: '无法获取当前标签页' });
          return;
        }
        const data = await getTabMedia(tabId);
        sendResponse(Object.assign({ ok: true }, data));
      })().catch(function () {
        try { sendResponse({ ok: false, items: [] }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'send':
      (async function () {
        const result = await sendTasks(message.tabId, message.ids);
        sendResponse(Object.assign({ ok: true }, result));
      })().catch(function (err) {
        try { sendResponse({ ok: false, message: '发送失败：' + (err && err.message ? err.message : err) }); }
        catch (e) { /* 忽略 */ }
      });
      return true;

    case 'clear':
      (async function () {
        await clearTab(message.tabId);
        sendResponse({ ok: true });
      })().catch(function () {
        try { sendResponse({ ok: false }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'rescan':
      (async function () {
        const tabId = message.tabId;
        try {
          await chrome.tabs.sendMessage(tabId, { cmd: 'scan' });
          sendResponse({ ok: true, message: '已请求页面重新扫描' });
        } catch (e) {
          // content script 未注入（如 chrome:// 页面）时尝试手动注入
          try {
            await chrome.scripting.executeScript({ target: { tabId: tabId }, files: ['content/content.js'] });
            sendResponse({ ok: true, message: '已注入扫描脚本，请再试一次' });
          } catch (e2) {
            sendResponse({ ok: false, message: '该页面不支持扫描（受浏览器限制）' });
          }
        }
      })().catch(function () {
        try { sendResponse({ ok: false }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'reconnect':
      (async function () {
        await loadSettings();
        tokenError = false;
        reconnectAttempt = 0;
        forceReconnect();
        sendResponse({ ok: true, message: '正在重新连接 ' + wsUrl() });
      })().catch(function () {
        try { sendResponse({ ok: false }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'saveSettings':
      (async function () {
        const saved = await saveSettings(message.settings || {});
        sendResponse({ ok: true, settings: saved, wsUrl: wsUrl() });
      })().catch(function () {
        try { sendResponse({ ok: false }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'testConnection':
      (async function () {
        // 仅强制重连当前已保存的配置（设置页会先调用 saveSettings 再调用本命令）
        await loadSettings();
        tokenError = false;
        reconnectAttempt = 0;
        forceReconnect();
        sendResponse({ ok: true, wsUrl: wsUrl(), message: '正在连接 ' + wsUrl() });
      })().catch(function () {
        try { sendResponse({ ok: false }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'clearPending':
      (async function () {
        await chrome.storage.session.set({ pending: [] });
        broadcastToExtension({ cmd: 'pending-updated', count: 0 });
        sendResponse({ ok: true });
      })().catch(function () {
        try { sendResponse({ ok: false }); } catch (e) { /* 忽略 */ }
      });
      return true;

    case 'browserDownload':
      (async function () {
        // 兜底：用浏览器自身的下载器下载直链（非 HLS）
        try {
          const id = await chrome.downloads.download({
            url: message.url,
            filename: (sanitizeFilename(message.filename || '') || undefined),
            saveAs: false
          });
          sendResponse({ ok: true, downloadId: id, message: '已交给浏览器下载' });
        } catch (e) {
          sendResponse({ ok: false, message: '浏览器下载失败：' + (e && e.message ? e.message : e) });
        }
      })();
      return true;

    case 'toastTab':
      (async function () {
        try {
          await chrome.tabs.sendMessage(message.tabId, { cmd: 'toast', text: message.text || '', level: message.level || 'info' });
          sendResponse({ ok: true });
        } catch (e) {
          try { sendResponse({ ok: false }); } catch (e2) { /* 忽略 */ }
        }
      })();
      return true;

    default:
      return;
  }
});

/**
 * content script 上报条目的归一化：
 * - 补齐 kind / type / site / 标题等字段
 * - 把 inject.js 的 playlist 解析结果展开成 variants
 */
async function ingestFromContent(tabId, bucket, raw, via) {
  let kind = raw.kind;
  let type = raw.type;
  let isHls = !!raw.isHls;

  if (raw.playlist) {
    const pl = raw.playlist;
    const isMaster = !!pl.isMaster;
    const variants = Array.isArray(pl.variants) ? pl.variants : [];
    const merged = {
      url: raw.url,
      method: raw.method || 'GET',
      headers: raw.headers || {},
      variants: variants,
      variantCount: variants.length,
      isMaster: isMaster,
      isMedia: !!pl.isMedia,
      segmentCount: pl.segmentCount || 0,
      encrypted: !!pl.encrypted,
      keyMethod: pl.keyMethod || '',
      playlistType: raw.playlistType || (isHls ? 'hls' : 'dash'),
      isHls: isHls,
      kind: isHls ? 'hls' : 'file',
      type: isHls ? 'hls' : 'dash',
      quality: variants.length && variants[0].quality ? variants[0].quality : qualityFromUrl(raw.url),
      via: via,
      pageUrl: raw.pageUrl || bucket.pageUrl
    };
    if (variants.length) {
      const best = variants.reduce(function (a, b) {
        return ((b && b.height) || 0) > ((a && a.height) || 0) ? b : a;
      }, variants[0]);
      if (best && best.height) merged.height = best.height;
    }
    await recordMedia(tabId, merged, via);
    return;
  }

  if (raw.mse) {
    // MSE 播放提示：真实地址通常同时被网络层嗅探到，这里只作为提示条目
    const id = 'blob|' + raw.url + '|' + (raw.via || '');
    if (!bucket.items.has(id) && !Array.from(bucket.items.values()).some(function (x) { return x.mse; })) {
      // MSE 的 mime 里带 codecs，是判断 HEVC 最可靠的来源之一
      const mseCodec = parseCodec(raw.mime);
      const mseItem = {
        url: raw.url,
        kind: 'file',
        type: 'blob',
        isHls: false,
        encrypted: false,
        isBlob: true,
        mse: true,
        mime: raw.mime || '',
        title: '（检测到 MSE / blob: 播放，正在解析真实地址）',
        quality: '',
        via: via,
        pageUrl: raw.pageUrl || bucket.pageUrl
      };
      if (mseCodec && mseCodec.codec) {
        mseItem.codec = mseCodec.codec;
        mseItem.codecFull = mseCodec.codecFull || '';
        if (mseCodec.hevc) mseItem.hevc = true;
      }
      await recordMedia(tabId, mseItem, via);
    }
    return;
  }

  // DOM / 直链条目
  const ext = pathExt(raw.url);
  const byExt = ext && EXT_KIND[ext];
  let audioOnly = false;
  if (raw.mime && !kind) {
    const byCt = classifyByContentType(raw.mime);
    if (byCt) {
      kind = byCt.kind;
      type = byCt.type;
      isHls = !!byCt.isHls;
      audioOnly = !!byCt.audioOnly;
    }
  }
  if (!kind && byExt) {
    kind = byExt.kind;
    type = byExt.type;
    isHls = !!byExt.isHls;
    audioOnly = !!byExt.audioOnly;
  }
  if (!kind) {
    if (String(raw.url).indexOf('blob:') === 0) return; // 未知 blob 忽略
    // DOM 层拿不到 mime 时，退回 URL 里的 mime_type= 提示（抖音常见）
    const hints = urlHints(raw.url);
    if (hints.audio && !hints.video) {
      kind = 'file';
      type = 'audio';
      audioOnly = true;
    } else if (hints.video) {
      kind = 'video';
      type = 'mp4';
    } else {
      kind = 'file';
      type = ext || 'unknown';
    }
  }

  const domCodec = parseCodec(raw.mime) || codecFromUrl(raw.url);
  const item = {
    url: raw.url,
    kind: kind,
    type: type,
    isHls: isHls,
    encrypted: false,
    fragment: !!raw.fragment,
    isFragment: !!raw.fragment,
    method: 'GET',
    headers: raw.headers || {},
    cookies: '',
    referer: bucket.pageUrl || '',
    userAgent: (bucket.page && bucket.page.userAgent) || '',
    title: (bucket.page && bucket.page.title) || '',
    thumbnail: (bucket.page && bucket.page.thumbnail) || '',
    duration: firstPositive(raw.duration, bucket.page && bucket.page.duration),
    poster: raw.poster || '',
    width: raw.width || 0,
    height: raw.height || 0,
    mime: raw.mime || '',
    quality: qualityFromUrl(raw.url),
    via: via,
    pageUrl: raw.pageUrl || bucket.pageUrl
  };
  if (audioOnly) item.audioOnly = true;
  if (domCodec && domCodec.codec) {
    item.codec = domCodec.codec;
    item.codecFull = domCodec.codecFull || '';
    if (domCodec.hevc) item.hevc = true;
  }
  await recordMedia(tabId, item, via);
}

function firstPositive() {
  for (let i = 0; i < arguments.length; i++) {
    const n = parseFloat(arguments[i]);
    if (isFinite(n) && n > 0) return Math.round(n);
  }
  return 0;
}

// ---------------------------------------------------------------------------
// 启动
// ---------------------------------------------------------------------------

try {
  chrome.action.setBadgeBackgroundColor({ color: '#0A84FF' });
} catch (e) { /* 忽略 */ }

registerWebRequest();
bootstrap('worker-start').catch(function () { /* 忽略 */ });
