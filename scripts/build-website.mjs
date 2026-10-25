#!/usr/bin/env node
/**
 * 官网打包脚本：生成可直接上传到虚拟主机的自包含目录。
 *
 *   node scripts/build-website.mjs
 *
 * 产物：dist/website/  —— 整个目录就是站点根目录，拷到虚拟主机即可。
 *
 * 为什么需要它：website/ 里有 12 个指向仓库其它目录的链接
 * （../artifacts/…exe、../docs/…、../scripts/…、../extension/…）。
 * 这些在本地双击 index.html 时能打开，但一旦只上传 website/ 到服务器就会 404。
 * 本脚本把安装包、文档、脚本、插件清单一起复制进产物，并把 ../ 改写成站内相对路径，
 * 同时保持 index.html 的语义、类名、样式加载顺序完全不变。
 */

import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** 复制单个文件，带重试：Windows 上大文件可能被杀毒/索引服务短暂占用（EBUSY） */
function copyWithRetry(src, dst, attempts = 6) {
  let lastErr;
  for (let i = 0; i < attempts; i++) {
    try {
      copyFileSync(src, dst);
      return;
    } catch (err) {
      lastErr = err;
      if (err.code !== 'EBUSY' && err.code !== 'EPERM') throw err;
      // 同步等待，避免引入异步复杂度；每次 400ms
      const until = Date.now() + 400;
      while (Date.now() < until) { /* busy wait */ }
    }
  }
  throw lastErr;
}

/** 站点可用的最小字体集合：即使跳过安装包，站点本身也必须完整 */
const REQUIRED = ['index.html', 'assets/styles/theme.css', 'assets/js/main.js', 'assets/img/mark.svg'];

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..');
const SITE = path.join(ROOT, 'website');
const OUT = path.join(ROOT, 'dist', 'website');
const WITH_INSTALLER = !process.argv.includes('--no-installer');
const INSTALLER_REL = 'download/YunsuoDownloader-Setup-1.0.0.exe';

/** 需要随站点一起发布的仓库内资源：源路径 → 产物内路径 */
const EXTRA = [
  ['artifacts/YunsuoDownloader-Setup-1.0.0.exe', 'download/YunsuoDownloader-Setup-1.0.0.exe'],
  ['docs/EULA.txt', 'docs/EULA.txt'],
  ['docs/使用手册.txt', 'docs/使用手册.txt'],
  ['docs/PROTOCOL.md', 'docs/PROTOCOL.md'],
  ['docs/PAN_PROVIDERS.md', 'docs/PAN_PROVIDERS.md'],
  ['docs/第三方开源项目清单.txt', 'docs/第三方开源项目清单.txt'],
  ['README.md', 'docs/项目说明.md'],
  ['scripts/build.ps1', 'download/scripts/build.ps1'],
  ['scripts/build-installer.ps1', 'download/scripts/build-installer.ps1'],
  ['extension/manifest.json', 'download/extension/manifest.json'],
];

/** 原链接 → 产物内链接 */
const REWRITE = {
  '../artifacts/YunsuoDownloader-Setup-1.0.0.exe': 'download/YunsuoDownloader-Setup-1.0.0.exe',
  '../docs/EULA.txt': 'docs/EULA.txt',
  '../docs/使用手册.txt': 'docs/使用手册.txt',
  '../docs/PROTOCOL.md': 'docs/PROTOCOL.md',
  '../docs/PAN_PROVIDERS.md': 'docs/PAN_PROVIDERS.md',
  '../docs/第三方开源项目清单.txt': 'docs/第三方开源项目清单.txt',
  '../README.md': 'docs/项目说明.md',
  '../scripts/build.ps1': 'download/scripts/build.ps1',
  '../scripts/build-installer.ps1': 'download/scripts/build-installer.ps1',
  '../extension/manifest.json': 'download/extension/manifest.json',
};

const kb = (n) => `${(n / 1024).toFixed(1)} KiB`;
let total = 0;
const fail = [];
const warn = [];

console.log('打包官网 → dist/website/\n' + '='.repeat(52));

