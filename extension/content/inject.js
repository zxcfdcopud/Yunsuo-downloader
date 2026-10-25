/*
 * inject.js —— 运行在页面 MAIN world（document_start）的嗅探脚本。
 *
 * 职责：
 *  1. hook window.fetch / XMLHttpRequest，捕获页面播放器（hls.js、dash.js、自研播放器）发出的
 *     媒体请求 URL、请求方法与请求头（Referer / Cookie / Origin）。
 *  2. 对 m3u8 / mpd 响应体做就地解析（复用页面自身的响应，不额外发起网络请求）：
 *     - 区分 master playlist（含 #EXT-X-STREAM-INF）与 media playlist（含 #EXTINF）
 *     - 解析清晰度列表（分辨率 / 带宽 / NAME）
 *     - 解析 #EXT-X-KEY 判断是否加密
 *  3. 探测 MSE（MediaSource）播放链路，识别 blob: 播放并回传媒体类型（mime/codec）。
 *  4. 通过 window.postMessage 把结果交给 ISOLATED world 的 content.js，再转交 background。
 *
 * 本文件必须保持"零第三方依赖、绝不破坏页面原有行为"：
 * 所有 hook 都保留原函数语义与返回值，异常一律吞掉并回退到原生实现。
 */
(function () {
  'use strict';

  // 防止重复注入（同一 frame 多次注入会重复 hook）
  if (window.__VSD_INJECT_LOADED__) return;
  window.__VSD_INJECT_LOADED__ = true;

  /** 与 content.js 约定的 postMessage 通道名 */
  var CHANNEL = 'vsd-inject';

  /** 媒体扩展名（含 HLS 分片） */
  var MEDIA_EXT_RE = /\.(m3u8|mpd|mp4|webm|flv|mkv|mov|m4v|avi|3gp|ts|m4s|mp3|m4a|aac|flac|ogg|ogv|wav)(?:[?#]|$)/i;
  var HLS_RE = /\.m3u8(?:[?#]|$)/i;
  var DASH_RE = /\.mpd(?:[?#]|$)/i;
  var SEGMENT_RE = /\.(?:ts|m4s)(?:[?#]|$)/i;

  /** 单位：字节，超过该长度的响应体不做文本解析（避免读取整个视频文件） */
  var MAX_PLAYLIST_BYTES = 2 * 1024 * 1024;

  // ---------------------------------------------------------------------------
  // 工具函数
  // ---------------------------------------------------------------------------

  /** 相对地址转绝对地址 */
  function absolutize(url) {
    try {
      return new URL(String(url), location.href).href;
    } catch (e) {
      return String(url || '');
    }
  }

  /** 以指定基地址解析相对地址（播放列表里的分片/变体必须相对播放列表地址解析） */
  function resolveAgainst(url, base) {
    try {
      return new URL(String(url), String(base || location.href)).href;
    } catch (e) {
      return absolutize(url);
    }
  }

  /** 判断是否像媒体地址 */
  function isMediaUrl(url) {
    if (!url) return false;
    var u = String(url);
    if (u.indexOf('blob:') === 0 || u.indexOf('data:') === 0) return false;
    return MEDIA_EXT_RE.test(u);
  }

  /** 把 Headers / 普通对象 / 数组 归一化成普通对象 */
  function normalizeHeaders(input) {
    var out = {};
    try {
      if (!input) return out;
      if (typeof Headers !== 'undefined' && input instanceof Headers) {
        input.forEach(function (v, k) { out[k] = v; });
      } else if (Array.isArray(input)) {
        input.forEach(function (pair) {
          if (pair && pair.length >= 2) out[String(pair[0])] = String(pair[1]);
        });
      } else if (typeof input === 'object') {
        Object.keys(input).forEach(function (k) { out[k] = String(input[k]); });
      }
    } catch (e) { /* 忽略 */ }
    return out;
  }

  /** 解析 m3u8 属性行：BANDWIDTH=123,RESOLUTION=1920x1080,NAME="1080P" */
  function parseAttributes(text) {
    var out = {};
    var re = /([A-Za-z0-9-]+)=("[^"]*"|[^,]*)/g;
    var m;
    while ((m = re.exec(text)) !== null) {
      var v = m[2];
      if (v.charAt(0) === '"') v = v.slice(1, -1);
      out[m[1].toUpperCase()] = v.trim();
    }
    return out;
  }

  /** 高度 -> 清晰度标签 */
  function qualityFromHeight(height, name) {
    var named = String(name || '').trim();
    if (named && /(\d{3,4})\s*[pP]/.test(named)) return named;
    if (height >= 2160) return '2160p';
    if (height >= 1440) return '1440p';
    if (height >= 1080) return '1080p';
    if (height >= 720) return '720p';
    if (height >= 576) return '576p';
    if (height >= 480) return '480p';
    if (height >= 360) return '360p';
    if (height >= 240) return '240p';
    return named;
  }

  /**
   * 解析 m3u8 文本。
   * @returns {{isMaster:boolean,isMedia:boolean,variants:Array,encrypted:boolean,targetDuration:number,segmentCount:number}}
   */
  function parseM3U8(text, baseUrl) {
    var result = {
      isMaster: false,
      isMedia: false,
      variants: [],
      encrypted: false,
      keyMethod: '',
      targetDuration: 0,
      segmentCount: 0
    };
    if (!text || typeof text !== 'string') return result;

    var lines = text.split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i].trim();
      if (!line) continue;

      if (line.indexOf('#EXT-X-KEY') === 0) {
        // 判断是否加密：METHOD=NONE 表示未加密
        var keyM = /METHOD=([^,]+)/i.exec(line);
        if (keyM && !/^NONE$/i.test(keyM[1].trim())) {
          result.encrypted = true;
          result.keyMethod = keyM[1].trim();
        }
      } else if (line.indexOf('#EXT-X-STREAM-INF') === 0) {
        var attrs = parseAttributes(line.slice(line.indexOf(':') + 1));
        // 紧跟其后的第一条非注释行即该清晰度的播放地址
        var uri = '';
        for (var j = i + 1; j < lines.length; j++) {
          var next = lines[j].trim();
          if (!next || next.charAt(0) === '#') continue;
          uri = next;
          break;
        }
        var resText = attrs.RESOLUTION || '';
        var rm = /(\d+)\s*[xX]\s*(\d+)/.exec(resText);
        var width = rm ? parseInt(rm[1], 10) : 0;
        var height = rm ? parseInt(rm[2], 10) : 0;
        result.variants.push({
          url: uri ? resolveAgainst(uri, baseUrl) : String(baseUrl || ''),
          bandwidth: parseInt(attrs.BANDWIDTH || attrs['AVERAGE-BANDWIDTH'] || '0', 10) || 0,
          resolution: resText,
          width: width,
          height: height,
          name: attrs.NAME || '',
          codecs: attrs.CODECS || '',
          quality: qualityFromHeight(height, attrs.NAME)
        });
      } else if (line.indexOf('#EXT-X-MEDIA') === 0) {
        // 单独的音轨/字幕轨，记录但不算清晰度
        var mediaAttrs = parseAttributes(line.slice(line.indexOf(':') + 1));
        if ((mediaAttrs.TYPE || '').toUpperCase() === 'AUDIO' && mediaAttrs.URI) {
          result.variants.push({
            url: resolveAgainst(mediaAttrs.URI, baseUrl),
            bandwidth: 0,
            resolution: '',
            width: 0,
            height: 0,
            name: mediaAttrs.NAME || '音频轨',
            codecs: '',
            quality: 'audio',
            audioOnly: true
          });
        }
      } else if (line.indexOf('#EXTINF') === 0) {
        result.isMedia = true;
        result.segmentCount++;
      } else if (line.indexOf('#EXT-X-TARGETDURATION') === 0) {
        result.targetDuration = parseFloat(line.split(':')[1]) || 0;
      }
    }

    result.isMaster = result.variants.length > 0;
    // 清晰度按 高度 -> 带宽 降序，便于 popup 默认取最高清
    result.variants.sort(function (a, b) {
      return (b.height - a.height) || (b.bandwidth - a.bandwidth);
    });
    return result;
  }

  /** 解析 mpd（DASH）中的清晰度：<Representation ... width height bandwidth> */
  function parseMPD(text) {
    var out = { variants: [], encrypted: false, isDash: true };
    if (!text || typeof text !== 'string') return out;
    try {
      if (/<ContentProtection/i.test(text)) out.encrypted = true;
      var re = /<Representation\b[^>]*>/gi;
      var m;
      while ((m = re.exec(text)) !== null) {
        var tag = m[0];
        var w = /\bwidth="(\d+)"/i.exec(tag);
        var h = /\bheight="(\d+)"/i.exec(tag);
        var bw = /\bbandwidth="(\d+)"/i.exec(tag);
        var mime = /\bmimeType="([^"]+)"/i.exec(tag);
        var height = h ? parseInt(h[1], 10) : 0;
        out.variants.push({
          url: '',
          bandwidth: bw ? parseInt(bw[1], 10) : 0,
          resolution: (w && h) ? (w[1] + 'x' + h[1]) : '',
          width: w ? parseInt(w[1], 10) : 0,
          height: height,
          name: mime ? mime[1] : '',
          codecs: '',
          quality: qualityFromHeight(height, '')
        });
      }
      out.variants.sort(function (a, b) {
        return (b.height - a.height) || (b.bandwidth - a.bandwidth);
      });
    } catch (e) { /* 忽略 */ }
    return out;
  }

  // ---------------------------------------------------------------------------
  // 上报（批量 + 节流，避免高频分片把 postMessage 打爆）
  // ---------------------------------------------------------------------------

  var pending = [];
  var flushTimer = null;

  function scheduleFlush() {
    if (flushTimer) return;
    flushTimer = setTimeout(function () {
      flushTimer = null;
      flush();
    }, 250);
  }

  function flush() {
    if (!pending.length) return;
    var items = pending;
    pending = [];
    try {
      window.postMessage({ __vsd: CHANNEL, dir: 'to-content', items: items }, '*');
    } catch (e) {
      // structured clone 失败时逐个降级发送
      for (var i = 0; i < items.length; i++) {
        try {
          window.postMessage({ __vsd: CHANNEL, dir: 'to-content', items: [items[i]] }, '*');
        } catch (e2) { /* 忽略 */ }
      }
    }
  }

  function report(item) {
    if (!item || !item.url) return;
    item.source = item.source || 'inject';
    item.pageUrl = item.pageUrl || location.href;
    pending.push(item);
    // 播放列表信息优先，立刻发送
    if (item.playlist) { flush(); } else { scheduleFlush(); }
  }

  // ---------------------------------------------------------------------------
  // 1) hook fetch
  // ---------------------------------------------------------------------------

  var nativeFetch = window.fetch;
  if (typeof nativeFetch === 'function') {
    window.fetch = function (input, init) {
      var url = '';
      var method = 'GET';
      var headers = {};
      try {
        url = (typeof input === 'string') ? input : (input && input.url) || '';
        method = (init && init.method) || (input && input.method) || 'GET';
        headers = normalizeHeaders((init && init.headers) || (input && input.headers));
      } catch (e) { /* 忽略 */ }

      var promise = nativeFetch.apply(this, arguments);

      try {
        if (isMediaUrl(url)) {
          var abs = absolutize(url);
          report({
            url: abs,
            method: String(method).toUpperCase(),
            headers: headers,
            via: 'fetch'
          });
          // 只对播放列表读取响应体，视频实体绝不读取
          if (HLS_RE.test(abs) || DASH_RE.test(abs)) {
            promise.then(function (res) {
              try { inspectPlaylistResponse(abs, res, headers, String(method).toUpperCase()); } catch (e) { /* 忽略 */ }
            }, function () { /* 忽略失败请求 */ });
          }
        }
      } catch (e) { /* 嗅探失败不能影响页面 */ }

      return promise;
    };
    try {
      window.fetch.toString = function () { return nativeFetch.toString(); };
    } catch (e) { /* 忽略 */ }
  }

  /** 读取播放列表响应文本并解析（仅 m3u8/mpd） */
  function inspectPlaylistResponse(url, response, headers, method) {
    if (!response || typeof response.clone !== 'function') return;
    var contentType = '';
    try { contentType = (response.headers && response.headers.get('content-type')) || ''; } catch (e) { /* 忽略 */ }
    var len = 0;
    try { len = parseInt(response.headers.get('content-length') || '0', 10) || 0; } catch (e) { /* 忽略 */ }
    if (len && len > MAX_PLAYLIST_BYTES) return;

    var isHls = HLS_RE.test(url) || /mpegurl|x-mpegurl/i.test(contentType);
    var isDash = DASH_RE.test(url) || /dash\+xml/i.test(contentType);
    if (!isHls && !isDash) return;

    var clone = response.clone();
    clone.text().then(function (text) {
      reportPlaylist(url, text, isHls ? 'hls' : 'dash', headers, method, response.status, 'fetch');
    }, function () { /* 忽略 */ });
  }

  function reportPlaylist(url, text, type, headers, method, status, via) {
    var playlist = (type === 'hls') ? parseM3U8(text, url) : parseMPD(text);
    report({
      url: url,
      method: method || 'GET',
      headers: headers || {},
      status: status || 0,
      via: via || 'fetch',
      playlist: playlist,
      playlistType: type,
      isHls: type === 'hls',
      contentLength: text ? text.length : 0
    });
  }

  // ---------------------------------------------------------------------------
  // 2) hook XMLHttpRequest
  // ---------------------------------------------------------------------------

  var XHRProto = window.XMLHttpRequest && window.XMLHttpRequest.prototype;
  if (XHRProto) {
    var nativeOpen = XHRProto.open;
    var nativeSend = XHRProto.send;
    var nativeSetHeader = XHRProto.setRequestHeader;

    XHRProto.open = function (method, url) {
      try {
        this.__vsdInfo = {
          method: String(method || 'GET').toUpperCase(),
          url: String(url || ''),
          headers: {}
        };
      } catch (e) { /* 忽略 */ }
      return nativeOpen.apply(this, arguments);
    };

    if (typeof nativeSetHeader === 'function') {
      XHRProto.setRequestHeader = function (name, value) {
        try {
          if (this.__vsdInfo && this.__vsdInfo.headers) {
            this.__vsdInfo.headers[String(name)] = String(value);
          }
        } catch (e) { /* 忽略 */ }
        return nativeSetHeader.apply(this, arguments);
      };
    }

    XHRProto.send = function () {
      var info = null;
      try { info = this.__vsdInfo; } catch (e) { /* 忽略 */ }

      if (info && isMediaUrl(info.url)) {
        var abs = absolutize(info.url);
        var xhr = this;
        report({
          url: abs,
          method: info.method,
          headers: info.headers || {},
          via: 'xhr'
        });

        if (HLS_RE.test(abs) || DASH_RE.test(abs)) {
          var onDone = function () {
            try {
              var finalUrl = xhr.responseURL || abs;
              var ct = '';
              try { ct = xhr.getResponseHeader('content-type') || ''; } catch (e) { /* 忽略 */ }
              var isHls = HLS_RE.test(finalUrl) || /mpegurl|x-mpegurl/i.test(ct);
              var isDash = DASH_RE.test(finalUrl) || /dash\+xml/i.test(ct);
              if (!isHls && !isDash) return;
              // 仅当响应是文本时才能读取
              var rt = xhr.responseType;
              var text = '';
              if (!rt || rt === 'text') {
                if (typeof xhr.responseText === 'string') text = xhr.responseText;
                else if (typeof xhr.response === 'string') text = xhr.response;
              }
              if (!text) return;
              reportPlaylist(
                absolutize(finalUrl),
                text,
                isHls ? 'hls' : 'dash',
                info.headers || {},
                info.method,
                xhr.status,
                'xhr'
              );
            } catch (e) { /* 忽略 */ }
          };
          try {
            xhr.addEventListener('loadend', onDone, { once: true });
          } catch (e) {
            try {
              var prev = xhr.onreadystatechange;
              xhr.onreadystatechange = function () {
                try { if (xhr.readyState === 4) onDone(); } catch (e2) { /* 忽略 */ }
                if (typeof prev === 'function') return prev.apply(this, arguments);
              };
            } catch (e2) { /* 忽略 */ }
          }
        }
      }
      return nativeSend.apply(this, arguments);
    };
  }

  // ---------------------------------------------------------------------------
  // 3) MSE（blob: 播放）探测
  // ---------------------------------------------------------------------------

  var mseHints = [];
  var isMsePlaying = false;

  if (typeof window.MediaSource !== 'undefined' && window.MediaSource.prototype) {
    var nativeAddSourceBuffer = window.MediaSource.prototype.addSourceBuffer;
    if (typeof nativeAddSourceBuffer === 'function') {
      window.MediaSource.prototype.addSourceBuffer = function (mimeType) {
        try {
          isMsePlaying = true;
          mseHints.push({ mime: String(mimeType || ''), at: Date.now() });
          report({
            url: 'blob:mse#' + String(mimeType || ''),
            mse: true,
            mime: String(mimeType || ''),
            via: 'mse',
            isBlob: true
          });
        } catch (e) { /* 忽略 */ }
        return nativeAddSourceBuffer.apply(this, arguments);
      };
    }
  }

  if (window.URL && typeof window.URL.createObjectURL === 'function') {
    var nativeCreateObjectURL = window.URL.createObjectURL;
    window.URL.createObjectURL = function (obj) {
      var result = nativeCreateObjectURL.apply(this, arguments);
      try {
        var isMediaSource = (typeof window.MediaSource !== 'undefined' && obj instanceof window.MediaSource);
        var isSourceBuffer = (typeof window.SourceBuffer !== 'undefined' && obj instanceof window.SourceBuffer);
        if (isMediaSource || isSourceBuffer) {
          isMsePlaying = true;
          report({
            url: 'blob:mse#' + (isSourceBuffer ? 'buffer' : 'source'),
            mse: true,
            mime: '',
            via: 'mse',
            isBlob: true
          });
        }
      } catch (e) { /* 忽略 */ }
      return result;
    };
  }

  // ---------------------------------------------------------------------------
  // 4) 响应 content.js 的探针请求（ISOLATED world 需要知道 MSE 状态与已捕获列表）
  // ---------------------------------------------------------------------------

  var capturedUrls = [];
  var nativeReport = report;
  report = function (item) {
    try {
      if (item && item.url && capturedUrls.length < 500) {
        capturedUrls.push({ url: item.url, at: Date.now(), via: item.via || 'inject' });
      }
    } catch (e) { /* 忽略 */ }
    return nativeReport(item);
  };

  window.addEventListener('message', function (event) {
    if (event.source !== window) return;
    var data = event.data;
    if (!data || data.__vsd !== CHANNEL || data.dir !== 'to-inject') return;
    if (data.action === 'probe') {
      try {
        window.postMessage({
          __vsd: CHANNEL,
          dir: 'to-content',
          probeAnswer: true,
          requestId: data.requestId || '',
          payload: {
            mse: isMsePlaying,
            mseHints: mseHints.slice(-10),
            urls: capturedUrls.slice(-200),
            pageUrl: location.href,
            frames: window === window.top ? 'top' : 'child'
          }
        }, '*');
      } catch (e) { /* 忽略 */ }
    }
  }, false);

  // 页面卸载前把剩余结果送出去
  window.addEventListener('pagehide', flush, true);
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden') flush();
  }, true);
})();
