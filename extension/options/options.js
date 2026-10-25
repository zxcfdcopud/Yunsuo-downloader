/*
 * options.js —— 设置页逻辑。
 *
 * 复用 background.js 暴露的消息接口（getState / saveSettings / reconnect / clearPending），
 * 可配置：主机、端口、路径、token、自动重连、通知、角标、自动发送；并实时展示连接状态。
 */
'use strict';

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

const el = {
  host: document.getElementById('host'),
  port: document.getElementById('port'),
  path: document.getElementById('path'),
  token: document.getElementById('token'),
  autoReconnect: document.getElementById('autoReconnect'),
  notify: document.getElementById('notify'),
  showBadge: document.getElementById('showBadge'),
  autoSend: document.getElementById('autoSend'),
  wsPreview: document.getElementById('wsPreview'),
  connBadge: document.getElementById('connBadge'),
  connText: document.getElementById('connText'),
  tokenBanner: document.getElementById('tokenBanner'),
  statConn: document.getElementById('statConn'),
  statAppVersion: document.getElementById('statAppVersion'),
  statDir: document.getElementById('statDir'),
  statPending: document.getElementById('statPending'),
  statMessage: document.getElementById('statMessage'),
  btnSave: document.getElementById('btnSave'),
  btnTest: document.getElementById('btnTest'),
  btnReset: document.getElementById('btnReset'),
  btnClearPending: document.getElementById('btnClearPending'),
  btnToggleToken: document.getElementById('btnToggleToken'),
  saveHint: document.getElementById('saveHint')
};

function sendBg(message) {
  return new Promise(function (resolve) {
    try {
      chrome.runtime.sendMessage(message, function (response) {
        const err = chrome.runtime.lastError;
        if (err) {
          resolve({ ok: false, message: err.message });
          return;
        }
        resolve(response || { ok: false });
      });
    } catch (e) {
      resolve({ ok: false, message: String(e && e.message ? e.message : e) });
    }
  });
}

function currentFormSettings() {
  return {
    host: (el.host.value || '127.0.0.1').trim(),
    port: parseInt(el.port.value, 10) || DEFAULT_SETTINGS.port,
    path: (el.path.value || '/media').trim(),
    token: el.token.value || '',
    autoReconnect: !!el.autoReconnect.checked,
    notify: !!el.notify.checked,
    showBadge: !!el.showBadge.checked,
    autoSend: !!el.autoSend.checked
  };
}

function fillForm(settings) {
  const s = Object.assign({}, DEFAULT_SETTINGS, settings || {});
  el.host.value = s.host;
  el.port.value = s.port;
  el.path.value = s.path;
  el.token.value = s.token || '';
  el.autoReconnect.checked = !!s.autoReconnect;
  el.notify.checked = !!s.notify;
  el.showBadge.checked = !!s.showBadge;
  el.autoSend.checked = !!s.autoSend;
  updatePreview();
}

function updatePreview() {
  const s = currentFormSettings();
  let path = s.path || '/media';
  if (path.charAt(0) !== '/') path = '/' + path;
  el.wsPreview.textContent = '当前地址：ws://' + (s.host || '127.0.0.1') + ':' + (s.port || 16888) + path;
}

function renderConn(conn, pendingCount, wsUrl) {
  const c = conn || {};
  let cls = 'conn conn--idle';
  let text = '未连接';

  if (c.state === 'connected') {
    cls = 'conn conn--ok';
    text = c.appVersion ? '已连接 v' + c.appVersion : '已连接';
  } else if (c.state === 'token-error') {
    cls = 'conn conn--error';
    text = '令牌错误';
  } else if (c.state === 'connecting' || c.state === 'handshaking') {
    cls = 'conn conn--pending';
    text = c.state === 'handshaking' ? '握手中…' : '连接中…';
  }

  el.connBadge.className = cls;
  el.connText.textContent = text;
  el.tokenBanner.classList.toggle('hidden', c.state !== 'token-error');

  el.statConn.textContent = text + (wsUrl ? '（' + wsUrl + '）' : '');
  el.statAppVersion.textContent = c.appVersion || '—';
  el.statDir.textContent = c.downloadDir || '—';
  el.statPending.textContent = String(pendingCount || 0);
  el.statMessage.textContent = c.message || '—';
}

async function refresh() {
  const res = await sendBg({ cmd: 'getState' });
  if (!res || !res.ok) {
    renderConn({ state: 'disconnected', message: (res && res.message) || '后台未响应' }, 0, '');
    return;
  }
  if (!el.host.value) fillForm(res.settings || {});
  renderConn(res.conn, res.pendingCount, res.wsUrl);
}

// ---------------------------------------------------------------------------
// 事件
// ---------------------------------------------------------------------------

el.btnSave.addEventListener('click', async function () {
  el.btnSave.disabled = true;
  el.saveHint.textContent = '保存中…';
  const res = await sendBg({ cmd: 'saveSettings', settings: currentFormSettings() });
  el.btnSave.disabled = false;
  if (res && res.ok) {
    el.saveHint.textContent = '已保存，正在重连 ' + (res.wsUrl || '');
    setTimeout(refresh, 600);
    setTimeout(refresh, 2500);
  } else {
    el.saveHint.textContent = '保存失败：' + ((res && res.message) || '未知错误');
  }
  setTimeout(function () { el.saveHint.textContent = ''; }, 6000);
});

el.btnTest.addEventListener('click', async function () {
  el.btnTest.disabled = true;
  el.statMessage.textContent = '正在保存并测试连接…';
  // 先保存表单里的地址/令牌，再强制重连，这样"测试"用的就是屏幕上看到的配置
  const saved = await sendBg({ cmd: 'saveSettings', settings: currentFormSettings() });
  if (!saved || !saved.ok) {
    el.btnTest.disabled = false;
    el.statMessage.textContent = '保存失败：' + ((saved && saved.message) || '未知错误');
    return;
  }
  const res = await sendBg({ cmd: 'reconnect' });
  el.btnTest.disabled = false;
  el.statMessage.textContent = (res && res.message) || '已发起连接测试';
  setTimeout(refresh, 800);
  setTimeout(refresh, 2500);
});

el.btnReset.addEventListener('click', function () {
  fillForm(DEFAULT_SETTINGS);
  el.saveHint.textContent = '已恢复默认值，记得点击「保存设置」';
  setTimeout(function () { el.saveHint.textContent = ''; }, 5000);
});

el.btnClearPending.addEventListener('click', async function () {
  const res = await sendBg({ cmd: 'clearPending' });
  el.saveHint.textContent = res && res.ok ? '待发送队列已清空' : '清空失败';
  await refresh();
  setTimeout(function () { el.saveHint.textContent = ''; }, 4000);
});

el.btnToggleToken.addEventListener('click', function () {
  const isPassword = el.token.type === 'password';
  el.token.type = isPassword ? 'text' : 'password';
  el.btnToggleToken.textContent = isPassword ? '隐藏' : '显示';
});

['host', 'port', 'path'].forEach(function (key) {
  el[key].addEventListener('input', updatePreview);
});

chrome.runtime.onMessage.addListener(function (message) {
  if (message && message.cmd === 'conn-updated') {
    refresh();
  }
});

// ---------------------------------------------------------------------------
// 启动
// ---------------------------------------------------------------------------

(async function start() {
  await refresh();
  setInterval(refresh, 3000);
})();
