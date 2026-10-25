/*
 * tests/douyin-hevc-test.js —— 抖音 HEVC(hvc1) 嗅探回归测试（纯 Node，无依赖）。
 *
 * 复现并锁定三件事：
 *   1. 同一个 mp4/HEVC 地址被播放器用 Range 分段拉取时，必须合并成 **一条**，
 *      并且要用 Content-Range 里的总长回填 totalBytes（不能把"第一段的大小"当成文件大小）。
 *   2. HEVC 标识（hvc1 / hev1 / h265）必须被识别并记录到 item.codec，
 *      content-type 里的 codecs="hvc1.1.6.L93.B0" 也要解析出来。
 *   3. 同一视频的不同清晰度（不同 URL）、音视频分离轨（audio/mp4、mime_type=audio_mp4）
 *      必须是 **不同条目**，且音频轨标记 audioOnly 并关联到视频条目（item.audioUrl）。
 *
 * 运行：
 *   node tests/douyin-hevc-test.js           # 断言模式
 *   node tests/douyin-hevc-test.js --dump    # 只打印嗅探结果 JSON（用于改前/改后对比取证）
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const ROOT = path.join(__dirname, '..');
const DUMP = process.argv.indexOf('--dump') >= 0;

let failures = 0;
let checks = 0;

function check(name, condition, extra) {
  checks++;
  if (condition) {
    if (!DUMP) console.log('  ok   ' + name);
  } else {
    failures++;
    if (!DUMP) console.log('  FAIL ' + name + (extra !== undefined ? ' -> ' + JSON.stringify(extra) : ''));
  }
}

function sleep(ms) {
  return new Promise(function (r) { setTimeout(r, ms); });
}

// ---------------------------------------------------------------------------
// chrome API 桩（只实现 background.js 用到的部分）
// ---------------------------------------------------------------------------

function makeEvent() {
  const listeners = [];
  return {
    listeners: listeners,
    addListener: function (fn) { listeners.push(fn); },
    removeListener: function () {},
    hasListener: function () { return true; }
  };
}

const captured = {
  webRequest: {
    onBeforeRequest: makeEvent(),
    onBeforeSendHeaders: makeEvent(),
    onHeadersReceived: makeEvent()
  },
  badge: [],
  notifications: [],
  sentMessages: []
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
        Object.keys(keys).forEach(function (k) { out[k] = (k in src) ? src[k] : keys[k]; });
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

class FakeWebSocket {
  constructor(url) {
    this.url = url;
    this.readyState = 0;
    this.sent = [];
    FakeWebSocket.instances.push(this);
  }
  send(data) { if (this.readyState === 1) this.sent.push(JSON.parse(data)); }
  close() { this.readyState = 3; }
}
FakeWebSocket.instances = [];

const chromeStub = {
  runtime: {
    lastError: undefined,
    id: 'test-extension',
    getURL: function (p) { return 'chrome-extension://test/' + p; },
    getContexts: function () { return Promise.resolve([]); },
    onMessage: { addListener: function () {}, removeListener: function () {}, hasListener: function () { return true; } },
    onInstalled: makeEvent(),
    onStartup: makeEvent(),
    sendMessage: function (message, callback) {
      captured.sentMessages.push(message);
      if (typeof callback === 'function') callback({ ok: true });
    }
  },
  storage: { local: storageArea('local'), session: storageArea('session') },
  action: {
    setBadgeText: function (arg, cb) { captured.badge.push(arg); if (cb) cb(); },
    setBadgeBackgroundColor: function () {}
  },
  alarms: { create: function () {}, onAlarm: makeEvent() },
  webRequest: captured.webRequest,
  tabs: {
    onRemoved: makeEvent(),
    onUpdated: makeEvent(),
    query: function () { return Promise.resolve([{ id: 11, url: 'https://www.douyin.com/video/7300000000000000000' }]); },
    sendMessage: function (tabId, msg, cb) { if (cb) cb({ ok: true }); }
  },
  scripting: { executeScript: function () { return Promise.resolve([]); } },
  notifications: { create: function (id, options, cb) { captured.notifications.push(options); if (cb) cb(); } },
  downloads: { download: function () { return Promise.resolve(1); } }
};

// ---------------------------------------------------------------------------
// 载入 background.js
// ---------------------------------------------------------------------------

const bgSource = fs.readFileSync(path.join(ROOT, 'background.js'), 'utf8');
const exposed = '\n;globalThis.__t = { recordMedia: recordMedia, getTabMedia: getTabMedia, classify: classify, ' +
  'itemId: itemId, buildSendTask: buildSendTask, parseM3U8Text: parseM3U8Text, qualityFromUrl: qualityFromUrl };\n';

const sandbox = {
  chrome: chromeStub,
  WebSocket: FakeWebSocket,
  fetch: function () {
    return Promise.resolve({ ok: false, headers: { get: function () { return ''; } }, text: function () { return Promise.resolve(''); } });
  },
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

// ---------------------------------------------------------------------------
// 抖音真实 URL 形态
// ---------------------------------------------------------------------------

const TAB = 11;
const PAGE = 'https://www.douyin.com/video/7300000000000000000';

/** 抖音 HEVC 视频轨：douyinvod 域名、路径里没有扩展名、mime_type 在 query 里 */
const HEVC_VIDEO_URL =
  'https://v3-web.douyinvod.com/2f2b1c8e9a/video/tos/cn/tos-cn-ve-15/oQABCDEFG/?a=6383&ch=26&cr=3&dr=0&lr=all' +
  '&cd=0%7C0%7C0%7C3&cv=1&br=3282&bt=3282&cs=0&ds=6&ft=4k6uXhB4vz&mime_type=video_mp4&qs=0&rc=abc&os=xyz' +
  '&uid=123456&sid=987654&sign=deadbeef&l=20261018120000A1B2C3D4';

