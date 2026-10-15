#!/usr/bin/env bash
# check_shot.sh — 渲完一镜的验收（脚手架·通用版）
#
# ★★★ 每渲完一镜就跑一次，**不许攒着批量查**。它给四条判据里的三条（时长/体积/抽帧），
#     第四条「抽帧肉眼核对」要你自己 Read 那些 jpg 看图，机器替不了。
#
# 用法：bash check_shot.sh 5        ← 验 #5
#       bash check_shot.sh 5 13 26 ← 验这三镜
#       bash check_shot.sh         ← 验全部已出片的镜
set -u

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
[ -n "$PROJ" ] && [ -d "$PROJ/stages" ] || { echo "✗ 找不到项目根（要有 stages/）。"; exit 1; }

OUT="$PROJ/渲染产物"
STAGE="$PROJ/stages/04-render"
HF_ROOT="${HYPERFRAMES_ROOT:-/d/HyperFrames}"
FF="$HF_ROOT/_tools/bin/ffmpeg.exe"
FP="$HF_ROOT/_tools/bin/ffprobe.exe"
CHK="${CHK:-$HF_ROOT/tmp/chk}"
mkdir -p "$CHK"

PY="${PY:-C:/Users/wuhao/.workbuddy/binaries/python/versions/3.13.12/python.exe}"
command -v "$PY" >/dev/null 2>&1 || PY=python

if [ $# -gt 0 ]; then LIST="$@"; else
  LIST=$(cd "$OUT" && ls shot_*.mp4 2>/dev/null | sed 's/shot_//;s/\.mp4//' | tr '\n' ' ')
fi

# 目标时长表（durationSec + 0.10 安全尾），从任务包读
TARGETS=$("$PY" - "$STAGE/render-tasks.json" <<'PY' 2>/dev/null
import json, sys
try:
    d = json.load(open(sys.argv[1], encoding='utf-8-sig'))
except Exception:
    sys.exit()
rows = d if isinstance(d, list) else d.get('shots', d.get('tasks', []))
for r in rows:
    print("%d %.3f" % (r['index'], float(r['durationSec']) + 0.10))
PY
)

bad=0
for n in $LIST; do
  nn=$(printf "%03d" "$((10#$n))")
  f="$OUT/shot_$nn.mp4"
  [ -f "$f" ] || { echo "  #$nn 缺产物"; bad=$((bad+1)); continue; }

  dur=$("$FP" -v error -show_entries format=duration -of csv=p=0 "$f" 2>/dev/null)
  size=$(stat -c%s "$f" 2>/dev/null || echo 0)
  tgt=$(echo "$TARGETS" | awk -v i="$((10#$n))" '$1==i{print $2}')

  # 抽 2~3 个时间点（≈0.42 / 0.85），末帧若接近结尾就跳过
  for tag in a b c; do
    case $tag in a) frac=0.42;; b) frac=0.85;; c) frac=0.60;; esac
    t=$(awk -v d="$dur" -v f="$frac" 'BEGIN{printf "%.2f", d*f}')
    "$FF" -ss "$t" -i "$f" -frames:v 1 -vf scale=760:-2 -q:v 4 -y "$CHK/chk_${nn}_${tag}.jpg" >/dev/null 2>&1
  done

  note=""
  [ "$size" -lt 20000 ] && { note="$note  ⚠ 体积偏小(可能空画面/纯色)"; }
  if [ -n "$tgt" ]; then
    note="$note$(awk -v d="$dur" -v t="$tgt" 'BEGIN{dd=d-t; if(dd<0)dd=-dd; if(dd>0.12) printf "  ⚠ 时长偏差 %+.3fs", d-t}')"
  fi
  [ -z "$note" ] && note="  ✓ 三条机检通过（抽帧仍需肉眼核）"
  echo "  #$nn  ${dur}s  $((size/1024))KB  目标 ${tgt:-?}s$note"
  [ -n "$(echo "$note" | grep '⚠')" ] && bad=$((bad+1))
done

echo ""
echo "抽帧写到 $CHK/chk_NNN_{a,b,c}.jpg —— ★ 下一步必须 Read 这些图，"
echo "逐个核对该镜「画面/动效」要求的元素是否真出现、有没有出界/重叠/被裁/主体过小。"
echo "机检不通过或肉眼不对：$bad 镜需处理"
