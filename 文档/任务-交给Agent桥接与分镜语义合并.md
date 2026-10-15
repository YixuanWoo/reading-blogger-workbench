# 任务文档：交给 Agent 桥接 + 分镜语义合并（算法初切）

> 状态：**任务一/二/三已完成（桥接+自动唤醒+分镜语义合并）**。任务四并入任务六、任务五/六待做（依赖⑤组接器与④块结构稳定）。
> 关联：`litecut-workbench` skill 5.9 / 5.10 / 5.11；项目 MEMORY.md「领任务」段；
> 9-29 日志「只读复核 Agent 被代码唤醒」。（原关联的 `精剪下游工作流-设计草案.md` 已于 2026-10-01 有意删除 —— 项目已落地，结论已并入 skill 与《维护说明》。）

---

## 0. 已验证前提（写文档时当场复核）

- **Gateway 唤醒机制已实测通过**（2026-09-28/29）：CodeBuddy Gateway `POST /api/v1/runs`
  往**当前活跃会话**灌消息 → Agent 自动开新一轮，用户零操作（实锤 IN"请只回答一个字：好"→OUT"好"）。
- **凭据确实存在本会话进程 env**（写文档时复核）：`SERVER__PORT=55881`、`CODEBUDDY_GATEWAY_PASSWORD` 有值。
  → 由 WorkBuddy 拉起的子进程能继承这对凭据；工作台工作台是独立进程，自己读不到，所以需要桥接代持。
- 「领任务」契约已立：用户点「交给 Agent」→ 写任务包到 `stages/.../agent-request.json`
  → 我（WorkBuddy 会话里的 Agent）读包、执行、写回 `agent-response.json`。当前**唯一缺口**是
  工作台拿不到 Gateway 凭据，所以现在「交给 Agent」仍需用户手动说"领任务"。

---

## 1. 任务一：Gateway 桥接（凭据交接，让按钮自动唤醒）

### 1.1 方案：桥接守护进程（推荐，已验证凭据可继承）
- 由 **WorkBuddy 在会话启动时拉起**一个轻量桥接进程（Node 或 .NET 均可）。它作为 WorkBuddy 子进程，
  **继承 `SERVER__PORT` + `CODEBUDDY_GATEWAY_PASSWORD`**。
- 桥接进程监听**固定 localhost 端口**（建议 `18765`），对外只暴露一个语义化接口：
  - `POST /wake`，body：`{ "kind": "shot|render|reshoot", "packagePath": "<项目>/stages/.../agent-request.json" }`
  - 桥接内部：拼 `POST http://127.0.0.1:<SERVER__PORT>/api/v1/runs`，带 Gateway 鉴权，
    注入一条"领任务"语义消息（指明 kind + 包路径），返回 run 状态。
- 工作台工作台「交给 Agent」按钮**只连这个固定端口的桥接**，绝不直接持有密码。
- 安全：localhost 单用户；桥接只接受"写任务包 + 唤醒"语义、拒绝任意指令；可选加本机共享 secret 二次校验。

### 1.2 用户问"写好就能自动拉起对吧？"——自答
- **机制层：确定能。** Gateway 唤醒 9-28/29 实测通过，非推测。
- **桥接层：确定能，前提是桥接由 WorkBuddy 拉起（拿到密码）。** 这是唯一风险点。
  若届时 WorkBuddy 不拉起桥接、或密码确认无法离开会话进程且无文件化通道，则改走
  **B 方案：WorkBuddy 把凭据写到仅当前用户可读的本地文件（`%LOCALAPPDATA%/workbuddy/.gateway_creds.json`，
  ACL 限当前用户），工作台读文件直连 Gateway。** 实现时**第一步就验证凭据可达性**，二选一落地。
- **结论**：桥接跑起来后，点「交给 Agent」会**自动唤醒我、走完领任务全流程、回写响应**，
  不再需要你手动说"领任务"。

### 1.3 验收标准
- 手动起桥接 + 工作台点一次「交给 Agent 渲染」→ 我被自动唤醒（新回合）、读包、整批渲、写回
  `agent-response.json` + `agent-progress.json`，工作台进度区刷新。**全程无手动"领任务"。**

---

## 2. 任务二：分镜 / 渲染 / 单镜重渲 「交给 Agent」按钮 一次性完善

在任务一桥接基础上，把三处按钮统一走桥接（不再依赖手动"领任务"）：

