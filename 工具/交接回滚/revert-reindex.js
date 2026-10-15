// ============================================================
// revert-reindex.js —— 离线撤销一次「应用到下游」（交接）
// ============================================================
//
// 【什么时候用它】
//   工作台里的「撤销这次交接」按钮**只在当前页面停留期间有效** ——
//   分镜页不是 KeepAlive 页（见 WorkbenchWindow.GoTo/Resolve），切页就销毁重建，
//   撤销快照（_lastCommitLog / _preCommitShots）跟着没了。
//   一旦你已经离开分镜页、又想把交接退回去，就只能用这个脚本。
//
//   依赖依据：`stages\03-shot\reindex-log-<时间戳>.json` —— 那一次交接的完整记录
//   （每条文件名怎么搬的、镜号怎么重排的都记在里面），这是唯一权威。
//
// 【用法】
//   node revert-reindex.js --project "<项目目录>" [--log "<日志路径>"] [--before "<交接前的 project.json 备份>"]
//   node revert-reindex.js --project "<项目目录>" --before "..." --apply      ← 真回滚
//
//   --log     不给就自动取 stages\03-shot\ 里最新的 reindex-log-*.json
//   --before  不给就自动找同目录下 ...\stages\03-shot\ 的 …不行 —— **必须给**：
//             交接前的镜头表内容（含已交接的镜号/文案/时长）日志里没有，
//             只能来自一份交接前的 project.json 快照。
//             取法：交接前先 cp project.json 到某个备份名。
//
// 【它做了什么】（严格对齐工程里 ReindexMigration.Rollback 的算法，并补齐它漏掉的账本）
//   1. 204 个文件双缓冲反向搬回（第一遍全改临时名，第二遍落原位 —— 防互相覆盖）
//   2. project.json：Rows ← 交接前快照；DraftRows 清空；CommittedAt 清空
//      ★ NextShotSeq **不回退**（ID 永久不复用，回退会造成将来撞 ID）
//   3. stages\03-shot\shotlist.json ← 交接前快照（下游只读它）
//   4. 渲染产物\render-manifest.json ← 镜号/文件名改回旧值
//      ★ 这一步工程里的 Rollback **没做**（Execute 有 Remap、Rollback 没有）——
//        不做的话渲染页会把产物全判成「产物过期（镜号已对不上）」。
//   5. 渲染产物\render_tasks.json + stages\04-render\render-tasks.json 按旧表重建
//   6. reindex-log 标记 Status=rolledback（留痕）
//   7. 清掉回滚后变空的 _已废弃 目录
//
// 【跑之前务必】
//   · **关掉工作台**（否则它退出时会把内存里的新表写回来，覆盖你的回滚）
//   · 先整目录备份（脚本不负责备）
//
// 详见 skill「litecut-workbench」§5.4 / §5.13。
// ============================================================

const fs = require('fs');
const path = require('path');

// ---------------- 参数 ----------------
function argOf(name) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 ? process.argv[i + 1] : null;
}
const APPLY = process.argv.includes('--apply');
const P = argOf('project');
if (!P) {
  console.error('用法: node revert-reindex.js --project "<项目目录>" --before "<交接前的 project.json>" [--log <日志>] [--apply]');
  process.exit(2);
}
const BAK_PROJECT = argOf('before');
if (!BAK_PROJECT || !fs.existsSync(BAK_PROJECT)) {
  console.error('缺 --before：需要一份**交接前**的 project.json 快照（交接前的镜头表内容日志里没有）。');
  process.exit(2);
}
function latestLog(dir) {
  const d = path.join(dir, 'stages/03-shot');
  if (!fs.existsSync(d)) return null;
  const fs2 = fs.readdirSync(d).filter(f => /^reindex-log-.*\.json$/.test(f)).sort();
  return fs2.length ? path.join(d, fs2[fs2.length - 1]) : null;
}
const LOG_PATH = argOf('log') || latestLog(P);
if (!LOG_PATH || !fs.existsSync(LOG_PATH)) {
  console.error('找不到 reindex-log-*.json（用 --log 指定）');
  process.exit(2);
}

const rd = f => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, ''));
const pad3 = n => String(n).padStart(3, '0');

/** 与工程一致：CRLF + 2 空格缩进 + 中文不转义；BOM 按各文件原有习惯 */
function writeJson(file, obj, { bom = false } = {}) {
  const text = JSON.stringify(obj, null, 2).replace(/\n/g, '\r\n');
  const body = Buffer.from(text, 'utf8');
  fs.writeFileSync(file, bom ? Buffer.concat([Buffer.from([0xEF, 0xBB, 0xBF]), body]) : body);
}

