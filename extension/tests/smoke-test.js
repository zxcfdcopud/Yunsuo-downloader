/*
 * tests/smoke-test.js —— 可选的自检脚本（纯 Node，无任何依赖，也不参与扩展运行）。
 *
 * 用途：给 background.js 造一套 chrome API / WebSocket 桩，验证：
 *   1. m3u8（master/media）解析、清晰度与加密识别
 *   2. 扩展名与 content-type 归类
 *   3. 站点识别（含 CDN 域名兜底）
 *   4. content script 上报 -> 按 tabId 缓存 -> 查询
 *   5. webRequest 嗅探链路（请求头 + 响应头）
 *   6. WebSocket 握手、hello 字段、add 任务字段严格符合契约、add-ack
 *   7. 关闭码 4001 -> 令牌错误状态
 *
 * 运行：node tests/smoke-test.js   （在 extension 目录下）
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const ROOT = path.join(__dirname, '..');
let failures = 0;
let checks = 0;

function check(name, condition, extra) {
  checks++;
  if (condition) {
    console.log('  ok   ' + name);
  } else {
    failures++;
    console.log('  FAIL ' + name + (extra !== undefined ? ' -> ' + JSON.stringify(extra) : ''));
  }
}

// ---------------------------------------------------------------------------
// chrome API 桩
// ---------------------------------------------------------------------------

function makeEvent() {
  const listeners = [];
  return {
    listeners: listeners,
    addListener: function (fn) { listeners.push(fn); },
    removeListener: function (fn) {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    },
    hasListener: function (fn) { return listeners.indexOf(fn) >= 0; }
  };
}

const captured = {
  onMessage: null,
  webRequest: { onBeforeRequest: makeEvent(), onBeforeSendHeaders: makeEvent(), onHeadersReceived: makeEvent() },
  onInstalled: makeEvent(),
  onStartup: makeEvent(),
  onAlarm: makeEvent(),
  tabsOnRemoved: makeEvent(),
  tabsOnUpdated: makeEvent(),
  badge: [],
  notifications: [],
  sentMessages: [],
  downloads: [],
  alarms: []
};

const storageData = { local: {}, session: {} };

function storageArea(name) {
  return {
    get: function (keys) {
      return new Promise(function (resolve) {
        const src = storageData[name];
        if (keys === undefined || keys === null) { resolve(Object.assign({}, src)); return; }
        if (typeof keys === 'string') {
          const out = {};
          if (keys in src) out[keys] = src[keys];
          resolve(out);
          return;
        }
        if (Array.isArray(keys)) {
          const out = {};
          keys.forEach(function (k) { if (k in src) out[k] = src[k]; });
          resolve(out);
          return;
        }
        const out = {};
        Object.keys(keys).forEach(function (k) {
          out[k] = (k in src) ? src[k] : keys[k];
        });
        resolve(out);
      });
    },
    set: function (obj) {
      return new Promise(function (resolve) {
        Object.assign(storageData[name], JSON.parse(JSON.stringify(obj)));
        resolve();
      });
    },
    remove: function (keys) {
      return new Promise(function (resolve) {
        const list = Array.isArray(keys) ? keys : [keys];
        list.forEach(function (k) { delete storageData[name][k]; });
        resolve();
      });
    },
    clear: function () {
      return new Promise(function (resolve) {
        Object.keys(storageData[name]).forEach(function (k) { delete storageData[name][k]; });
        resolve();
      });
    }
  };
}

/** 假的 WebSocket 实现（记录实例与发出的帧） */
class FakeWebSocket {
  constructor(url) {
    this.url = url;
    this.readyState = FakeWebSocket.CONNECTING;
    this.sent = [];
    this.onopen = null;
    this.onmessage = null;
    this.onerror = null;
    this.onclose = null;
    FakeWebSocket.instances.push(this);
  }
  send(data) {
    if (this.readyState !== FakeWebSocket.OPEN) throw new Error('not open');
    this.sent.push(JSON.parse(data));
  }
  close(code, reason) {
    this.readyState = FakeWebSocket.CLOSED;
    if (this.onclose) this.onclose({ code: code || 1000, reason: reason || '' });
  }
  // 测试辅助
  fireOpen() {
    this.readyState = FakeWebSocket.OPEN;
    if (this.onopen) this.onopen({});
  }
  fireMessage(obj) {
    if (this.onmessage) this.onmessage({ data: JSON.stringify(obj) });
  }
  fireClose(code) {
    this.readyState = FakeWebSocket.CLOSED;
    if (this.onclose) this.onclose({ code: code, reason: '' });
  }
}
FakeWebSocket.CONNECTING = 0;
FakeWebSocket.OPEN = 1;
FakeWebSocket.CLOSING = 2;
FakeWebSocket.CLOSED = 3;
FakeWebSocket.instances = [];