| 按钮位置 | kind | 任务包 | 唤醒后我做啥 | 回包 |
|---|---|---|---|---|
| 分镜页「交给 Agent 设计」 | `shot` | `03-shot/agent-request.json` | 读块表+设计要求，出 shots JSON | 同目录 `agent-response.json` |
| 渲染页「交给 Agent 渲染」 | `render` | `04-render/agent-request.json` | 整批渲 + 实时进度 + 图片清单 | 同目录 `agent-response.json` |
| 单镜重渲「交给 Agent 重渲」 | `reshoot` | `04-render/agent-reshoot.json` | 原 HTML 上改（不重写）、时长不动 | `agent-reshoot-response.json` |

- 统一契约：按钮点击 = **写包 → 调桥接 `/wake` → 进度区轮询/读响应**，与现有 `AgentBridge`
  文件通道协议一致（skill 5.9/5.10）。
- **进度指示不能省**（设计决策⑥：点下去不能干等；沿用现有进度区/事件流水）。
- 三处按钮现已存在（手动"领任务"可用），本任务=把它们从"手动唤醒"升级为"桥接自动唤醒"。

---

## 3. 任务三：分镜页「语义合并」—— 算法初切 + 手动调边界（两步式）

> **用户拍板（2026-09-30）：算法初切，不走 AI。** 短视频文案量小，初切是机械分组，
> 算法瞬成确定、用户紧接着手动调边界即可；AI 留在终局"设计画面"（任务二.1）。

### 3.1 两步式流程
1. **点「合并」→ 算法出块表**：本地**贪心语义成组**（复用 `make_blocks.py` 逻辑搬进工作台 UI），
   每块 5~9s；纯算法、瞬成、确定、可复现、**不走 Agent**。
2. **用户手动调边界**：合并/拆分**纯本地瞬成**（不走 Agent）；拆分后**重算相邻块 `renderDurationSec`**
   （到下一块开始，含句间气口，不能丢）；改过的块打 **`Edited` 标记**，重算/重开项目都保住
   （匹配口径 = 源起止时间，与 skill 5.7 句子 `Edited` 同思路）。
3. （之后）边界定稿 → 走任务二.1 的「交给 Agent 设计」做画面。

### 3.2 实现要点（出改造方案时细化，先读代码）
- 先读：`Step2ShotView.xaml(.cs)`、`ShotSplitter.cs`、`FinalScriptBuilder.cs`、`project.json` 的 `Shots.Rows`。
- 块表 UI：列出 N 个块，每块显示「起止句 / 时间码 / 时长 / Edited 标记」。
- 交互：合并按钮、拆分按钮、边界微调（拖拽或输入句索引）。
- 时长重算 + Edited 保真（防止被重算/重开冲掉）。

---

## 4. 任务四：音频拼接改用工作台原生能力，废弃 build_audio.py 的 main()

> 来源：2026-09-30 讨论。用户指出"工作台本来就有导出，为什么用 Python 脚本"。经核对代码确认。

### 4.1 现状核对（已读代码）
- 工作台原生 `FFmpegExporter.ExportRanges`：按 `CutPlan`（来自 `FineCut.Clips` 的保留区间）把源素材裁切拼接。
- `FFmpegAssembler.BuildAudioGraph`：⑤页组接时**已原生从 `FineCut.Clips` + 源录音重建音轨**，**根本不读** `build_audio.py` 产物。
- `build_audio.py` 的 `main()`：读 `FineCut.Clips` → atrim+concat+音量+pan 转立体声 → `audio-final.wav`。
  **与工作台原生导出功能重复**，且只有 Python 原型（assemble.py）用过它，⑤页从未消费。
- `build_audio.py` 的 `warp_to_blocks()`：按块微拉伸音频去凑渲染画面（asetrate）。
  这是"下游特有"逻辑，但**在新「音频优先」方针下应改为 conform 画面、不再拉伸音频**。

### 4.2 结论
- **`build_audio.py` 的 `main()` 可以删**（原型残留；⑤页用原生 `FFmpegAssembler` 重建音轨，等价且更稳）。
- 音频拼接的"真源"= 工作台原生能力（`FineCut.Clips` → `FFmpegAssembler`）。下游拿到的音轨就是精剪完成稿，零额外处理。
- `warp_to_blocks()` 代表的"拉伸音频"逻辑，随「音频优先」改造一并移除（见任务六）。

### 4.3 验收
- 删除 `build_audio.py`（或仅保留 `warp_to_blocks` 注释作历史参考，标注已废弃）。
- 确认⑤页组接**不依赖任何 Python 脚本**：音轨 100% 由 `FFmpegAssembler` 从 `FineCut.Clips` 重建。

---

## 5. 任务五：渲染页「应用到下游」按钮 —— 只宣布源 + 算 Δ 预览，不碰文件

> 来源：2026-09-30 讨论。用户拍板业务隔离原则：**渲染只管渲染，拼接补帧逻辑在最后拼接做，职责不可混**。