/** C# WorkbenchState.Fnv1a（64 位，x16 小写） */
function fnv1a(str) {
  const mask = (1n << 64n) - 1n;
  let h = 14695981039346656037n;
  for (let i = 0; i < str.length; i++) {
    h = (h ^ BigInt(str.charCodeAt(i))) & mask;
    h = (h * 1099511628211n) & mask;
  }
  return h.toString(16).padStart(16, '0');
}

/** C# RenderManifest.FingerprintOf */
function fingerprintOf(r) {
  const dur = typeof r.DurationSec === 'number' ? r.DurationSec : (r.EndSec - r.StartSec);
  return fnv1a([r.Text || '', r.Description || '', r.Motion || '', r.Image || '',
                Number(dur).toFixed(3)].join('\u0001'));
}

// ============================================================
const log = rd(LOG_PATH);
const cur = rd(path.join(P, 'project.json'));
const bak = rd(BAK_PROJECT);
const rows55 = bak.Shots.Rows;

console.log('====================================================');
console.log(APPLY ? '★ 回滚模式（--apply）' : '体检模式（dry-run，不动文件）');
console.log('  项目 : ' + P);
console.log('  日志 : ' + LOG_PATH);
console.log('  快照 : ' + BAK_PROJECT);
console.log('====================================================\n');

// ---------- 0. 自检：用快照的行重算指纹，跟 manifest 已存指纹对账 ----------
console.log('【自检】用快照重算指纹 ↔ manifest 已存指纹');
const manPath = path.join(P, '渲染产物/render-manifest.json');
const man = rd(manPath);
const byId = {};
rows55.forEach(r => { byId[r.Id] = r; });
let fpOk = 0, fpBad = 0;
Object.entries(man).forEach(([id, e]) => {
  const r = byId[id];
  if (!r) { fpBad++; console.log('   ✗ ' + id + ' 快照里没有这一镜（两次交接之间又改过？）'); return; }
  const calc = fingerprintOf(r);
  if (calc !== e.Fingerprint) { fpBad++; console.log('   ✗ ' + id + ' 算得=' + calc + ' 清单里=' + e.Fingerprint); }
  else fpOk++;
});
console.log('   对得上 = ' + fpOk + ' / 对不上 = ' + fpBad +
            (fpOk > 0 && fpBad === 0 ? '  ✅ 快照与清单一致，可以回滚' : '  ⚠ 对不上说明这份快照不是"这次交接之前"的那份，请换一份'));

// ---------- 1. 文件反向搬回 ----------
console.log('\n【1】文件反向搬回（双缓冲，共 ' + log.Moves.length + ' 条）');
const token = Math.random().toString(16).slice(2, 10);
const pass1 = [];
for (const m of log.Moves) {
  if (!m.Done) continue;
  const cur0 = fs.existsSync(m.To) ? m.To : (m.Temp && fs.existsSync(m.Temp) ? m.Temp : null);
  if (!cur0) continue;
  pass1.push({ m, cur: cur0 });
}
console.log('   第一遍 ' + pass1.length + ' 个 → 临时名；第二遍 → 原位');
if (!APPLY && pass1.length === 0) console.log('   （没有可搬的：要么已经回滚过，要么日志与新状态不符）');

if (APPLY) {
  let ok1 = 0, bad1 = 0;
  for (const x of pass1) {
    const t = x.m.From + '.rollback-' + token + '.tmp';
    try { fs.renameSync(x.cur, t); x.m.Temp = t; x.done1 = true; ok1++; }
    catch (e) { x.m.Error = '回滚第一遍：' + e.message; bad1++; console.log('   ✗ ' + x.m.From + ' → ' + e.message); }
  }
  let ok2 = 0, bad2 = 0;
  for (const x of pass1) {
    if (!x.done1) continue;
    try {
      fs.mkdirSync(path.dirname(x.m.From), { recursive: true });
      if (fs.existsSync(x.m.From)) fs.rmSync(x.m.From, { force: true });
      fs.renameSync(x.m.Temp, x.m.From);
      x.m.Temp = ''; x.m.Done = false; ok2++;
    } catch (e) { x.m.Error = '回滚第二遍：' + e.message; bad2++; console.log('   ✗ ' + x.m.From + ' → ' + e.message); }
  }
  console.log('   第一遍 ' + ok1 + '/' + pass1.length + '，第二遍 ' + ok2 + '/' + pass1.length);
  if (bad1 || bad2) { console.log('\n⚠ 有失败 —— 停止后续步骤。'); process.exit(1); }
}

// ---------- 2. project.json ----------
console.log('\n【2】project.json  Rows ' + cur.Shots.Rows.length + ' → ' + rows55.length +
            ' ｜ DraftRows ' + (cur.Shots.DraftRows || []).length + ' → 0 ｜ NextShotSeq ' + cur.Shots.NextShotSeq + ' 保持不回退');
