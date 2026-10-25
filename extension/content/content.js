/*
 * content.js —— 运行在 ISOLATED world 的内容脚本。
 *
 * 职责：
 *  1. 接收 MAIN world（inject.js）通过 postMessage 上报的媒体（fetch/XHR/m3u8 解析结果）。
 *  2. 扫描页面 DOM：<video> / <source> 的 src、currentSrc、data-src 等，识别 blob:（MSE）播放。
 *  3. 采集页面级元信息：og:title / og:image / <title> / 视频时长 / document.cookie / userAgent。
 *  4. 转发给 background.js 统一去重、缓存与角标统计；响应 background 的"重新扫描"指令。
 *  5. 用 content.css 显示轻量中文提示（如"已发送到下载器"）。
 */
(function () {
  'use strict';

  if (window.__VSD_CONTENT_LOADED__) return;
  window.__VSD_CONTENT_LOADED__ = true;

  var CHANNEL = 'vsd-inject';
  var outbox = [];
  var outboxTimer = null;
  var lastSentSignature = {};
  var observedFingerprint = '';
  var lastMeta = null;

  // ---------------------------------------------------------------------------
  // 基础工具
  // ---------------------------------------------------------------------------

  function absolute(url) {
    try {
      return new URL(String(url), location.href).href;
    } catch (e) {
      return String(url || '');
    }
  }

  function isBlobUrl(url) {
    return String(url || '').indexOf('blob:') === 0;
  }

  function textOf(el) {
    return el && el.textContent ? el.textContent.trim() : '';
  }

  function metaContent(selector) {
    try {
      var el = document.querySelector(selector);
      if (!el) return '';
      return (el.getAttribute('content') || '').trim();
    } catch (e) {
      return '';
    }
  }

  /** 解析 ISO8601 时长（PT1H2M3S）为秒 */
  function parseIsoDuration(value) {
    if (!value) return 0;
    var m = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?)?$/i.exec(String(value).trim());
    if (!m) return 0;
    var days = parseInt(m[1] || '0', 10);
    var hours = parseInt(m[2] || '0', 10);
    var minutes = parseInt(m[3] || '0', 10);
    var seconds = parseFloat(m[4] || '0');
    return Math.round(days * 86400 + hours * 3600 + minutes * 60 + seconds);
  }

  function firstPositiveNumber() {
    for (var i = 0; i < arguments.length; i++) {
      var n = parseFloat(arguments[i]);
      if (isFinite(n) && n > 0) return Math.round(n);
    }
    return 0;
  }

  // ---------------------------------------------------------------------------
  // 页面元信息采集
  // ---------------------------------------------------------------------------

  function collectPageMeta() {
    var title =
      metaContent('meta[property="og:title"]') ||
      metaContent('meta[name="twitter:title"]') ||
      metaContent('meta[name="title"]') ||
      (document.title || '').trim() ||
      textOf(document.querySelector('h1'));

    var thumbnail =
      metaContent('meta[property="og:image:secure_url"]') ||
      metaContent('meta[property="og:image"]') ||
      metaContent('meta[name="twitter:image"]') ||
      metaContent('meta[name="twitter:image:src"]') ||
      metaContent('meta[itemprop="image"]');

    var duration = firstPositiveNumber(
      metaContent('meta[property="og:video:duration"]'),
      metaContent('meta[property="video:duration"]'),
      metaContent('meta[itemprop="duration"]'),
      readJsonLdDuration(),
      readBestVideoDuration()
    );

    var cookies = '';
    try { cookies = document.cookie || ''; } catch (e) { cookies = ''; }

    return {
      url: location.href,
      title: title,
      thumbnail: thumbnail ? absolute(thumbnail) : '',
      duration: duration,
      cookies: cookies,
      userAgent: navigator.userAgent || '',
      referer: location.href,
      isTopFrame: window === window.top
    };
  }

  /** 从 JSON-LD 中读取 duration */
  function readJsonLdDuration() {
    try {
      var scripts = document.querySelectorAll('script[type="application/ld+json"]');
      for (var i = 0; i < scripts.length && i < 8; i++) {
        var raw = scripts[i].textContent || '';
        if (raw.indexOf('duration') === -1) continue;
        var data = JSON.parse(raw);
        var found = findDurationInObject(data, 0);
        if (found) return found;
      }
    } catch (e) { /* 忽略 */ }
    return 0;
  }

  function findDurationInObject(obj, depth) {
    if (!obj || depth > 4) return 0;
    if (Array.isArray(obj)) {
      for (var i = 0; i < obj.length; i++) {
        var r = findDurationInObject(obj[i], depth + 1);
        if (r) return r;
      }
      return 0;
    }
    if (typeof obj !== 'object') return 0;
    if (typeof obj.duration === 'string') {
      var iso = parseIsoDuration(obj.duration);
      if (iso) return iso;
      var plain = parseFloat(obj.duration);
      if (isFinite(plain) && plain > 0) return Math.round(plain);
    }
    if (typeof obj.duration === 'number' && obj.duration > 0) return Math.round(obj.duration);
    for (var key in obj) {
      if (!Object.prototype.hasOwnProperty.call(obj, key)) continue;
      var nested = findDurationInObject(obj[key], depth + 1);
      if (nested) return nested;
    }
    return 0;
  }

  /** 取页面上最长的 video 时长 */
  function readBestVideoDuration() {
    var best = 0;
    try {
      var videos = document.querySelectorAll('video');
      for (var i = 0; i < videos.length; i++) {
        var d = parseFloat(videos[i].duration);
        if (isFinite(d) && d > best) best = d;
      }
    } catch (e) { /* 忽略 */ }
    return best ? Math.round(best) : 0;
  }

  // ---------------------------------------------------------------------------
  // DOM 扫描
  // ---------------------------------------------------------------------------

  /** 扫描 <video> / <source> / 播放器容器 */
  function scanDomMedia() {
    var items = [];
    var seen = {};

    function push(url, extra) {
      if (!url) return;
      var abs = isBlobUrl(url) ? String(url) : absolute(url);
      var key = abs;
      if (seen[key]) {
        // 合并补充信息
        var existing = seen[key];
        for (var k in extra) {
          if (Object.prototype.hasOwnProperty.call(extra, k) && !existing[k]) existing[k] = extra[k];
        }
        return;
      }
      var item = {
        url: abs,
        via: 'dom',
        source: 'dom'
      };
      for (var j in extra) {
        if (Object.prototype.hasOwnProperty.call(extra, j)) item[j] = extra[j];
      }
      seen[key] = item;
      items.push(item);
    }

    try {
      var videos = document.querySelectorAll('video');
      for (var i = 0; i < videos.length; i++) {
        var video = videos[i];
        var shared = {
          duration: firstPositiveNumber(video.duration),
          width: video.videoWidth || 0,
          height: video.videoHeight || 0,
          poster: video.poster || '',
          isBlob: false
        };
        var candidates = [
          video.currentSrc,
          video.getAttribute('src'),
          video.getAttribute('data-src'),
          video.getAttribute('data-original'),
          video.getAttribute('data-url'),
          video.getAttribute('data-video-url'),
          video.getAttribute('data-play-url')
        ];
        for (var c = 0; c < candidates.length; c++) {
          var raw = candidates[c];
          if (!raw) continue;
          if (isBlobUrl(raw)) {
            shared.isBlob = true;
            continue;
          }
          if (raw.indexOf('data:') === 0 || raw.indexOf('javascript:') === 0) continue;
          push(raw, shared);
        }
        if (shared.isBlob) {
          // MSE 播放：真实地址由 inject.js 从网络层捕获
          push('blob:mse#video', {
            isBlob: true,
            mse: true,
            duration: shared.duration,
            width: shared.width,
            height: shared.height,
            poster: shared.poster
          });
        }

        var sources = video.querySelectorAll('source');
        for (var s = 0; s < sources.length; s++) {
          var srcEl = sources[s];
          var sraw = srcEl.getAttribute('src') || srcEl.getAttribute('data-src');
          if (!sraw || isBlobUrl(sraw) || sraw.indexOf('data:') === 0) continue;
          push(sraw, {
            mime: srcEl.getAttribute('type') || '',
            duration: shared.duration,
            width: shared.width,
            height: shared.height
          });
        }
      }
    } catch (e) { /* 忽略 */ }

    // og:video / twitter:player:stream
    try {
      var metaSelectors = [
        'meta[property="og:video:secure_url"]',
        'meta[property="og:video:url"]',
        'meta[property="og:video"]',
        'meta[name="twitter:player:stream"]'
      ];
      for (var m = 0; m < metaSelectors.length; m++) {
        var content = metaContent(metaSelectors[m]);
        if (content && content.indexOf('http') === 0) {
          push(content, { via: 'meta', mime: metaContent('meta[property="og:video:type"]') });
        }
      }
    } catch (e) { /* 忽略 */ }

    // 页面里直接指向媒体文件的链接（低优先级，标记为 dom-link）
    try {
      var links = scanAnchors();
      for (var li = 0; li < links.length; li++) {
        push(links[li].url, { via: 'dom-link', source: 'dom-link' });
      }
    } catch (e) { /* 忽略 */ }

    return items;
  }

  /** 扫描页面里直接指向媒体文件的链接（低优先级） */
  function scanAnchors() {
    var items = [];
    try {
      var anchors = document.querySelectorAll('a[href]');
      var count = 0;
      for (var i = 0; i < anchors.length && count < 15; i++) {
        var href = anchors[i].getAttribute('href') || '';
        if (!/\.(m3u8|mpd|mp4|webm|flv|mkv|mov|m4v|ts|mp3|m4a)(?:[?#]|$)/i.test(href)) continue;
        if (href.indexOf('javascript:') === 0) continue;
        items.push({ url: absolute(href), via: 'dom-link', source: 'dom-link' });
        count++;
      }
    } catch (e) { /* 忽略 */ }
    return items;
  }

  /** 计算页面指纹，用于判断是否值得重新扫描 */
  function fingerprint() {
    var videoCount = 0;
    var firstSrc = '';
    try {
      videoCount = document.querySelectorAll('video').length;
      var v = document.querySelector('video');
      if (v) firstSrc = v.currentSrc || v.getAttribute('src') || '';
    } catch (e) { /* 忽略 */ }
    return location.href + '|' + videoCount + '|' + firstSrc;
  }

  // ---------------------------------------------------------------------------
  // 上报到 background
  // ---------------------------------------------------------------------------

  function enqueue(items, meta) {
    for (var i = 0; i < items.length; i++) {
      outbox.push(items[i]);
    }
    if (meta) lastMeta = meta;
    scheduleSend();
  }

  function scheduleSend() {
    if (outboxTimer) return;
    outboxTimer = setTimeout(function () {
      outboxTimer = null;
      sendNow();
    }, 400);
  }

  function sendNow() {
    if (!outbox.length) return;
    var meta = collectPageMeta();
    var items = outbox;
    outbox = [];

    // 简单去重：同一 (url+via) 在短时间内只发一次
    var deduped = [];
    for (var i = 0; i < items.length; i++) {
      var it = items[i];
      var sig = String(it.url) + '|' + String(it.via || '');
      var last = lastSentSignature[sig] || 0;
      if (Date.now() - last < 3000) continue;
      lastSentSignature[sig] = Date.now();
      deduped.push(it);
    }
    if (!deduped.length) return;

    try {
      chrome.runtime.sendMessage({
        cmd: 'report',
        frameUrl: location.href,
        isTopFrame: window === window.top,
        page: meta,
        items: deduped
      }, function () {
        // 读取 lastError，避免 "Unchecked runtime.lastError"
        void chrome.runtime.lastError;
      });
    } catch (e) {
      // service worker 重启瞬间可能失败，回填队列稍后重试
      for (var k = 0; k < deduped.length; k++) outbox.push(deduped[k]);
      setTimeout(scheduleSend, 1500);
    }
  }

  // ---------------------------------------------------------------------------
  // 接收 inject.js（MAIN world）消息
  // ---------------------------------------------------------------------------

  window.addEventListener('message', function (event) {
    if (event.source !== window) return;
    var data = event.data;
    if (!data || data.__vsd !== CHANNEL || data.dir !== 'to-content') return;

    if (data.probeAnswer) {
      // 页面探针回答：把 MSE 状态与已捕获 URL 一并上报（作为补充信息）
      try {
        var payload = data.payload || {};
        if (payload.mse) {
          enqueue([{
            url: 'blob:mse#probe',
            via: 'mse-probe',
            mse: true,
            isBlob: true,
            mime: (payload.mseHints && payload.mseHints.length) ? payload.mseHints[payload.mseHints.length - 1].mime : ''
          }], null);
        }
      } catch (e) { /* 忽略 */ }
      return;
    }

    var items = Array.isArray(data.items) ? data.items : [];
    if (items.length) enqueue(items, null);
  }, false);

  /** 向 MAIN world 请求补充信息（MSE 状态等） */
  function probeMainWorld() {
    try {
      window.postMessage({
        __vsd: CHANNEL,
        dir: 'to-inject',
        action: 'probe',
        requestId: String(Date.now())
      }, '*');
    } catch (e) { /* 忽略 */ }
  }

  // ---------------------------------------------------------------------------
  // 接收 background 指令 / 轻提示
  // ---------------------------------------------------------------------------

  chrome.runtime.onMessage.addListener(function (message, sender, sendResponse) {
    if (!message || !message.cmd) return;

    if (message.cmd === 'scan') {
      // popup / background 主动要求重新扫描
      var found = scanDomMedia();
      probeMainWorld();
      enqueue(found, null);
      sendResponse({ ok: true, scanned: found.length });
      return true;
    }

    if (message.cmd === 'toast') {
      showToast(message.text || '', message.level || 'info');
      sendResponse({ ok: true });
      return true;
    }

    if (message.cmd === 'ping') {
      sendResponse({ ok: true });
      return true;
    }
  });

  // ---------------------------------------------------------------------------
  // 轻量提示 UI（样式见 content.css）
  // ---------------------------------------------------------------------------

  var toastTimer = null;
  var toastEl = null;

  function showToast(text, level) {
    if (!text) return;
    try {
      if (!toastEl) {
        toastEl = document.createElement('div');
        toastEl.className = 'vsd-toast';
        toastEl.setAttribute('role', 'status');
        (document.body || document.documentElement).appendChild(toastEl);
      }
      toastEl.textContent = text;
      toastEl.setAttribute('data-level', level || 'info');
      toastEl.classList.add('vsd-toast--visible');
      if (toastTimer) clearTimeout(toastTimer);
      toastTimer = setTimeout(function () {
        if (toastEl) toastEl.classList.remove('vsd-toast--visible');
      }, 3200);
    } catch (e) { /* 忽略 */ }
  }

  // ---------------------------------------------------------------------------
  // 扫描调度
  // ---------------------------------------------------------------------------

  var scanTimer = null;
  function scheduleScan(delay) {
    if (scanTimer) clearTimeout(scanTimer);
    scanTimer = setTimeout(function () {
      scanTimer = null;
      runScan();
    }, delay || 600);
  }

  function runScan() {
    var fp = fingerprint();
    if (fp === observedFingerprint) return;
    observedFingerprint = fp;
    var found = scanDomMedia();
    if (found.length) enqueue(found, null);
    probeMainWorld();
  }

  function startObserving() {
    runScan();
    scheduleScan(1500);
    setTimeout(runScan, 4000);
    setTimeout(runScan, 10000);

    try {
      var observer = new MutationObserver(function () {
        // DOM 变化时低频重扫，避免性能问题
        scheduleScan(1200);
      });
      observer.observe(document.documentElement || document, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ['src', 'data-src', 'href', 'poster']
      });
    } catch (e) { /* 忽略 */ }

    // 视频开始播放 / 元数据就绪时再扫一次
    document.addEventListener('loadedmetadata', function () { scheduleScan(300); }, true);
    document.addEventListener('play', function () { scheduleScan(300); }, true);
    window.addEventListener('load', function () { scheduleScan(500); }, { once: true });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', startObserving, { once: true });
    // document_start 阶段立即做一次极轻量扫描（有些播放器很早就插入 video）
    scheduleScan(300);
  } else {
    startObserving();
  }
})();
