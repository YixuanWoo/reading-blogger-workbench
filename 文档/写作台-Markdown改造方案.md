# 写作台 Markdown 化改造方案

> 方案稿 · 2026-10-04 · **状态：已拍板，实施中**
> 基线：**V1.10.15 → 目标 V1.11.0**
> 相关旧档：`写作台-开发方案.md`、`写作台-UI原型.html`（描述已删除的"回车块"设计，已移入 `文档\历史文档\`）
> 上游思路：`远景规划-写作台与文稿驱动.md`（本文是对其中"写作台"环节的一次方向性修正）

---

## 0. 一句话结论

把写作台从「**回车分块 + 段落录音脚手架**」改成「**真正的 Markdown 写作**」：

- **回车只是换行**，不再切分段落节点
- **Markdown 实时识别并渲染**（标题/列表/引用/代码/粗斜体/链接…）
- **左栏导航栏不再按回车分栏**，而是**按标题大小自动生成文章目录（TOC）**，真正的中文标题结构
- 录音相关的整套（永久 ID、audio/takes、待重录、script.json）**全删**
- 写作台整体收敛进独立文件夹 `Workbench\WritingDesk\`，内部不依赖"项目"，**将来可整夹迁移出去做成独立写作软件**

---

## 1. 为什么改

原设计（V1.10.x）把"回车"当成结构切分 —— 一个回车 = 一个段落节点 `ScriptBlock`，左栏大纲显示的是"块列表"（s01 + 每块字数/时长），配套了一套为分段录音服务的状态（永久 ID、audio、待重录、script.json）。

这套设计有两个根本问题：

1. **它不识别 Markdown。** 稿子本质是文档，用户在 Obsidian / 平时写作里就是 Markdown；写作台却只能当纯文本切块，标题/列表/层级这些文档结构完全丢失。
2. **把结构押在"回车"上，跟文档的真实结构（标题层级）脱节。** 一个回车在文档里只是换行，不该承担"分段"的语义。

**结论**：写作台要成为标准的 Markdown 写作工具 —— 存盘是 md、能渲染、导航跟着标题结构走。

---

## 2. 核心转变

| | V1.10.x（旧） | V1.11.0（新） |
|---|---|---|
| 文档本质 | 回车切出的段落块列表 `ScriptBlock[]` | **一整篇 Markdown 原文**（一个字符串） |
| 左栏 | 回车块列表（s01 + 字数/时长） | **文章目录 TOC**：按 `#`~`######` 标题自动生成，按层级缩进 |
| 回车 | 结构切分（=新段落节点） | **只是换行**，不产生任何结构 |
| Markdown | 完全不识别 | **实时解析渲染**（编辑/分屏预览） |
| 录音/重录 | 挂在段落块上（sID/audio/待重录） | **整块移除** |
| 数据文件 | script.md + script.json（块状态） | **仅 script.md** + view-state.json（本机 UI 状态） |

---

## 3. 模块隔离：为将来独立成写作软件

新建独立文件夹 `Workbench\WritingDesk\`。**内部代码禁止引用** `Project` / `WorkbenchWindow` / `WorkbenchState` / `NavTree` —— 它只认「文件路径 / 文本内容」，不认"项目"。

```
Workbench\WritingDesk\
├─ Markdown\
│  ├─ MarkdownOutline.cs      // markdown → 目录树（标题层级/顺序/锚点）
│  └─ MarkdownRenderer.cs     // markdown → WPF FlowDocument（渲染预览，套深色主题）
├─ WritingDocument.cs         // 文档模型：原文 + 目录 + 字数/句数/行数等派生统计
├─ WritingDocumentStore.cs    // 按「路径」原子读写 .md（无 BOM）；不依赖 ProjectRef
├─ IWritingDeskHost.cs        // 外壳抽象：状态提示/未保存确认（默认空实现，便于无头测试）
└─ WritingDeskView.xaml(.cs)  // 编辑界面：左TOC + 原文/预览 + 底栏统计
```

工作台侧只保留**极薄的适配**：

- 导航节点把 `项目\script.md` 的**路径**喂给它
- 保存管线通过它暴露的**接口**（`Save()` / `HasUnsavedChanges`）调用，不依赖具体类

将来独立成软件：换一个路径来源，整个 `WritingDesk\` 文件夹直接拷走。

---

## 4. 页面新形态

### 4.1 布局

```
┌────────────┬───────────────────────────────┬─────────────┐
│ 文章目录TOC │  原文编辑（主区，占满）          │ 渲染预览     │
│ 240px      │                               │（点右上角    │
│ · # 一级     │   在这里打 Markdown            │  「展开预览」 │
│    · ## 二级 │   回车只是换行                 │   才滑出）    │
│       · ### │                               │             │
│ 点标题跳转   │                               │             │
└────────────┴───────────────────────────────┴─────────────┘
            底栏：字数 / 句数 / 行数 / 标题数 / 上次保存 / 未保存标记
