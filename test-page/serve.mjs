// serve.mjs —— 零依赖静态服务器（仅用 Node 内置模块）
//
// 作用：把本目录（含 media/）以正确的 content-type 提供出来，
// 让 YunsuoDownloader 的浏览器扩展能真实嗅探到页面里的媒体请求。
//
// 关键点：
//   * .m3u8  -> application/vnd.apple.mpegurl （触发扩展的 HLS 判定）
//   * .mpd   -> application/dash+xml
//   * .ts    -> video/mp2t
//   * .mp4   -> video/mp4
//   * .webm  -> video/webm
//   * .key   -> application/octet-stream （AES-128 密钥，供加密 HLS 回源）
//   * 其余 HTML/JS/CSS 按常见类型返回
//
// 用法：
//   node serve.mjs            # 默认 127.0.0.1:8000
//   node serve.mjs 9000       # 指定端口
//   PORT=9000 node serve.mjs  # 用环境变量
//
// 然后通过本机访问：http://127.0.0.1:8000/

import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import path from 'node:path';

const ROOT = process.cwd();
const PORT = Number(process.argv[2] || process.env.PORT || 8000);
const HOST = '127.0.0.1';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.m3u8': 'application/vnd.apple.mpegurl',
  '.mpd': 'application/dash+xml',
  '.ts': 'video/mp2t',
  '.m4s': 'video/iso.segment',
  '.mp4': 'video/mp4',
  '.webm': 'video/webm',
  '.mov': 'video/quicktime',
  '.mkv': 'video/x-matroska',
  '.flv': 'video/x-flv',
  '.mp3': 'audio/mpeg',
  '.m4a': 'audio/mp4',
  '.aac': 'audio/aac',
  '.key': 'application/octet-stream',
  '.bin': 'application/octet-stream',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon'
};

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, `http://${HOST}:${PORT}`);
    let pathname = decodeURIComponent(url.pathname);
    if (pathname === '/') pathname = '/index.html';

    // 防目录穿越：解析后必须仍在 ROOT 内
    const filePath = path.normalize(path.join(ROOT, pathname));
    if (!filePath.startsWith(ROOT)) {
      res.writeHead(403).end('Forbidden');
      return;
    }

    const info = await stat(filePath);
    if (info.isDirectory()) {
      res.writeHead(403).end('Forbidden');
      return;
    }

    const ext = path.extname(filePath).toLowerCase();
    const type = MIME[ext] || 'application/octet-stream';
    res.writeHead(200, {
      'Content-Type': type,
      'Content-Length': info.size,
      'Access-Control-Allow-Origin': '*',
      'Cache-Control': 'no-store'
    });

    const stream = createReadStream(filePath);
    stream.on('error', () => res.destroy());
    stream.pipe(res);
  } catch (err) {
    if (err && err.code === 'ENOENT') {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' }).end('404 Not Found');
    } else {
      res.writeHead(500).end('500 Internal Server Error');
    }
  }
});

server.listen(PORT, HOST, () => {
  console.log(`YunsuoDownloader 测试页已启动`);
  console.log(`  本机访问：http://${HOST}:${PORT}/`);
  console.log(`  媒体目录：${path.join(ROOT, 'media')}`);
  console.log(`  按 Ctrl+C 停止`);
});