/** 同一视频的音频轨：独立 URL，mime_type=audio_mp4 */
const AUDIO_URL =
  'https://v3-web.douyinvod.com/2f2b1c8e9a/video/tos/cn/tos-cn-ve-15/oQHIJKLMN/?a=6383&ch=26&cr=3&dr=0&lr=all' +
  '&br=192&bt=192&cs=0&ds=4&ft=4k6uXhB4vz&mime_type=audio_mp4&qs=0&rc=def&os=xyz&uid=123456&sid=987654&sign=cafebabe';

/** 同视频不同清晰度：不同 URL（br= 不同、路径不同） */
const VIDEO_720_URL =
  'https://v3-web.douyinvod.com/9a8b7c6d/video/tos/cn/tos-cn-ve-15/oQ720P/?a=6383&ch=26&cr=3&dr=0&lr=all' +
  '&br=1024&bt=1024&cs=0&ds=6&ft=4k6uXhB4vz&mime_type=video_mp4&qs=0&rc=ghi&os=xyz&uid=123456&sid=987654&sign=1234abcd';

const VIDEO_TOTAL_BYTES = 10485760; // 10 MiB
const CHUNK = 4194304;             // 4 MiB -> 正好切成 3 段

// ---------------------------------------------------------------------------
// 模拟 webRequest 三段：onBeforeSendHeaders -> onBeforeRequest -> onHeadersReceived
// ---------------------------------------------------------------------------

function fire(url, opts) {
  const o = opts || {};
  const requestId = o.requestId || ('req-' + Math.random().toString(36).slice(2));
  const requestHeaders = [{ name: 'Referer', value: PAGE }];
  if (o.range) requestHeaders.push({ name: 'Range', value: o.range });
  captured.webRequest.onBeforeSendHeaders.listeners.forEach(function (fn) {
    fn({ requestId: requestId, url: url, method: 'GET', tabId: TAB, type: 'media', requestHeaders: requestHeaders });
  });
  captured.webRequest.onBeforeRequest.listeners.forEach(function (fn) {
    fn({ requestId: requestId, url: url, method: 'GET', tabId: TAB, type: 'media' });
  });
  const responseHeaders = [
    { name: 'Content-Type', value: o.contentType || 'video/mp4' },
    { name: 'Content-Length', value: String(o.contentLength || 0) }
  ];
  if (o.contentRange) responseHeaders.push({ name: 'Content-Range', value: o.contentRange });
  captured.webRequest.onHeadersReceived.listeners.forEach(function (fn) {
    fn({
      requestId: requestId,
      url: url,
      tabId: TAB,
      statusCode: o.statusCode || 200,
      method: 'GET',
      responseHeaders: responseHeaders
    });
  });
}

