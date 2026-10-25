/*
 * tests/sniff-test.js —— 可选的自检脚本（纯 Node，无依赖，不参与扩展运行）。
 *
 * 用最简的 window / document / fetch / XHR 桩加载：
 *   content/inject.js（MAIN world 嗅探：fetch hook、XHR hook、MSE、m3u8 解析、probe）
 *   content/content.js（ISOLATED world：DOM 扫描、og 元信息、上报 background）
 * 验证探测链路与上报数据结构。
 *
 * 运行：node tests/sniff-test.js
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

function sleep(ms) {
  return new Promise(function (r) { setTimeout(r, ms); });
}

const MASTER_M3U8 = [
  '#EXTM3U',
  '#EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080,NAME="1080P"',
  'v1080/index.m3u8',
  '#EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=1280x720,NAME="720P"',
  'v720/index.m3u8',
  '#EXT-X-KEY:METHOD=AES-128,URI="key"'
].join('\n');

// ---------------------------------------------------------------------------
// 通用沙箱工厂
// ---------------------------------------------------------------------------

function baseGlobals() {
  return {
    console: console,
    setTimeout: setTimeout,
    clearTimeout: clearTimeout,
    setInterval: setInterval,
    clearInterval: clearInterval,
    Date: Date,
    JSON: JSON,
    Math: Math,
    Object: Object,
    Array: Array,
    String: String,
    Number: Number,
    Boolean: Boolean,
    RegExp: RegExp,
    Error: Error,
    Promise: Promise,
    Map: Map,
    Set: Set,
    Headers: undefined,
    parseInt: parseInt,
    parseFloat: parseFloat,
    isFinite: isFinite,
    decodeURIComponent: decodeURIComponent,
    encodeURIComponent: encodeURIComponent,
    MutationObserver: function () { this.observe = function () {}; this.disconnect = function () {}; }
  };
}

// ===========================================================================
// 第一部分：content/inject.js
// ===========================================================================

async function testInject() {
  console.log('\n[A] content/inject.js（MAIN world）');

  const posted = [];
  const fetchCalls = [];
  const winListeners = {};
  const docListeners = {};

  const sandbox = baseGlobals();
  sandbox.location = { href: 'https://www.bilibili.com/video/BV1x' };
  sandbox.document = {
    addEventListener: function (type, fn) { (docListeners[type] = docListeners[type] || []).push(fn); },
    visibilityState: 'visible',
    body: { appendChild: function () {} },
    documentElement: {}
  };
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  sandbox.top = sandbox;
  sandbox.addEventListener = function (type, fn) { (winListeners[type] = winListeners[type] || []).push(fn); };
  sandbox.postMessage = function (msg) { posted.push(msg); };

  // URL 桩：保留 Node URL 的解析能力，并补一个 createObjectURL
  const RealURL = URL;
  sandbox.URL = Object.assign(function (u, b) { return new RealURL(u, b); }, {
    createObjectURL: function () { return 'blob:https://www.bilibili.com/fake-blob'; }
  });

  // fetch 桩：返回 m3u8 / mp4 两种响应
  sandbox.fetch = function (url) {
    fetchCalls.push(String(url));
    const isHls = String(url).indexOf('.m3u8') >= 0;
    const body = isHls ? MASTER_M3U8 : 'BINARY-VIDEO-DATA';
    return Promise.resolve({
      ok: true,
      status: 200,
      headers: {
        get: function (h) {
          const n = String(h).toLowerCase();
          if (n === 'content-type') return isHls ? 'application/vnd.apple.mpegurl' : 'video/mp4';
          if (n === 'content-length') return String(body.length);
          return null;
        }
      },
      clone: function () {
        return { text: function () { return Promise.resolve(body); }, status: 200 };
      }
    });
  };

  // XHR 桩
  function FakeXHR() {
    this._listeners = {};
    this.readyState = 0;
    this.status = 0;
    this.responseType = '';
    this.responseText = '';
    this.responseURL = '';
    this._contentType = '';
  }
  FakeXHR.prototype.open = function (method, url) { this._method = method; this._url = url; this.readyState = 1; };
  FakeXHR.prototype.setRequestHeader = function () {};
  FakeXHR.prototype.send = function () { this._sent = true; };
  FakeXHR.prototype.addEventListener = function (type, fn) {
    (this._listeners[type] = this._listeners[type] || []).push(fn);
  };
  FakeXHR.prototype.getResponseHeader = function (n) {
    return String(n).toLowerCase() === 'content-type' ? this._contentType : null;
  };
  FakeXHR.prototype._fire = function (type) {
    (this._listeners[type] || []).forEach(function (fn) { fn.call(this); }, this);
  };
  sandbox.XMLHttpRequest = FakeXHR;

  // MediaSource 桩
  function FakeMediaSource() {}
  FakeMediaSource.prototype.addSourceBuffer = function () { return {}; };
  sandbox.MediaSource = FakeMediaSource;
  sandbox.SourceBuffer = function () {};

  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  // vm 的全局是 sandbox 的代理对象，必须在上下文内部建立 window/self/top 的自引用
  vm.runInContext('window = globalThis; self = globalThis; top = globalThis;', sandbox);
  const src = fs.readFileSync(path.join(ROOT, 'content', 'inject.js'), 'utf8');
  vm.runInContext(src, sandbox, { filename: 'inject.js' });

  const items = function () {
    const out = [];
    posted.forEach(function (msg) {
      if (msg && msg.__vsd === 'vsd-inject' && msg.dir === 'to-content' && Array.isArray(msg.items)) {
        msg.items.forEach(function (i) { out.push(i); });
      }
    });
    return out;
  };

  check('hook 了 window.fetch', sandbox.fetch !== undefined && sandbox.fetch.toString().indexOf('nativeFetch') < 0);

  // 1) fetch 一个 m3u8 -> 应解析出清晰度与加密
  await sandbox.fetch('https://cdn.example.com/hls/index.m3u8');
  await sleep(60);
  const hlsReport = items().find(function (i) { return i.playlist; });
  check('fetch 的 m3u8 被解析', !!hlsReport);
  check('解析出 2 个清晰度', hlsReport && hlsReport.playlist.variants.length === 2,
    hlsReport && hlsReport.playlist.variants.length);
  check('识别加密（AES-128）', hlsReport && hlsReport.playlist.encrypted === true);
  check('标记 isMaster', hlsReport && hlsReport.playlist.isMaster === true);
  check('分辨率为 1920x1080', hlsReport && hlsReport.playlist.variants[0].resolution === '1920x1080');
  check('清晰度名称保留（1080P）', hlsReport && hlsReport.playlist.variants[0].quality === '1080P',
    hlsReport && hlsReport.playlist.variants[0].quality);
  check('变体地址绝对化', hlsReport && hlsReport.playlist.variants[0].url === 'https://cdn.example.com/hls/v1080/index.m3u8',
    hlsReport && hlsReport.playlist.variants[0].url);
  check('playlistType = hls', hlsReport && hlsReport.playlistType === 'hls');

  // 2) fetch 一个 mp4 -> 只记录地址，不读响应体
  await sandbox.fetch('https://cdn.example.com/video.mp4');
  await sleep(320);
  const mp4Report = items().find(function (i) { return String(i.url).indexOf('video.mp4') >= 0; });
  check('fetch 的 mp4 被记录', !!mp4Report);
  check('mp4 不带 playlist 字段', mp4Report && mp4Report.playlist === undefined);
  check('mp4 来源为 fetch', mp4Report && mp4Report.via === 'fetch');

  // 3) fetch 失败不应抛出
  const failingFetch = sandbox.fetch;
  sandbox.fetch = function (url) {
    if (String(url).indexOf('.m3u8') >= 0) return Promise.reject(new Error('network'));
    return failingFetch.apply(null, arguments);
  };
  let threw = false;
  try {
    await sandbox.fetch('https://cdn.example.com/hls/x.m3u8');
  } catch (e) {
    threw = true;
  }
  check('fetch hook 不吞掉页面异常语义（仍抛出）', threw === true);

  // 4) XHR 请求 m3u8
  const xhr = new sandbox.XMLHttpRequest();
  xhr.open('GET', 'https://cdn.example.com/hls/xhr.m3u8');
  xhr.setRequestHeader('Referer', 'https://www.bilibili.com/video/BV1x');
  xhr.send();
  xhr.status = 200;
  xhr._contentType = 'application/vnd.apple.mpegurl';
  xhr.responseText = MASTER_M3U8;
  xhr.responseURL = 'https://cdn.example.com/hls/xhr.m3u8';
  xhr._fire('loadend');
  await sleep(60);
  const xhrRequest = items().find(function (i) { return String(i.url).indexOf('xhr.m3u8') >= 0 && !i.playlist; });
  check('XHR 请求被记录', !!xhrRequest);
  check('XHR 记录了请求头 Referer', xhrRequest && xhrRequest.headers && xhrRequest.headers.Referer === 'https://www.bilibili.com/video/BV1x',
    xhrRequest && xhrRequest.headers);
  const xhrPlaylist = items().find(function (i) { return String(i.url).indexOf('xhr.m3u8') >= 0 && i.playlist; });
  check('XHR 的 m3u8 响应被解析', !!xhrPlaylist);
  check('XHR 解析出 2 个清晰度', xhrPlaylist && xhrPlaylist.playlist.variants.length === 2,
    xhrPlaylist && xhrPlaylist.playlist.variants.length);
  check('XHR 来源为 xhr', xhrPlaylist && xhrPlaylist.via === 'xhr');

  // 5) MSE 探测
  const ms = new sandbox.MediaSource();
  ms.addSourceBuffer('video/mp4; codecs="avc1.64001f"');
  sandbox.URL.createObjectURL(new sandbox.MediaSource());
  await sleep(320);
  const mseItem = items().find(function (i) { return i.mse; });
  check('MSE addSourceBuffer 被捕获', !!mseItem);
  check('记录 mime 类型', mseItem && String(mseItem.mime).indexOf('avc1') >= 0, mseItem && mseItem.mime);

  // 6) probe 应答
  (winListeners.message || []).forEach(function (fn) {
    fn({ source: sandbox.window, data: { __vsd: 'vsd-inject', dir: 'to-inject', action: 'probe', requestId: 'r1' } });
  });
  const answer = posted.find(function (m) { return m && m.probeAnswer; });
  check('probe 有应答', !!answer);
  check('probe 返回 MSE 状态', answer && answer.payload && answer.payload.mse === true);
  check('probe 返回已捕获 URL 列表', answer && answer.payload && Array.isArray(answer.payload.urls) && answer.payload.urls.length > 0,
    answer && answer.payload && answer.payload.urls && answer.payload.urls.length);
  check('probe 回传 requestId', answer && answer.requestId === 'r1');
}

// ===========================================================================
// 第二部分：content/content.js
// ===========================================================================

async function testContent() {
  console.log('\n[B] content/content.js（ISOLATED world）');

  const sentMessages = [];
  let runtimeListener = null;

  const META = {
    'meta[property="og:title"]': 'B站测试视频标题',
    'meta[property="og:image"]': 'https://i0.hdslb.com/cover.jpg',
    'meta[property="og:video:duration"]': '245',
    'meta[property="og:video:type"]': 'video/mp4',
    'meta[itemprop="duration"]': '',
    'meta[name="twitter:image"]': '',
    'meta[name="twitter:title"]': '',
    'meta[name="title"]': '',
    'meta[property="og:image:secure_url"]': '',
    'meta[property="video:duration"]': '',
    'meta[property="og:video:secure_url"]': '',
    'meta[property="og:video:url"]': '',
    'meta[property="og:video"]': '',
    'meta[name="twitter:player:stream"]': '',
    'meta[name="twitter:image:src"]': '',
    'meta[itemprop="image"]': ''
  };

  function metaEl(value) {
    return { getAttribute: function (n) { return n === 'content' ? value : null; } };
  }

  const videoEl = {
    currentSrc: 'blob:https://www.bilibili.com/8f2a-blob',
    duration: 245.4,
    videoWidth: 1920,
    videoHeight: 1080,
    poster: 'https://i0.hdslb.com/poster.jpg',
    getAttribute: function (name) {
      const map = {
        src: null,
        'data-src': 'https://cdn.example.com/media/data-src.m3u8',
        'data-original': null,
        'data-url': null,
        'data-video-url': null,
        'data-play-url': null
      };
      return map[name] !== undefined ? map[name] : null;
    },
    querySelectorAll: function (sel) {
      if (sel !== 'source') return [];
      return [
        { getAttribute: function (n) { return n === 'src' ? 'https://cdn.example.com/media/source.webm' : (n === 'type' ? 'video/webm' : null); } }
      ];
    }
  };

  const anchorEls = [
    { getAttribute: function (n) { return n === 'href' ? 'https://cdn.example.com/files/movie.mp4' : null; } }
  ];

  const docListeners = {};
  const winListeners = {};

  const sandbox = baseGlobals();
  sandbox.location = { href: 'https://www.bilibili.com/video/BV1x' };
  sandbox.navigator = { userAgent: 'UA-content-test' };
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  sandbox.top = sandbox;
  sandbox.addEventListener = function (type, fn) { (winListeners[type] = winListeners[type] || []).push(fn); };
  sandbox.document = {
    readyState: 'complete',
    title: '页面标题兜底',
    cookie: 'SESSDATA=abc; bili_jct=xyz',
    documentElement: {},
    body: { appendChild: function () {} },
    addEventListener: function (type, fn) { (docListeners[type] = docListeners[type] || []).push(fn); },
    querySelector: function (sel) {
      if (sel === 'h1') return null;
      if (sel === 'video') return videoEl;
      if (sel in META) {
        const v = META[sel];
        return v ? metaEl(v) : null;
      }
      return null;
    },
    querySelectorAll: function (sel) {
      if (sel === 'video') return [videoEl];
      if (sel === 'a[href]') return anchorEls;
      if (sel === 'script[type="application/ld+json"]') return [];
      if (sel === 'video source') return videoEl.querySelectorAll('source');
      return [];
    },
    createElement: function () {
      return {
        className: '',
        textContent: '',
        style: {},
        classList: { add: function () {}, remove: function () {} },
        setAttribute: function () {},
        appendChild: function () {}
      };
    }
  };

  sandbox.chrome = {
    runtime: {
      lastError: undefined,
      sendMessage: function (msg, cb) {
        sentMessages.push(msg);
        if (typeof cb === 'function') cb({ ok: true });
      },
      onMessage: { addListener: function (fn) { runtimeListener = fn; } }
    }
  };

  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext('window = globalThis; self = globalThis; top = globalThis;', sandbox);
  const src = fs.readFileSync(path.join(ROOT, 'content', 'content.js'), 'utf8');
  vm.runInContext(src, sandbox, { filename: 'content.js' });

  await sleep(900); // 等首次扫描 + 400ms 批量上报

  check('已向 background 上报', sentMessages.length > 0, sentMessages.length);
  const report = sentMessages[sentMessages.length - 1];
  check('消息 cmd = report', report && report.cmd === 'report', report && report.cmd);
  check('标记为顶层 frame', report && report.isTopFrame === true);

  const page = (report && report.page) || {};
  check('page.title 取 og:title', page.title === 'B站测试视频标题', page.title);
  check('page.thumbnail 取 og:image', page.thumbnail === 'https://i0.hdslb.com/cover.jpg', page.thumbnail);
  check('page.duration 取 og:video:duration', page.duration === 245, page.duration);
  check('page.cookies 取 document.cookie', page.cookies === 'SESSDATA=abc; bili_jct=xyz', page.cookies);
  check('page.userAgent 存在', page.userAgent === 'UA-content-test');
  check('page.url 为当前地址', page.url === 'https://www.bilibili.com/video/BV1x', page.url);

  const urls = (report.items || []).map(function (i) { return i.url; });
  check('捕获 data-src 的 m3u8', urls.indexOf('https://cdn.example.com/media/data-src.m3u8') >= 0, urls);
  check('捕获 <source> 的 webm', urls.indexOf('https://cdn.example.com/media/source.webm') >= 0, urls);
  check('识别 blob:（MSE）播放', urls.indexOf('blob:mse#video') >= 0, urls);
  check('捕获页面直链 mp4', urls.indexOf('https://cdn.example.com/files/movie.mp4') >= 0, urls);

  const blobItem = (report.items || []).find(function (i) { return i.url === 'blob:mse#video'; });
  check('blob 条目标记 isBlob', blobItem && blobItem.isBlob === true);
  check('blob 条目带时长与分辨率', blobItem && blobItem.duration === 245 && blobItem.height === 1080,
    blobItem && { d: blobItem.duration, h: blobItem.height });

  // 响应 background 的重新扫描指令
  if (runtimeListener) {
    let response = null;
    runtimeListener({ cmd: 'scan' }, {}, function (r) { response = r; });
    check('scan 指令有应答', response && response.ok === true, response);
  } else {
    check('已注册 runtime.onMessage 监听', false);
  }

  // 轻提示（toast）不应抛错
  if (runtimeListener) {
    let tostResponse = null;
    let toastError = null;
    try {
      runtimeListener({ cmd: 'toast', text: '已发送到下载器' }, {}, function (r) { tostResponse = r; });
    } catch (e) {
      toastError = e;
    }
    check('toast 指令处理正常', !toastError && tostResponse && tostResponse.ok === true, toastError ? String(toastError) : tostResponse);
  }
}

(async function main() {
  await testInject();
  await testContent();
  console.log('\n========================================');
  console.log('断言 ' + checks + ' 项，失败 ' + failures + ' 项');
  console.log('========================================\n');
  process.exit(failures ? 1 : 0);
})().catch(function (err) {
  console.error('测试异常：', err);
  process.exit(1);
});
