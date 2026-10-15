// ============================================================
// agent-bridge.js — 轻剪「交给 Agent」按钮的本地唤醒桥
// ------------------------------------------------------------
// 职责（单一、最小）：
//   1. 监听固定 localhost 端口（默认 18765），只暴露 POST /wake。
//   2. /wake 收到 { kind, packagePath } 后，转发一条 "领任务" 语义消息
//      到 CodeBuddy Gateway 的 /api/v1/runs，从而唤醒当前 WorkBuddy
//      会话里的 Agent（即我），由我读任务包、执行、写回响应。
//   3. 绝不直接持有/泄露网关密码：凭据从「会话 env」或「本机凭据文件」
//      读取，工作台只连这个固定端口，永不见密码。
//
// 为什么不是工作台直连 Gateway：
//   网关密码只在 WorkBuddy 会话进程 env 里（SERVER__PORT +
//   CODEBUDDY_GATEWAY_PASSWORD），工作台是独立进程读不到。
//   本桥由会话（或带凭据文件）拉起，代持凭据做转发。
//
// 依赖：仅 Node 内置模块（http / fs / os / crypto），无需 npm install。
// ============================================================

'use strict';

const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { URL } = require('url');

const BRIDGE_PORT = parseInt(process.env.BRIDGE_PORT || '18765', 10);
const BRIDGE_HOST = '127.0.0.1';
// 可选二次校验：若设了 BRIDGE_SECRET，/wake 必须带 X-Bridge-Secret 头。
const BRIDGE_SECRET = process.env.BRIDGE_SECRET || '';

const STARTED_AT = new Date().toISOString();

// ★ 日志落盘：桥接通常以隐藏窗口启动，console 输出没人看得到，
//   出问题时无从追溯。这里把 console 同时写一份到同目录 bridge.log
//   （超过 512KB 自动清空重来，不会无限增长）。
const LOG_FILE = require('path').join(__dirname, 'bridge.log');
const _logStream = (() => {
  try {
    if (fs.existsSync(LOG_FILE) && fs.statSync(LOG_FILE).size > 512 * 1024) {
      fs.writeFileSync(LOG_FILE, '');
    }
    return fs.createWriteStream(LOG_FILE, { flags: 'a' });
  } catch (e) {
    return null;
  }
})();
function _fmtArg(a) {
  if (typeof a === 'string') return a;
  if (a && a.stack) return a.stack;
  try { return JSON.stringify(a); } catch (e) { return String(a); }
}
function _writeLog(level, args) {
  if (!_logStream) return;
  try {
    _logStream.write(`[${new Date().toISOString()}] [${level}] ${args.map(_fmtArg).join(' ')}\n`);
  } catch (e) { /* 日志写失败不能影响主流程 */ }
}
const _origLog = console.log.bind(console);
const _origErr = console.error.bind(console);
console.log = (...a) => { _origLog(...a); _writeLog('INFO', a); };
console.error = (...a) => { _origErr(...a); _writeLog('ERROR', a); };

// 凭据文件：会话把当前端口+密码写到这里（仅当前用户可读），
// 供"工作台自启回退"用（工作台进程无会话 env）。env 优先于文件。
const CREDS_FILE = process.env.GATEWAY_CREDS_FILE
  || (process.env.LOCALAPPDATA
      ? require('path').join(process.env.LOCALAPPDATA, 'workbuddy', '.gateway_creds.json')
      : require('path').join(os.homedir(), '.gateway_creds.json'));

// ★ 实时凭据文件（★ 放在**用户本地目录**，不放项目目录 —— 含密码，避免随项目备份/上传泄露）：
//   官方文件由宿主写、只读、且常常是**上一会话**的旧值（R-10）；
//   这个文件由当前 Agent 会话写，每次开会话后刷一次，桥接**每次请求都重读**。
//   顺序：env > live 文件 > 官方文件。live 文件不存在时行为与从前完全一致。
const LIVE_CREDS_FILE = process.env.BRIDGE_LIVE_CREDS_FILE
  || (process.env.LOCALAPPDATA
      ? require('path').join(process.env.LOCALAPPDATA, 'LiteCut', 'bridge-creds.json')
      : require('path').join(os.homedir(), '.litecut-bridge-creds.json'));

