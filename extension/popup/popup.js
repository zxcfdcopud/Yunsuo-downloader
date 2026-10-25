/*
 * popup.js —— 工具栏弹窗逻辑。
 *
 * 功能：
 *  - 展示当前标签页探测到的媒体（按 tabId 隔离，不串页）
 *  - 去重展示、类型/清晰度/来源标签、HLS 清晰度列表、加密标记
 *  - 一键发送到下载器（支持勾选部分、全部发送）、复制直链、清空列表
 *  - 显示与桌面 App 的连接状态（未连接 / 已连接 + App 版本 / 令牌错误 4001）
 *
 * 无第三方依赖，全部原生 JS + chrome API。
 */
'use strict';

// ---------------------------------------------------------------------------
// 状态
// ---------------------------------------------------------------------------

const state = {
  tabId: -1,
  tabUrl: '',
  tabTitle: '',
  items: [],
  page: {},
  conn: { state: 'disconnected', message: '', appVersion: '', downloadDir: '' },
  settings: {},
  pendingCount: 0,
  lastSendMessage: '',
  unsupported: false
};

/** 上次渲染的列表指纹（避免轮询导致的重复渲染） */
let lastListSignature = '';

const SITE_LABELS = {
  bilibili: '哔哩哔哩',
  youtube: 'YouTube',
  douyin: '抖音',
  kuaishou: '快手',
  xiaohongshu: '小红书',
  other: '其他站点'
};

const VIA_LABELS = {
  webRequest: '网络嗅探',
  inject: '播放器请求',
  'background-parse': '后台解析',
  dom: '页面元素',
  'dom-link': '页面链接',
  'inject-mse': 'MSE 探测'
};

// ---------------------------------------------------------------------------
// DOM 引用
// ---------------------------------------------------------------------------

const el = {
  list: document.getElementById('list'),
  empty: document.getElementById('emptyState'),
  tabHost: document.getElementById('tabHost'),
  connBadge: document.getElementById('connBadge'),
  connText: document.getElementById('connText'),
  tokenBanner: document.getElementById('tokenBanner'),
  queueBanner: document.getElementById('queueBanner'),
  queueText: document.getElementById('queueText'),
  statusText: document.getElementById('statusText'),
  countText: document.getElementById('countText'),
  appInfo: document.getElementById('appInfo'),
  showFragments: document.getElementById('showFragments'),
  btnRescan: document.getElementById('btnRescan'),
  btnSendAll: document.getElementById('btnSendAll'),
  btnCopyAll: document.getElementById('btnCopyAll'),
  btnClear: document.getElementById('btnClear'),
  btnOptions: document.getElementById('btnOptions'),
  btnOpenOptions: document.getElementById('btnOpenOptions'),
  btnRetryConnect: document.getElementById('btnRetryConnect'),
  linkOptions: document.getElementById('linkOptions')
};

// ---------------------------------------------------------------------------
// 基础工具
// ---------------------------------------------------------------------------

function sendBg(message) {
  return new Promise(function (resolve) {
    try {
      chrome.runtime.sendMessage(message, function (response) {
        const err = chrome.runtime.lastError;
        if (err) {
          resolve({ ok: false, message: '后台未响应：' + err.message });
          return;
        }
        resolve(response || { ok: false, message: '后台未响应' });
      });
    } catch (e) {
      resolve({ ok: false, message: String(e && e.message ? e.message : e) });
    }
  });
}

