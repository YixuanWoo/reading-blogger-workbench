#!/usr/bin/env python
# -*- coding: utf-8 -*-
r"""
gen_shots.py — 从 body 片段拼出可渲染的 shots/shot_NNN.html（脚手架·通用版）

【怎么用】
  1. 把「工具/渲染脚手架/」整个目录拷进  项目\<项目名>\渲染工作区\
  2. 写 bodies\shot_NNN.body.html + shot_NNN.body.js（自足内联样式）
  3. python gen_shots.py            # 全部
     python gen_shots.py 5 13 26    # 只生成这几镜（★ 逐镜作业就传单镜号）

【为什么要有这个】工作台派任务时生成的 shot_NNN.html 是**文字兜底版**
（把台词和设计构思当普通文字显示在画面上），那不是成片。
真正的实现要按分镜的 motion 写 GSAP 动效 → 骨架由这里统一拼。

【本文件里沉淀的坑（全部 2026-10-03 实机踩过，别再改回去）】
  - 底图铺 body 的 background-image，**不放** #root（root 被门控到场景窗口，深内容露黑底），
    **也不放** <img>（中文路径必须百分号编码，漏一个字符就 media_load_failed → 渲出白底）；
    最稳是拷一份进 shots\ 用相对路径 ./bg.jpg。
  - <script src> 的公共库**必须是 .js**：浏览器对 <script src="x.html"> 不做 JS 解析，
    静默不执行 → 动效全失效、只剩静态文字。
  - GSAP 时间轴必须注册 window.__timelines['main']，且**恰好一个 paused 时间轴**，
    否则引擎报 timeline_not_paused / timeline_not_registered。
  - 渲染时长 = durationSec + 0.10s（3 帧安全尾），末尾定格不放新内容。
  - 资源一律相对路径（./gsap.min.js / ./bg.jpg / ./_lib.js），别跨目录别走 CDN。
"""
import json
import os
import sys

# ★ PROJ 自动探测：脚本放在 <项目>\渲染工作区\ 下时，向上找到含 stages\ 的那一层。
#   也可用环境变量 PROJ 覆盖。
def detect_proj():
    env = os.environ.get("PROJ")
    if env and os.path.isdir(os.path.join(env, "stages")):
        return env
    d = os.path.dirname(os.path.abspath(__file__))
    for _ in range(3):
        if os.path.isdir(os.path.join(d, "stages")):
            return d
        d = os.path.dirname(d)
    return ""


PROJ = detect_proj()
if not PROJ:
    sys.exit("✗ 找不到项目根（要有 stages\\ 目录）。把本脚本放在 <项目>\\渲染工作区\\ 下，或用环境变量 PROJ 指定。")

STAGE = os.path.join(PROJ, "stages", "04-render")
WORK = os.path.join(PROJ, "渲染工作区")
SHOTS = os.path.join(WORK, "shots")
BODIES = os.path.join(WORK, "bodies")

BG_URI = "./bg.jpg"     # 必须已拷进 shots\（见文件头「坑」）
LIB = "./_lib.js"       # 同上
GSAP = "./gsap.min.js"  # 同上

TAIL = 0.10  # 3 帧安全尾 @30fps

SKELETON = """<!doctype html>
<html lang="zh-CN" data-resolution="landscape">
<head>
<meta charset="UTF-8" />
<title>镜头 {NNN}</title>
<script src="{GSAP}"></script>
<style>
  /* 底图直接铺在 body 上：★ 别放 #root 的 background（root 被门控到场景窗口，深内容会露黑底），
     也别用 <img> —— 中文路径在 <img src> 里必须百分号编码，漏了就 media_load_failed 渲出白底。 */
  html, body {{ width:1920px; height:1080px; overflow:hidden; margin:0; padding:0; }}
  body {{
    background-color:#0b1020;
    background-image:url("{BG}");
    background-size:cover; background-position:center;
    color:#f3ecdc;
    font-family:'Noto Serif SC','Songti SC','Microsoft YaHei',serif;
  }}
  #root {{ position:absolute; left:0; top:0; width:1920px; height:1080px; overflow:hidden; }}
</style>
</head>
<body>
<div id="root" data-composition-id="main"
     data-start="0" data-duration="{DUR}"
     data-width="1920" data-height="1080">
{BODY}
</div>

<script src="{LIB}"></script>
<script>
  const D = {DUR};            // 该镜时长（已含安全尾）
  const USABLE = {USABLE};    // 内容必须收住的位置
  const tl = gsap.timeline({{ paused: true, defaults: {{ ease: 'power2.out' }} }});
  window.__timelines = window.__timelines || {{}};
{BODY_JS}
  window.__timelines['main'] = tl;
  tl.seek(0);
</script>
</body>
</html>
"""


def main():
    tasks_path = os.path.join(STAGE, "render-tasks.json")
    if not os.path.exists(tasks_path):
        sys.exit("✗ 缺 %s —— 先在工作台渲染页点一次「复制提示词 / 生成任务包」。" % tasks_path)
    with open(tasks_path, encoding="utf-8-sig") as f:
        data = json.load(f)
    shots = data if isinstance(data, list) else data.get("shots", data.get("tasks", []))

    only = [int(x) for x in sys.argv[1:]] if len(sys.argv) > 1 else None

    os.makedirs(SHOTS, exist_ok=True)
    made = 0
    for s in shots:
        idx = s["index"]
        if only and idx not in only:
            continue
        body_path = os.path.join(BODIES, "shot_%03d.body.html" % idx)
        js_path = os.path.join(BODIES, "shot_%03d.body.js" % idx)
        if not (os.path.exists(body_path) and os.path.exists(js_path)):
            print("  跳过 #%03d：缺 body 片段" % idx)
            continue
        with open(body_path, encoding="utf-8") as f:
            body = f.read()
        with open(js_path, encoding="utf-8") as f:
            body_js = f.read()

        dur = round(s["durationSec"] + TAIL, 3)
        html = SKELETON.format(
            NNN="%03d" % idx,
            GSAP=GSAP,
            BG=BG_URI,
            LIB=LIB,
            DUR=dur,
            USABLE=round(s["durationSec"], 3),
            BODY=body,
            BODY_JS=body_js,
        )
        dst = os.path.join(SHOTS, "shot_%03d.html" % idx)
        with open(dst, "w", encoding="utf-8", newline="\n") as f:
            f.write(html)
        made += 1

    print("已生成 %d 个实现文件 → %s" % (made, SHOTS))


if __name__ == "__main__":
    main()