function readCredsFile(file, source) {
  try {
    if (!fs.existsSync(file)) return null;
    const j = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (!j.port || !j.password) return null;
    return {
      port: String(j.port),
      password: j.password,
      source,
      updatedAt: j.updatedAt || null,
    };
  } catch (e) {
    console.error(`[bridge] 读凭据文件失败(${source}):`, e.message);
    return null;
  }
}

function loadCreds() {
  // 1) 会话 env 优先（最新鲜，端口每会话变）
  const port = process.env.SERVER__PORT;
  const pw = process.env.CODEBUDDY_GATEWAY_PASSWORD;
  if (port && pw) return { port: String(port), password: pw, source: 'env' };

  // 2) ★ 实时凭据文件（Agent 会话写；比官方文件新鲜）
  const live = readCredsFile(LIVE_CREDS_FILE, 'live');
  if (live) return live;

  // 3) 官方凭据文件（宿主写，可能是旧会话的）
  return readCredsFile(CREDS_FILE, 'file');
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    let tooBig = false;
    req.on('data', (c) => {
      data += c;
      if (data.length > 1_000_000) { tooBig = true; req.destroy(); }
    });
    req.on('end', () => (tooBig ? reject(new Error('body too large')) : resolve(data)));
    req.on('error', reject);
  });
}

function sendJson(res, status, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Access-Control-Allow-Origin': '*',
  });
  res.end(body);
}

// 唤醒内容
function buildWakeContent(kind, packagePath, overrideContent) {
  return overrideContent
    || `领任务（自动唤醒·桥接）：kind=${kind || 'unknown'}。\n` +
       `任务包已写入：${packagePath}\n` +
       `请按「领任务」契约：读取该目录下的 agent-request.json（取 id/system/user），` +
       `执行对应任务，结果写回同目录 agent-response.json（id 必须配对）。\n` +
       `无需用户手动说"领任务"，这条消息就是唤醒。`;
}

// ★★ 探"会话忙闲"（2026-10-01 实测出的关键前置检查）
// 为什么必须先探：网关收到 POST /api/v1/runs 后是**直接调 agentService.runDefault**，
// **既不进 UI 队列、也不排队** —— 如果 Agent 正在跑别的回合，网关只判一句
//   "result is same as initial (old execution), ignoring from runState_idle"
// 就把这条消息丢掉（日志实测结局：Task was aborted / Task was cancelled）。
// 于是"点了按钮 → 界面显示已唤醒 → Agent 毫无动静"。
// 正确做法：先看 GET /api/v1/status 的 data.busy；忙就等它空闲再发。
function isSessionBusy(creds) {
  return new Promise((resolve) => {
    const req = http.request(
      {
        hostname: '127.0.0.1',
        port: creds.port,
        path: '/api/v1/status',
        method: 'GET',
        headers: { 'Authorization': 'Bearer ' + creds.password },
        timeout: 5000,
      },
      (resp) => {
        let buf = '';
        resp.on('data', (d) => (buf += d));
        resp.on('end', () => {
          try {
            const busy = JSON.parse(buf)?.data?.busy;
            resolve(typeof busy === 'boolean' ? busy : null);
          } catch (e) {
            resolve(null);
          }
        });
      }
    );
    req.on('timeout', () => { req.destroy(); resolve(null); });
    req.on('error', () => resolve(null));
    req.end();
  });
}