/** 假的 fetch（供后台播放列表解析兜底逻辑使用） */
let fetchImpl = function () {
  return Promise.resolve({
    ok: false,
    headers: { get: function () { return ''; } },
    text: function () { return Promise.resolve(''); }
  });
};

const chromeStub = {
  runtime: {
    lastError: undefined,
    id: 'test-extension',
    getURL: function (p) { return 'chrome-extension://test/' + p; },
    getContexts: function () { return Promise.resolve([{ contextType: 'POPUP' }]); },
    onMessage: {
      addListener: function (fn) { captured.onMessage = fn; },
      removeListener: function () {},
      hasListener: function () { return true; }
    },
    onInstalled: captured.onInstalled,
    onStartup: captured.onStartup,
    sendMessage: function (message, callback) {
      captured.sentMessages.push(message);
      if (typeof callback === 'function') callback({ ok: true });
    }
  },
  storage: {
    local: storageArea('local'),
    session: storageArea('session')
  },
  action: {
    setBadgeText: function (arg, cb) { captured.badge.push(arg); if (cb) cb(); },
    setBadgeBackgroundColor: function () {}
  },
  alarms: {
    create: function (name, info) { captured.alarms.push({ name: name, info: info }); },
    onAlarm: captured.onAlarm
  },
  webRequest: captured.webRequest,
  tabs: {
    onRemoved: captured.tabsOnRemoved,
    onUpdated: captured.tabsOnUpdated,
    query: function () { return Promise.resolve([{ id: 7, url: 'https://www.bilibili.com/video/BV1x', title: '测试视频' }]); },
    sendMessage: function (tabId, msg, cb) { if (cb) cb({ ok: true }); }
  },
  scripting: { executeScript: function () { return Promise.resolve([]); } },
  notifications: {
    create: function (id, options, cb) { captured.notifications.push({ id: id, options: options }); if (cb) cb(); }
  },
  downloads: {
    download: function (options) {
      captured.downloads.push(options);
      return Promise.resolve(1);
    }
  }
};

// ---------------------------------------------------------------------------
// 载入 background.js
// ---------------------------------------------------------------------------

const bgSource = fs.readFileSync(path.join(ROOT, 'background.js'), 'utf8');
// 追加导出片段（不改动源文件），把内部函数暴露给测试
const exposed = '\n;globalThis.__t = { parseM3U8Text: parseM3U8Text, classify: classify, detectSite: detectSite, ' +
  'buildSendTask: buildSendTask, getTabMedia: getTabMedia, recordMedia: recordMedia, itemId: itemId, ' +
  'wsUrl: wsUrl, buildFilename: buildFilename, qualityFromHeight: qualityFromHeight, ' +
  'parsePlaylistInBackground: parsePlaylistInBackground, ' +
  'get connState() { return connState; }, get socket() { return socket; } };\n';

