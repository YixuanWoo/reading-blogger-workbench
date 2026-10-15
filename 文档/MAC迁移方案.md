# Mac 迁移方案 · 翊瑄的工作台（工作台 LiteCut）

> 本文回答：**如果将来要把这套东西搬到 macOS，要怎么搬、换什么、保留什么、先动哪里。**
> 适用版本 V1.0.0 ｜ 入口见 [`README.md`](../README.md)

---

## 第一节 · 结论先行

**推荐：Avalonia UI + 保留 LiteCut.Core 原样一行不改。**

理由一句话：`LiteCut.Core` 从写下第一行起就刻意不碰任何 Windows API ——
它存在的最大意义就是「有一天能搬到 Mac」。届时**33 个文件、几乎全部踩坑经验可以整体复用**，
工作量集中在「换一个界面壳」和「换一个音频播放器」这两件事上。

一句话分工：

```
保留（几乎不动）      → LiteCut.Core（契约/算法/ffmpeg编排/时间轴逻辑）
重写（界面壳）        → 工作台 WPF → Avalonia（同样是 XAML，技能可迁移）
替换（平台专属件）    → 音频播放解码 NAudio；坐标/路径/单实例/编码器选择
新增适配              → ffmpeg 的 Mac 常见位置、VideoToolbox 硬件编码
```

---

## 第二节 · 现状盘点：资产分类

### 🟢 可直接复用（一行不用改）