// 真正发一条唤醒到 Gateway
function sendWake(creds, content) {
  const gateway = `http://127.0.0.1:${creds.port}/api/v1/runs`;

  // ★ 字段契约（2026-10-01 实测校正，务必别再写错）：
  //   网关 generic 适配器解析 body 时：
  //     id / type        —— 必填，缺失直接 400（"missing required fields (id, type)"）
  //     payload          —— 优先取 body.payload，否则 { text: body.text || body.prompt }
  //     source           —— 缺省为 { platform:'generic', sender:{id:'unknown'},
  //                                 conversation:{ id: body.id, type:'direct' } }
  //   投递时 handleMessageAsync 只认 payload.text；
  //   ★ 曾经写成 content → payload 落空 → EMPTY_PROMPT → run 秒退（202 但毫无动静）。
  //   source.conversation.id 不用自己传：网关有 primarySession 时直接用它（= 用户当前会话）。
  const payload = {
    id: 'bridge-' + crypto.randomBytes(8).toString('hex'),
    type: 'user_message',
    text: content,
    sender: { id: 'litecut-bridge', name: 'LiteCut 工作台' },
  };

  const body = JSON.stringify(payload);
  const url = new URL(gateway);

  return new Promise((resolve) => {
    const req = http.request(
      {
        hostname: url.hostname,
        port: url.port,
        path: url.pathname,
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': 'Bearer ' + creds.password,
          'Content-Length': Buffer.byteLength(body),
        },
        timeout: 20000,
      },
      (resp) => {
        let buf = '';
        resp.on('data', (d) => (buf += d));
        resp.on('end', () => {
          const ok = resp.statusCode >= 200 && resp.statusCode < 300;
          let runId = null;
          try { const j = JSON.parse(buf); runId = j?.data?.runId || null; } catch {}
          resolve({
            ok,
            status: resp.statusCode,
            runId,
            credsSource: creds.source,
            error: ok ? null : buf.slice(0, 300),
          });
        });
      }
    );
    req.on('timeout', () => { req.destroy(); resolve({ ok: false, error: '网关超时' }); });
    req.on('error', (e) => resolve({ ok: false, error: e.message }));
    req.write(body);
    req.end();
  });
}

// ★★ 排队：Agent 忙时不硬发，等它空闲再发（2026-10-01 实测出的正确做法）
//   同一 packagePath 的重复点击会**合并**（只留最后一次），避免连点堆出多条重复唤醒。
const pendingWakes = new Map();          // packagePath -> { timer, queuedAt, tries }
const WAKE_POLL_MS = 2000;               // 轮询间隔
const WAKE_MAX_WAIT_MS = 30 * 60 * 1000; // 最长等 30 分钟

function pendingCount() { return pendingWakes.size; }

function scheduleWake(creds, packagePath, content) {
  const old = pendingWakes.get(packagePath);
  if (old && old.timer) clearTimeout(old.timer);

  const queuedAt = Date.now();
  const entry = { timer: null, queuedAt, tries: 0 };
  pendingWakes.set(packagePath, entry);
  console.log(`[bridge] Agent 忙，唤醒已排队: ${packagePath}`);

  const tick = async () => {
    const cur = pendingWakes.get(packagePath);
    if (!cur || cur.queuedAt !== queuedAt) return;      // 已被更新的点击取代
    if (Date.now() - queuedAt > WAKE_MAX_WAIT_MS) {
      console.error(`[bridge] 排队唤醒超时（等满 ${WAKE_MAX_WAIT_MS / 60000} 分钟）放弃: ${packagePath}`);
      pendingWakes.delete(packagePath);
      return;
    }
    cur.tries++;
    const c = loadCreds() || creds;                     // 每次重读凭据（端口可能变）
    const busy = await isSessionBusy(c);
    if (busy === false) {
      const r = await sendWake(c, content);
      console.log(
        `[bridge] 排队唤醒已发出（约等 ${Math.round((Date.now() - queuedAt) / 1000)}s / 探测 ${cur.tries} 次）-> ` +
        `${r.ok ? 'OK' : 'FAIL'}` + (r.runId ? ` runId=${r.runId}` : '') + (r.error ? ` error=${r.error}` : '')
      );
      pendingWakes.delete(packagePath);
      return;
    }
    // true = 还忙；null = 探不到（网关重启等）→ 都继续等
    cur.timer = setTimeout(tick, WAKE_POLL_MS);
  };

  entry.timer = setTimeout(tick, 300);
  return entry;
}

// 对外入口：先探忙闲 → 忙则排队，闲则直接发
//   mergeKey：排队合并用的键。默认为 packagePath；自检（/ping）会另给一个，
//   免得"自检"和"真任务"互相把对方从队列里顶掉。
async function wakeGateway(kind, packagePath, overrideContent, mergeKey) {
  const creds = loadCreds();
  if (!creds) {
    return { ok: false, error: '凭据不可用（会话未写入凭据文件，且本进程无 env）。请让 Agent 会话先写凭据。' };
  }
  const content = buildWakeContent(kind, packagePath, overrideContent);
  if (!String(content).trim()) {
    return { ok: false, error: '消息内容为空，已拒绝发送（网关会静默丢弃空消息）' };
  }

  const busy = await isSessionBusy(creds);
  if (busy === true) {
    const e = scheduleWake(creds, mergeKey || packagePath, content);
    return {
      ok: true,
      queued: true,
      queuedAt: new Date(e.queuedAt).toISOString(),
      credsSource: creds.source,
      note: 'Agent 正在忙：已排队，等它空闲后自动唤醒（同一任务包重复点击会合并成一次）。',
    };
  }
  const r = await sendWake(creds, content);
  return { ...r, queued: false, busy };
}