/* 1. 清理并复制站点本体（不含验收工具与内部文档） */
rmSync(path.join(ROOT, 'dist'), { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

const SKIP = new Set(['tools', 'CONTRACT.md', 'VERIFY-REPORT.md', 'README.md']);
let copied = 0;
const walk = (dir, rel = '') => {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (rel === '' && SKIP.has(e.name)) continue;
    const src = path.join(dir, e.name);
    const dstRel = path.join(rel, e.name);
    const dst = path.join(OUT, dstRel);
    if (e.isDirectory()) {
      mkdirSync(dst, { recursive: true });
      walk(src, dstRel);
    } else {
      mkdirSync(path.dirname(dst), { recursive: true });
      cpSync(src, dst);
      copied++;
      total += statSync(dst).size;
    }
  }
};
walk(SITE);
console.log(`站点本体：${copied} 个文件，${kb(total)}`);

/* 2. 复制安装包 / 文档 / 脚本 / 插件清单 */
console.log('\n随站点发布的附加资源：');
for (const [from, to] of EXTRA) {
  const src = path.join(ROOT, from);
  const dst = path.join(OUT, to);
  const isInstaller = to === INSTALLER_REL;
  if (isInstaller && !WITH_INSTALLER) {
    warn.push('按 --no-installer 跳过安装包（下载按钮会 404，仅用于快速预览布局）');
    continue;
  }
  if (!existsSync(src)) { fail.push(`缺失源文件：${from}`); continue; }
  mkdirSync(path.dirname(dst), { recursive: true });
  try {
    copyWithRetry(src, dst);
  } catch (err) {
    // 安装包动辄 150 MB，常被杀毒软件扫描时独占（EBUSY/EPERM）。
    // 站点本体不依赖它：跳过并明确告警，而不是让整次打包失败。
    warn.push(`跳过 ${from}（${err.code}：文件被占用，稍后重跑即可）`);
    continue;
  }
  const size = statSync(dst).size;
  total += size;
  console.log(`  ${from}  →  ${to}  (${kb(size)})`);
}

/* 3. 改写 index.html 里的 ../ 链接 */
const indexPath = path.join(OUT, 'index.html');
let html = readFileSync(indexPath, 'utf8');
let rewritten = 0;
for (const [from, to] of Object.entries(REWRITE)) {
  const before = html;
  html = html.split(`"${from}"`).join(`"${to}"`);
  if (before !== html) rewritten++;
}
writeFileSync(indexPath, html);
console.log(`\n链接改写：${rewritten} 组`);

/* 4. 产物自检：所有 href/src 必须能在产物内解析，且不得残留 ../ */
const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]));
const broken = [];
const softBroken = [];
let refs = 0;
for (const m of html.matchAll(/\b(href|src)="([^"]*)"/g)) {
  const url = m[2];
  refs++;
  if (/^https?:\/\//i.test(url)) { broken.push(`外部引用：${url}`); continue; }
  if (url.startsWith('#')) {
    if (url.length > 1 && !ids.has(url.slice(1))) broken.push(`锚点无对应 id：${url}`);
    continue;
  }
  if (/^(mailto|tel|data):/i.test(url)) continue;
  if (url.includes('../')) { broken.push(`残留 ../：${url}`); continue; }
  const target = path.join(OUT, decodeURIComponent(url.split('#')[0].split('?')[0]));
  if (existsSync(target)) continue;
  // 安装包缺失只影响下载按钮，不阻塞站点发布
  if (decodeURIComponent(url).includes('YunsuoDownloader-Setup')) softBroken.push(url);
  else broken.push(`目标不存在：${url}`);
}

/* 5. 字体与样式必须齐全 */
for (const need of REQUIRED) {
  if (!existsSync(path.join(OUT, need))) fail.push(`产物缺少：${need}`);
}
const fonts = existsSync(path.join(OUT, 'assets/fonts'))
  ? readdirSync(path.join(OUT, 'assets/fonts')).filter((f) => f.endsWith('.woff2'))
  : [];
if (fonts.length === 0) fail.push('产物缺少字体文件');

/* 安装包若被跳过，下载按钮会指向不存在的文件——单独提示，不阻塞打包 */
const installerPath = path.join(OUT, 'download/YunsuoDownloader-Setup-1.0.0.exe');
if (!existsSync(installerPath)) {
  warn.push('产物中没有安装包：下载按钮会 404。关掉杀毒扫描后重跑本脚本即可补上。');
}

console.log('\n' + '='.repeat(52));
console.log(`检查 ${refs} 个 href/src 引用：`);
if (broken.length) { broken.forEach((b) => console.log(`  ✗ ${b}`)); }
else console.log('  ✓ 全部可在产物内解析，无残留 ../，无外部引用');
if (softBroken.length) console.log(`  ! 安装包链接 ${softBroken.length} 处（产物内暂无安装包，见下方警告）`);
if (fonts.length) console.log(`  ✓ 自托管字体 ${fonts.length} 个`);

const files = readdirSync(OUT, { recursive: true }).filter((f) => statSync(path.join(OUT, f)).isFile()).length;
console.log(`\n产物：dist/website/  共 ${files} 个文件，${(total / 1024 / 1024).toFixed(1)} MiB`);

if (warn.length) {
  console.log('\n警告（不阻塞发布）：');
  warn.forEach((w) => console.log(`  ! ${w}`));
}

if (fail.length || broken.length) {
  console.log('\n打包失败：');
  [...fail, ...broken].forEach((x) => console.log(`  ✗ ${x}`));
  process.exit(1);
}
console.log('\n✓ 打包完成。把 dist/website/ 里的全部内容上传到虚拟主机根目录即可。');