| 资产 | 为什么能直接用 |
|---|---|
| `LiteCut.Core\Contracts\` 全部 6 个文件 | 纯数据类 + `System.Text.Json`，标准库 |
| `LiteCut.Core\Engine\` 全部 15 个文件 | 纯数学/字符串处理。`PcmData` 刻意设计成跨平台载体，解码由界面层注入 |
| `LiteCut.Core\Timeline\` 全部 3 个文件 | 纯逻辑，无界面依赖（当初就是为脱离 UI 单测才放 Core 的） |
| `FFmpegAssembler.cs` / `FFmpegExporter.cs` / `FFmpegLocator.cs` / `FFmpegDecoder.cs` | ★ 全部通过 `System.Diagnostics.Process` 调 ffmpeg 命令行，这是 .NET 标准库，Mac 一样能用。**只要目标机装了 ffmpeg，这里一行都不用改** |
| `WavWriter.cs` | 手写 PCM 字节头，零系统调用 |
| `Schemas\*.json` | 内嵌资源文本 |

> 这就是为什么 `FFmpegDecoder` 当初放弃了 NAudio 选 ffmpeg ——
> NAudio 是 Windows 专用库，选了它今天就要重写解码；选 ffmpeg 则迁移免费。

### 🟡 需要小幅适配（改默认值/加分支）

| 资产 | 要改什么 |
|---|---|
| `FFmpegLocator.cs` | Mac 常见位置要加：`/opt/homebrew/bin/ffmpeg`（Apple Silicon Homebrew）、`/usr/local/bin/ffmpeg`（Intel Homebrew）、应用包内 `Contents/MacOS/ffmpeg`。现有「用户配置 → 环境变量 FFMPEG → PATH → 常见位置」的兜底顺序已经是对的，只是常见位置表要扩一项 |
| `FFmpegExporter.cs` 编码器选择 | Windows 优先 NVENC（你的 RTX 2070）。Mac 上应改为优先 **`h264_videotoolbox`**（Apple 的硬件编码），再回落 `libx264`。现有"失败自动回落软件编码"的机制直接可用 |
| `AppConfig.cs` | 路径默认值全是 `D:\...`。改成按平台返回默认值：`Environment.OSVersion` / `RuntimeInformation.IsOSPlatform`。**不要硬改死**，保留用户覆盖能力 |
| 配置持久化位置 | Windows 用 `%LOCALAPPDATA%`；Mac 应用 `~/Library/Application Support/翊瑄的工作台/` |
| `App.xaml.cs` 单实例锁 | 现有实现依赖 Windows 的跨进程通信。Mac 改成「锁文件 + PID 检测」或走 Avalonia 提供的单实例 API |
| 字体指定 | `Microsoft YaHei` 在 Mac 上没有 → 换成 `PingFang SC`（苹方）。这一条**同时影响 HyperFrames 渲染**，别忘了 CSS 里的 font-family |

### 🔴 必须重写

| 资产 | 现状 | 迁移到 |
|---|---|---|
| **WPF 界面层**（`工作台\` 全部 xaml + .cs） | Window/UserControl/XAML 绑定 | **Avalonia**（同样是 XAML + MVVM 绑定，语法高度相似，改造量远低于完全重写） |
| **`AudioEngine` 播放部分** | NAudio 输出设备 | Mac 用 `AVFoundation`/`CoreAudio`；或更简单——用 ffmpeg 管道输出 PCM 再喂播放器。<br>注意：`TimelineAudioRenderer` 的**拼流逻辑在 Core/WPF 层是可复用的**，只有"最后把 float[] 送到喇叭"这一段要换 |
| `WaveformControl.cs` / `WaveformView.cs` / `FinalWaveformBar.cs` | WPF 自绘控件 | 用 Avalonia 的绘制 API 重写（逻辑可移植，绘制调用要换） |
| `UploadServer.cs` | 手机上传服务 | HTTP 层可复用，注意 macOS 沙盒/防火墙授权提示 |

### ⚪ 依赖站 piston (__path__桥接为零(True))

| 资产 | 说明 |
|---|---|
| `工具\agent-bridge\bridge.js` | **Node.js 天然跨平台，Mac 上直接能跑**。只需把进程里的 node 绝对路径换成 Mac 上的 node 路径。它转发的是本机 HTTP，与操作系统无关 |
| CodeBuddy Gateway 唤醒机制 | 取决于宿主（WorkBuddy）是否在该机运行，与 OS 无关 |
| HyperFrames 渲染引擎 | HTML/CSS/GSAP → 无头浏览器出片。<br>⚠️ 现在用的可能是 Edge/Chrome 的 Windows 路径，要换成 Mac 的 Chrome/Chromium 路径；无头参数大概率通用 |

---

## 第三节 · 三条路线对比

| 方案 | 语言 | 工作量 | 优点 | 缺点 | 评价 |
|---|---|---|---|---|---|
| **A Avalonia（推荐）** | C# | 中 | **Core 一行不改**；XAML 技能可迁移；一套代码同时还能跑 Windows/Linux；社区活跃 | 某些 WPF 高级特性（触发器/某些第三方控件）要找替代写法 | ★ **首选**。性价比最高 |
| B SwiftUI 原生重写 | Swift | 大 | 最好的 Mac 原生体验、菜单栏/触控板手势/沙盒一套到底 | Core 33 个文件要全部用 Swift 重写一遍 —— 那些踩过的坑会重新踩一遍；两套代码库要双份维护 | 只在你决心彻底 Mac 化、放弃 Windows 时考虑 |
| C Electron/Tauri + Core 当后台服务 | TS/Rust + Core | 中偏大 | 界面可以用 Web 技术快速写；如果未来想做 Web 版可顺带兼顾 | 多一层进程通信；音视频本地处理走 IPC 反而绕；Tauri 体积OK但 Rust 要新学 | 性价比较 Avalonia 低 |

**如果一定要推荐一个备选**：若将来想同时支持 Windows 和 Mac 且希望界面现代化，Avalonia 是唯一同时满足「保留 Core」+「一套代码双平台」的方案。

---

## 第四节 · 逐模块迁移方案

### 4.1 Core（唯一 design 目标：原样搬过去）

**做法：什么都不做**，把 `LiteCut.Core.csproj` 的目标框架保持 `net8.0`（不带 `-windows` 后缀，现在就是），
新方案直接引用它。

⚠️ 迁移前先跑一遍体检，确认没有潜伏的 Windows 依赖：

```bash
# 检查 Core 里有没有偷跑 Windows 专属引用
grep -rn "System.Windows\|Microsoft.Win32\|Registry\|NAudio\|\\\\D:\\\\" LiteCut.Core/
# 理想结果：0 命中（如果命中了，说明有人在 Core 里违背了架构铁律，先改造再迁移）
```

### 4.2 界面层：WPF → Avalonia

改造要点：

| WPF 写法 | Avalonia 对应 | 备注 |
|---|---|---|
| `System.Windows.Controls.*` | `Avalonia.Controls.*` | 命名空间换，名字大多相同 |
| `Window` / `UserControl` | 同名存在 | `EditorView` 从 Window 改 UserControl 的那套«惨テク»经验可以直接迁移 |
| `{Binding}` / `DataContext` | 一样 | MVVM 不用改 |
| `Dispatcher.CheckAccess()` | `Dispatcher.UIThread.CheckAccess()` | **★ 容易漏**：现有代码里凡是 `await` 后碰控件都套了这个守卫，迁移时要逐个改成新 API |
| FinderName / 逻辑树查找 | `LogicalTreeHelper` 仍在 | 无头验证用的查找方式要重验 |
| 自绘控件 `OnRender` + DrawingContext | Avalonia 的 DrawingContext API 不同 | `WaveformControl` 等要重写绘制部分（格栅/home สร้าง的逻辑可复用） |

**迁移顺序建议**：先做一个"能编译、能开空窗口"的 Avalonia 壳 → 逐个搬靖页面（先 Dashboard/ProjectList，最后搬 Step3RenderView 这种最大的） → 最后搬自绘波形控件。

### 4.3 音频：解码与播放

现在的设计已经把这件事隔离得很好了 —— Core 只认 `float[]`，解码由界面层通过 `IAudioDecoder` 喂进来。

| 环节 | Windows 现在 | Mac 方案 |
|---|---|---|
| 解码为 PCM（`FFmpegDecoder`） | ffmpeg 管道 | **ffmpeg 管道，不用改** |
| 试听播放（`AudioEngine`） | NAudio → WaveOut | Mac: CoreAudio（`AudioUnit`）或 `AVAudioPlayer`；简单做法：ffmpeg 输出 PCM 到 stdout 管道 → 内存队列 → 播放器消费 |
| 导出 | ffmpeg | 不用改 |

★ 建议：把"最后把样本送到喇叭"抽成一个新的平台接口（类似 `IAudioOutput`），
这样将来换第三个平台（Linux）也只换这个文件。

### 4.4 ffmpeg 与硬件编码

```csharp
// 现状（Windows）
videoCodec = opt.VideoCodec ?? (IsNvencAvailable(ffmpeg) ? "h264_nvenc" : "libx264");

