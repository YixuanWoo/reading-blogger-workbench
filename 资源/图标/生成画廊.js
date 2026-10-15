/* ============================================================
 * 生成画廊 —— 把 6 个 SVG + 多尺寸 PNG 拼进一张对比页
 * 用法：node 生成画廊.js   →  文档\图标方案对比.html
 * ============================================================ */
const fs = require('fs');
const path = require('path');

const root = __dirname;
const svgDir = path.join(root, 'svg');
const pngRoot = path.join(root, 'png');
const outPath = 'D:\\工作台工程\\工作台\\文档\\图标方案对比.html';

const PLANS = [
  { file: 'A-刀锋切线',  name: '刀锋切线', star: 3,
    desc: '一条时间线被一刀切断 —— 就是「LiteCut」名字本身，也是剪辑软件最本分的表达。',
    tags: ['最贴名字', '小尺寸最清晰'], rec: '我的首选推荐' },
  { file: 'B-剪刀',      name: '剪刀', star: 2,
    desc: '最经典的剪辑符号，一眼就懂。代价是：同类剪辑工具几乎都在用，容易撞脸。',
    tags: ['最直白', '容易同质化'], rec: '' },
  { file: 'C-时间轴',    name: '时间轴', star: 2,
    desc: '三条素材轨 + 一根亮金播放头 —— 就是工作台精剪页本页的样子。',
    tags: ['最像软件本身', '辨识度高'], rec: '次选' },
  { file: 'D-流水线阶梯', name: '流水线阶梯', star: 1,
    desc: '五级台阶逐级点亮，暗合 ①转写→粗剪→精剪→分镜→⑤成片的流水线。寓意最深，但要慢半拍才看懂。',
    tags: ['寓意最深', '第一眼不够直给'], rec: '' },
  { file: 'E-胶片',      name: '胶片', star: 1,
    desc: '胶片齿孔 + 播放键，最传统的视频符号。也正因太通用，容易和一个普通播放器混淆。',
    tags: ['最通用', '辨识度最低'], rec: '' },
  { file: 'F-LC字标',    name: 'LC 字标', star: 2,
    desc: 'LiteCut 的首字母，C 的弧口留了一道切痕，一刀斜线贯穿。品牌感最强，最不像工具图标。',
    tags: ['最有品牌感', '气质更"产品"'], rec: '' },
];

function b64(p) {
  return 'data:image/png;base64,' + fs.readFileSync(p).toString('base64');
}
function svgInline(p) {
  // 掉 width/height，只留 viewBox，方便 CSS 控制大小
  return fs.readFileSync(p, 'utf8')
    .replace(/\swidth="256"/, '').replace(/\sheight="256"/, '');
}

const cards = PLANS.map(p => {
  const dir = path.join(pngRoot, p.file);
  const s48 = b64(path.join(dir, '48.png'));
  const s32 = b64(path.join(dir, '32.png'));
  const s16 = b64(path.join(dir, '16.png'));
  const stars = '★'.repeat(p.star) + '☆'.repeat(3 - p.star);
  const badge = p.rec ? `<div class="badge">${p.rec}</div>` : '';
  return `
<section class="card">
  ${badge}
  <div class="stage">${svgInline(path.join(svgDir, p.file + '.svg'))}</div>
  <div class="meta">
    <h2>${p.name} <span class="stars">${stars}</span></h2>
    <p class="desc">${p.desc}</p>
    <div class="tags">${p.tags.map(t => `<span>${t}</span>`).join('')}</div>
    <div class="sizes">
      <figure><img src="${s48}" width="48" height="48"><figcaption>48px<br>资源管理器</figcaption></figure>
      <figure><img src="${s32}" width="32" height="32"><figcaption>32px<br>任务栏</figcaption></figure>
      <figure><img src="${s16}" width="16" height="16"><figcaption>16px<br>标题栏</figcaption></figure>
    </div>
    <div class="files">源码 <code>资源\\图标\\svg\\${p.file}.svg</code> · 成品 <code>资源\\图标\\ico\\${p.file}.ico</code></div>
  </div>
</section>`;
}).join('\n');

