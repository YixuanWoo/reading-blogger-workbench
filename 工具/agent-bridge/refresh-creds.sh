#!/usr/bin/env bash
# ============================================================================
# LiteCut 桥接「实时凭据」刷新 —— 由 WorkBuddy 的 SessionStart hook 自动调用
#
# 【为什么必须有它】
#   WorkBuddy 网关的「端口 + 密码」每个会话都不一样（实测端口 59769 → 49920），
#   而工作台拉起的 agent-bridge 是独立进程，读不到会话的 env。
#   桥接每次 /wake 都会重读下面这个文件：
#       %LOCALAPPDATA%\LiteCut\bridge-creds.json
#   所以在「每次新会话开始」时把当前凭据写进去，桥接就永远连得上。
#
# 【没有它时的症状】（2026-10-01/02 实测）
#   工作台点「检测 Agent」→ 弹「无法连接」，
#   桥接日志 bridge.log 里是：
#       connect ECONNREFUSED 127.0.0.1:<上一次会话的旧端口>
#   表现就是"每次都得先让 AI 配合一下才能用"。
#
# 【参数】第一个参数是触发的事件名（SessionStart / UserPromptSubmit），
#   只有 SessionStart 才回一行提示（会进入新会话上下文，便于日后排查）。
#
# 【安全】拿不到 env 就什么都不做 —— 绝不覆盖/破坏已有的凭据文件。
# ============================================================================
set -u

EVENT="${1:-SessionStart}"

PORT="${SERVER__PORT:-}"
PW="${CODEBUDDY_GATEWAY_PASSWORD:-}"

# 拿不到凭据（例如 hook 环境被净化）→ 静默退出，保持原文件不动
if [ -z "$PORT" ] || [ -z "$PW" ]; then
  exit 0
fi

DIR="${LOCALAPPDATA:-$HOME/AppData/Local}/LiteCut"
FILE="$DIR/bridge-creds.json"
mkdir -p "$DIR" 2>/dev/null || exit 0

# GNU date 的 %:z 会给出 +08:00；拿不到就退回不带冒号的写法（桥接只展示、不解析）
TS=$(date +%Y-%m-%dT%H:%M:%S%:z 2>/dev/null || date +%Y-%m-%dT%H:%M:%S)

# 注意：绝不能用 `Encoding.UTF8` 那种带 BOM 的写法 —— 桥接是 JSON.parse 读它，带 BOM 会解析失败。
# 这里用 heredoc 直出，天然无 BOM、LF 换行。
cat > "$FILE" <<EOF
{
  "port": "$PORT",
  "password": "$PW",
  "updatedAt": "$TS",
  "note": "LiteCut 桥接实时凭据。由 SessionStart hook 自动写入；桥接每次 /wake 都重读。端口与密码每会话都会变。"
}
EOF

if [ "$EVENT" = "SessionStart" ]; then
  echo "[LiteCut] 桥接实时凭据已刷新：gatewayPort=$PORT（桥接下次 /wake 即生效，无需重启桥接）"
fi

exit 0