// ============================================================
// ★★ 通路自检（POST /ping + GET /ping/:token）
// ------------------------------------------------------------
// 【为什么要有】
//   2026-10-01 用户的痛点原话：「点这个，测试太无感了感觉不到发生了啥」。
//   老做法（/wake）只回一句 HTTP 202，界面不知道该显示什么，用户也拿不到
//   "到底通没通"的证据 —— 而 202 恰恰**不代表 Agent 真处理了**（网关可静默丢弃）。
//
// 【自检怎么算通过】
//   不认「HTTP 202」，认**收据**：唤醒消息里要求 Agent 用工具把一行 JSON
//   写进 %LOCALAPPDATA%\LiteCut\ping-receipt.json，内容含本次一次性 token。
//   桥接盯着这个文件，**token 对上了才算通**。
//   ★ 这一条还有个额外好处：它强制 Agent 动工具 ——
//     能区分「被叫醒了但只回了句话」和「被叫醒了且真能干活」。
//
// 【为什么两段式（先 POST 拿 token，再 GET 轮询）】
//   唤醒可能要先排队（Agent 忙），一口气等到回执会挂很久、界面还是一片死寂。
//   两段式让工作台每秒刷新一次「已等 xx 秒 / 正在排队」，看得见在动。
// ============================================================

// ★★ 超时必须与「唤醒排队上限」对齐（2026-10-03 修）。
//   原来这里写死 5 分钟，而 WAKE_MAX_WAIT_MS 是 30 分钟 —— 两者矛盾：
//   Agent 忙过 5 分钟后唤醒本身还能在队列里好好等着，自检却先判 timeout 失败，
//   用户看到"连不上"而其实唤醒没死。**自检等得比唤醒短，就是自检在撒谎。**
//   现在多给 60 秒余量（覆盖唤醒排完队 + Agent 写收据的往返）。
const PING_TIMEOUT_MS = parseInt(
  process.env.BRIDGE_PING_TIMEOUT_MS || String(WAKE_MAX_WAIT_MS + 60 * 1000), 10);
const PING_RECEIPT_FILE = process.env.BRIDGE_PING_RECEIPT
  || (process.env.LOCALAPPDATA
      ? path.join(process.env.LOCALAPPDATA, 'LiteCut', 'ping-receipt.json')
      : path.join(os.homedir(), '.litecut-ping-receipt.json'));

const pings = new Map();                 // token -> 自检记录
const PING_TOKEN_RE = /^PING-[0-9a-f]{8}$/;

/** 自检用的唤醒内容：把「写收据」说成硬要求，并明确禁止做别的事。 */
function buildPingContent(token, receiptPath) {
  return `【通路自检 · 不要做任何别的事】\n` +
    `这是一次「工作台 → 桥接 → 你」的连通性自检，不是真任务，不需要读任何任务包。\n` +
    `请**立刻**做下面两件事：\n` +
    `1. 用工具把这一行 JSON 写入文件（UTF-8，不要 BOM，覆盖写入）：\n` +
    `   文件路径：${receiptPath}\n` +
    `   文件内容：{"token":"${token}","ts":"<当前时间的 ISO 字符串>"}\n` +
    `2. 然后只回复一句话：桥接自检 OK ${token}\n` +
    `除此之外什么都不要做。`;
}

/** 后台盯收据文件：token 对上了 → done；等满 → timeout。 */
function watchPingReceipt(rec) {
  const tick = () => {
    if (rec.state === 'done' || rec.state === 'timeout' || rec.state === 'failed') return;

    const elapsed = Date.now() - rec.startedAt;
    if (elapsed > PING_TIMEOUT_MS) {
      rec.state = 'timeout';
      rec.elapsedMs = elapsed;
      rec.message = `等了 ${Math.round(elapsed / 1000)} 秒没等到 Agent 的收据写盘（Agent 可能没在跑，或没按自检要求写文件）`;
      console.error(`[bridge] 自检超时: token=${rec.token}`);
      return;
    }

    let got = null;
    try {
      if (fs.existsSync(rec.receiptPath)) {
        got = JSON.parse(fs.readFileSync(rec.receiptPath, 'utf8'));
      }
    } catch (e) { /* 写到一半，下一轮再读 */ }

    if (got && got.token === rec.token) {
      rec.state = 'done';
      rec.elapsedMs = Date.now() - rec.startedAt;
      rec.message = 'Agent 已写回收据';
      console.log(`[bridge] 自检通过: token=${rec.token} 耗时 ${(rec.elapsedMs / 1000).toFixed(1)}s`);
      return;
    }
    setTimeout(tick, 800);
  };
  setTimeout(tick, 500);
}