if (APPLY) {
  cur.Shots.Rows = rows55;
  cur.Shots.DraftRows = [];
  cur.Shots.CommittedAt = '';
  writeJson(path.join(P, 'project.json'), cur, { bom: false });
}

// ---------- 3. shotlist.json ----------
console.log('\n【3】stages/03-shot/shotlist.json → ' + rows55.length + ' 条；shotlist-draft.json 删除');
if (APPLY) {
  writeJson(path.join(P, 'stages/03-shot/shotlist.json'), rows55, { bom: false });
  const dp = path.join(P, 'stages/03-shot/shotlist-draft.json');
  if (fs.existsSync(dp)) fs.rmSync(dp, { force: true });
}

// ---------- 4. render-manifest.json ----------
console.log('\n【4】render-manifest.json');
const newMan = {};
let remapped = 0, rebuilt = 0;
for (const [id, e] of Object.entries(man)) {
  const old = log.OldIndexById[id];
  if (old == null) continue;
  newMan[id] = { Shot: old, RenderedAt: e.RenderedAt, Fingerprint: e.Fingerprint, File: 'shot_' + pad3(old) + '.mp4' };
  remapped++;
}
const parkedIds = new Set(log.Moves.filter(m => m.Kind === 'park-mp4').map(m => m.Id));
const sampleAt = (man[Object.keys(man)[0]] || {}).RenderedAt || new Date().toISOString();
for (const id of (log.RemovedIds || [])) {
  if (!parkedIds.has(id)) continue;          // 没产物被停放 = 原始清单里本来就没它
  const r = byId[id];
  const old = log.OldIndexById[id];
  if (!r || old == null) continue;
  newMan[id] = { Shot: old, RenderedAt: sampleAt, Fingerprint: fingerprintOf(r), File: 'shot_' + pad3(old) + '.mp4' };
  rebuilt++;
}
const sortedMan = {};
Object.keys(newMan).sort().forEach(k => { sortedMan[k] = newMan[k]; });
console.log('   改回镜号 ' + remapped + ' 条 + 重建 ' + rebuilt + ' 条 = ' + Object.keys(sortedMan).length + ' 条');
if (APPLY) writeJson(manPath, sortedMan, { bom: true });   // 与原件一致（工程用 Encoding.UTF8 写，带 BOM）

// ---------- 5. render_tasks.json ×2 ----------
console.log('\n【5】render_tasks.json（两份）→ ' + rows55.length + ' 条');
if (APPLY) {
  const curTasks = rd(path.join(P, '渲染产物/render_tasks.json'));
  curTasks.tasks = rows55.map(r => ({
    index: r.Index, id: r.Id,
    startSec: Number(r.StartSec.toFixed(3)),
    endSec: Number(r.EndSec.toFixed(3)),
    durationSec: Number((typeof r.DurationSec === 'number' ? r.DurationSec : r.EndSec - r.StartSec).toFixed(3)),
    script: r.Text, visualIdea: r.Description, motion: r.Motion,
    imageNeed: r.Image, imageFile: r.ImageFile,
    source: 'shot_' + pad3(r.Index) + '.html',
    output: 'shot_' + pad3(r.Index) + '.mp4',
  }));
  curTasks.generatedAt = new Date().toISOString();
  writeJson(path.join(P, '渲染产物/render_tasks.json'), curTasks, { bom: false });
  writeJson(path.join(P, 'stages/04-render/render-tasks.json'), curTasks, { bom: false });
}

// ---------- 6. 日志留痕 ----------
console.log('\n【6】reindex-log → Status=rolledback');
if (APPLY) {
  log.Status = 'rolledback';
  log.Reason = '已搬回 ' + pass1.length + ' 个文件';
  writeJson(LOG_PATH, log, { bom: false });
}

// ---------- 7. 清空目录 ----------
const cand = [
  path.join(P, '渲染产物/_已废弃'),
  path.join(P, '渲染工作区/bodies/_已废弃'),
  path.join(P, '渲染工作区/shots-blocks/_已废弃'),
  path.join(P, 'stages/04-render/thumbs/_已废弃'),
];
console.log('\n【7】清掉变空的 _已废弃');
for (const d of cand) {
  if (!fs.existsSync(d)) continue;
  const n = fs.readdirSync(d).length;
  console.log('   ' + d + ' 剩 ' + n);
  if (APPLY && n === 0) { try { fs.rmdirSync(d); } catch { } }
}

console.log('\n' + (APPLY ? '★ 回滚已执行。' : '体检结束（未改动文件）。加 --apply 才真做。'));
