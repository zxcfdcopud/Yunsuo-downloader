#!/usr/bin/env node
/**
 * 云梭下载器官网 — 验收脚本（零依赖）
 *
 *   node scripts/verify-website.mjs [--no-browser] [--json]
 *
 * A. 静态检查：文件齐备、类名双向对账、禁用模式、外链白名单、事实溯源
 * B. 浏览器检查（Chrome headless + CDP）：控制台错误、外部请求、断点横向溢出、
 *    reduced-motion 持续动画、字体是否真自托管、截图
 *
 * 用 Node 原生 WebSocket / fetch，不依赖任何 npm 包。Chrome 以 stdio:'ignore' 启动，
 * 通过对 /json/version 轮询拿调试端口，避免管道 stdio 在受限环境下的 EPERM。
 */

import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..');
const SITE = path.join(ROOT, 'website');
const INDEX = path.join(SITE, 'index.html');
const SHOTS = path.join(ROOT, 'artifacts', 'website-shots');

const CSS_FILES = ['theme.css', 'fonts.css', 'base.css', 'layout.css', 'sections.css', 'motion.css'];
const JS_FILES = ['main.js'];
const IMG_FILES = ['mark.svg', 'favicon.svg'];
/** 页面上真实出现的产品截图：必须存在，且被 index.html 引用 */
const SHOT_FILES = ['ui-downloads.png', 'ui-newtask.png', 'ui-extension.png', 'ui-settings.png'];
const BREAKPOINTS = [1440, 1180, 900, 640, 390];
/** 运行时由 main.js / 内联脚本添加的状态类，不算“孤儿样式” */
const RUNTIME_CLASSES = new Set(['js', 'is-visible', 'is-scrolled', 'is-copied']);
/** <a href> 允许的外部地址：只允许指向本仓库（含 Releases） */
const ALLOWED_EXTERNAL = /^https:\/\/github\.com\/zxcfdcopud\/Yunsuo-downloader(?:[/?#]|$)/;

const args = process.argv.slice(2);
const NO_BROWSER = args.includes('--no-browser');
const AS_JSON = args.includes('--json');

const results = [];
const problems = [];
function check(id, title, ok, detail = '') {
  results.push({ id, title, ok: !!ok, detail });
  if (!ok) problems.push({ id, title, detail });
  const mark = ok ? 'PASS' : 'FAIL';
  console.log(`[${mark}] ${id} ${title}${detail ? '\n        ' + String(detail).replace(/\n/g, '\n        ') : ''}`);
}

const readText = (p) => readFileSync(p, 'utf8');
const toPosix = (p) => p.split(path.sep).join('/');

/* ------------------------------------------------------------------ 静态检查 */

const CHROME_CANDIDATES = [
  path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Google\\Chrome\\Application\\chrome.exe'),
  path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Google\\Chrome\\Application\\chrome.exe'),
  path.join(process.env.LOCALAPPDATA || '', 'Google\\Chrome\\Application\\chrome.exe'),
  path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Microsoft\\Edge\\Application\\msedge.exe'),
];

/** 剥离 CSS 注释（保留长度以便定位） */
function stripCssComments(css) {
  return css.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '));
}

/**
 * 收集 CSS 中“选择器位置”出现的类名。
 * 逐字符扫描，记录花括号深度：只在深度 0（顶层选择器）与深度 1（@media 内选择器）
 * 且该语句以 `{` 结束、不以 `@` 开头时，才把这一段当作选择器解析。
 */
function collectCssClasses(css) {
  const src = stripCssComments(css);
  const out = new Map(); // class -> count
  const add = (name) => out.set(name, (out.get(name) || 0) + 1);

  let depth = 0;
  let buf = '';
  for (let i = 0; i < src.length; i++) {
    const ch = src[i];
    if (ch === '{') {
      const sel = buf.trim();
      if (depth <= 1 && sel && !sel.startsWith('@') && !sel.startsWith('from') && !sel.startsWith('to') && !/^\d/.test(sel)) {
        for (const m of sel.matchAll(/\.(-?[_a-zA-Z][\w-]*)/g)) add(m[1]);
      }
      depth++;
      buf = '';
    } else if (ch === '}') {
      depth = Math.max(0, depth - 1);
      buf = '';
    } else if (ch === ';' && depth === 0) {
      buf = '';
    } else {
      buf += ch;
    }
  }
  return out;
}

function collectHtmlClasses(html) {
  const out = new Map();
  for (const m of html.matchAll(/\bclass\s*=\s*"([^"]*)"/g)) {
    for (const c of m[1].split(/\s+/)) if (c) out.set(c, (out.get(c) || 0) + 1);
  }
  return out;
}

function lineOf(text, index) {
  return text.slice(0, index).split('\n').length;
}

/* ------------------------------------------------------------------ A. 静态 */

let html = '';
let cssAll = new Map();
let htmlClasses = new Map();

function staticChecks() {
  // A1 交付物齐备
  const missing = [];
  if (!existsSync(INDEX)) missing.push('website/index.html');
  for (const f of CSS_FILES) if (!existsSync(path.join(SITE, 'assets', 'styles', f))) missing.push(`website/assets/styles/${f}`);
  for (const f of JS_FILES) if (!existsSync(path.join(SITE, 'assets', 'js', f))) missing.push(`website/assets/js/${f}`);
  for (const f of IMG_FILES) if (!existsSync(path.join(SITE, 'assets', 'img', f))) missing.push(`website/assets/img/${f}`);
  for (const f of SHOT_FILES) if (!existsSync(path.join(SITE, 'assets', 'img', f))) missing.push(`website/assets/img/${f}`);
  const fontDir = path.join(SITE, 'assets', 'fonts');
  const woff2 = existsSync(fontDir) ? readdirSync(fontDir).filter((f) => f.endsWith('.woff2')) : [];
  if (!woff2.length) missing.push('website/assets/fonts/*.woff2');
  check('A1', '交付物文件齐备', missing.length === 0,
    missing.length ? `缺失：${missing.join('、')}` : `字体 ${woff2.length} 个、产品截图 ${SHOT_FILES.length} 张`);

  if (!existsSync(INDEX)) return;

  html = readText(INDEX);
  htmlClasses = collectHtmlClasses(html);

  // A2 样式加载顺序
  const order = [...html.matchAll(/<link[^>]+href="assets\/styles\/([\w.-]+)"/g)].map((m) => m[1]);
  const expected = CSS_FILES;
  check('A2', '样式加载顺序符合契约', JSON.stringify(order) === JSON.stringify(expected),
    `实际：${order.join(' → ')}${JSON.stringify(order) === JSON.stringify(expected) ? '' : `\n期望：${expected.join(' → ')}`}`);

  // A3 类名双向对账
  for (const f of CSS_FILES) {
    const p = path.join(SITE, 'assets', 'styles', f);
    if (!existsSync(p)) continue;
    for (const [name, count] of collectCssClasses(readText(p))) {
      cssAll.set(name, (cssAll.get(name) || 0) + count);
    }
  }
  // HTML 用了但没样式
  const unusedInCss = [...htmlClasses.keys()].filter((c) => !cssAll.has(c) && !RUNTIME_CLASSES.has(c)).sort();
  // 有样式但 HTML 没用
  const unusedInHtml = [...cssAll.keys()].filter((c) => !htmlClasses.has(c) && !RUNTIME_CLASSES.has(c)).sort();
  check('A3', '类名双向对账：无孤儿', unusedInCss.length === 0 && unusedInHtml.length === 0,
    [
      unusedInCss.length ? `HTML 用了但 CSS 未定义（${unusedInCss.length}）：${unusedInCss.join(', ')}` : 'HTML→CSS 方向无缺口',
      unusedInHtml.length ? `CSS 定义了但 HTML 未用（${unusedInHtml.length}）：${unusedInHtml.join(', ')}` : 'CSS→HTML 方向无多余',
    ].join('\n'));

  // A4 运行时状态类确实有实现
  const motionCss = existsSync(path.join(SITE, 'assets', 'styles', 'motion.css')) ? readText(path.join(SITE, 'assets', 'styles', 'motion.css')) : '';
  const layoutCss = existsSync(path.join(SITE, 'assets', 'styles', 'layout.css')) ? readText(path.join(SITE, 'assets', 'styles', 'layout.css')) : '';
  const js = existsSync(path.join(SITE, 'assets', 'js', 'main.js')) ? readText(path.join(SITE, 'assets', 'js', 'main.js')) : '';
  const runtimeOk = /\.js\s/.test(motionCss) && /is-visible/.test(motionCss)
    && /is-scrolled/.test(layoutCss) && /is-scrolled/.test(js)
    && /is-visible/.test(js) && /is-copied/.test(js) && /is-copied/.test(readText(path.join(SITE, 'assets', 'styles', 'sections.css')));
  check('A4', '状态类（.js/.is-visible/.is-scrolled/.is-copied）在 CSS 与 main.js 中均有实现', runtimeOk,
    `motion.css .js=${/\.js\s/.test(motionCss)} is-visible=${/is-visible/.test(motionCss)}；layout.css is-scrolled=${/is-scrolled/.test(layoutCss)}；main.js is-visible=${/is-visible/.test(js)} is-scrolled=${/is-scrolled/.test(js)} is-copied=${/is-copied/.test(js)}`);

  // A5 禁用模式
  const offenders = [];
  for (const f of CSS_FILES) {
    const p = path.join(SITE, 'assets', 'styles', f);
    if (!existsSync(p)) continue;
    const t = readText(p);
    const noComment = stripCssComments(t);
    if (/!important/.test(noComment)) offenders.push(`${f}: !important`);
    // #id 选择器：选择器段里出现 #xxx
    let depth = 0, buf = '';
    for (const ch of noComment) {
      if (ch === '{') {
        const sel = buf.trim();
        if (depth <= 1 && sel && !sel.startsWith('@') && /#[-_a-zA-Z]/.test(sel)) offenders.push(`${f}: #id 选择器 → ${sel.slice(0, 60)}`);
        depth++; buf = '';
      } else if (ch === '}') { depth = Math.max(0, depth - 1); buf = ''; }
      else if (ch === ';' && depth === 0) buf = '';
      else buf += ch;
    }
  }
  if (/!important/.test(html)) offenders.push('index.html: !important');
  check('A5', '无 !important、无 #id 选择器', offenders.length === 0, offenders.join('\n'));

  // A6 子资源全本地 + 外链白名单 + 链接目标存在
  const linkProblems = [];
  const ids = new Set([...html.matchAll(/\bid\s*=\s*"([^"]+)"/g)].map((m) => m[1]));
  /** 取属性所在标签名：外链只允许出现在 <a> 上，子资源一律不许出站 */
  const tagAt = (index) => {
    const open = html.lastIndexOf('<', index);
    const m = /^<([a-zA-Z][\w-]*)/.exec(html.slice(open, open + 40));
    return m ? m[1].toLowerCase() : '';
  };
  const externalAnchors = [];
  for (const m of html.matchAll(/\b(href|src)\s*=\s*"([^"]*)"/g)) {
    const attr = m[1];
    const url = m[2];
    const tag = tagAt(m.index);
    if (/^\/\//.test(url)) { linkProblems.push(`<${tag} ${attr}="${url}"> → 协议相对地址`); continue; }
    if (/^https?:\/\//i.test(url)) {
      if (tag !== 'a') { linkProblems.push(`<${tag} ${attr}="${url}"> → 子资源禁止外部地址`); continue; }
      if (!ALLOWED_EXTERNAL.test(url)) { linkProblems.push(`<a href="${url}"> → 不在外链白名单（只允许本仓库地址）`); continue; }
      externalAnchors.push(url);
      continue;
    }
    if (url.startsWith('#')) {
      const frag = url.slice(1);
      if (frag && !ids.has(frag)) linkProblems.push(`href="${url}" → 页面内无对应 id`);
      continue;
    }
    if (/^(mailto|tel|data|javascript):/i.test(url)) continue;
    const clean = decodeURIComponent(url.split('#')[0].split('?')[0]);
    const target = path.resolve(SITE, clean);
    if (!existsSync(target)) linkProblems.push(`${attr}="${url}" → 文件不存在（${toPosix(path.relative(ROOT, target))}）`);
  }
  for (const m of html.matchAll(/<script[^>]*>/g)) {
    if (/type\s*=\s*"module"/.test(m[0])) linkProblems.push(`<script type="module"> 违反契约（file:// 下会 CORS 失败）`);
  }
  check('A6', '子资源全为本地文件；外链仅指向本仓库（https）；本地链接目标均存在', linkProblems.length === 0,
    linkProblems.length ? linkProblems.join('\n')
      : `外链 ${externalAnchors.length} 处，全部为 https://github.com/zxcfdcopud/Yunsuo-downloader；其余 href/src 均为站内文件`);

  // A6b 下载入口必须指向 Releases（站内不存在安装包，不能假装有）
  const downloadLinks = [...html.matchAll(/<a\b[^>]*data-download-link[^>]*>/g)].map((m) => m[0]);
  const downloadTargets = downloadLinks.map((tag) => (/href="([^"]*)"/.exec(tag) || [])[1] || '');
  const downloadOk = downloadTargets.length >= 2
    && downloadTargets.every((u) => /^https:\/\/github\.com\/zxcfdcopud\/Yunsuo-downloader\/releases$/.test(u));
  check('A6b', '所有下载按钮均指向 GitHub Releases（没有指向本地/失效安装包）', downloadOk,
    downloadTargets.length ? `${downloadTargets.length} 个 [data-download-link]：${[...new Set(downloadTargets)].join(', ')}` : '未找到 [data-download-link]');

  // A7 语义与无障碍
  const sem = [];
  const h1 = [...html.matchAll(/<h1\b/g)].length;
  if (h1 !== 1) sem.push(`<h1> 数量 = ${h1}（应为 1）`);
  for (const tag of ['header', 'nav', 'main', 'section', 'footer']) {
    if (!new RegExp(`<${tag}\\b`).test(html)) sem.push(`缺少 <${tag}>`);
  }
  if (!/<html[^>]+lang="/.test(html)) sem.push('<html> 缺少 lang');
  for (const m of html.matchAll(/<img\b[^>]*>/g)) {
    if (!/\balt\s*=/.test(m[0])) sem.push(`<img> 缺少 alt：${m[0].slice(0, 60)}`);
  }
  if (!/class="skip-link"/.test(html)) sem.push('缺少 .skip-link 跳转链接');
  if (!/aria-label="[^"]+"/.test(html)) sem.push('缺少 aria-label');
  if (!/<meta[^>]+name="viewport"/.test(html)) sem.push('缺少 viewport meta');
  if (!/<meta[^>]+name="description"/.test(html)) sem.push('缺少 description meta');
  check('A7', '语义化与无障碍基线', sem.length === 0, sem.join('\n'));

  // A8 客户端 JS 硬约束
  const jsIssues = [];
  if (js) {
    if (/\bimport\s+[\w{*]/.test(js) || /\bexport\s+(default|const|function)/.test(js)) jsIssues.push('出现 ES Module 语法');
    if (/\bfetch\s*\(/.test(js)) jsIssues.push('出现 fetch()');
    if (/new\s+XMLHttpRequest/.test(js)) jsIssues.push('出现 XMLHttpRequest');
    if (/https?:\/\//.test(js)) jsIssues.push('出现外部 URL');
  } else jsIssues.push('main.js 不存在');
  check('A8', 'main.js 为经典脚本、无网络请求、无外部 URL', jsIssues.length === 0, jsIssues.join('\n'));

  // A9 页脚合规声明存在
  check('A9', '页脚保留合规声明', /不提供、不托管任何第三方内容/.test(html) && /DRM/.test(html), '');

  // A10 本地字体自托管（fonts.css 不得有 http）
  const fontsCssPath = path.join(SITE, 'assets', 'styles', 'fonts.css');
  const fontIssues = [];
  if (existsSync(fontsCssPath)) {
    const fc = readText(fontsCssPath);
    for (const m of fc.matchAll(/url\(\s*["']?([^"')]+)["']?\s*\)/g)) {
      if (/^https?:/i.test(m[1])) fontIssues.push(`外部字体源：${m[1]}`);
    }
    for (const m of fc.matchAll(/url\(\s*["']?([^"')]+)["']?\s*\)/g)) {
      if (/^https?:/i.test(m[1])) continue;
      const p = path.resolve(path.dirname(fontsCssPath), m[1].split('?')[0].split('#')[0]);
      if (!existsSync(p)) fontIssues.push(`字体文件不存在：${m[1]}`);
    }
    if (!/font-display\s*:\s*swap/.test(fc)) fontIssues.push('缺少 font-display: swap');
    if (!/unicode-range/.test(fc)) fontIssues.push('缺少 unicode-range（未做子集）');
  } else fontIssues.push('fonts.css 不存在');
  check('A10', '字体为本地自托管且路径有效', fontIssues.length === 0, fontIssues.join('\n'));

  // A11 事实一致性：页面上的每个数字/承诺都要能在仓库文档里找到出处
  const factIssues = [];
  const CORPUS_FILES = ['README.md', 'CHANGELOG.md', 'docs/使用手册.txt', 'docs/PROTOCOL.md'];
  const corpus = [];
  for (const rel of CORPUS_FILES) {
    const p = path.join(ROOT, rel);
    if (existsSync(p)) corpus.push([rel, readText(p)]);
  }
  /** 每一条都是页面上真实出现的说法 → 文档里必须能查到 */
  const factMap = [
    ['单任务最多 16 连接', /16/],
    ['线程数 1~16 自动推荐', /1~16|1-16/],
    ['yt-dlp 覆盖 1800+ 站点', /1800\+/],
    ['离线自检 80+ 项', /80\+/],
    ['版本 1.0.0', /1\.0\.0/],
    ['.NET 8 自包含', /自包含/],
    ['磁盘约 500 MB', /500 MB/],
    ['Windows 10 1809 起', /1809/],
    ['四步首次引导', /四步/],
    ['aria2 多连接内核', /aria2/],
    ['HLS / DASH 流', /DASH/],
    ['插件服务端口 16888', /16888/],
    ['MIT 许可证', /MIT/],
    ['免管理员、不弹 UAC', /不弹 UAC|不需要管理员/],
    ['不做 DRM 破解', /不做.{0,6}破解|不绕过.{0,10}DRM/],
  ];
  for (const [label, re] of factMap) {
    const hit = corpus.find(([, text]) => re.test(text));
    if (!hit) factIssues.push(`仓库文档中未找到“${label}”的出处（已查 ${CORPUS_FILES.join('、')}）`);
  }
  check('A11', '页面所有关键事实均可在仓库文档中溯源', factIssues.length === 0,
    factIssues.length ? factIssues.join('\n') : `${factMap.length} 条事实全部命中：${corpus.map(([f]) => f).join('、')}`);

  // A12 动效失效保护：main.js 没跑起来时页面不能是空白
  const guardWired = /__fdMotionGuard/.test(html) && /classList\.add\(['"]js['"]\)/.test(html);
  const guardCleared = /clearTimeout\s*\(\s*window\.__fdMotionGuard\s*\)/.test(js);
  check('A12', '动效失效保护已接线（main.js 未执行则摘掉 js 根类，避免整页空白）', guardWired && guardCleared,
    `index.html: js 根类=${/classList\.add\(['"]js['"]\)/.test(html)}, __fdMotionGuard=${/__fdMotionGuard/.test(html)}; main.js: clearTimeout=${guardCleared}`);

  // A13 无遗留临时文件 / 无构建垃圾（website/tools/ 是验收工程师的合法工作区，跳过）
  const strays = [];
  const TOOLS = path.join(SITE, 'tools');
  const walk = (dir) => {
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (p === TOOLS) continue;
      if (e.isDirectory()) {
        if (/^_|^node_modules$|^\.cache$/.test(e.name)) strays.push(toPosix(path.relative(ROOT, p)) + '/（临时目录）');
        else walk(p);
      } else if (/\.(mjs|txt|log|bak|tmp)$/i.test(e.name)) {
        strays.push(toPosix(path.relative(ROOT, p)));
      }
    }
  };
  walk(SITE);
  check('A13', 'website/ 下无遗留临时文件或垃圾', strays.length === 0, strays.join('\n'));
}

/* ------------------------------------------------------------------ B. 浏览器 */

class CDP {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); this.handlers = new Map(); }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => {
      ws.addEventListener('open', res, { once: true });
      ws.addEventListener('error', () => rej(new Error('WS 连接失败')), { once: true });
    });
    const c = new CDP(ws);
    ws.addEventListener('message', (ev) => {
      let msg; try { msg = JSON.parse(ev.data); } catch { return; }
      if (msg.id && c.pending.has(msg.id)) {
        const { res, rej } = c.pending.get(msg.id);
        c.pending.delete(msg.id);
        msg.error ? rej(new Error(msg.error.message)) : res(msg.result);
      } else if (msg.method) {
        for (const h of c.handlers.get(msg.method) || []) h(msg.params);
        for (const h of c.handlers.get('*') || []) h(msg);
      }
    });
    return c;
  }
  on(method, fn) {
    if (!this.handlers.has(method)) this.handlers.set(method, []);
    this.handlers.get(method).push(fn);
  }
  send(method, params = {}) {
    const id = ++this.id;
    return new Promise((res, rej) => {
      this.pending.set(id, { res, rej });
      this.ws.send(JSON.stringify({ id, method, params }));
      setTimeout(() => { if (this.pending.has(id)) { this.pending.delete(id); rej(new Error(`CDP 超时：${method}`)); } }, 30000);
    });
  }
  close() { try { this.ws.close(); } catch {} }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitForDevtools(port, timeoutMs = 25000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const r = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (r.ok) return await r.json();
    } catch {}
    await sleep(200);
  }
  throw new Error(`Chrome 调试端口 ${port} 在 ${timeoutMs}ms 内未就绪`);
}

async function browserChecks() {
  const chrome = CHROME_CANDIDATES.find((p) => p && existsSync(p));
  if (!chrome) {
    check('B0', '找到 Chrome/Edge', false, `尝试过：\n${CHROME_CANDIDATES.join('\n')}`);
    return;
  }
  check('B0', '找到 Chrome/Edge', true, chrome);

  const port = 9411 + Math.floor(Math.random() * 200);
  const profile = path.join(process.env.TEMP || '/tmp', `fd-web-verify-${Date.now()}`);
  mkdirSync(profile, { recursive: true });
  mkdirSync(SHOTS, { recursive: true });

  const child = spawn(chrome, [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--disable-extensions', '--hide-scrollbars', '--force-device-scale-factor=1',
    '--allow-file-access-from-files', '--disable-features=Translate',
    `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank',
  ], { stdio: 'ignore', detached: false });

  let cdp;
  try {
    await waitForDevtools(port);
    const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    const target = list.find((t) => t.type === 'page');
    if (!target) throw new Error('找不到 page target');
    cdp = await CDP.connect(target.webSocketDebuggerUrl);

    const consoleErrors = [];
    const pageErrors = [];
    const requests = [];
    const failedLoads = [];
    cdp.on('Runtime.consoleAPICalled', (p) => {
      if (p.type === 'error' || p.type === 'warning') {
        consoleErrors.push(`${p.type}: ${(p.args || []).map((a) => a.value ?? a.description ?? a.type).join(' ')}`);
      }
    });
    cdp.on('Runtime.exceptionThrown', (p) => {
      pageErrors.push(p.exceptionDetails?.exception?.description || p.exceptionDetails?.text || 'unknown exception');
    });
    cdp.on('Network.requestWillBeSent', (p) => requests.push(p.request.url));
    cdp.on('Network.loadingFailed', (p) => failedLoads.push(`${p.type} ${p.errorText} blocked=${p.blockedReason || '-'}`));

    await cdp.send('Runtime.enable');
    await cdp.send('Network.enable');
    await cdp.send('Page.enable');
    await cdp.send('Log.enable');

    const fileUrl = 'file:///' + toPosix(INDEX).replace(/^\//, '');
    const navigate = async () => {
      const loaded = new Promise((res) => { cdp.on('Page.loadEventFired', res); });
      await cdp.send('Page.navigate', { url: fileUrl });
      await Promise.race([loaded, sleep(15000)]);
      await sleep(900); // 给 rAF / IntersectionObserver 一点时间
    };

    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
    consoleErrors.length = 0; pageErrors.length = 0; requests.length = 0; failedLoads.length = 0;
    await navigate();

    // B1 控制台干净
    check('B1', 'file:// 打开后无控制台报错 / 未捕获异常', consoleErrors.length === 0 && pageErrors.length === 0,
      [...pageErrors.map((e) => 'exception: ' + e), ...consoleErrors].join('\n'));

    // B2 无外部请求
    const external = requests.filter((u) => !/^(file|data|blob):/i.test(u));
    const failedLocal = failedLoads.filter((s) => !/blocked=/.test(s) || !/inspector|favicon/i.test(s));
    check('B2', '无外部网络请求，且本地资源无加载失败', external.length === 0 && failedLocal.length === 0,
      [external.length ? `外部请求：\n${external.join('\n')}` : '全部请求均为 file://',
       failedLocal.length ? `加载失败：\n${failedLocal.join('\n')}` : '无失败加载'].join('\n'));

    // B3 字体真加载
    const fontInfo = await cdp.send('Runtime.evaluate', {
      expression: `(async () => {
        await document.fonts.ready;
        const want = ['Inter', 'Geist Mono', 'Sentient'];
        const loaded = [...document.fonts].map(f => f.family.replace(/["']/g, '')).filter(Boolean);
        const uniq = [...new Set(loaded)];
        const probe = {};
        for (const w of want) probe[w] = document.fonts.check('16px "' + w + '"');
        return JSON.stringify({ uniq, probe, count: document.fonts.size });
      })()`,
      awaitPromise: true, returnByValue: true,
    });
    let fontData = { uniq: [], probe: {} };
    try { fontData = JSON.parse(fontInfo.result.value); } catch {}
    const fontMissing = Object.entries(fontData.probe || {}).filter(([, v]) => !v).map(([k]) => k);
    check('B3', '@font-face 声明的三套字体均真实加载', fontMissing.length === 0 && (fontData.count || 0) > 0,
      `已加载 face 数=${fontData.count}；families=${(fontData.uniq || []).join(', ')}；check() 未命中=${fontMissing.join(', ') || '无'}`);

    // B4 断点无横向滚动
    const overflow = [];
    for (const width of BREAKPOINTS) {
      await cdp.send('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: width <= 640 });
      await sleep(450);
      const r = await cdp.send('Runtime.evaluate', {
        expression: `JSON.stringify({
          sw: document.documentElement.scrollWidth,
          cw: document.documentElement.clientWidth,
          bw: document.body.scrollWidth
        })`, returnByValue: true,
      });
      const v = JSON.parse(r.result.value);
      const bad = v.sw > v.cw + 1 || v.bw > v.cw + 1;
      if (bad) overflow.push(`${width}px: documentElement ${v.sw}/${v.cw}，body ${v.bw}/${v.cw}`);
    }
    check('B4', `断点 ${BREAKPOINTS.join('/')} 下无横向滚动`, overflow.length === 0, overflow.join('\n'));

    // B5 reduced-motion 无持续动画
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
    await navigate();
    await sleep(1200);
    const rm = await cdp.send('Runtime.evaluate', {
      expression: `(() => {
        const anims = (document.getAnimations ? document.getAnimations() : [])
          .filter(a => a.playState === 'running')
          .map(a => (a.animationName || a.effect?.target?.className || 'unknown') + '');
        const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
        return JSON.stringify({ reduced, running: anims, count: anims.length });
      })()`, returnByValue: true,
    });
    const rmv = JSON.parse(rm.result.value);
    check('B5', 'prefers-reduced-motion: reduce 下无持续运行动画', rmv.reduced && rmv.count === 0,
      `媒体匹配=${rmv.reduced}，运行中动画 ${rmv.count} 个${rmv.count ? '：' + rmv.running.join(', ') : ''}`);

    // B6 截断：视觉类硬指标（阴影 / 圆角 / 渐变）
    const cssAudit = await cdp.send('Runtime.evaluate', {
      expression: `(() => {
        const out = { heavyShadow: [], bigRadius: [], gradients: [] };
        for (const el of document.querySelectorAll('*')) {
          const s = getComputedStyle(el);
          const tag = el.tagName.toLowerCase() + (el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\\s+/).join('.') : '');
          if (s.boxShadow && s.boxShadow !== 'none') {
            const nums = s.boxShadow.match(/-?\\d+(?:\\.\\d+)?px/g) || [];
            const blur = parseFloat(nums[2] || '0');
            if (blur > 24) out.heavyShadow.push(tag + ' → ' + s.boxShadow);
          }
          for (const k of ['borderTopLeftRadius','borderTopRightRadius','borderBottomLeftRadius','borderBottomRightRadius']) {
            const r = parseFloat(s[k] || '0');
            if (r > 12) out.bigRadius.push(tag + ' ' + k + '=' + r + 'px');
          }
          if (s.backgroundImage && s.backgroundImage.includes('gradient')) {
            out.gradients.push(tag);
          }
        }
        return JSON.stringify(out);
      })()`, returnByValue: true,
    });
    const audit = JSON.parse(cssAudit.result.value);
    const auditBad = audit.heavyShadow.length || audit.bigRadius.length;
    check('B6', '无重阴影（blur>24px）、无过度圆角（>12px）', !auditBad,
      [audit.heavyShadow.length ? `重阴影 ${audit.heavyShadow.length} 处：\n  ` + audit.heavyShadow.slice(0, 8).join('\n  ') : '无重阴影',
       audit.bigRadius.length ? `大圆角 ${audit.bigRadius.length} 处：\n  ` + audit.bigRadius.slice(0, 8).join('\n  ') : '无过度圆角'].join('\n'));
    console.log(`        （信息）使用渐变背景的元素 ${audit.gradients.length} 个：${audit.gradients.slice(0, 10).join(', ') || '无'}`);

    // B7 截图（先真实滚动整页，触发所有 IntersectionObserver，否则整页截图里 below-fold 内容会停在 opacity:0）
    await cdp.send('Emulation.setEmulatedMedia', { features: [] });
    const settle = async () => {
      const h = await cdp.send('Runtime.evaluate', {
        expression: 'JSON.stringify({ sh: document.documentElement.scrollHeight, ih: window.innerHeight })',
        returnByValue: true,
      });
      const { sh, ih } = JSON.parse(h.result.value);
      const step = Math.max(200, ih - 140);
      for (let y = 0; y < sh + step; y += step) {
        await cdp.send('Runtime.evaluate', { expression: `window.scrollTo(0, ${y})` });
        await sleep(200);
      }
      await cdp.send('Runtime.evaluate', { expression: 'window.scrollTo(0, 0)' });
      await sleep(600);
    };
    const shots = [];
    for (const [name, width] of [['desktop-1440', 1440], ['mobile-390', 390]]) {
      await cdp.send('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: width <= 640 });
      await navigate();
      await settle();
      const metrics = await cdp.send('Page.getLayoutMetrics');
      const size = metrics.cssContentSize || metrics.contentSize;
      const height = Math.min(Math.ceil(size.height), 16000);
      const shot = await cdp.send('Page.captureScreenshot', {
        format: 'png', captureBeyondViewport: true, fromSurface: true,
        clip: { x: 0, y: 0, width, height, scale: 1 },
      });
      const p = path.join(SHOTS, `${name}.png`);
      writeFileSync(p, Buffer.from(shot.data, 'base64'));
      shots.push(`${toPosix(path.relative(ROOT, p))} (${width}×${height})`);
    }
    check('B7', '截图已生成（已滚动整页触发入场动效）', shots.length === 2, shots.join('\n'));

    // B8 结构 + 入场动效是否真的全量收敛（滚动后不应有残留的不可见内容）
    const struct = await cdp.send('Runtime.evaluate', {
      expression: `(() => {
        const sec = [...document.querySelectorAll('main > section')].map(s => s.id || s.className);
        const all = [...document.querySelectorAll('.reveal')];
        const notVisible = all.filter(el => {
          const s = getComputedStyle(el);
          return parseFloat(s.opacity) < 0.99 || (s.transform !== 'none' && s.transform !== 'matrix(1, 0, 0, 1, 0, 0)');
        }).map(el => el.className);
        const hiddenText = [...document.querySelectorAll('main h1, main h2, main h3, main p, main li')]
          .filter(el => parseFloat(getComputedStyle(el).opacity) < 0.1).length;
        const canvas = document.querySelector('canvas[data-texture="decode"]');
        return JSON.stringify({
          sec, total: all.length, visible: all.length - notVisible.length,
          notVisible: notVisible.slice(0, 10), hiddenText,
          kanvas: canvas ? { w: canvas.width, h: canvas.height } : null,
        });
      })()`, returnByValue: true,
    });
    const st = JSON.parse(struct.result.value);
    check('B8', '主区块结构完整，且滚动后所有入场元素均已收敛可见',
      st.sec.length >= 6 && st.notVisible.length === 0 && st.hiddenText === 0,
      `main > section：${st.sec.join(' | ')}\n.reveal ${st.total} 个，可见 ${st.visible} 个${st.notVisible.length ? `；未收敛：${st.notVisible.join(' | ')}` : ''}\n正文中不可见（opacity<0.1）的元素：${st.hiddenText}\ncanvas 解码纹理：${st.kanvas ? st.kanvas.w + '×' + st.kanvas.h : '未找到'}`);

    // B9 对抗性：阻断 main.js，验证失效保护真的把整页救回来（否则 .js .reveal 会让全站空白）
    await cdp.send('Network.setBlockedURLs', { urls: ['*assets/js/main.js'] });
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
    await navigate();
    await sleep(2800); // 等 __fdMotionGuard 的 2s 保险到期
    const fb = await cdp.send('Runtime.evaluate', {
      expression: `(() => {
        const els = [...document.querySelectorAll('main h1, main h2, main p, main li, .statement__quote')];
        const hidden = els.filter(el => parseFloat(getComputedStyle(el).opacity) < 0.99);
        const h1 = document.querySelector('h1');
        return JSON.stringify({
          jsClass: document.documentElement.classList.contains('js'),
          fallbackFired: document.documentElement.hasAttribute('data-motion-fallback'),
          h1Opacity: h1 ? getComputedStyle(h1).opacity : null,
          total: els.length, hidden: hidden.length,
          hiddenSample: hidden.slice(0, 5).map(el => el.tagName + '.' + el.className),
        });
      })()`, returnByValue: true,
    });
    await cdp.send('Network.setBlockedURLs', { urls: [] });
    const fbv = JSON.parse(fb.result.value);
    check('B9', 'main.js 被阻断时失效保护生效：整页内容仍然可见',
      fbv.fallbackFired && !fbv.jsClass && fbv.hidden === 0 && fbv.h1Opacity === '1',
      `js 根类=${fbv.jsClass}（应为 false），失效保护触发=${fbv.fallbackFired}，h1 opacity=${fbv.h1Opacity}\n正文采样 ${fbv.total} 个，不可见 ${fbv.hidden} 个${fbv.hidden ? '：' + fbv.hiddenSample.join(' | ') : ''}`);

    cdp.close();
  } finally {
    try { child.kill(); } catch {}
    await sleep(300);
  }
}

/* ------------------------------------------------------------------ main */

console.log('云梭下载器官网验收\n' + '='.repeat(60));
staticChecks();
if (!NO_BROWSER) {
  console.log('\n' + '-'.repeat(60) + '\n浏览器检查（Chrome headless + CDP）\n' + '-'.repeat(60));
  try {
    await browserChecks();
  } catch (e) {
    check('B?', '浏览器检查执行', false, String(e && e.stack || e));
  }
}

console.log('\n' + '='.repeat(60));
const passed = results.filter((r) => r.ok).length;
console.log(`结果：${passed}/${results.length} 通过`);
if (problems.length) {
  console.log('\n未通过项：');
  for (const p of problems) console.log(`  ✗ [${p.id}] ${p.title}\n      ${String(p.detail).replace(/\n/g, '\n      ')}`);
}
if (AS_JSON) {
  const outDir = path.join(ROOT, 'artifacts');
  mkdirSync(outDir, { recursive: true });
  writeFileSync(path.join(outDir, 'website-verify-result.json'), JSON.stringify({ results, problems }, null, 2));
}
process.exit(problems.length ? 1 : 0);