// Mac 方向：加平台分支
//   macOS  → 先试 "h264_videotoolbox"（Apple 硬件编码，Mac 上人人可用）
//   Windows → 先试 "h264_nvenc"
//   兜底   → "libx264"
```

现有技术公告 DFT "NVENC 失败自动回落"的机制是对的 —— 只要把候选列表做成按平台拼出来的即可。

### 4.5 路径与 IO

| 项目 | Windows | macOS |
|---|---|---|
| 路径分隔符 | `\` | `/` —— **凡手拼字符串拼路径的地方都要换成 `Path.Combine`** |
| 目录联接 Junction | 历史上用过，**V1.0.1 起已全部废除**，现状无任何联接 | 无此问题。维持"配置里只写真实路径"的现行原则即可 |
| 临时目录 | `Path.GetTempPath()` | 同名 API，Mac 上正常可用 |
| 用户配置目录 | `%LOCALAPPDATA%` | `~/Library/Application Support/` |
| 盘符 `D:\` | 有 | **没有** —— 所有默认值必须去掉盘符（这也是 §2 里说的 AppConfig 要改的地方） |

⚠️ **历史教训**：当初 C 盘被占满导致渲染崩溃，才把所有东西迁到 D 盘；中间过渡期用过目录联接，V1.0.1 已全部清理。
Mac 上没有盘符概念，反而是好事 —— 但**配置里那些 `D:\...` 旧值必须全部改掉**，否则在 Mac 上会变成"路径不存在 → 直接崩"。

### 4.6 Agent 桥接（这一块几乎零成本）

`工具\agent-bridge\bridge.js` 是 **Node 零依赖 HTTP 服务**，与操作系统无关。迁移只需：

1. 装 Node.js（或用现成的 `~/.workbuddy/binaries/node`，如果那里也有 Mac 版）；
2. 把 `AgentBridge.EnsureBridgeLaunched()` 里写死的 **node.exe Windows 绝对路径** 改成 Mac 的 node 路径（建议改成"先找 PATH 里的 node，找不到再回退"）；
3. 端口 18765 不变，`/health`、`/wake` 协议不变。

CodeBuddy Gateway 的凭据 env（`SERVER__PORT` / `CODEBUDDY_GATEWAY_PASSWORD`）是由宿主注入的，与 OS 无关。
凭据回退文件位置 Windows 用 `%LOCALAPPDATA%\workbuddy\`，Mac 换成对应目录即可。

---

## 第五节 · 差异速查表（迁移 checklist 用）

| # | 检查项 | 状态 |
|---|---|---|
| 1 | Core 里无 `System.Windows` / `NAudio` / 注册表引用 | ☐ |
| 2 | 所有路径用 `Path.Combine`，无手拼 `\` / `/` | ☐ |
| 3 | 无写死的 `D:\` / `C:\` 默认值（改为按平台返回） | ☐ |
| 4 | ffmpeg 搜索表加入 Mac 常见位置 | ☐ |
| 5 | 编码器改为 VideoToolbox → libx264 回落 | ☐ |
| 6 | `Dispatcher.CheckAccess()` → `Dispatcher.UIThread.CheckAccess()` | ☐ |
| 7 | 单实例锁改为跨平台实现 | ☐ |
| 8 | 字体 `Microsoft YaHei` → `PingFang SC`（含 HyperFrames 的 CSS） | ☐ |
| 9 | 配置目录改 `~/Library/Application Support/` | ☐ |
| 10 | node 路径查找改为跨平台 | ☐ |
| 11 | HyperFrames 无头浏览器路径改为 Mac Chrome | ☐ |
| 12 | 音频输出换成 CoreAudio，抽一层 `IAudioOutput` | ☐ |
| 13 | 发布目标改为 `osx-arm64` / `osx-x64` | ☐ |
| 14 | 跑一遍完整流水线：录音 → 成片（这是唯一真正的验收标准） | ☐ |

---

## 第六节 · 风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| **Safari/WebKit 与 Chromium 渲染差异导致 HyperFrames 成片画面不一致** | 高——这是画面制作的核心 | 迁移第一步就先验证：拿现有项目的某个块 HTML 在 Mac Chrome 上渲一遍，逐像素对比。**这个问题不解决，后面全白搭** |
| 中文字体替换导致排版位移（苹方与雅黑字宽不同） | 中——卡片式布局可能溢出 | 渲染前对 51 个块做一次全量视觉抽查 |
| Avalonia 缺少某些 WPF 高级 UI 特性 | 中 | 提前列出现有 XAML 用到的所有 WPF 专属特性（触发器、`FindName`写法、`IsChecked`事件时机等），逐个确认 Avalonia 对应方案 |
| ⚠️ **XAML 里写死 `IsChecked` 初值导致事件早触发炸整页**（现有代码踩过） | 中 | Avalonia 事件时机不同，**极可能复现**。现有代码已用 `UiReady` 守卫解决，迁移时保留这个写法 |
| Apple Silicon arm64 下第三方 native 库缺失 | 低 | 优先选纯托管实现；ffmpeg 装 arm64 版本 |
| macOS 沙盒/公证（Notarization）要求 | 中——分发时需要 | 自用时可绕过（右键打开）；要分发给他人就必须走开发者账号签名 |
| 旧迁移期遗留的路径假设（盘符、目录联接） | 低 | V1.0.1 起已全部清除，配置里只写真实路径 |

---

## 第七节 · 分阶段路线图

| 阶段 | 目标 | 验收标准 |
|---|---|---|
| **P0 可行性验证** | 一台 Mac 上跑通「渲染 → 组接」这一小段 | 拿现有项目的块 HTML + 已有 MP4，在 Mac 上渲一块并与 Win 产物逐像素比；`FFmpegAssembler` 能在 Mac 上拼出成片。**这一步不过就别往下走** |
| **P1 Core 搬家** | 新建 Avalonia 壳，引用原封不动的 LiteCut.Core | Core 在 Mac 上编译通过；跑一遍无转写稿的粗剪，产出的 `cutplan.json` 与 Windows **完全一致** |
| **P2 界面搬移** | 逐个搬七个步骤页 + 自绘控件 | 七步页面全部可用；波形显示正常 |
| **P3 平台件替换** | 音频播放、编码器、路径、配置、单实例 | 完整流水线：录音 → 转写 → 粗剪 → 精剪 → 分镜 → 渲染 → 成片，跑通一整支片子 |
| **P4 打磨** | 字体、LMac 交互习惯、打包签名 | 可以当日常主力用 |

---

## 第八节 · 给未来执行者的一句话

> 这套代码在 Windows 上踩过的每一个坑，注释里都写了「上次为什么错」。
> 迁移时**不要图快重写**——那些看起来啰嗦的判断（看 dB 均值不看峰值、
>  merge 时保留信息量更大的 reason、口播稿必须按成片先后排序…）
> 每一行都是用一次事故换来的。**搬家 ≠ 重写，搬家是把它们完整地抬过去。**

迁移一旦动手，请同步维护：
- 本文（记录实际遇到的新差异）
- [`维护说明.md`](维护说明.md)（逐文件说明要更新平台信息）
- [`更新日志.md`](更新日志.md)（每次 Mac 相关改动都要留痕）