const sandbox = {
  chrome: chromeStub,
  WebSocket: FakeWebSocket,
  fetch: function () { return fetchImpl.apply(null, arguments); },
  navigator: { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/126.0.0.0 Safari/537.36' },
  console: console,
  setTimeout: setTimeout,
  clearTimeout: clearTimeout,
  setInterval: setInterval,
  clearInterval: clearInterval,
  URL: URL,
  URLSearchParams: URLSearchParams,
  TextEncoder: TextEncoder,
  TextDecoder: TextDecoder,
  Promise: Promise,
  Map: Map,
  Set: Set,
  JSON: JSON,
  Object: Object,
  Array: Array,
  Math: Math,
  Date: Date,
  String: String,
  Number: Number,
  Boolean: Boolean,
  RegExp: RegExp,
  Error: Error,
  parseInt: parseInt,
  parseFloat: parseFloat,
  isFinite: isFinite,
  globalThis: null
};
sandbox.globalThis = sandbox;
vm.createContext(sandbox);

vm.runInContext(bgSource + exposed, sandbox, { filename: 'background.js' });

const T = sandbox.__t;

function callMessage(message, sender) {
  return new Promise(function (resolve) {
    const returned = captured.onMessage(message, sender || { tab: { id: 7 } }, function (response) {
      resolve(response);
    });
    if (returned !== true) {
      // 同步分支（没有 sendResponse 的 case）
      setTimeout(function () { resolve(undefined); }, 0);
    }
  });
}

function sleep(ms) {
  return new Promise(function (r) { setTimeout(r, ms); });
}

// ---------------------------------------------------------------------------
// 测试用例
// ---------------------------------------------------------------------------

const MASTER_M3U8 = [
  '#EXTM3U',
  '#EXT-X-VERSION:3',
  '#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,NAME="1080P 高清"',
  'v1080/index.m3u8',
  '#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,NAME="720P"',
  'v720/index.m3u8',
  '#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360',
  'v360/index.m3u8',
  '#EXT-X-KEY:METHOD=AES-128,URI="key.key"'
].join('\n');

const MEDIA_M3U8 = [
  '#EXTM3U',
  '#EXT-X-VERSION:3',
  '#EXT-X-TARGETDURATION:6',
  '#EXTINF:5.0,',
  'seg-1.ts',
  '#EXTINF:5.0,',
  'seg-2.ts'
].join('\n');

const MPD_XML = [
  '<?xml version="1.0"?>',
  '<MPD><Period><AdaptationSet>',
  '<ContentProtection schemeIdUri="urn:uuid:edef8ba9"/>',
  '<Representation id="1" width="1920" height="1080" bandwidth="5000000" mimeType="video/mp4"/>',
  '<Representation id="2" width="1280" height="720" bandwidth="2000000" mimeType="video/mp4"/>',
  '</AdaptationSet></Period></MPD>'
].join('\n');

async function main() {
  console.log('\n[1] m3u8 / mpd 解析');
  const master = T.parseM3U8Text(MASTER_M3U8, 'https://cdn.example.com/hls/index.m3u8');
  check('master 被识别', master.isMaster === true);
  check('清晰度数量 = 3', master.variants.length === 3, master.variants.length);
  check('最高清为 1080p', master.variants[0].quality === '1080P 高清', master.variants[0].quality);
  check('分辨率解析正确', master.variants[0].resolution === '1920x1080', master.variants[0].resolution);
  check('变体地址解析为绝对地址', master.variants[0].url === 'https://cdn.example.com/hls/v1080/index.m3u8', master.variants[0].url);
  check('识别 AES-128 加密', master.encrypted === true);

  const media = T.parseM3U8Text(MEDIA_M3U8, 'https://cdn.example.com/hls/v360/index.m3u8');
  check('media playlist 被识别', media.isMedia === true && media.isMaster === false);
  check('分片数量 = 2', media.segmentCount === 2, media.segmentCount);
  check('未加密', media.encrypted === false);

  console.log('\n[2] 扩展名 / content-type 归类');
  check('.m3u8 -> hls', T.classify('https://a/b/index.m3u8', '').kind === 'hls');
  check('content-type mpegurl -> hls', T.classify('https://a/b/play', 'application/vnd.apple.mpegurl').kind === 'hls');
  check('.mpd -> file/dash', T.classify('https://a/b/manifest.mpd', '').type === 'dash');
  check('content-type dash+xml -> dash', T.classify('https://a/b/x', 'application/dash+xml').type === 'dash');
  check('.mp4 -> video', T.classify('https://a/b/v.mp4', '').kind === 'video');
  check('content-type video/mp4 -> video', T.classify('https://a/b/stream', 'video/mp4').kind === 'video');
  check('content-type audio/mpeg -> file', T.classify('https://a/b/s', 'audio/mpeg').kind === 'file');
  check('.ts 标记为分片', T.classify('https://a/b/seg-1.ts', '').fragment === true);
  check('普通 html 不误判', T.classify('https://a/b/index.html', 'text/html') === null);
  check('octet-stream 无扩展名不误判', T.classify('https://a/b/x', 'application/octet-stream') === null);
  check('query 参数不影响扩展名', T.classify('https://a/b/index.m3u8?sign=abc&t=1', '').kind === 'hls');

  console.log('\n[3] 站点识别');
  check('bilibili 页面', T.detectSite('https://www.bilibili.com/video/BV1x') === 'bilibili');
  check('bilibili CDN', T.detectSite('https://upos-sz-mirror.bilivideo.com/x.m4s') === 'bilibili');
  check('youtube 页面', T.detectSite('https://www.youtube.com/watch?v=1') === 'youtube');
  check('googlevideo CDN', T.detectSite('https://rr1---sn-x.googlevideo.com/videoplayback') === 'youtube');
  check('抖音', T.detectSite('https://www.douyin.com/video/1') === 'douyin');
  check('快手', T.detectSite('https://www.kuaishou.com/short-video/1') === 'kuaishou');
  check('小红书', T.detectSite('https://www.xiaohongshu.com/explore/1') === 'xiaohongshu');
  check('其他站点', T.detectSite('https://example.com/a.mp4') === 'other');

  console.log('\n[4] content script 上报 -> 缓存 -> 查询');
  await callMessage({
    cmd: 'report',
    isTopFrame: true,
    page: {
      url: 'https://www.bilibili.com/video/BV1x',
      title: '测试视频标题',
      thumbnail: 'https://i0.hdslb.com/cover.jpg',
      duration: 123,
      cookies: 'SESSDATA=abc; bili_jct=xyz',
      userAgent: 'UA-test'
    },
    items: [
      { url: 'https://upos.bilivideo.com/a.m4s', via: 'dom', mime: 'video/mp4', duration: 123 },
      {
        url: 'https://cdn.example.com/hls/index.m3u8',
        via: 'fetch',
        method: 'GET',
        headers: { Referer: 'https://www.bilibili.com/video/BV1x', 'User-Agent': 'UA-test' },
        isHls: true,
        playlistType: 'hls',
        playlist: T.parseM3U8Text(MASTER_M3U8, 'https://cdn.example.com/hls/index.m3u8')
      }
    ]
  });
  await sleep(60);
  const mediaRes = await callMessage({ cmd: 'getTabMedia', tabId: 7 });
  check('查询返回 ok', mediaRes.ok === true);
  const list = mediaRes.items || [];
  check('缓存到 2 条媒体', list.length === 2, list.length);
  const hlsItem = list.find(function (x) { return x.isHls; });
  check('存在 HLS 条目', !!hlsItem);
  check('HLS 条目 kind = hls', hlsItem && hlsItem.kind === 'hls', hlsItem && hlsItem.kind);
  check('HLS 条目 quality = 1080P 高清', hlsItem && hlsItem.quality === '1080P 高清', hlsItem && hlsItem.quality);
  check('HLS 条目 site = bilibili', hlsItem && hlsItem.site === 'bilibili', hlsItem && hlsItem.site);
  check('HLS 条目带标题', hlsItem && hlsItem.title === '测试视频标题', hlsItem && hlsItem.title);
  check('HLS 条目带时长', hlsItem && hlsItem.duration === 123, hlsItem && hlsItem.duration);
  check('HLS 条目带封面', hlsItem && hlsItem.thumbnail === 'https://i0.hdslb.com/cover.jpg', hlsItem && hlsItem.thumbnail);
  check('记录了来源标签', hlsItem && Array.isArray(hlsItem.sources) && hlsItem.sources.length > 0, hlsItem && hlsItem.sources);

  console.log('\n[5] webRequest 嗅探链路（m3u8 + 响应头）');
  const reqDetails = {
    requestId: 'req-1',
    url: 'https://cdn.example.com/hls/v720/index.m3u8?token=1',
    method: 'GET',
    tabId: 7,
    type: 'xmlhttprequest',
    requestHeaders: [
      { name: 'Referer', value: 'https://www.bilibili.com/video/BV1x' },
      { name: 'Cookie', value: 'SESSDATA=abc' },
      { name: 'User-Agent', value: 'UA-test' },
      { name: 'Origin', value: 'https://www.bilibili.com' }
    ]
  };
  captured.webRequest.onBeforeSendHeaders.listeners.forEach(function (fn) { fn(reqDetails); });
  captured.webRequest.onBeforeRequest.listeners.forEach(function (fn) {
    fn({ requestId: 'req-1', url: reqDetails.url, method: 'GET', tabId: 7, type: 'xmlhttprequest' });
  });
  captured.webRequest.onHeadersReceived.listeners.forEach(function (fn) {
    fn({
      requestId: 'req-1',
      url: reqDetails.url,
      tabId: 7,
      statusCode: 200,
      method: 'GET',
      responseHeaders: [
        { name: 'Content-Type', value: 'application/vnd.apple.mpegurl' },
        { name: 'Content-Length', value: '2048' }
      ]
    });
  });
  await sleep(80); // 等后台兜底解析（fetch 桩返回失败，属正常路径）
  const afterWeb = await callMessage({ cmd: 'getTabMedia', tabId: 7 });
  const sniffed = (afterWeb.items || []).find(function (x) { return x.url.indexOf('v720') >= 0; });
  check('webRequest 嗅探到 m3u8', !!sniffed);
  check('归类为 hls', sniffed && sniffed.kind === 'hls', sniffed && sniffed.kind);
  check('记录了 Cookie', sniffed && sniffed.cookies === 'SESSDATA=abc', sniffed && sniffed.cookies);
  check('记录了 Referer', sniffed && sniffed.referer === 'https://www.bilibili.com/video/BV1x', sniffed && sniffed.referer);
  check('记录了大小', sniffed && sniffed.size === 2048, sniffed && sniffed.size);
  check('来源为 webRequest', sniffed && sniffed.sources.indexOf('webRequest') >= 0, sniffed && sniffed.sources);

  console.log('\n[6] 后台播放列表兜底解析（fetch 成功时）');
  fetchImpl = function (url) {
    if (String(url).indexOf('.m3u8') >= 0) {
      return Promise.resolve({
        ok: true,
        headers: { get: function (h) { return h.toLowerCase() === 'content-type' ? 'application/vnd.apple.mpegurl' : ''; } },
        text: function () { return Promise.resolve(MASTER_M3U8); }
      });
    }
    return Promise.resolve({ ok: false, headers: { get: function () { return ''; } }, text: function () { return Promise.resolve(''); } });
  };
  try {
    await T.parsePlaylistInBackground(7, {
      url: 'https://cdn.example.com/hls/v720/index.m3u8?token=1',
      isHls: true,
      pageUrl: 'https://www.bilibili.com/video/BV1x',
      quality: ''
    });
  } catch (e) {
    console.log('     parsePlaylistInBackground 抛错：' + (e && e.stack ? e.stack : e));
  }
  await sleep(60);
  const afterDirect = await callMessage({ cmd: 'getTabMedia', tabId: 7 });
  const direct = (afterDirect.items || []).find(function (x) { return x.url.indexOf('v720') >= 0; });
  check('直接调用兜底解析也能补全（含 URL 带 query）',
    direct && Array.isArray(direct.variants) && direct.variants.length === 3,
    direct ? (direct.variants || []).length : 'item-not-found');
  captured.webRequest.onBeforeSendHeaders.listeners.forEach(function (fn) {
    fn({
      requestId: 'req-2', url: 'https://cdn.example.com/hls/v480/index.m3u8', method: 'GET', tabId: 7,
      requestHeaders: [{ name: 'Referer', value: 'https://www.bilibili.com/video/BV1x' }]
    });
  });
  captured.webRequest.onHeadersReceived.listeners.forEach(function (fn) {
    fn({
      requestId: 'req-2', url: 'https://cdn.example.com/hls/v480/index.m3u8', tabId: 7, statusCode: 200, method: 'GET',
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }]
    });
  });
  await sleep(150);
  const afterParse = await callMessage({ cmd: 'getTabMedia', tabId: 7 });
  const parsed = (afterParse.items || []).find(function (x) { return x.url.indexOf('v480') >= 0; });
  check('自动兜底解析补全清晰度', parsed && Array.isArray(parsed.variants) && parsed.variants.length === 3,
    parsed ? (parsed.variants || []).length : 'item-not-found');
  check('自动兜底解析标记加密', parsed && parsed.encrypted === true, parsed && parsed.encrypted);

  console.log('\n[7] WebSocket 连接与任务契约');
  await callMessage({ cmd: 'getState' });
  await sleep(30);
  const ws = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
  check('使用了默认地址 ws://127.0.0.1:16888/media', ws && ws.url === 'ws://127.0.0.1:16888/media', ws && ws.url);

  ws.fireOpen();
  const hello = ws.sent[0];
  check('首帧为 hello', hello && hello.type === 'hello', hello);
  check('hello.client = browser-extension', hello && hello.client === 'browser-extension');
  check('hello.version 存在', hello && typeof hello.version === 'string' && hello.version.length > 0);
  check('hello.browser = chrome', hello && hello.browser === 'chrome', hello && hello.browser);
  check('hello.token 为字符串', hello && typeof hello.token === 'string');

  ws.fireMessage({ type: 'hello-ack', appVersion: '1.0.0', downloadDir: 'F:\\下载器工程\\downloads', settings: {} });
  await sleep(30);
  const st = await callMessage({ cmd: 'getState' });
  check('握手后状态为 connected', st.conn.state === 'connected', st.conn.state);
  check('记录 App 版本', st.conn.appVersion === '1.0.0');
  check('记录下载目录', st.conn.downloadDir === 'F:\\下载器工程\\downloads');

  const sendRes = await callMessage({ cmd: 'send', tabId: 7, ids: [hlsItem.id] });
  check('发送返回成功', sendRes.ok === true && sendRes.sent === 1, sendRes);
  const addFrame = ws.sent[ws.sent.length - 1];
  check('发出 add 消息', addFrame && addFrame.type === 'add', addFrame && addFrame.type);

  const CONTRACT_KEYS = ['url', 'kind', 'title', 'filename', 'referer', 'userAgent', 'cookies',
    'headers', 'duration', 'thumbnail', 'site', 'quality', 'isHls', 'encrypted'];
  const task = addFrame.tasks[0];
  const extraKeys = Object.keys(task).filter(function (k) { return CONTRACT_KEYS.indexOf(k) < 0; });
  check('任务无契约外字段', extraKeys.length === 0, extraKeys);
  const missing = ['url', 'kind', 'title', 'referer', 'userAgent', 'cookies', 'headers', 'duration',
    'thumbnail', 'site', 'quality', 'isHls', 'encrypted'].filter(function (k) { return !(k in task); });
  check('任务包含全部必需字段', missing.length === 0, missing);
  check('kind 合法（video|hls|file）', ['video', 'hls', 'file'].indexOf(task.kind) >= 0, task.kind);
  check('site 合法', ['bilibili', 'youtube', 'douyin', 'kuaishou', 'xiaohongshu', 'other'].indexOf(task.site) >= 0, task.site);
  check('headers.Referer 正确', task.headers && task.headers.Referer === 'https://www.bilibili.com/video/BV1x', task.headers);
  check('headers.User-Agent 正确', task.headers && task.headers['User-Agent'] === 'UA-test', task.headers);
  check('headers.Origin 正确', task.headers && task.headers.Origin === 'https://www.bilibili.com', task.headers);
  check('duration 为数字', typeof task.duration === 'number', task.duration);
  check('isHls 为 true', task.isHls === true);

  ws.fireMessage({ type: 'add-ack', accepted: 1, rejected: 0, message: 'ok' });
  await sleep(20);
  check('add-ack 触发系统通知', captured.notifications.length > 0, captured.notifications.length);

  ws.fireMessage({ type: 'config', downloadDir: 'D:\\videos' });
  await sleep(20);
  const st2 = await callMessage({ cmd: 'getState' });
  check('config 更新下载目录', st2.conn.downloadDir === 'D:\\videos', st2.conn.downloadDir);

  ws.fireMessage({ type: 'notify', level: 'success', message: '下载完成' });
  await sleep(20);
  check('notify 触发通知', captured.notifications.some(function (n) { return n.options.message === '下载完成'; }));

  console.log('\n[8] 4001 令牌错误处理');
  ws.fireClose(4001);
  await sleep(40);
  const st3 = await callMessage({ cmd: 'getState' });
  check('状态为 token-error', st3.conn.state === 'token-error', st3.conn.state);
  check('关闭码记录为 4001', st3.conn.closeCode === 4001, st3.conn.closeCode);
  check('提示文案包含"令牌"', /令牌/.test(st3.conn.message || ''), st3.conn.message);

  console.log('\n[9] 未连接时发送 -> 排队');
  const queued = await callMessage({ cmd: 'send', tabId: 7, ids: [hlsItem.id] });
  check('返回排队数量', queued.queued === 1, queued);
  const st4 = await callMessage({ cmd: 'getState' });
  check('待发送队列计数 = 1', st4.pendingCount === 1, st4.pendingCount);

  console.log('\n[10] 清空列表');
  await callMessage({ cmd: 'clear', tabId: 7 });
  const cleared = await callMessage({ cmd: 'getTabMedia', tabId: 7 });
  check('清空后条目为 0', (cleared.items || []).length === 0, (cleared.items || []).length);

  console.log('\n[11] 标签页关闭清理');
  captured.tabsOnRemoved.listeners.forEach(function (fn) { fn(7, { windowId: 1, isWindowClosing: false }); });
  await sleep(30);
  check('tab 缓存已从 session 存储删除', !('tab:7' in storageData.session));

  // -------------------------------------------------------------------------
  // 以下用 tabId 9 继续验证：分片/角标、全部发送、跳转清理、重连与排队补发
  // -------------------------------------------------------------------------
  const TAB = 9;
  const PAGE = 'https://www.bilibili.com/video/BV9';

  function lastBadge(tabId) {
    for (let i = captured.badge.length - 1; i >= 0; i--) {
      const b = captured.badge[i];
      if (b.tabId === tabId) return b.text;
    }
    return null;
  }

  function sniff(url, contentType, requestId, extraHeaders) {
    const headers = [{ name: 'Referer', value: PAGE }].concat(extraHeaders || []);
    captured.webRequest.onBeforeSendHeaders.listeners.forEach(function (fn) {
      fn({ requestId: requestId, url: url, method: 'GET', tabId: TAB, type: 'xmlhttprequest', requestHeaders: headers });
    });
    captured.webRequest.onBeforeRequest.listeners.forEach(function (fn) {
      fn({ requestId: requestId, url: url, method: 'GET', tabId: TAB, type: 'xmlhttprequest' });
    });
    captured.webRequest.onHeadersReceived.listeners.forEach(function (fn) {
      fn({
        requestId: requestId, url: url, tabId: TAB, statusCode: 200, method: 'GET',
        responseHeaders: [
          { name: 'Content-Type', value: contentType || 'application/octet-stream' },
          { name: 'Content-Length', value: '4096' }
        ]
      });
    });
  }

  console.log('\n[12] HLS 分片与角标');
  sniff('https://cdn.e/hls/seg-1.ts', 'video/mp2t', 'f1');
  await sleep(30);
  check('分片不计入角标（只有分片时角标为空）', lastBadge(TAB) === '', lastBadge(TAB));
  const onlyFrag = await callMessage({ cmd: 'getTabMedia', tabId: TAB });
  check('分片仍被记录下来（仅供诊断）', (onlyFrag.items || []).length === 1, (onlyFrag.items || []).length);
  check('分片条目 isFragment = true', !!(onlyFrag.items[0] && onlyFrag.items[0].isFragment));
  check('分片 kind = file', onlyFrag.items[0] && onlyFrag.items[0].kind === 'file', onlyFrag.items[0] && onlyFrag.items[0].kind);

  sniff('https://cdn.e/hls/index.m3u8', 'application/vnd.apple.mpegurl', 'm1');
  await sleep(40);
  check('主媒体计入角标 = 1', lastBadge(TAB) === '1', lastBadge(TAB));
  sniff('https://cdn.e/hls/seg-2.ts', 'video/mp2t', 'f2');
  await sleep(40);
  check('角标仍为 1（分片不算）', lastBadge(TAB) === '1', lastBadge(TAB));
  const withFrag = await callMessage({ cmd: 'getTabMedia', tabId: TAB });
  const linkedFrag = (withFrag.items || []).find(function (x) { return x.url.indexOf('seg-2.ts') >= 0; });
  check('分片关联到母 m3u8', linkedFrag && linkedFrag.parentUrl === 'https://cdn.e/hls/index.m3u8',
    linkedFrag && linkedFrag.parentUrl);
  check('hasHls = true', withFrag.hasHls === true);

  console.log('\n[13] 全部发送（ids 为空时自动过滤分片）');
  await callMessage({ cmd: 'reconnect' });
  await sleep(60);
  let ws2 = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
  ws2.fireOpen();
  ws2.fireMessage({ type: 'hello-ack', appVersion: '1.0.0', downloadDir: 'D:\\dl', settings: {} });
  await sleep(40);
  const sendAll = await callMessage({ cmd: 'send', tabId: TAB, ids: [] });
  check('全部发送只发 1 个任务（分片被过滤）', sendAll.sent === 1, sendAll);
  const addAll = ws2.sent[ws2.sent.length - 1];
  check('发送的是 m3u8 主地址', addAll && addAll.tasks[0].url === 'https://cdn.e/hls/index.m3u8', addAll && addAll.tasks[0].url);
  check('kind = hls', addAll && addAll.tasks[0].kind === 'hls');

  console.log('\n[14] 浏览器下载兜底与队列清理');
  const dl = await callMessage({ cmd: 'browserDownload', url: 'https://cdn.e/video.mp4', filename: '视频' });
  check('调用 chrome.downloads', dl.ok === true && captured.downloads.length === 1, captured.downloads.length);
  const clr = await callMessage({ cmd: 'clearPending' });
  check('清空待发送队列', clr.ok === true);
  const st5 = await callMessage({ cmd: 'getState' });
  check('队列计数归零', st5.pendingCount === 0, st5.pendingCount);

  console.log('\n[15] 标签页跳转 -> 清理旧页面结果');
  captured.tabsOnUpdated.listeners.forEach(function (fn) {
    fn(TAB, { url: 'https://other.example.com/page' }, { id: TAB, url: 'https://other.example.com/page' });
  });
  await sleep(40);
  const afterNav = await callMessage({ cmd: 'getTabMedia', tabId: TAB });
  check('跳转后旧媒体被清空', (afterNav.items || []).length === 0, (afterNav.items || []).length);

  console.log('\n[16] 断线自动重连（指数退避）');
  const beforeCount = FakeWebSocket.instances.length;
  ws2.fireClose(1006);
  await sleep(1600); // 首次退避约 1s
  const reconnected = FakeWebSocket.instances.length > beforeCount;
  check('断线后自动新建连接', reconnected,
    { before: beforeCount, after: FakeWebSocket.instances.length });
  const ws3 = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
  check('重连地址不变', ws3 && ws3.url === 'ws://127.0.0.1:16888/media', ws3 && ws3.url);

  console.log('\n[17] 保存设置后 hello 携带 token，且排队任务自动补发');
  const saved = await callMessage({ cmd: 'saveSettings', settings: { token: 'T-12345', port: 16888 } });
  check('设置已保存', saved.ok === true && storageData.local.settings.token === 'T-12345', storageData.local.settings);
  await sleep(80);
  const ws4 = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
  ws4.fireOpen();
  await sleep(20);
  check('hello.token 为保存的令牌', ws4.sent[0] && ws4.sent[0].token === 'T-12345', ws4.sent[0]);

  // 断开 -> 发送（应排队）-> 重连握手 -> 自动补发
  ws4.fireClose(1006);
  await sleep(30);
  sniff('https://cdn.e/hls/second.m3u8', 'application/vnd.apple.mpegurl', 'm2');
  await sleep(60);
  const queueSend = await callMessage({ cmd: 'send', tabId: TAB, ids: [] });
  check('未连接时任务进入队列', queueSend.queued === 1, queueSend);

  await sleep(1700); // 等自动重连
  const ws5 = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
  ws5.fireOpen();
  ws5.fireMessage({ type: 'hello-ack', appVersion: '1.0.0', downloadDir: 'D:\\dl', settings: {} });
  await sleep(80);
  const addFrame2 = ws5.sent.find(function (m) { return m.type === 'add'; });
  check('握手后自动补发排队任务', !!addFrame2, ws5.sent.map(function (m) { return m.type; }));
  check('补发的任务地址正确', addFrame2 && addFrame2.tasks[0].url === 'https://cdn.e/hls/second.m3u8',
    addFrame2 && addFrame2.tasks[0].url);
  const st6 = await callMessage({ cmd: 'getState' });
  check('补发后队列清空', st6.pendingCount === 0, st6.pendingCount);

  console.log('\n========================================');
  console.log('断言 ' + checks + ' 项，失败 ' + failures + ' 项');
  console.log('========================================\n');
  if (failures) process.exit(1);
  process.exit(0);
}

main().catch(function (err) {
  console.error('测试异常：', err);
  process.exit(1);
});