/** 起一次自检：清旧收据 → 发唤醒 → 立即返回 token（不等结果）。 */
async function startPing() {
  const token = 'PING-' + crypto.randomBytes(4).toString('hex');
  const receiptPath = PING_RECEIPT_FILE;

  // ★ 先删旧收据：否则会立刻读到上一次的 token，把"没通"误判成"通了"。
  try { if (fs.existsSync(receiptPath)) fs.unlinkSync(receiptPath); } catch (e) { /* 删不掉就靠 token 比对兜底 */ }

  const rec = {
    token,
    receiptPath,
    startedAt: Date.now(),
    state: 'waiting',
    queued: false,
    runId: null,
    elapsedMs: null,
    message: '',
  };
  pings.set(token, rec);

  // 兜底清理：自检记录不在内存里长住
  setTimeout(() => { if (pings.has(token)) pings.delete(token); }, PING_TIMEOUT_MS + 5 * 60 * 1000);

  const r = await wakeGateway('ping', receiptPath, buildPingContent(token, receiptPath), 'ping:' + token);

  if (!r.ok) {
    rec.state = 'failed';
    rec.elapsedMs = Date.now() - rec.startedAt;
    rec.message = r.error || '唤醒失败';
    console.error(`[bridge] 自检唤醒失败: ${rec.message}`);
    return rec;
  }

  rec.queued = !!r.queued;
  rec.runId = r.runId || null;
  rec.state = r.queued ? 'queued' : 'waiting';
  watchPingReceipt(rec);
  return rec;
}