/** 模拟播放器用 Range 把同一个文件分成 3 段拉取（抖音网页播放器典型行为） */
function rangeChunks(url, total, chunk, contentType) {
  const ranges = [];
  for (let start = 0; start < total; start += chunk) {
    const end = Math.min(start + chunk - 1, total - 1);
    ranges.push({ start: start, end: end, total: total });
  }
  ranges.forEach(function (r) {
    fire(url, {
      range: 'bytes=' + r.start + '-' + r.end,
      contentRange: 'bytes ' + r.start + '-' + r.end + '/' + r.total,
      contentLength: r.end - r.start + 1,
      statusCode: 206,
      contentType: contentType || 'video/mp4; codecs="hvc1.1.6.L93.B0"'
    });
  });
  return ranges;
}

function dumpItems(items) {
  return items.map(function (it) {
    const out = {};
    ['id', 'url', 'kind', 'type', 'codec', 'hevc', 'quality', 'size', 'totalBytes', 'rangeCount',
      'audioOnly', 'audioUrl', 'audioCodec', 'height', 'width', 'status', 'site', 'isFragment']
      .forEach(function (k) { if (it[k] !== undefined && it[k] !== null && it[k] !== '') out[k] = it[k]; });
    return out;
  });
}

// ---------------------------------------------------------------------------
// 主流程
// ---------------------------------------------------------------------------