### 5.1 决策（用户拍板）
- 渲染页新增「应用到下游」按钮。点击后**只干两件事**：
  1. **宣布**：这套块（51 个 MP4）成为下游组接的正式源（渲染层"已应用"标记，具体字段出方案时定）。
  2. **算 Δ 预览**：逐块用 ffprobe 取渲染 MP4 实测时长，对照该块对应精剪音频段时长，算出
     `Δ = 画面时长 − 音频段时长`，列成预览（**不落文件、不改文件**）。
- **不碰任何渲染产物文件**：不在 ④ 做 tpad/trim 重写，不 mutate 原始块。
- 配套铁律（任务六）：画面设计时长 = 音频段时长 + N 帧安全缓冲，所以 Δ 理论上有界（≈安全帧+整帧余量）。

### 5.2 业务隔离依据
- conform（补帧/切帧）本质属「组装」阶段，放 ⑤ 才顺：⑤本就要 concat，顺手在 concat 图里
  trim/tpad，一次重编码搞定；音频反正也要重编码成 aac。④若提前重写块，等于 mutate 源、多一代 h264 损耗，重渲对账困难。
- ④职责保持"渲染"，⑤职责保持"组装"，不可混。

### 5.3 验收
- 点「应用到下游」→ 进度区列出 51 块逐块 Δ（正=画面偏长待裁 / 负=偏短待补），**磁盘上渲染产物字节数不变**。

---

## 6. 任务六：⑤ 组接 conform —— 音频零处理 + 画面裁安全帧对齐 + 轻量剪辑区

> 来源：2026-09-30 讨论。用户拍板：conform 退化成"裁掉安全帧 + 整帧余量"的轻量切帧，补帧几乎用不上。

### 6.1 设计铁律（已钉进 MEMORY.md）
- **画面设计时长 = 该块对应精剪音频段总时长 + N 帧安全缓冲**（N 待定，建议 3~6 帧 ≈ 0.1~0.2s）。
- Agent 设计动画时，**有效内容（台词/关键词/动画落点）必须在音频结束前收住**，安全帧是纯静止缓冲尾。
  这样下游裁安全帧永远切定格画面，不误伤内容。

### 6.2 组接 conform 逻辑（替代旧 `warp_to_blocks` 的"拉伸音频"）
- **音频全程零处理**：音轨从 `FineCut.Clips` 原生重建（任务四），「音频优先」= 画面去迁就音频，**绝不用 asetrate 拉伸精剪音频**。
- **画面逐块 conform**：每个渲染 MP4 实测时长 vs 对应音频段时长 →
  - 画面偏长（常态）：`trim` 裁掉尾部（安全帧+整帧余量，亚帧~0.1s 级）。
  - 画面偏短（极少，安全帧设计后基本不发生）：`tpad=stop_mode=clone` 冻最后一帧补齐。
  - 目标：每个块画面时长 = 音频段时长，**画面 = 音频的从属**。
- 这正是根治上次「块接缝咔哒」的路径（旧 warp 音频在每块边界时间拉伸不连续 → 咔哒；改为 conform 画面后音频连续无痕）。

### 6.3 轻量剪辑区（组接页 UI）
- 逐块对齐表：每行「块序 / 画面时长 / 音频段时长 / Δ / 音频列锁死只读，画面列允许 ±几帧 hold/trim 微调」。
- 「对齐并组接」按钮 = 应用 conform → concat 出片。音频列永远不可编辑。
- 定位上：风险极低（平均 Δ≈14ms 亚帧级），剪辑区主要是让你肉眼确认安全帧裁得对、动画在缓冲前已收住。

### 6.4 唯一待你拍板的坑
- 若瑕疵有 **B 类（块内 184 个精剪切点本身硬切、没交叉淡入）**，conform 改不了；消 B 类得在拼接缝加几 ms crossfade
  （一次极小动音频）。届时再单独问你。当前默认只做 A 类（块接缝）根治。

### 6.5 验收
- ⑤出片：音频与精剪源**逐样本一致**（ffmpeg 比对或 RMS 一致），51 块画面逐块 = 音频段时长；
  端到端时长与手工成片差 <50ms；无块接缝咔哒。

---

## 7. 执行顺序建议
**任务一（桥接）→ 任务二（按钮自动唤醒）→ 任务三（分镜语义合并）**；任务四（废弃 build_audio.py 的 main）并入任务六实现。
**任务五（④ 应用到下游预览）** 与任务六（⑤ conform）紧耦合，放最后做（都依赖⑤组接器与④块结构已稳定）。
分镜语义合并（任务三）与桥接链相对独立，可并行推进。