const server = http.createServer(async (req, res) => {
  // 只允许本机
  const ip = req.socket.remoteAddress || '';
  if (!ip.includes('127.0.0.1') && !ip.includes('::1') && ip !== 'localhost') {
    sendJson(res, 403, { ok: false, error: '仅允许本机访问' });
    return;
  }

  if (req.method === 'GET' && req.url === '/health') {
    const creds = loadCreds();
    sendJson(res, 200, {
      ok: true,
      port: BRIDGE_PORT,
      pid: process.pid,
      startedAt: STARTED_AT,
      hasCreds: !!creds,
      credsSource: creds?.source || null,
      credsUpdatedAt: creds?.updatedAt || null,
      gatewayPort: creds?.port || null,
      liveCredsFile: LIVE_CREDS_FILE,
      liveCredsExists: fs.existsSync(LIVE_CREDS_FILE),
      pendingWakes: pendingCount(),
      pendingPings: pings.size,
      pingReceiptFile: PING_RECEIPT_FILE,
    });
    return;
  }

  if (req.method === 'POST' && req.url === '/wake') {
    if (BRIDGE_SECRET && req.headers['x-bridge-secret'] !== BRIDGE_SECRET) {
      sendJson(res, 401, { ok: false, error: '缺少/错误的 X-Bridge-Secret' });
      return;
    }
    try {
      const raw = await readBody(req);
      const j = JSON.parse(raw || '{}');
      const kind = String(j.kind || '');
      const packagePath = String(j.packagePath || '');
      if (!packagePath) {
        sendJson(res, 400, { ok: false, error: '缺少 packagePath' });
        return;
      }
      const r = await wakeGateway(kind, packagePath, j.content || null);
      // ★ 每次唤醒都留痕（成功/失败都记）：出问题时能一眼看出「工作台到底有没有打过来」。
      console.log(
        `[bridge] /wake kind=${kind || '?'} -> ${r.ok ? 'OK' : 'FAIL'}` +
        (r.queued ? ' [已排队·Agent忙]' : '') +
        (r.runId ? ` runId=${r.runId}` : '') +
        (r.credsSource ? ` creds=${r.credsSource}` : '') +
        (r.error ? ` error=${r.error}` : '') +
        ` packagePath=${packagePath}`
      );
      sendJson(res, r.ok ? 200 : 502, r);
    } catch (e) {
      console.error('[bridge] /wake 处理异常:', (e && e.stack) || e);
      sendJson(res, 400, { ok: false, error: e.message });
    }
    return;
  }

  // ---- ★ 通路自检：起一次（立即返回 token，不等结果） ----
  if (req.method === 'POST' && req.url === '/ping') {
    if (BRIDGE_SECRET && req.headers['x-bridge-secret'] !== BRIDGE_SECRET) {
      sendJson(res, 401, { ok: false, error: '缺少/错误的 X-Bridge-Secret' });
      return;
    }
    try {
      const rec = await startPing();
      // ★ 留痕：自检是用户"能不能感知到链路"的唯一入口，每次都要记。
      console.log(
        `[bridge] /ping 已发起 token=${rec.token} -> ${rec.state}` +
        (rec.queued ? ' [已排队·Agent忙]' : '') +
        (rec.runId ? ` runId=${rec.runId}` : '') +
        (rec.message ? ` msg=${rec.message}` : '')
      );
      sendJson(res, rec.state === 'failed' ? 502 : 200, {
        ok: rec.state !== 'failed',
        token: rec.token,
        state: rec.state,
        queued: rec.queued,
        runId: rec.runId,
        receiptPath: rec.receiptPath,
        error: rec.state === 'failed' ? rec.message : null,
      });
    } catch (e) {
      console.error('[bridge] /ping 处理异常:', (e && e.stack) || e);
      sendJson(res, 400, { ok: false, error: e.message });
    }
    return;
  }

  // ---- ★ 通路自检：查一次（工作台每秒轮询它来刷新"已等 xx 秒"） ----
  if (req.method === 'GET' && req.url.startsWith('/ping/')) {
    const token = req.url.slice('/ping/'.length).split('?')[0];
    if (!PING_TOKEN_RE.test(token)) {
      sendJson(res, 400, { ok: false, error: '自检 token 格式不对' });
      return;
    }
    const rec = pings.get(token);
    if (!rec) {
      sendJson(res, 404, { ok: false, error: '这次自检已过期或不存在（桥接可能重启过）' });
      return;
    }
    sendJson(res, 200, {
      ok: true,
      token: rec.token,
      state: rec.state,
      queued: rec.queued,
      runId: rec.runId,
      elapsedMs: rec.elapsedMs == null ? Date.now() - rec.startedAt : rec.elapsedMs,
      message: rec.message,
    });
    return;
  }

  sendJson(res, 404, { ok: false, error: 'not found' });
});

server.listen(BRIDGE_PORT, BRIDGE_HOST, () => {
  const creds = loadCreds();
  console.log(`[bridge] 监听 http://${BRIDGE_HOST}:${BRIDGE_PORT}`);
  console.log(`[bridge] 凭据来源: ${creds ? creds.source : '无（/wake 将失败，请让会话写凭据文件）'}`);
  if (creds && creds.updatedAt) console.log(`[bridge] 凭据写入时间: ${creds.updatedAt}`);
  console.log(`[bridge] 实时凭据文件: ${LIVE_CREDS_FILE}（${fs.existsSync(LIVE_CREDS_FILE) ? '存在' : '不存在'}）`);
  console.log(`[bridge] 通路自检: POST /ping → 拿 token；GET /ping/<token> → 查状态（收据 ${PING_RECEIPT_FILE}）`);
});

server.on('error', (e) => {
  console.error('[bridge] 监听失败:', e.message);
  process.exit(1);
});

// ★ 全局异常保护：桥接是常驻后台进程，任何未捕获异常都不该让它**静默消失**。
//   历史成因：进程死了 → 工作台点按钮报「由于计算机积极拒绝，无法连接」(ECONNREFUSED)，
//   而界面还以为桥接在跑。宁可留一条日志继续存活，也不要无声退出。
process.on('uncaughtException', (e) => {
  console.error('[bridge] 未捕获异常（进程继续存活）:', (e && e.stack) || e);
});
process.on('unhandledRejection', (e) => {
  console.error('[bridge] 未处理的 Promise 拒绝（进程继续存活）:', (e && e.stack) || e);
});
