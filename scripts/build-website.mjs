#!/usr/bin/env node
/**
 * 官网打包脚本：生成可直接上传到虚拟主机的自包含目录。
 *
 *   node scripts/build-website.mjs                 # 完整打包
 *   node scripts/build-website.mjs --no-docs       # 跳过 docs/ 与下载页脚本（只发页面）
 *
 * 产物：dist/website/  —— 整个目录就是站点根目录，拷到虚拟主机即可。
 *
 * 为什么需要它：website/index.html 里有若干指向仓库其它目录的链接
 * （../docs/…、../README.md、../scripts/…、../extension/manifest.json）。
 * 这些在本地双击 index.html 时能打开，但一旦只上传 website/ 到服务器就会 404。
 * 本脚本把它们一起复制进产物并改写为站内相对路径，同时保持 index.html 的语义、
 * 类名、样式加载顺序完全不变。
 *
 * 注意：安装包不在产物里。站点上的下载按钮一律指向
 * https://github.com/zxcfdcopud/Yunsuo-downloader/releases（见 index.html 的
 * [data-download-link]），所以打包不依赖那个 77 MiB 的 exe，上传也快得多。
 */

import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** 站点可用的最小文件集合：缺任何一个都不许出包 */
const REQUIRED = [
  'index.html',
  'assets/styles/theme.css',
  'assets/styles/sections.css',
  'assets/js/main.js',
  'assets/img/mark.svg',
  'assets/img/favicon.svg',
  'assets/img/ui-downloads.png',
];

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..');
const SITE = path.join(ROOT, 'website');
const OUT = path.join(ROOT, 'dist', 'website');
const WITH_DOCS = !process.argv.includes('--no-docs');

/** 需要随站点一起发布的仓库内资源：源路径 → 产物内路径 */
const EXTRA = [
  ['docs/EULA.txt', 'docs/EULA.txt'],
  ['docs/使用手册.txt', 'docs/使用手册.txt'],
  ['docs/PROTOCOL.md', 'docs/PROTOCOL.md'],
  ['docs/第三方开源项目清单.txt', 'docs/第三方开源项目清单.txt'],
  ['README.md', 'docs/项目说明.md'],
  ['scripts/build.ps1', 'download/scripts/build.ps1'],
  ['scripts/build-installer.ps1', 'download/scripts/build-installer.ps1'],
  ['extension/manifest.json', 'download/extension/manifest.json'],
];

/** 原链接 → 产物内链接 */
const REWRITE = {
  '../docs/EULA.txt': 'docs/EULA.txt',
  '../docs/使用手册.txt': 'docs/使用手册.txt',
  '../docs/PROTOCOL.md': 'docs/PROTOCOL.md',
  '../docs/第三方开源项目清单.txt': 'docs/第三方开源项目清单.txt',
  '../README.md': 'docs/项目说明.md',
  '../scripts/build.ps1': 'download/scripts/build.ps1',
  '../extension/manifest.json': 'download/extension/manifest.json',
};

const kb = (n) => `${(n / 1024).toFixed(1)} KiB`;
let total = 0;
const fail = [];
const warn = [];

console.log('打包官网 → dist/website/\n' + '='.repeat(52));

/* 1. 清理并复制站点本体（不含内部文档与验收产物） */
rmSync(path.join(ROOT, 'dist'), { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

const SKIP = new Set(['README.md', 'CONTRACT.md', 'VERIFY-REPORT.md', 'tools']);
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
      copyFileSync(src, dst);
      copied++;
      total += statSync(dst).size;
    }
  }
};
walk(SITE);
console.log(`站点本体：${copied} 个文件，${kb(total)}`);

/* 2. 复制文档 / 脚本 / 插件清单 */
console.log('\n随站点发布的附加资源：');
for (const [from, to] of EXTRA) {
  const src = path.join(ROOT, from);
  const dst = path.join(OUT, to);
  if (!WITH_DOCS) { warn.push(`按 --no-docs 跳过 ${from}（页脚文档链接会 404，仅用于快速预览）`); continue; }
  if (!existsSync(src)) { fail.push(`缺失源文件：${from}`); continue; }
  mkdirSync(path.dirname(dst), { recursive: true });
  copyFileSync(src, dst);
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

/* 4. 产物自检：站内引用必须能解析，外链只允许本仓库 https，不得残留 ../ */
const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]));
const broken = [];
const external = new Set();
let refs = 0;
for (const m of html.matchAll(/\b(href|src)="([^"]*)"/g)) {
  const url = m[2];
  refs++;
  if (/^https?:\/\//i.test(url)) {
    if (!/^https:\/\/github\.com\/zxcfdcopud\/Yunsuo-downloader(?:[/?#]|$)/.test(url)) broken.push(`非白名单外链：${url}`);
    else external.add(url);
    continue;
  }
  if (url.startsWith('#')) {
    if (url.length > 1 && !ids.has(url.slice(1))) broken.push(`锚点无对应 id：${url}`);
    continue;
  }
  if (/^(mailto|tel|data):/i.test(url)) continue;
  if (url.includes('../')) { broken.push(`残留 ../：${url}`); continue; }
  const target = path.join(OUT, decodeURIComponent(url.split('#')[0].split('?')[0]));
  if (!existsSync(target)) broken.push(`目标不存在：${url}`);
}

/* 5. 必需文件与字体必须齐全 */
for (const need of REQUIRED) {
  if (!existsSync(path.join(OUT, need))) fail.push(`产物缺少：${need}`);
}
const fonts = existsSync(path.join(OUT, 'assets/fonts'))
  ? readdirSync(path.join(OUT, 'assets/fonts')).filter((f) => f.endsWith('.woff2'))
  : [];
if (fonts.length === 0) fail.push('产物缺少字体文件');

const shots = existsSync(path.join(OUT, 'assets/img'))
  ? readdirSync(path.join(OUT, 'assets/img')).filter((f) => f.startsWith('ui-') && f.endsWith('.png'))
  : [];

console.log('\n' + '='.repeat(52));
console.log(`检查 ${refs} 个 href/src 引用：`);
if (broken.length) { broken.forEach((b) => console.log(`  ✗ ${b}`)); }
else console.log('  ✓ 全部可在产物内解析（或为白名单外链），无残留 ../');
if (external.size) console.log(`  ✓ 白名单外链 ${external.size} 个：${[...external].join('、')}`);
if (fonts.length) console.log(`  ✓ 自托管字体 ${fonts.length} 个`);
if (shots.length) console.log(`  ✓ 产品截图 ${shots.length} 张`);
if (WITH_DOCS) console.log('  ✓ 文档 / 构建脚本 / 插件清单已一并发布');

const files = readdirSync(OUT, { recursive: true }).filter((f) => statSync(path.join(OUT, f)).isFile()).length;
console.log(`\n产物：dist/website/  共 ${files} 个文件，${(total / 1024 / 1024).toFixed(2)} MiB`);

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