function escapeHtml(text) {
  return String(text === undefined || text === null ? '' : text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function formatSize(bytes) {
  const n = parseInt(bytes, 10) || 0;
  if (n <= 0) return '';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(0) + ' KB';
  if (n < 1024 * 1024 * 1024) return (n / 1024 / 1024).toFixed(1) + ' MB';
  return (n / 1024 / 1024 / 1024).toFixed(2) + ' GB';
}

function formatDuration(seconds) {
  const s = parseInt(seconds, 10) || 0;
  if (s <= 0) return '';
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return h + ':' + String(m).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
  return m + ':' + String(sec).padStart(2, '0');
}

function kindLabel(item) {
  if (item.isBlob && String(item.url).indexOf('blob:mse') === 0) return 'MSE';
  const t = String(item.type || '').toLowerCase();
  if (item.isHls || t === 'hls') return 'HLS';
  if (t === 'dash') return 'DASH';
  if (t === 'mp4') return 'MP4';
  if (t === 'webm') return 'WebM';
  if (t === 'flv') return 'FLV';
  if (t === 'mkv') return 'MKV';
  if (t === 'ts') return 'TS 分片';
  if (t === 'm4s') return 'M4S 分片';
  if (t === 'audio') return '音频';
  if (t === 'blob') return 'MSE';
  return (t || 'FILE').toUpperCase();
}

/** 编码格式的中文标签：hvc1/hev1 -> HEVC(H.265)，avc1 -> H.264 */
function codecLabel(codec) {
  const c = String(codec || '').toLowerCase();
  if (!c) return '';
  if (c === 'hvc1' || c === 'hev1') return 'HEVC · ' + c.toUpperCase();
  if (c === 'avc1') return 'H.264 · AVC1';
  if (c === 'av01') return 'AV1';
  if (c === 'vp09') return 'VP9';
  if (c === 'mp4a') return 'AAC';
  if (c === 'opus') return 'Opus';
  if (c === 'ac3' || c === 'eac3') return 'Dolby ' + c.toUpperCase();
  return c.toUpperCase();
}

function kindClass(item) {
  if (item.isHls) return 'tag tag--kind';
  if (String(item.type) === 'dash') return 'tag tag--kind tag--dash';
  return 'tag tag--kind tag--file';
}

function shortUrl(url) {
  const s = String(url || '');
  return s.length > 120 ? s.slice(0, 117) + '…' : s;
}

function fileNameOf(url) {
  try {
    const u = new URL(url);
    const name = decodeURIComponent(u.pathname.split('/').filter(Boolean).pop() || '');
    return name || u.hostname;
  } catch (e) {
    return String(url || '').slice(-40);
  }
}

// ---------------------------------------------------------------------------
// 渲染
// ---------------------------------------------------------------------------

function setStatus(text, level) {
  el.statusText.textContent = text || '';
  el.statusText.className = 'status' + (level ? ' status--' + level : '');
}

function renderConn() {
  const conn = state.conn || {};
  let cls = 'conn conn--idle';
  let text = '未连接';

  if (conn.state === 'connected') {
    cls = 'conn conn--ok';
    text = conn.appVersion ? '已连接 v' + conn.appVersion : '已连接';
  } else if (conn.state === 'token-error') {
    cls = 'conn conn--error';
    text = '令牌错误';
  } else if (conn.state === 'connecting' || conn.state === 'handshaking') {
    cls = 'conn conn--pending';
    text = conn.state === 'handshaking' ? '握手中…' : '连接中…';
  } else {
    cls = 'conn conn--idle';
    text = '未连接';
  }

  el.connBadge.className = cls;
  el.connText.textContent = text;
  el.connBadge.title = conn.message || text;

  el.tokenBanner.classList.toggle('hidden', conn.state !== 'token-error');

  const info = [];
  if (conn.state === 'connected') {
    info.push('下载器 v' + (conn.appVersion || '?'));
    if (conn.downloadDir) info.push(conn.downloadDir);
  } else if (conn.message) {
    info.push(conn.message);
  } else {
    info.push('下载器：未连接');
  }
  el.appInfo.textContent = info.join(' · ');

  if (state.pendingCount > 0) {
    el.queueBanner.classList.remove('hidden');
    el.queueText.textContent = '有 ' + state.pendingCount + ' 个任务排队中，连接成功后会自动发送';
  } else {
    el.queueBanner.classList.add('hidden');
  }
}

function buildCard(item) {
  const card = document.createElement('div');
  card.className = 'card';
  const isMseHint = item.isBlob && String(item.url).indexOf('blob:mse') === 0;
  if (item.isFragment) card.classList.add('card--frag');
  if (isMseHint) card.classList.add('card--hint');

  const title = item.title || fileNameOf(item.url) || '未命名媒体';

  const tags = [];
  tags.push('<span class="' + (isMseHint ? 'tag tag--file' : kindClass(item)) + '">' + escapeHtml(kindLabel(item)) + '</span>');
  if (item.site && SITE_LABELS[item.site]) {
    tags.push('<span class="tag tag--site">' + escapeHtml(SITE_LABELS[item.site]) + '</span>');
  }
  if (item.quality) tags.push('<span class="tag">' + escapeHtml(item.quality) + '</span>');
  // 编码格式：让用户一眼看出是 HEVC 还是 H.264
  const codecText = codecLabel(item.codec);
  if (codecText) {
    tags.push('<span class="tag' + (item.hevc ? ' tag--codec-hevc' : ' tag--codec') + '">' +
      escapeHtml(codecText) + '</span>');
  }
  if (item.audioOnly) tags.push('<span class="tag tag--audio">纯音轨</span>');
  if (item.needsMerge && item.audioUrl) tags.push('<span class="tag tag--ok">含独立音轨</span>');
  if (item.encrypted) tags.push('<span class="tag tag--warn">加密</span>');
  else if (String(item.type) === 'hls') tags.push('<span class="tag tag--ok">未加密</span>');
  // 有总长时显示总长（Range 分段读取的 content-length 只是其中一段，不能当文件大小）
  const sizeBytes = parseInt(item.totalBytes, 10) || parseInt(item.size, 10) || 0;
  if (sizeBytes) tags.push('<span class="tag">' + escapeHtml(formatSize(sizeBytes)) + '</span>');
  if (item.rangeCount > 1) tags.push('<span class="tag">分段 ' + item.rangeCount + '</span>');
  if (item.duration) tags.push('<span class="tag">时长 ' + escapeHtml(formatDuration(item.duration)) + '</span>');
  if (Array.isArray(item.sources) && item.sources.length) {
    const labels = item.sources.map(function (s) { return VIA_LABELS[s] || s; });
    tags.push('<span class="tag">来源：' + escapeHtml(labels.join('/')) + '</span>');
  }

  // HEVC 提示：能嗅探到 ≠ 能播；部分播放器/系统缺 HEVC 解码器
  let warnHtml = '';
  if (item.hevc) {
    warnHtml = '<p class="card-warn">该视频为 HEVC(H.265) 编码，部分播放器/系统不支持直接播放，' +
      '下载后可能需要转码（ffmpeg）或用支持 HEVC 的播放器打开。</p>';
  } else if (item.needsMerge && item.audioUrl) {
    warnHtml = '<p class="card-hint">该视频音轨独立，下载器会自动合并音视频（需要 ffmpeg）。</p>';
  }

  let variantsHtml = '';
  if (Array.isArray(item.variants) && item.variants.length > 1) {
    const chips = item.variants.slice(0, 8).map(function (v) {
      const label = v.quality || v.resolution || (v.bandwidth ? Math.round(v.bandwidth / 1000) + 'kbps' : '轨');
      return '<span class="variant">' + escapeHtml(label) + '</span>';
    }).join('');
    variantsHtml = '<div class="variants">' + chips + '</div>';
  }

  const actions = [];
  if (!isMseHint) {
    actions.push('<button class="btn btn--primary btn--tiny" data-act="send" data-id="' + escapeHtml(item.id) + '">发送到下载器</button>');
    actions.push('<button class="btn btn--tiny" data-act="copy" data-id="' + escapeHtml(item.id) + '">复制直链</button>');
    if (!item.isHls && String(item.type) !== 'dash' && !item.isFragment) {
      actions.push('<button class="btn btn--tiny" data-act="browser" data-id="' + escapeHtml(item.id) + '">浏览器下载</button>');
    }
  } else {
    actions.push('<button class="btn btn--tiny" data-act="rescan" data-id="' + escapeHtml(item.id) + '">继续嗅探</button>');
  }

  card.innerHTML =
    '<div class="card-top">' +
      '<input type="checkbox" data-id="' + escapeHtml(item.id) + '"' + (item.isFragment ? '' : ' checked') + '>' +
      '<div class="card-main">' +
        '<p class="card-title" title="' + escapeHtml(title) + '">' + escapeHtml(title) + '</p>' +
        '<div class="tags">' + tags.join('') + '</div>' +
        variantsHtml +
        warnHtml +
        '<p class="url" title="' + escapeHtml(item.url) + '">' + escapeHtml(shortUrl(item.url)) + '</p>' +
        '<div class="card-actions">' + actions.join('') + '</div>' +
      '</div>' +
    '</div>';
  return card;
}

function renderList() {
  const items = state.items || [];
  const showFragments = !!el.showFragments.checked;

  // 轮询会反复触发渲染，内容没变时直接跳过，避免列表闪烁/滚动位置丢失
  const signature = items.map(function (it) {
    return [it.id, it.lastSeen || 0, (it.variants && it.variants.length) || 0,
      it.encrypted ? 1 : 0, it.title || '', it.size || 0,
      it.codec || '', it.hevc ? 1 : 0, it.totalBytes || 0,
      it.audioUrl || '', it.rangeCount || 0].join(':');
  }).join('|') + '|frag=' + showFragments;
  if (signature === lastListSignature) return;
  lastListSignature = signature;

  const scrollTop = el.list.scrollTop;
  const main = [];
  const fragments = [];
  for (const it of items) {
    if (it.isFragment) fragments.push(it);
    else main.push(it);
  }

  el.list.innerHTML = '';

  if (!main.length && !fragments.length) {
    el.list.appendChild(el.empty);
    el.empty.classList.remove('hidden');
    el.countText.textContent = '0 个媒体';
    return;
  }
  el.empty.classList.add('hidden');

  main.forEach(function (it) { el.list.appendChild(buildCard(it)); });

  if (fragments.length && showFragments) {
    const title = document.createElement('div');
    title.className = 'section-title';
    title.textContent = 'HLS/DASH 分片（' + fragments.length + '）— 一般无需单独下载';
    el.list.appendChild(title);
    fragments.forEach(function (it) { el.list.appendChild(buildCard(it)); });
  }

  const sendable = main.filter(function (it) {
    return !(it.isBlob && String(it.url).indexOf('blob:mse') === 0);
  }).length;
  el.countText.textContent = sendable + ' 个可下载媒体' + (fragments.length ? '，' + fragments.length + ' 个分片' : '');
  el.list.scrollTop = scrollTop;
}

// ---------------------------------------------------------------------------
// 数据刷新
// ---------------------------------------------------------------------------

async function refreshState() {
  const res = await sendBg({ cmd: 'getState' });
  if (res && res.ok) {
    state.conn = res.conn || state.conn;
    state.settings = res.settings || {};
    state.pendingCount = res.pendingCount || 0;
  } else if (res && res.conn) {
    state.conn = res.conn;
  }
  renderConn();
}

async function refreshMedia() {
  if (state.tabId < 0) return;
  const res = await sendBg({ cmd: 'getTabMedia', tabId: state.tabId });
  if (res && res.ok) {
    state.items = res.items || [];
    state.page = res.page || {};
    state.unsupported = false;
  } else {
    state.items = [];
    state.unsupported = true;
  }
  renderList();
}

async function initTab() {
  try {
    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    const tab = tabs && tabs[0];
    if (tab) {
      state.tabId = tab.id;
      state.tabUrl = tab.url || '';
      state.tabTitle = tab.title || '';
      let host = '';
      try { host = new URL(tab.url).hostname; } catch (e) { host = tab.url || ''; }
      el.tabHost.textContent = host || '—';
      if (!/^https?:/i.test(tab.url || '')) {
        state.unsupported = true;
      }
    }
  } catch (e) { /* 忽略 */ }
}

// ---------------------------------------------------------------------------
// 操作
// ---------------------------------------------------------------------------

function checkedIds() {
  const boxes = el.list.querySelectorAll('input[type="checkbox"][data-id]');
  const ids = [];
  boxes.forEach(function (box) {
    if (box.checked) ids.push(box.getAttribute('data-id'));
  });
  return ids;
}

function findItem(id) {
  for (const it of state.items) {
    if (it.id === id) return it;
  }
  return null;
}

async function doSend(ids) {
  const payload = { cmd: 'send', tabId: state.tabId, ids: ids || [] };
  const res = await sendBg(payload);
  if (res && res.ok) {
    setStatus(res.message || '已发送', res.queued ? 'warn' : 'ok');
    state.lastSendMessage = res.message || '';
    // 在页面上给一个轻提示（content script 可能不存在，忽略失败）
    const text = res.queued ? (res.message || '任务已排队') : ('已发送 ' + (res.sent || 0) + ' 个任务到下载器');
    sendBg({ cmd: 'toastTab', tabId: state.tabId, text: text, level: res.queued ? 'info' : 'success' });
  } else {
    setStatus((res && res.message) || '发送失败', 'error');
  }
  await refreshState();
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch (e) {
    // 兜底：execCommand
    try {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand('copy');
      document.body.removeChild(ta);
      return ok;
    } catch (e2) {
      return false;
    }
  }
}

// ---------------------------------------------------------------------------
// 事件绑定
// ---------------------------------------------------------------------------

el.list.addEventListener('click', async function (event) {
  const btn = event.target.closest('button[data-act]');
  if (!btn) return;
  const act = btn.getAttribute('data-act');
  const id = btn.getAttribute('data-id');
  const item = findItem(id);

  if (act === 'send' && item) {
    btn.disabled = true;
    await doSend([id]);
    btn.disabled = false;
    return;
  }

  if (act === 'copy' && item) {
    const ok = await copyText(item.url);
    setStatus(ok ? '已复制直链' : '复制失败（请手动复制）', ok ? 'ok' : 'error');
    return;
  }

  if (act === 'browser' && item) {
    const res = await sendBg({ cmd: 'browserDownload', url: item.url, filename: item.filename || item.title || '' });
    setStatus((res && res.message) || '已交给浏览器下载', res && res.ok ? 'ok' : 'error');
    return;
  }

  if (act === 'rescan') {
    const res = await sendBg({ cmd: 'rescan', tabId: state.tabId });
    setStatus((res && res.message) || '已请求重新扫描', res && res.ok ? 'ok' : 'warn');
    setTimeout(refreshMedia, 1200);
    return;
  }
});

el.btnSendAll.addEventListener('click', async function () {
  // 勾选了条目就发勾选的；一个都没勾则发送全部可发送媒体
  let ids = checkedIds().filter(function (id) {
    const it = findItem(id);
    return it && !it.isFragment && !(it.isBlob && String(it.url).indexOf('blob:mse') === 0);
  });
  if (!ids.length) {
    ids = state.items.filter(function (it) {
      return !it.isFragment && !(it.isBlob && String(it.url).indexOf('blob:mse') === 0);
    }).map(function (it) { return it.id; });
  }
  if (!ids.length) {
    setStatus('没有可发送的媒体', 'warn');
    return;
  }
  el.btnSendAll.disabled = true;
  await doSend(ids);
  el.btnSendAll.disabled = false;
});

el.btnCopyAll.addEventListener('click', async function () {
  const urls = state.items
    .filter(function (it) { return !it.isFragment; })
    .map(function (it) { return it.url; });
  if (!urls.length) {
    setStatus('没有可复制的直链', 'warn');
    return;
  }
  const ok = await copyText(urls.join('\n'));
  setStatus(ok ? '已复制 ' + urls.length + ' 条直链' : '复制失败（请手动复制）', ok ? 'ok' : 'error');
});

el.btnClear.addEventListener('click', async function () {
  await sendBg({ cmd: 'clear', tabId: state.tabId });
  state.items = [];
  renderList();
  setStatus('已清空当前标签页列表', 'ok');
});

el.btnRescan.addEventListener('click', async function () {
  const res = await sendBg({ cmd: 'rescan', tabId: state.tabId });
  setStatus((res && res.message) || '已请求重新扫描', res && res.ok ? 'ok' : 'warn');
  setTimeout(refreshMedia, 1200);
});

el.btnRetryConnect.addEventListener('click', async function () {
  setStatus('正在重试连接…', 'warn');
  const res = await sendBg({ cmd: 'reconnect' });
  setStatus((res && res.message) || '正在重连', 'warn');
  await refreshState();
});

function openOptions() {
  try {
    chrome.runtime.openOptionsPage();
  } catch (e) {
    try { chrome.tabs.create({ url: chrome.runtime.getURL('options/options.html') }); } catch (e2) { /* 忽略 */ }
  }
}

el.btnOptions.addEventListener('click', openOptions);
el.btnOpenOptions.addEventListener('click', openOptions);
el.linkOptions.addEventListener('click', function (event) {
  event.preventDefault();
  openOptions();
});

el.showFragments.addEventListener('change', renderList);

// 后台推送的事件
chrome.runtime.onMessage.addListener(function (message) {
  if (!message || !message.cmd) return;
  if (message.cmd === 'conn-updated') {
    state.conn = message.conn || state.conn;
    renderConn();
  } else if (message.cmd === 'media-updated') {
    refreshMedia();
  } else if (message.cmd === 'pending-updated') {
    state.pendingCount = message.count || 0;
    renderConn();
  } else if (message.cmd === 'pending-flushed') {
    setStatus('已自动补发 ' + message.sent + ' 个排队任务', 'ok');
    refreshState();
  } else if (message.cmd === 'add-ack') {
    setStatus('下载器已接收 ' + (message.accepted || 0) + ' 个任务' +
      (message.rejected ? '，拒绝 ' + message.rejected + ' 个' : ''), message.rejected ? 'error' : 'ok');
  } else if (message.cmd === 'app-notify') {
    setStatus(message.message || '', message.level === 'error' ? 'error' : 'ok');
  }
});

// ---------------------------------------------------------------------------
// 启动
// ---------------------------------------------------------------------------

(async function start() {
  setStatus('正在读取探测结果…');
  await initTab();
  await refreshState();
  await refreshMedia();
  if (state.unsupported) {
    setStatus('当前页面不支持嗅探（仅支持 http/https 页面）', 'warn');
  } else if (state.items.length) {
    setStatus('探测到 ' + state.items.filter(function (it) { return !it.isFragment; }).length + ' 个媒体', 'ok');
  } else {
    setStatus('尚未探测到媒体，请播放视频或点击「刷新探测」');
  }

  // popup 生命周期内轮询，保证数据新鲜（popup 关闭后自动停止）
  setInterval(function () {
    refreshState();
    refreshMedia();
  }, 1800);
})();
