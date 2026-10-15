/* ============================================================
 * 导出ico.js —— 图标打包器
 *
 * 作用：把 资源\图标\svg\*.svg 变成 Windows 真正能用的 .ico
 *      顺手输出一套多尺寸 PNG（做文档、做网页都能直接抄）
 *
 * 用法：
 *   NODE_PATH=<isolation>\node_modules ^ node 资源\图标\导出ico.js
 *
 * 为什么这么绕：sharp 只能读 svg / 写 png，不能产出 ico。
 * 而 .ico 其实只是个"文件柜"——前面一张目录表，后面塞图片。
 *
 * ★ 关键规矩（2026-10-02 血泪）：柜子里**只有 256 那档允许塞 PNG**，
 *   小尺寸（16~48）必须塞老式 BMP/DIB —— 微软官方就这么定的。
 *   全塞 PNG 的话资源管理器/任务栏读不到小图，会自己乱缩大图甚至
 *   渲染出一个不相干的默认样子 —— 表现就是"任务栏图标小到看不清"，
 *   而 SVG 画得再大也没用（压根没用上你给的图）。
 * ============================================================ */

const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

// —— Windows 实际会用到的尺寸尽量都备一份 ——
// 尺寸给全，系统就不用拿一张缩放来凑，边缘更锐、细节更少糊。
// 20 / 24 / 40 是任务栏和 Alt+Tab 的常用档（100%/125% 缩放下各有各的取值）；
// 256 那张在 ico 目录里写 0 表示 256（一字节装不下 256，这是约定）。
const SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];

const svgDir = __dirname + '\\svg';
const pngRoot = __dirname + '\\png';
const icoDir = __dirname + '\\ico';

fs.mkdirSync(pngRoot, { recursive: true });
fs.mkdirSync(icoDir, { recursive: true });

/**
 * RGBA 原始像素 → BMP/DIB 格式（ico 小尺寸档专用）
 * raw 是 sharp 吐的 RGBA、自上而下；DIB 要 BGRA、自下而上，
 * 且 biHeight 写两倍高（像素数据 + AND 掩码两段合一，这是 ICO 特有约定）。
 */
function rgbaToDib(raw, w) {
  const h = w;
  const pixelSize = w * h * 4;
  const maskStride = Math.ceil(w / 32) * 4;   // 1bpp，每行凑够 4 字节
  const maskSize = maskStride * h;
  const dib = Buffer.alloc(40 + pixelSize + maskSize);

  // BITMAPINFOHEADER（40 字节）
  dib.writeUInt32LE(40, 0);            // biSize
  dib.writeInt32LE(w, 4);              // biWidth
  dib.writeInt32LE(h * 2, 8);          // biHeight = 两倍高（像素+掩码）
  dib.writeUInt16LE(1, 12);            // biPlanes
  dib.writeUInt16LE(32, 14);           // biBitCount
  dib.writeUInt32LE(0, 16);            // biCompression = BI_RGB
  dib.writeUInt32LE(pixelSize + maskSize, 20); // biSizeImage

  // 像素数据：行序上下翻转 + R/B 互换
  for (let y = 0; y < h; y++) {
    const src = (h - 1 - y) * w * 4;
    const dst = 40 + y * w * 4;
    for (let x = 0; x < w; x++) {
      const s = src + x * 4, d = dst + x * 4;
      dib[d]     = raw[s + 2]; // B
      dib[d + 1] = raw[s + 1]; // G
      dib[d + 2] = raw[s];     // R
      dib[d + 3] = raw[s + 3]; // A
    }
  }
  // AND 掩码保持全 0 = 全不透明（透明度交给 alpha 通道，32bpp 就该这么干）
  return dib;
}

/** 把若干张图打成单个 .ico —— 手写 ICO 容器（小图 BMP / 256 PNG） */
function packIco(images) {
  const count = images.length;
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);      // reserved，恒 0
  header.writeUInt16LE(1, 2);      // type，1 = 图标
  header.writeUInt16LE(count, 4);  // 柜子里有几张图

  const entrySize = 16;
  let offset = 6 + entrySize * count;
  const entries = [];

  for (const img of images) {
    const e = Buffer.alloc(entrySize);
    // 尺寸：256 这个数塞不进 1 字节，约定用 0 表示 256
    e.writeUInt8(img.size >= 256 ? 0 : img.size, 0);
    e.writeUInt8(img.size >= 256 ? 0 : img.size, 1);
    e.writeUInt8(0, 2);     // 调色板色数
    e.writeUInt8(0, 3);     // reserved
    e.writeUInt16LE(1, 4);  // 色平面
    e.writeUInt16LE(32, 6); // 位深
    e.writeUInt32LE(img.data.length, 8);
    e.writeUInt32LE(offset, 12);
    entries.push(e);
    offset += img.data.length;
  }

  return Buffer.concat([header, ...entries, ...images.map(i => i.data)]);
}

(async () => {
  const files = fs.readdirSync(svgDir).filter(f => f.toLowerCase().endsWith('.svg'));
  if (files.length === 0) {
    console.error('svg 目录里没有文件：' + svgDir);
    process.exit(1);
  }

  for (const file of files) {
    const name = path.basename(file, '.svg');
    const svg = fs.readFileSync(path.join(svgDir, file));

    const pngDir = path.join(pngRoot, name);
    fs.mkdirSync(pngDir, { recursive: true });

    const images = [];
    for (const size of SIZES) {
      const pngData = await sharp(svg, { density: 384 })
        .resize(size, size, { kernel: 'lanczos3' })
        .png({ compressionLevel: 9 })
        .toBuffer();
      fs.writeFileSync(path.join(pngDir, `${size}.png`), pngData);

      // ★ ico 里：256 塞 PNG（官方允许），小尺寸必须转 BMP/DIB
      let icoData;
      if (size >= 256) {
        icoData = pngData;
      } else {
        const raw = await sharp(svg, { density: 384 })
          .resize(size, size, { kernel: 'lanczos3' })
          .ensureAlpha()
          .raw()
          .toBuffer();
        icoData = rgbaToDib(raw, size);
      }
      images.push({ size, data: icoData });
    }

    // ICO 里从大到小排，符合惯例
    images.sort((a, b) => b.size - a.size);
    fs.writeFileSync(path.join(icoDir, `${name}.ico`), packIco(images));

    const kb = (fs.statSync(path.join(icoDir, `${name}.ico`)).size / 1024).toFixed(1);
    console.log(`OK  ${name}  →  ico/${name}.ico (${kb} KB)  +  png/${name}/`);
  }
})();