async function main() {
  if (!DUMP) console.log('\n[抖音 HEVC / Range 分段 / 音视频分离]');

  // ---- 场景 A：同一个 HEVC 地址被 Range 分 3 段拉取
  rangeChunks(HEVC_VIDEO_URL, VIDEO_TOTAL_BYTES, CHUNK);
  // 重复一次同样的 3 段（真实播放器会 seek / 预读重复拉同一段），
  // 用于确认"重复的同一段"不会把条目拆开、也不会把大小改小
  rangeChunks(HEVC_VIDEO_URL, VIDEO_TOTAL_BYTES, CHUNK);

  // ---- 场景 B：独立音频轨（audio/mp4）
  fire(AUDIO_URL, { contentType: 'audio/mp4', contentLength: 786432, statusCode: 200 });

  // ---- 场景 C：同视频不同清晰度（不同 URL）
  fire(VIDEO_720_URL, {
    contentType: 'video/mp4; codecs="avc1.64001f"',
    contentLength: 3145728,
    statusCode: 200
  });

  // ---- 场景 D：CDN 只回 application/octet-stream，靠 URL 里的 mime_type= 才能识别
  const OCTET_URL =
    'https://v26-web.douyinvod.com/aa11bb22/video/tos/cn/tos-cn-ve-15/oQOCTET/?a=6383&mime_type=video_mp4' +
    '&br=4096&bt=4096&sign=feedface';
  fire(OCTET_URL, { contentType: 'application/octet-stream', contentLength: 4194304, statusCode: 200 });

  // ---- 场景 E：hev1 写法（同样是 HEVC，只是 codec 标签不同）
  const HEV1_URL =
    'https://v3-web.douyinvod.com/cc33dd44/video/tos/cn/tos-cn-ve-15/oQHEV1/?a=6383&mime_type=video_mp4' +
    '&br=2048&bt=2048&sign=0badf00d';
  fire(HEV1_URL, {
    contentType: 'video/mp4; codecs="hev1.1.6.L93.B0"',
    contentLength: 2097152,
    statusCode: 200
  });

  await sleep(120);

  const media = await T.getTabMedia(TAB);
  const items = media.items || [];

  if (DUMP) {
    const sendTasks = items
      .filter(function (it) { return !it.isFragment; })
      .map(function (it) { return T.buildSendTask(it); });
    console.log(JSON.stringify({
      pageUrl: media.pageUrl,
      count: items.length,
      items: dumpItems(items),
      sendTasks: sendTasks
    }, null, 2));
    process.exit(0);
  }

  const byUrl = {};
  items.forEach(function (it) { byUrl[it.url] = it; });

  // ---- A
  const hevc = byUrl[HEVC_VIDEO_URL];
  check('A1 同一地址的多段 Range 合并成 1 条', items.filter(function (it) {
    return it.url === HEVC_VIDEO_URL;
  }).length === 1, items.length);
  check('A2 识别 HEVC codec = hvc1', !!hevc && hevc.codec === 'hvc1', hevc && hevc.codec);
  check('A3 标记 hevc = true', !!hevc && hevc.hevc === true, hevc && hevc.hevc);
  check('A4 回填 totalBytes（Content-Range 总长）', !!hevc && hevc.totalBytes === VIDEO_TOTAL_BYTES,
    hevc && hevc.totalBytes);
  check('A5 size 取总长而不是第一段大小', !!hevc && hevc.size === VIDEO_TOTAL_BYTES,
    hevc && { size: hevc.size, total: hevc.totalBytes });
  check('A6 记录 Range 次数（诊断用，6 次请求）', !!hevc && hevc.rangeCount === 6, hevc && hevc.rangeCount);
  check('A7 Range 条目不算 HLS 分片（不能被当成分片隐藏/丢弃）', !!hevc && hevc.isFragment === false,
    hevc && hevc.isFragment);

  // ---- B
  const audio = byUrl[AUDIO_URL];
  check('B1 独立音频轨是单独条目（不与视频合并）', !!audio);
  check('B2 音频轨标记 audioOnly', !!audio && audio.audioOnly === true, audio && audio.audioOnly);
  check('B3 音频轨 kind = file + type = audio', !!audio && audio.kind === 'file' && audio.type === 'audio',
    audio && { kind: audio.kind, type: audio.type });
  check('B4 视频条目关联 audioUrl', !!hevc && hevc.audioUrl === AUDIO_URL, hevc && hevc.audioUrl);
  check('B5 视频条目标记 needsMerge', !!hevc && hevc.needsMerge === true, hevc && hevc.needsMerge);
  check('B6 音频条目不重复挂 audioUrl', !audio || !audio.audioUrl, audio && audio.audioUrl);

  // ---- C
  const v720 = byUrl[VIDEO_720_URL];
  check('C1 不同清晰度是不同条目', !!v720 && !!hevc && v720.id !== hevc.id);
  check('C2 720p 条目 codec = avc1', !!v720 && v720.codec === 'avc1', v720 && v720.codec);
  check('C3 720p 条目不是 hevc', !!v720 && !v720.hevc);
  check('C4 不同清晰度之间不互相覆盖 size', !!v720 && v720.size === 3145728, v720 && v720.size);

  // ---- D
  const octet = byUrl[OCTET_URL];
  check('D1 octet-stream + mime_type=video_mp4 仍被识别', !!octet, Object.keys(byUrl));
  check('D2 octet 条目 kind = video', !!octet && octet.kind === 'video', octet && octet.kind);

  // ---- E
  const hev1 = byUrl[HEV1_URL];
  check('E1 hev1 写法被识别', !!hev1 && hev1.codec === 'hev1', hev1 && hev1.codec);
  check('E2 hev1 也标记 hevc = true', !!hev1 && hev1.hevc === true, hev1 && hev1.hevc);

  // ---- 契约
  const task = T.buildSendTask(hevc);
  check('F1 发送任务带 codec', task.codec === 'hvc1', task.codec);
  check('F2 发送任务带 totalBytes', task.totalBytes === VIDEO_TOTAL_BYTES, task.totalBytes);
  check('F3 发送任务带 audioUrl', task.audioUrl === AUDIO_URL, task.audioUrl);
  check('F4 发送任务带 audioOnly（false 时省略或为 false）', task.audioOnly === false || task.audioOnly === undefined,
    task.audioOnly);
  const audioTask = T.buildSendTask(audio);
  check('F5 音频任务 audioOnly = true', audioTask.audioOnly === true, audioTask.audioOnly);
  check('F6 音频任务 kind = file（App 侧按直链处理）', audioTask.kind === 'file', audioTask.kind);
  check('F7 任务不带 Range 请求头', !(task.headers && (task.headers.Range || task.headers.range)), task.headers);

  // ---- 边界：非抖音的普通 mp4 不应被误判成 HEVC
  fire('https://cdn.example.com/plain.mp4', { contentType: 'video/mp4', contentLength: 1024, statusCode: 200 });
  await sleep(60);
  const after = await T.getTabMedia(TAB);
  const plain = (after.items || []).find(function (it) { return it.url === 'https://cdn.example.com/plain.mp4'; });
  check('G1 普通 mp4 不标记 hevc', !!plain && !plain.hevc, plain && plain.codec);
  check('G2 普通 mp4 不关联音轨', !!plain && !plain.audioUrl, plain && plain.audioUrl);

  console.log('\n========================================');
  console.log('断言 ' + checks + ' 项，失败 ' + failures + ' 项');
  console.log('========================================\n');
  process.exit(failures ? 1 : 0);
}

main().catch(function (err) {
  console.error('测试异常：', err && err.stack ? err.stack : err);
  process.exit(1);
});
