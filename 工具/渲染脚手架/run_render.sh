#!/usr/bin/env bash
# run_render.sh — 逐镜渲染（脚手架·通用版）
#
# ★★★ 硬规矩：一次只渲一个镜头，渲完立刻抽帧自查（check_shot.sh），过了才渲下一个。
#     禁止批量渲完再一起看 —— 批量会把同一处错误复制到所有镜头，最后只能全批重来。
#
# 用法：bash run_render.sh 5        ← 只渲 #5（逐镜作业的标准用法）
#       bash run_render.sh 5 13 26 ← 连续渲这三镜（仍需逐镜检查）
#       bash run_render.sh         ← 全部（只在已逐镜验过、需要整批重跑时用）
set -u

# ── 定位：脚本放在 <项目>\渲染工作区\ 下；向上找到含 stages\ 的那层 = 项目根
detect_proj() {
  local d="$1"
  for _ in 1 2 3; do
    [ -d "$d/stages" ] && { echo "$d"; return; }
    d="$(cd "$d/.." 2>/dev/null && pwd)"
  done
  echo ""
}
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
PROJ="${PROJ:-$(detect_proj "$HERE")}"
[ -n "$PROJ" ] && [ -d "$PROJ/stages" ] || { echo "✗ 找不到项目根（要有 stages/）。把脚本放在 <项目>/渲染工作区/ 下，或用 PROJ=... 指定。"; exit 1; }

SHOTS="$PROJ/渲染工作区/shots"
OUT="$PROJ/渲染产物"
HF_ROOT="${HYPERFRAMES_ROOT:-/d/HyperFrames}"
FF="$HF_ROOT/_tools/bin/ffmpeg.exe"
FP="$HF_ROOT/_tools/bin/ffprobe.exe"

export HYPERFRAMES_SKIP_SKILLS='1'
export HYPERFRAMES_BROWSER_PATH='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
export PATH="$HF_ROOT/_tools/bin:$PATH"
export TMP="$HF_ROOT/tmp" TEMP="$HF_ROOT/tmp" HYPERFRAMES_FONT_CACHE_DIR="$HF_ROOT/tmp"
HF="$HF_ROOT/node_modules/.bin/hyperframes"
LOG="$HF_ROOT/tmp/render.log"
mkdir -p "$OUT" "$HF_ROOT/tmp"

cd "$SHOTS" || exit 1
if [ $# -gt 0 ]; then LIST="$@"; else
  LIST=$(for f in shot_*.html; do echo "${f#shot_}" | sed 's/\.html//'; done | tr '\n' ' ')
fi

ok=0; bad=0
for n in $LIST; do
  nn=$(printf "%03d" "$((10#$n))")   # ★ 必须 10# 强制十进制：printf 把 008/009 当八进制解析会报 invalid octal
  [ -f "shot_$nn.html" ] || { echo "  #$nn 缺实现文件"; bad=$((bad+1)); continue; }
  before=0; [ -f "$OUT/shot_$nn.mp4" ] && before=$(stat -c%s "$OUT/shot_$nn.mp4" 2>/dev/null || echo 0)

  "$HF" render -c "shot_$nn.html" -o "$OUT/shot_$nn.mp4" -f 30 -w 2 >> "$LOG" 2>&1

  # ★ 成功判据：mp4 存在且**比实现 html 新**。
  #   "文件存在" 是旧口径 —— 渲染失败时旧产物还在原位，会被误报成 OK（2026-10-03 实际踩过）。
  if [ -f "$OUT/shot_$nn.mp4" ] && [ "$OUT/shot_$nn.mp4" -nt "shot_$nn.html" ]; then
    d=$("$FP" -v error -show_entries format=duration -of csv=p=0 "$OUT/shot_$nn.mp4")
    s=$(stat -c%s "$OUT/shot_$nn.mp4")
    echo "  #$nn  OK  ${d}s  $((s/1024))KB   （旧 ${before} B → 新 ${s} B）"
    ok=$((ok+1))
  else
    echo "  #$nn  失败（详见 $LOG）"
    grep -A6 "shot_$nn" "$LOG" | tail -8
    echo "  ⚠ 若日志里是 EPERM / rename 失败：旧 MP4 被播放器或审阅台占用了。"
    echo "     先挪走旧文件再重渲：mv \"$OUT/shot_$nn.mp4\" \"$OUT/shot_$nn.stuck.mp4\""
    bad=$((bad+1))
  fi
done
echo "完成：成功 $ok / 失败 $bad"
[ "$bad" -eq 0 ] || exit 2