const html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<title>工作台图标 · 6 个候选</title>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  body {
    font-family:"Microsoft YaHei UI","Microsoft YaHei",sans-serif;
    background:#0A1626; color:#F5F1E8; padding:48px 40px 80px;
  }
  body::before { content:""; position:fixed; inset:0; z-index:-1;
    background-image:linear-gradient(#1B3A5F 0,#0000 0),linear-gradient(90deg,#1B3A5F 0,#0000 0);
    background-size:36px 36px; opacity:.35; }
  .wrap { max-width:1080px; margin:0 auto; }
  h1 { font-size:34px; letter-spacing:1px; }
  h1 em { color:#F5C86B; font-style:normal; }
  .sub { margin:10px 0 36px; color:#9FB6CE; font-size:15px; line-height:1.8; }
  .sub b { color:#F5F1E8; }

  .grid { display:grid; grid-template-columns:repeat(3,1fr); gap:20px; }
  .card { position:relative; background:linear-gradient(160deg,#10233C,#0B1A2C);
    border:1px solid #24405F; border-radius:16px; overflow:hidden; }
  .badge { position:absolute; top:12px; right:12px; z-index:2;
    background:linear-gradient(135deg,#FFE3AC,#E09B2E); color:#3A2606;
    font-size:12px; font-weight:700; padding:4px 10px; border-radius:99px;
    box-shadow:0 2px 10px rgba(224,155,46,.45); }
  .stage { display:flex; align-items:center; justify-content:center;
    padding:26px 0 12px; }
  .stage svg { width:148px; height:148px; filter:drop-shadow(0 10px 24px rgba(0,0,0,.45)); }
  .meta { padding:6px 18px 18px; }
  h2 { font-size:19px; margin-bottom:8px; }
  .stars { font-size:13px; color:#F5C86B; letter-spacing:2px; }
  .desc { font-size:13px; line-height:1.75; color:#C4D3E2; min-height:66px; }
  .tags { margin:10px 0 12px; display:flex; gap:8px; flex-wrap:wrap; }
  .tags span { font-size:11.5px; color:#8FCBF3; background:rgba(79,155,224,.14);
    border:1px solid rgba(79,155,224,.35); padding:2.5px 9px; border-radius:99px; }
  .sizes { display:flex; gap:16px; align-items:flex-end;
    background:rgba(7,18,30,.55); border:1px solid #1D3550; border-radius:10px; padding:12px 14px 8px; }
  .sizes figure { text-align:center; }
  .sizes figcaption { margin-top:6px; font-size:10.5px; color:#7E93AA; line-height:1.5; }
  .files { margin-top:10px; font-size:11px; color:#6E86A0; }
  .files code { color:#9FB6CE; font-size:10.5px; }

  .how { margin-top:44px; background:linear-gradient(160deg,#10233C,#0B1A2C);
    border:1px solid #24405F; border-radius:16px; padding:24px 28px; }
  .how h3 { font-size:17px; margin-bottom:14px; color:#F5C86B; }
  .how ol { list-style:none; counter-reset:s; }
  .how li { counter-increment:s; position:relative; padding:0 0 12px 42px;
    font-size:13.5px; line-height:1.8; color:#C4D3E2; }
  .how li::before { content:counter(s); position:absolute; left:0; top:2px;
    width:26px; height:26px; border-radius:8px; background:rgba(79,155,224,.16);
    border:1px solid rgba(79,155,224,.4); color:#8FCBF3; font-weight:700;
    display:flex; align-items:center; justify-content:center; font-size:13px; }
  .how code { background:rgba(0,0,0,.35); border:1px solid #1D3550;
    padding:1px 7px; border-radius:6px; color:#F5C86B; font-size:12.5px; }
  .note { margin-top:16px; font-size:12px; color:#7E93AA; line-height:1.7; }
</style>
</head>
<body>
<div class="wrap">
  <h1>工作台图标 · <em>6 个候选</em></h1>
  <p class="sub">
    统一用了成片的视觉语言：<b>深蓝网格底 + 亮金/浅蓝</b>，圆角方底板（Windows 11 风格）。<br>
    每张卡片右下是<b>真实小尺寸实测</b>——图标好不好用，要看缩到 16px 还认不认得。
  </p>
  <div class="grid">
${cards}
  </div>

  <div class="how">
    <h3>挑好了之后，三步挂上 exe</h3>
    <ol>
      <li>把你选中的方案复制一份：<code>资源\\图标\\ico\\某方案.ico</code> → <code>资源\\图标\\app.ico</code></li>
      <li>在 <code>工作台\\工作台.csproj</code> 的 PropertyGroup 里加一行：<br>
        <code>&lt;ApplicationIcon&gt;..\\资源\\图标\\app.ico&lt;/ApplicationIcon&gt;</code></li>
      <li>重跑 <code>build.ps1</code>。打包后资源管理器里的 exe、任务栏、标题栏就都是新图标了。</li>
    </ol>
    <p class="note">注：Windows 有图标缓存，换了图标没变是老缓存作祟 —— 换个文件名或重启资源管理器即可。
    若还想让<b>软件窗口左上角</b>也显示，另需在主窗口 XAML 设 <code>Icon="app.ico"</code>（或运行时设 this.Icon），要弄喊我。</p>
  </div>
</div>
</body>
</html>`;

fs.writeFileSync(outPath, html, 'utf8');
console.log('已生成: ' + outPath + '  (' + (fs.statSync(outPath).size / 1024).toFixed(0) + ' KB)');