```

### 4.2 关键交互

- **默认只显示原文编辑区**，不默认分屏。
- **右上角「展开预览」按钮**：点开才在右侧滑出渲染预览面板；再点收起。**按需打开，不默认。**
- **左栏 TOC**：解析全文标题，显示「层级缩进 + 标题文字」，点击跳到对应位置（预览滚动 / 原文定位光标）。
- **顶栏**：保留「打开 script.md」、模式切换（若做）、保存仍走唯一入口（顶栏「保存项目」/ Ctrl+S / 20 秒自动保存，规则不变）。

### 4.3 Markdown 支持范围（手写解析，覆盖常用子集）

标题（#~######，含 Setext `===`/`---`）、段落、粗体/斜体/删除线、行内代码、代码块（围栏 ```）、无序/有序列表（含嵌套）、引用、分隔线、链接、转义、HTML 行级标签（原样或忽略，不做完整 HTML）。

表格、图片、数学公式：**本期不做**（或原样显示为文本），留到独立软件期按需补。

---

## 5. 依赖

**不引入任何新 NuGet。** 现有依赖只有 NAudio（主工程）+ LiteCut.Core。Markdown 解析与渲染**手写**，实现藏在 `WritingDesk\Markdown\` 子夹，其余代码只依赖我们自己的抽象 —— 将来想换库或重写只动这一处。

---

## 6. 逐文件改动清单

### 6.1 新增

| 文件 | 职责 |
|---|---|
| `Workbench\WritingDesk\Markdown\MarkdownOutline.cs` | 解析 markdown → 目录树（标题层级/顺序/锚点） |
| `Workbench\WritingDesk\Markdown\MarkdownRenderer.cs` | 解析 markdown → WPF FlowDocument（深色主题渲染预览） |
| `Workbench\WritingDesk\WritingDocument.cs` | 文档模型：原文 + 目录 + 统计（字数/句数/行数/标题数） |
| `Workbench\WritingDesk\WritingDocumentStore.cs` | 按「路径」原子读写 .md（无 BOM） |
| `Workbench\WritingDesk\IWritingDeskHost.cs` | 外壳抽象：状态提示/未保存确认（默认空实现） |
| `Workbench\WritingDesk\WritingDeskView.xaml(.cs)` | 编辑界面：左TOC + 原文 + 按需预览 + 底栏 |

### 6.2 删除（不留尾巴）

| 文件 | 说明 |
|---|---|
| `Workbench\Script\ScriptModels.cs` | 整文件删除。`ScriptBlock` / 永久 ID（NextId/ParseSeq）/ audio / DurSec / TakeCount / NeedsRerecord / SentenceMarks / EstSec / DurationText 全部随块模型消亡 |
| `Workbench\Script\ScriptStore.cs` | 整文件删除。`Parse`（回车切）/ `ParseMarkdown`（空行切块）/ `BuildMd` / `EnsureIds` / 块操作（Append/Insert/Split/Merge/RemoveAt/Move/MarkTextChanged）/ PendingRerecordCount / TotalSec 全部删除 |
| `Workbench\Views\ScriptView.xaml(.cs)` | 删除（被 `WritingDeskView` 取代）。删：段落卡片流（ListBlocks/CardRow）、块大纲（ListOutline 显示 sID）、全部拆合移动按钮及处理、回车分段 TextChanged、灰着的导出转写稿/应用到下游、待重录 UI |
| `Workbench\Script\` 整夹 | 删除（ScriptModels + ScriptStore 都在此） |

> 保留：`CountChars` / `CountSentences` 这类纯统计逻辑，搬到 `WritingDocument` 里继续用。

### 6.3 改写

| 文件 | 改什么 |
|---|---|
| `Navigation\NavTree.cs` | 写作台节点 `CreateView` → `WritingDeskView`；落地分流（有 script.md 落写作台）逻辑保留；**应用级左导航（文稿/成片/资源分组）不动** |
| `WorkbenchWindow.xaml.cs` | `SaveScriptIfCurrent` 与关窗拦截改为认 WritingDesk 暴露的**接口**（`Save()` / `HasUnsavedChanges`），不硬编码具体类；自动保存管线逻辑不变 |
| `Project\ProjectStore.cs` 的 `ProjectRef` | 删 `ScriptAudioDir` / `ScriptTakesDir`（录音未做的死路径）；保留 `ScriptDir`（放本机视图状态 view-state.json） |
| `ProjectModel.cs` | **无需改动**（块状态原本在 script.json、不在 project.json，零存档迁移） |

### 6.4 数据文件

| 文件 | 处理 |
|---|---|
| `script.md` | **唯一真相**，位置不变（项目根），原子写 + 无 BOM |
| `script.json`（旧块状态） | **停止读写、删除**。sID/录音/待重录已无意义 |
| `stages\00-script\view-state.json`（新增） | 仅存本机 UI 状态（模式、滚动/光标位置），明确"本机、不随文档迁移" |

---

## 7. 验证（按既有规范）

1. `dotnet build` 0 警告 0 错误。
2. 无头断言：标题目录提取（层级/顺序/嵌套）、md 往返读写（原子、无 BOM）、字数统计、Markdown 渲染关键语法（标题/列表/粗斜体/代码块/引用/链接）。
3. 实机：输入含标题的 Markdown → 目录自动生成、点「展开预览」实时渲染；点目录跳转；Ctrl+S → script.md 正确无 BOM；重开恢复；切走/关窗未保存拦截仍在。

---

## 8. 文档维护（按规范）

- `文档\更新日志.md`：新增 **V1.11.0** 条目（架构级变更）。
- `文档\维护说明.md`：第二节（第0步描述）、第三节代码结构、第五节逐文件（Script→WritingDesk）、第七节落盘（script.md + view-state.json，去掉 script.json/audio/takes）。
- 重写 `文档\写作台-使用说明.md`。
- 本文存为方案定稿。
- 旧的 `写作台-开发方案.md/.html`、`写作台-UI原型.html`（描述已删除的"回车块"设计）移入 `文档\历史文档\`，不在当前文档区留误导（保留历史、不删档）。

---

## 9. 风险与约定

| 风险 | 缓解 |
|---|---|
| 手写 Markdown 解析有规范边角缺口 | 覆盖常用子集即可（§4.3），边角语法原样显示为文本；解析与渲染都收在 `Markdown\` 一处，后续可换库 |
| 写作台做成孤岛 | 强制与项目只通过「路径 + 接口」相连，页面层不碰项目内部状态 |
| 与 Obsidian 改同一份 md | 单向：打开读一次 / 保存写一次（沿用旧规矩） |
| 老 script.json 遗留 | 不再读写；磁盘旧文件手动清，不影响新流程 |

**照抄工程既有约定：**

- 写盘一律 `ContractJson.Utf8NoBom`（R-13）
- 保存只走唯一入口（顶栏「保存项目」/ Ctrl+S / 20 秒自动保存）
- 验证：`dotnet build` 0 警告 0 错误 → 追加更新日志 → 行为约定变了同步维护说明