---

## 8. 进度记录（2026-09-30 晚）

### 任务一 + 任务二：已完成
- 桥接程序：`工作台/工具/agent-bridge/bridge.js`（Node，无依赖）+ `package.json`。
  监听 `127.0.0.1:18765`，`POST /wake {kind, packagePath}` → 转发 `POST /api/v1/runs`
  （Bearer 鉴权，body `{id,type:"user_message",content}`，实测 `202 Accepted` + runId）。
  凭据：env（`SERVER__PORT`/`CODEBUDDY_GATEWAY_PASSWORD`）优先，否则读本机凭据文件
  `%LOCALAPPDATA%/workbuddy/.gateway_creds.json`（已写，ACL 限当前用户）。
- `AgentBridge.cs`：新增 `BridgeAvailable()` / `WakeViaBridgeAsync()` / `EnsureBridgeLaunched()`；
  文件通道写完任务包后 best-effort 调 `/wake`，唤醒失败退回"手动领任务"提示。
- `App.xaml.cs`：`OnStartup` 后 `Task.Run(() => AgentBridge.EnsureBridgeLaunched())` 自启回退。
- 编译通过（0 警告 0 错误）；`/wake` 转发实测 `202` + runId，**机制闭环**。
- **待用户一次真实点击确认**：在 GUI 点「交给 Agent」应自动唤醒我（无需说"领任务"）。

### 任务三：已完成（2026-09-30 深夜）
- **算法 `ShotSplitter.SplitBySemantic`**（搬进工作台、替代原 `make_blocks.py` 的贪心成组）：
  贪心把相邻句并成 `[minSec, maxSec]` 秒块（默认 5~9s），切点恒落句末。
  边界处理（验证脚本实测纠正过）：① 块首即超长单句（≥maxSec）→ 独自成块不切半句；
  ② 循环撞到超长单句（非块首）→ 当前块截止到上一句，超长句留下一轮独自成块；
  ③ 尾部凑不满 minSec → 把能塞进 maxSec 的全部并成一块（不当成一句一块的碎块）。
- **`ShotSplitMode` 枚举新增 `SemanticMerge = 2`**；`Split()` 签名加 `minSec/maxSec` 参数并 switch 路由。
- **分镜表 `ShotRow` 新增 `Edited` 字段**（手动调过边界的镜头标记，重切分前保真、重切分前弹确认）。
- **XAML（Step2ShotView.xaml）**：工具栏加 `RbMerge` 单选（"语义合并"）+ `GrpMerge` 块长参数
  （`TxtMinSec` 5 ~ `TxtMaxSec` 9 秒/块，改即生效）；分镜表加手动边界工具条
  `BtnMerge`（合并选中镜+下一镜）/ `BtnSplit`（按句边界拆选中镜）/ `BtnResplit`（重新自动切分，覆盖前确认）
  + `TxtEditHint` 提示；表格加"编辑"列（`Binding EditMark`）；`GridShots` 加 `SelectionChanged` 联动按钮可用态。
- **cs 逻辑（Step2ShotView.xaml.cs）**：构造函数恢复 `RbMerge`/`SyncSplitModeUi`；
  `SplitMode_Changed` 三态、`SyncSplitModeUi` 显隐 GrpPace/GrpMerge、`TxtRange_TextChanged`/`ParseRange`
  读块长；`AutoSplit` 路由到 `SemanticMerge`；手动区 `BtnMerge_Click`/`BtnSplit_Click`/`BtnResplit_Click`
  /`ReindexAndReload`/`RefreshEditButtons`/`GridShots_SelectionChanged`；`SetBusy` 内补 `_busy` 赋值 +
  `RefreshEditButtons()` 调用（忙时禁用手动按钮）；`ShotVm.EditMark` 打 ✎。
- **验证**：
  - 主工程编译 0 警告 0 错误（XAML 名称/事件绑定全解析通过，证明 UI 接线无误）。
  - 独立控制台工程喂 4 组样例（常规短句群 / 含超长单句 / 长序列+短尾 / 自定义 6~10）跑 `SplitBySemantic`，
    逐块断言"切点落句末 + 连续 + 区间/单句/尾巴块合法"，**全部通过**（含修复前的 15s 错并块、碎尾退化两个 bug）。
- **未做（留给用户）**：语义合并作为"初切首选"已就绪，但当前默认仍是 `BySentence`（=0）；
  是否把默认切法改成语义合并、或把 `LastShotSplitMode` 持久化进 state 启动恢复，待你定。
- **待用户一次真实点击确认**：在 GUI 点「语义合并」+「生成镜头切分」应出块表；合并/拆分按钮瞬成。
