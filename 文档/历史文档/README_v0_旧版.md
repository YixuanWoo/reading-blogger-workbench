# 工作台 LiteCut・使用与维护手册

> 一个超级轻量的 Windows 音频剪辑软件，专为口播素材的 "审阅微调" 设计。
> 打开 m4a → 拖到时间轴 → 用剪刀 / 拖边缘裁切 → 导出 48kHz m4a（对齐达芬奇工程）。
> 本文档同时是
> **C# 入门教材 + 维护手册**
> ，从零带你看懂每一行代码。
> **版本说明**
>
> ：本手册已同步到当前代码（块轨道模型 v2）。
> 早期波形编辑模型（
>
> `Models.cs`
>
>  / 
>
> `EditService.cs`
>
>  / 
>
> `AudioPeaks.cs`
>
>  / 
>
> `LibraryWindow`
>
> ）
> 仍保留在工程里但已不在主流程，详见 5.9 节。



***

## 零、2026-09-29 改版说明（先读这一节）

从这一天起，程序从「**四个固定页面**」改成了「**项目制**」。零到九节里凡是提到
`MainWindow`、五步流程条、全局素材库的段落，讲的都是旧结构 —— 读的时候以本节为准。

### 0.1 现在长什么样

程序打开后**先看到的是项目，不是剪辑页**。

| 层级 | 菜单 | 什么时候出现 |
| --- | --- | --- |
| 全局 | **灵感 / 进行中 / 已归档** | 没打开项目时 |
| 项目内 | **音频粗剪 / 精剪 / 分镜设计 / 渲染 / 组接导出 / 素材库** | 打开某个项目之后 |

* **没有全局素材库了** —— 素材库是每个项目自己的（项目文件夹下的 `media\`）。
  这样做到第 10 期时，素材库里永远是"这一期的几段录音"，不会横着十期的文件。
* **没有"模板"和"发布"页** —— 用户明确不要。
* 打开项目后，左侧全局菜单**收起**，换成工作流菜单；顶栏显示
  `项目名 › 当前页` 和保存状态，左上角有「← 全部项目」回来。

### 0.2 目录结构（项目在磁盘上）

```
<项目根目录>\<项目名>\
    project.json    全部状态（源素材、粗剪决策、精剪时间轴、分镜、导出记录）
    media\          这个项目自己的素材（导入时复制进来，不是引用外部路径）
    render\         渲染输出
    export\         成片
```

项目根目录默认是 `D:\视频创作\项目`（D 盘不在就退到"我的文档"），
在项目列表页右上角「更改位置」可以改。

* 「归档」只是换个状态，文件一点都不动。
* 「移除」是把整个文件夹挪进 `_已删除`，**不真删** —— 剪到一半的东西不能一键没了。

### 0.3 保存机制

| 什么时候存 | 说明 |
| --- | --- |
| 自动保存 | 每 20 秒检查一次，**有改动才写盘** |
| 顶栏「保存」/ Ctrl+S | 手动存，即使没改动也写一次（让人安心） |
| 切项目 / 回项目列表 / 切页 | 都会先把当前页的编辑接回状态再存 |
| 关窗口 | 有未保存的改动会问一句；**存失败就不退出**（不让一次写盘失败吃掉一晚上的活） |

两个设计决定值得记住，将来改的时候别拆：

* **"有没有改动"是自动算出来的**，不靠手动打标记。
  做法是把内存状态投影成一份存档、序列化成 JSON、取 FNV-1a 哈希（`ProjectSession.Fingerprint()`），
  和上次存盘的哈希一比就知道。手动打标记只要漏一处，就会出现
  "看着存过了其实没存" —— 这种 bug 最难查。
* **写盘先写 `.tmp` 再 `File.Replace` 原子替换**。写到一半断电/崩溃，原来那份 `project.json` 还是完好的。

### 0.4 本次一起修掉的问题

| # | 问题 | 修法 |
| --- | --- | --- |
| 1 | 精剪界面**开头留空白就播不出声** | `AudioEngine.ComputeStartSample`：播放线停在第一块前面的空地时起点算成负数、取样本越界。改为自动从第一块开头起播（和导出一致） |
| 2 | 拖块**撞到邻居不停住**、多选**只能拖动鼠标下那一块** | 抽出 `LiteCut.Core/Timeline/TimelineCollision.cs`（纯逻辑，可独立测试），`WaveformControl` 按**整组**做碰撞求解：顶住 / 跨过空隙 / 塞不下三种状态；塞不下时整组变警告红、松手自动弹回原位 |
| 3 | 拖块**顶住时看不出来到头了** | 顶住那一侧的边缘画一层**渐变红**（像达芬奇），被顶住的块边缘还有 2px 实边 |
| 4 | 裁切**会覆盖邻居** | 左右裁切都走 `TimelineCollision.ResolveTrimLeft/Right`，邻居边界和源素材边界双重钳制 |
| 5 | **剪到一半没法存** | 见 0.3 |
| 6 | **点缝左侧选中的是右边的块**（"实际点击的位置偏右"）、**一按住裁切边块就自己延长顶到邻居**（2026-09-29 下午补修，见 0.10） | `HitTest` 改两遍扫描：点在谁身上，边缘只认谁自己的；`TrimTo` 的右边缘"时间轴→源素材"映射基准修正，边缘严格跟鼠标 |

### 0.5 新增的文件地图

界面外壳、菜单、项目持久化都在 `Workbench\` 下：

| 文件 | 职责 |
| --- | --- |
| `Workbench\WorkbenchWindow.xaml(.cs)` | 外壳：画左导航、换页面、开/关项目、保存 |
| `Workbench\Navigation\NavNode.cs` | 一个菜单项的数据模型 + 页面生命周期接口（`INavPage` / `ISaveSync`） |
| `Workbench\Navigation\NavTree.cs` | ★ **菜单结构的唯一真源** —— 想改菜单只改这个文件 |
| `Workbench\Navigation\NavHost.cs` | 把菜单项铺成左侧按钮、维护"当前选中"（只管渲染） |
| `Workbench\Project\ProjectModel.cs` | 存档结构（`project.json` 长什么样）+ 版本号 |
| `Workbench\Project\ProjectStore.cs` | 项目文件夹的增删查改（**唯一碰磁盘的地方**） |
| `Workbench\Project\ProjectSession.cs` | 当前打开的项目 + 内存状态 ↔ 存档的搬运 + 脏标记 |
| `Workbench\Project\InspirationStore.cs` | 灵感池（跟项目无关，存在 `%APPDATA%\翊瑄的工作台\`） |
| `Workbench\Views\ProjectListView.xaml(.cs)` | 项目卡片墙（一页两用：进行中 / 已归档） |
| `Workbench\Views\InspirationView.xaml(.cs)` | 灵感池：记一条 → 一键立项 |
| `Workbench\Views\ProjectLibraryView.xaml(.cs)` | **项目自己的**素材库 |
| `Workbench\Ui\TextPrompt.cs` | 代码搭的深色输入框（WPF 没有内置 InputBox） |
| `LiteCut.Core\Timeline\TimelineCollision.cs` | 时间轴碰撞求解（纯逻辑，不依赖界面，可跨平台） |

### 0.6 想改菜单，怎么改

**只动 `Workbench\Navigation\NavTree.cs` 一个文件。**

```csharp
// 加一项（页面类写好之后挂上去，其它文件一行都不用改）
new NavNode {
    Id = "step.xxx",            // 唯一标识，改它会让"上次停在哪"失效
    Title = "新页面",
    Icon = "6",
    CreateView = () => new XxxView(),
    KeepAlive = false,          // true = 切走再切回还是同一个实例（撤销历史保得住）
}
```

* **换顺序**：调列表顺序。
* **删一项**：从列表里去掉。程序找不到那一页只会显示一句占位说明，不会崩。
* **换默认落点**（打开程序 / 打开项目后先看哪一页）：改 `NavTree.DefaultGlobalId` /
  `DefaultProjectId`。刻意写成显式常量而不是"取列表第一项" —— 菜单顺序将来会按手感调，
  但"打开该落在哪"是产品决策，不该被顺序改动带跑。
* **加一个全新作用域**（比如将来真有"写作台"）：在 `NavTree` 里再加一个列表 +
  `ForScope` 里加分支，然后在 `WorkbenchWindow` 里决定什么时候用它。

拆成四层就是为了这件事：**NavNode 管"菜单项是什么"、NavTree 管"有哪些"、
NavHost 管"怎么画"、WorkbenchWindow 管"什么时候用哪一份"**。
所以换显示风格（横排 / 图标栏 / 折叠树）不用动业务代码。

### 0.7 加一个新页面

1. `Workbench\Views\` 下建 `XxxView.xaml` + `.xaml.cs`，继承 `UserControl`。
2. 需要"每次进来刷新"就实现 `INavPage`（`OnEnter` 刷数据、`OnLeave` 把编辑交回共享状态）；
   有"还没落地的编辑"就再实现 `ISaveSync`（点保存 / 自动保存前会被叫一次）。
3. 在 `NavTree.cs` 的对应列表里加一个 `NavNode`，`Id` 起个不重名的。
4. 完事。外壳、导航栏、保存流程都不需要改。

### 0.8 启动速度 & 单实例（2026-09-29 下午补的）

**启动只落主界面，绝不自动打开上次的项目。**

上一版是"上次在哪个项目里退出，这次就接着进那个项目"，看着贴心，实际是启动慢的元凶：

* 打开项目要把几百个素材块全铺回时间轴（读源文件、建块、算布局），
  还会连带创建精剪编辑器 —— 音频引擎、本地上传服务一起起来；
* 这段活是在**窗口显示之前**干的，所以用户看到的是"双击了没反应"；
* 他自然以为没点上，于是反复双击 → 开出好几个实例互相抢磁盘 → 越等越慢。

现在 `WorkbenchWindow.StartUp()` 一律走 `EnterGlobalScope()`，**加载推迟到用户真的点进某个项目那一刻**
—— 因果明确，等待可预期。上次在剪哪个项目仍然记着（`config.json` 的 `LastProjectDir`），
但只用在状态栏提一句，绝不去加载它。

**单实例锁**（`App.xaml.cs`）。两个理由：

1. 配置和项目存档都是"整份读、整份写"。两个实例各开一个 20 秒自动保存定时器，
   后写的会把先写的**整个盖掉** —— 等于悄悄丢活；
2. 每个实例都占一份音频引擎和上百 MB 内存，用户以为卡住时反复双击会叠出一堆实例。

第二个实例会尝试把已有窗口叫到前台，然后安静退出。为了让它**不创建窗口**（否则会闪一下），
`App.xaml` 里刻意删掉了 `StartupUri`，改成在 `App.OnStartup` 里手动 `new WorkbenchWindow()` ——
`StartupUri` 建的窗口由 WPF 框架在 `OnStartup` 返回之后才创建，代码里拦不住。

**想看启动到底花了多久**，日志里已经打点：

```
发布\工作台\logs\litecut_YYYYMMDD.log
========== 程序启动 2026-09-29 11:32:56 ==========
[11:32:56.663] [INFO] 启动就绪：外壳 124 ms，从进程启动算 421 ms
```

`外壳 124 ms` = 从窗口构造到主界面铺好；`从进程启动算 421 ms` = 再加上 .NET 运行时和 WPF 初始化，
**后面这个数才是用户感知的**。哪天觉得启动慢了，先看这一行。

**单文件 exe 的解压缓存**：`%LOCALAPPDATA%\Temp\.net\LiteCut\<hash>\`。
每次 publish 出来的 exe 内容不同 → hash 不同 → 会**新解压一份**（每份约 8MB），旧的不会自动清。
攒久了白占地方（曾经攒到 12 份 / 95MB），定期整个删掉即可，下次启动会自动重建（只慢那一次）：

```powershell
Remove-Item "$env:LOCALAPPDATA\Temp\.net\LiteCut" -Recurse -Force
```

### 0.9 精剪按空格"跟手"（2026-09-29 傍晚补的）

**症状**：精剪页面按空格，总要先等一秒才出声。

**先量，再改。** 用一个一次性探针工程（现在留在 `工具\音频探针\`）对着真实项目跑：

```
219 块 / 427 秒 / 20,501,520 个样本
旧实现：每次按空格 2459 ~ 2717 ms   ← 不是 1 秒，是 2.5 秒
```

这 2.5 秒花在哪（都是实测，不是猜）：

| 环节 | 耗时 | 说明 |
| --- | --- | --- |
| 纯解码 427 秒音频 | 1105 ~ 1225 ms | 硬成本，砍不掉 |
| **打开同一个源文件 219 次** | **791 ms** | 每块都 `new AudioFileReader(路径)` |
| 往 `List<float>` 里逐个 `Add` 填 2050 万个元素 | 50 ms | |
| 重采样 + 混音 | 23 ~ 45 ms | 大头不在我们自己代码里 |

**根因不是电脑慢，是"每次按播放都现场把整条音频重拼一遍"**。

改法四层，一层比一层收益大：

| 层 | 做法 | 效果 |
| --- | --- | --- |
| ① 拼法 | 同一个源文件**只开一次**解码器；相邻段用"顺读"接上不倒回；`float[]` 下标写不再逐个 `Add` | 2.5 s → 1.55 s |
| ② 缓存 | 拼好的整条按**内容指纹**缓存（`AudioRenderPlan.Fingerprint`，FNV-1a）。时间轴没动 → 直接复用 | 第二次按空格 **0 ms** |
| ③ 预热 | 时间轴一改，**后台线程**就把新的拼好（400ms 防抖）。等你手指按到空格，缓存通常已经热了 | 感知 ≈ 0 |
| ④ 增量重拼 | 只重算"改动过的那几块"，其余整片内存拷贝 | 改一块 **16 ms**（整条是 1541 ms） |

手感上还有两处：

* **播放设备复用**：设备还开着、而且喂的**就是同一份样本数组**时，只把读指针挪过去再开播
  （暂停后接着播、从头再播都走这条）。**判据必须是"同一份数组"，不能放宽成"设备还开着就行"** ——
  理由见下面那段。
* **设备缓冲 300ms → 100ms**（`DesiredLatency` / `NumberOfBuffers`）。数据全在内存里，压到 100ms 不会爆音，
  但从"按下"到"出声"快了一倍多。

#### 一个量过、但**没做**的优化（免得将来有人重走一遍）

编辑过时间轴之后，拼出来的是一份**新数组**，而设备缓冲里还压着最多 100ms 的**旧声音**。
要换数据就得先把设备缓冲冲掉（`WaveOutEvent.Stop()`）。于是很自然的想法是：

```
Stop() → 换 _provider.Samples 引用 → 挪 Position → Play()
```

实测这样确实省：**「拆了重建」34 ms/次，「留着设备重开」0 ms/次**。

**但这条路没走。** 因为 `Stop()` 会引发 `PlaybackStopped`，而它**不保证同步** ——
连跑 6 次实测：`异步 / 异步 / 同步 / 异步 / 异步 / 同步`（**4/6 次是在 `Stop()` 返回之后才发**）。
所以"在 `Stop()` 前后临时摘掉事件处理器、之后装回去"这种做法有竞态：晚到的那一次会漏进来，
被当成"播完了"→ 红线冻住、播放键弹回，而声音其实还在响。

这 34ms 不值得拿一个偶发 bug 去换。**换数据就老老实实重建设备** —— 代价明确、行为确定。
真要把这几十毫秒也省掉，正确的路子是"**后台预先开好一个空闲设备**"（把开设备的代价挪出关键路径），
而不是跟异步时序赌。探针里已经把这个时序数据固化下来了。

#### 三个反直觉的技术点（将来改这块务必先看）

**1）增量重拼"一个字节都不读源文件"。**

最直觉的做法是"只把改动的那一块从源文件里重新读一遍"。**实测走不通**：NAudio 在 m4a 上
用 `CurrentTime` 定位，落点是随机的 —— 同一个文件在不同位置会偏
`−768 / −512 / +1998 / −2039 / −1536 / 0` 个采样（**±42 毫秒，方向还不固定**）。
重读出来的那块会整体挪几十毫秒，和前后块之间就出现"丢一段 / 重一段"，接缝直接听得出问题。
（换成 `MediaFoundationReader` 结果一样。）

所以改成从**上一次的成品**就地推导。时间轴上每一块的样本本来就是
`源素材样本 × 增益 × 淡入淡出包络`，而上一份成品里已经存着它了：

```
新值 = 旧值 × (新包络 / 旧包络)
```

不碰源文件 → 不涉及定位 → 没有精度问题，而且几乎不花时间。
这条路覆盖日常绝大多数编辑：**挪块 / 裁短 / 调音量 / 改淡化 / 整条平移 / 删块**。
只有"要更多源素材"的操作（新增块、把边缘往外拉、切开、换源文件）才退回整条重拼。

判定逻辑全在 `LiteCut.Core/Timeline/AudioPatchLayout.cs`，纯算术、不碰音频库、可独立断言。

**2）新成品是新分配的数组，所以"没动的块"也必须搬过去。**

这条踩过一次坑：最初只把"改动过的段"写进新数组，结果一改音量整条时间轴只剩那一块有声音。
现在施工图（`AudioPatchPlan.Jobs`）对每个新段都给一个活 —— `IsCopy`（整片照搬，走 `Array.Copy`）
或者重算。按新计划顺序执行，段与段万一重叠时"后写的覆盖先写的"，和整条重拼行为一致。

同理，`NothingToDo`（可以直接复用旧数组）不能只判"所有段都是照搬"，还得判"旧的段一个都没少"
—— 否则**删掉一块**会被误判成"什么都没动"，被删那块的声音还留在成品里。

**3）偏移量必须用"两个截断值相减"，不能"先减再乘"。**

渲染器把源区间起点算成 `(long)(秒 × 采样率)`，是对每个起点各自截断一次。
所以 `24.4 × 48000 = 1171199.99999999993` 截断成 `1171199`，而 `0.4 × 48000` 截断成 `19199`，对不上。
差 1 个采样（20 微秒）听不出来，但会让"增量重拼"和"整条重拼"的结果对不上账 ——
这种"看着没事、账对不上"的状态最容易被后来的改动放大成真 bug。

#### 怎么验证没改坏

`工具\音频探针\`（**不是产品的一部分**，只在需要时手动跑）：

```powershell
cd 工具\音频探针
dotnet run -c Release     # 退出码 0 = 全通过
```

它把这条链路上"看不见的不变量"固化成断言，当前 **117 项全通过**：

* 每段到底读的是源素材哪个位置（用"值即下标"的合成素材反推，偏差按样本数报）
* **增量重拼的结果必须和整条重拼逐样本一致**（改音量 / 挪块 / 裁头 / 裁尾 / 平移 / 删块 / 重叠布局 / 连续 8 次不累积误差）
* 该拒绝的必须拒绝（新增块、边缘外扩、换源文件、旧增益为 0、去掉淡入、采样率变了、缓存与计划对不上）
* 缓存指纹、后台预热、播放设备复用（含上面那条"设备重建 34ms vs 重开 0ms"）
* `Stop()` → `PlaybackStopped` 的同步/异步时序（就是"不能靠摘处理器绕开"的依据）
* m4a"跳读 vs 顺读"到底差多少（上面那个 ±42ms 的证据，随时能重跑复现）

其中三段要用真实的项目文件和大段 m4a 素材（路径写在 `Program.cs` 顶部），
换台机器没有就自动跳过并打印提示，不会算失败。

***

### 0.10 点缝选块"偏右" + 裁切边"自己延长"（2026-09-29 下午补修的两个交互 bug）

用户报告的现象（两句话说的其实是同一条缝上的两个 bug）：

1. **A、B 两块首尾相接时，点 A 的右侧，选中的却是 B** —— "实际点击的位置偏右"。
2. **想拖 A 的右边缘裁切，一按住鼠标 A 就自己延长、顶到 B 的左边缘**（触发了"顶住"红光）。

两条根因都在按下鼠标那一刻：

**① `TimelineProject.HitTest` 的遍历顺序有偏心。** 旧实现从尾部逐块扫、每块"先查边缘再查身体"。
A、B 相接时缝两侧的边缘都在容差内，但 B 在列表后面先被扫到 —— 点在 A 身上（还没过缝），
返回的却是 `(B, "left")`。修法改成**两遍扫描**：

* 第一遍找"点在谁身上"：点在某个块身上，边缘只认**它自己的**边缘（够得着容差才给，否则就是拖块身）。
  于是缝左侧永远抓到 A 的右边缘，缝右侧抓 B 的左边缘。
* 第二遍只在"点在空白处"时用：在所有块的边缘里取**离点击位置最近**的那个 ——
  窄缝里的空白也能抓到两侧边缘，方便微调。

**② `WaveformControl.TrimTo` 的右边缘映射基准写错。** "时间轴位置 ↔ 源素材位置"的映射只有一条
（块内内容等长一一对应）：`源位置 = 源起点 + (时间轴位置 − 块起点)`。旧代码右边缘却写成了
`源出点 + (鼠标位置 − 块起点)`，换算回来的目标位置比鼠标位置**整整多出一整个块长** ——
刚一按住拖一下，块就"自己延长"撞到右边邻居。修法：左右两条边统一用同一条映射，
`目标时间轴位置 = 鼠标位置`，边缘严格跟手。

回归验证：探针新增"三·五、命中测试"一节（缝左/缝右/正中/窄缝/空白共 8 条断言），当前 **117 项全通过**。
`TrimTo` 的那半截在界面层没法直接断言，推导过程钉死在代码注释里了。

***

### 0.11 时间轴白痕 + 纯精剪项目走不通下游（2026-09-29 傍晚补修）

用户截图报了三件事，两件是 bug，一件是误解，一起记录：

**① 时间轴上那道"白色涂痕"是什么？** 是**素材块的名字标签**。每个块都在自己顶部左侧
画一条白字（素材文件名，截到 18 字），但用户项目 184 块里有 40 块短于 1 秒——缩放级别一低，
一块只有几个像素宽而文字有 80 像素长，十几个块的白字全叠在同一段区域，就成了白痕。
修法（`WaveformControl.OnRender`）：块宽不足 48px 就不画名字（**被选中的块除外**——
选中时要能认出它是谁）；并且文字**裁剪在本块矩形内**，再宽也不许溢出压到邻居头上。

**② 「应用到下游」永远灰着。** 用户这个项目**没跑过粗剪**（直接在精剪页导素材手工剪），
而下游全链条（应用到下游 / 分镜 / 渲染 / 导出）以前一律只认 `HasPlan`（粗剪决策）——
纯精剪项目处处碰壁。修法：引入**决策口径 `HasDecision` = 粗剪或精剪任一**：

* `WorkbenchState`：新增 `HasDecision` / `DecisionDurationSec`（成片总时长）/
  `DecisionTimeline()`（把保留区间首尾相接成"成片时间轴"，分镜页用来反推源位置）。
* `StepFineCutView`：「应用到下游」有内容就放开；`SyncBack` 不再因没粗剪就提前返回——
  **精剪时间轴本身就是决策**，没有粗剪时它就是唯一决策层。
* 分镜/渲染/导出三页：门槛全部从 `HasPlan` 换成 `HasDecision`，时长一律读
  `DecisionDurationSec`，不再直接解引用 `Plan`。达芬奇 XML 导出同样放开。

无头验证（临时工程，跑完即删）：纯精剪场景 8 项断言全通过。

**③ "下游不是双轨吗？"** —— 不是误解 bug，是名词对不上：代码里的**"双轨"只出现在第①步
粗剪算法**（`AutoCutEngine`：波形轨静音检测 + 语义轨转写对齐，第①步界面上显示
"双轨（对齐 xx%）"）。下游（分镜→渲染→组接导出）从来不是双轨界面，它是一条流水线：
分镜表 → 渲染任务 → ffmpeg 直接成片 / 达芬奇 XML。而且这个项目的源素材是纯音频 m4a，
根本没有画面轨。若想要"下游也能看到双轨（音频块 + 文字句）"的界面，那是新需求，待议。

***

### 0.12 ★「重启后粗剪白剪了」+ 粗剪页交棒按钮（2026-09-29 傍晚补修）

用户原话：*"我导入文件、点开始粗剪，然后直接点精剪页进入下一页。保存下次打开后，精剪也还有音频，
但粗剪页就似乎是未经粗剪的状态了。……之前那个应用到下游打不开。"*

**他的猜测完全正确，而且比他想的更严重 —— 决策是被主动擦掉、再被写回磁盘的。**

#### 病根：加载源文件时无条件清空决策

`Step1CutView.LoadFile()` 里原来有这么一段（看着很合理）：

```csharp
_s.Plan = null;          // 粗剪决策
_s.FineCutKeeps = null;  // "精剪接管下游"的标记
_s.Shots.Clear();        // 分镜表
```

理由写的是"切了源文件，之前的分析和分镜都作废了"。**但它漏了一条路：打开项目也会走到这里。**

```
打开项目 → ProjectSession.ApplyToState() 把源文件路径排进 Step1CutView.PendingFile
        → 第①步一构造就自动解码 → 上面三行执行 → 决策没了
        → 紧接着自动保存（IsDirty 成立），把这个空状态写回 project.json
```

于是每一步都"看起来对"，合起来就是：**跑完粗剪存下来了 → 点应用到下游也生效了 → 回第①步看一眼 → 决策静悄悄没了。**

验尸（用户项目 `project.json`，16:51 时）：

```
Cut.Plan        = None      ← 被擦掉了
FineCut.Clips   = 184 块    ← 精剪反而活下来（走的是另一条存储路径）
FineCut.Applied = False     ← 被擦掉了
Shots.Rows      = 0         ← 也被擦掉了
```

#### 修法：决策要不要作废，只看"源文件换没换"

判定与清理下沉到状态层（纯数据操作，能被无头验证直接断言）：

```csharp
// WorkbenchState
public static bool IsSameSource(string? a, string? b)   // 同路径 = 重载
public void ApplyDecodedSource(string path, PcmData pcm, MediaProbe probe, bool switchedSource)
```

* `switchedSource == false`（**同一个文件重新解码**：开项目、重新加载）→ 决策一个都不动；
* `switchedSource == true`（真换了素材）→ 清 `Plan` / `FineCutKeeps` / `Shots` /
  `Transcript` / `TimelineSnapshot`（全是"针对旧素材"的判断，留着就会串台）。

顺带补一个**必要的**防护：时间轴快照现在盖了"**我是剪哪个源文件排的**"的戳
（`WorkbenchState.TimelineSourcePath` ⇄ `FineCutPart.SourcePath`）。换素材后旧版面自动作废，
不会被拿去当新素材的剪辑决策（最坏情况是"拿 A 片的区间去剪 B 片"）。
老档案没有这个字段 → `""` 视为可信，向后兼容。

#### 新增：粗剪页的「应用到下游」+ 全局"下游在使用谁"

用户的第二个诉求：*"在粗剪页也设计一个应用到下游的按钮……如果我已经精剪过了，
又重新调整粗剪内容，不会破坏掉精剪结果。可以防止我误操作。"*

这套改动把它变成一条明确规矩：**改粗剪 ≠ 改生效决策；要切换必须明确点一次。**

| 改动 | 位置 |
| --- | --- |
| 新增 `DecisionSource`（None/Coarse/Fine）与 `ActiveDecision` / `ActiveDecisionLabel` / `FormatDuration` | `WorkbenchState.cs` |
| 新增 `UseCoarseDecision()`：只把"谁生效"拨回粗剪，**精剪时间轴一个字都不动** | `WorkbenchState.cs` |
| 粗剪页新增**决策条**：显示"下游当前使用：…" + 「应用到下游」按钮（含三种状态的措辞） | `Step1CutView.xaml(.cs)` |
| 精剪页：粗剪更新**只提示不自动重铺**（新增黄色提示条 + 「用新粗剪重铺时间轴」） | `StepFineCutView.xaml(.cs)` |
| 精剪页：「应用到下游」后按钮变「✓ 已应用到下游」+ 标题栏显示"下游正在使用：…" | `StepFineCutView` |
| 顶栏**常驻**"下游使用：精剪 · 184 段 · 6分12秒（精剪决策）"——四页都看得见 | `WorkbenchWindow.xaml(.cs)` |
| 分镜/渲染/导出三页写明"本次成片依据：粗剪/精剪 · N 段 · 时长" | 各 `Step*.xaml.cs` |
| `EditorView.ClearTimeline()`：换素材时清掉编辑器里残留的旧时间轴 | `EditorView.xaml.cs` |

**为什么"只提示不重铺"很重要**：旧行为是"粗剪换了引用就直接 `LoadPreCutProject` 重铺"，
等于你回第①步重跑一次粗剪，第②步手工挪过的块、调过的音量、加过的淡化**当场全没**，
而且没有任何提示。现在要不要重铺由你点，并且弹窗说清代价。

#### 还有一条：只是"来精剪页看一眼"不能把下游的控制权抢回去

这条是**端到端验证抓出来的**（不是用户报的，是我把两页真开起来点了一遍才发现的）——
它和 0.1 那个 bug 是同一类：**没让你知道，就把状态改了**。

`StepFineCutView.SyncBack()`（离开精剪页时调）的老规矩是"编辑器里的时间轴和当前决策不一样就接管"。
这规矩在"改了再走"的场景下是对的，但漏了一种：

```
你在第①步点了「改用粗剪结果」（下游 = 粗剪）
   ↓
顺手来精剪页看一眼
   ↓
离开 → SyncBack 发现"编辑器里的时间轴 ≠ 现在的粗剪决策" → 又把下游抢回精剪
```

你会觉得"我明明切过去了，怎么又自己回去了"。

**修法**：加一条基线 `_enteredKeeps` = **进本页那一刻**编辑器里的保留区间。
离开时比"进页时"和"离开时"是不是同一份：

* 一样 → 这次只是看了一眼 → **不碰决策**；
* 不一样 → 真的动过手 → 照旧接管（"改了再走"的便利没丢）。

`BtnApply_Click` / `BtnRevert_Click` / `BtnReloadCoarse_Click` 都会顺手对齐这条基线，
所以"明确点过按钮之后再离开"不会被误判成"改了"。

**顺带**：分镜/渲染/导出三页现在会明确写出"本次成片依据：精剪 · 184 段 · 6分12秒"，
解决"点了应用到下游但下游什么都看不到"的观感问题（以前只有状态栏一行小字）。

#### 回归

* 音频探针 **117 项全通过**（顺手修掉它写死 `Clips[218]` 导致的越界崩溃 ——
  用户项目从 219 块变成 184 块，写死的下标就是定时炸弹。现在一律按实际块数推算）。
* 决策链无头验证（临时工程）**93 项全通过**，其中 30 项是**真开窗口**跑的
  （`App.InitializeComponent()` + `WorkbenchWindow` + 手动泵 Dispatcher，
  然后真去找到粗剪页的决策条、真点一下「应用到下游」、真去改 `State.Plan` 看精剪页反应）。
  纯逻辑那部分 63 项覆盖决策的存/取/切换/串台防护。
* 精剪下游该怎么走，见 `文档\精剪下游工作流-设计草案.md`。

### 0.13 成片规格：背景图 / 横屏 / HyperFrames（2026-09-29 晚补）

**用户拍板的两件事**（以前只写在外部工作流文档里，程序不知道）：

1. **所有视频统一铺同一张背景图** —— `D:\视频创作\读书博主\公共素材\grid-bg-deep-blue-grid.jpg`
   （实测 1920×1080，深蓝底 + 细网格线，正好 16:9，铺满不形变。旧的 `视频背景.jpg` 停用）。
2. **渲染走 HyperFrames**（HTML/CSS/GSAP 编排动效 → 逐镜头 MP4），不是 Remotion。

**为什么要进程序，不能只留在文档里**：

* 分镜设计是"这 X 秒画面上放什么"。而"放什么"合不合理，取决于画面最终铺在哪张底子上、
  拿什么技术渲染 —— **底子是深蓝网格**却设计白底大色块，看着像贴了张纸；
  **渲染走网页技术**却设计实拍航拍镜头，根本落不了地。
  这类错在分镜稿上完全看不出来，等渲染完才发现，返工成本最大。
* 以前渲染页生成的卡片是写死的 1280×720 纯色底 —— 跟成片对不上，到渲染还得再问一遍底图。

**落地在哪**：

| 位置 | 干了什么 |
| --- | --- |
| `Workbench\AppConfig.cs` | 全局配置新增：`VideoBackgroundImage` / `VideoWidth/Height/Fps` / `RenderEngine` / `HyperFramesTemplateDir`。**改一次，以后每支视频都跟着走** |
| `Workbench\RenderSpec.cs`（新） | 规格对象：从配置算出来 + 落盘/读回 `render-spec.json` + `Describe()` 一行说明 + `BackgroundUri`（给 HTML 用的 file:/// URI） |
| `Workbench\Agent\ShotPrompt.cs` | `BuildSystemPrompt(spec)` 把规格拼进系统提示词：画幅、底图是深蓝网格、**元素要叠在底图上别糊住它**、动效必须能用网页技术做 |
| `Views\Step2ShotView` | 工具条下常驻一行「成片规格：横屏 1920×1080（16:9）· 30fps · HyperFrames · 背景 xxx.jpg」+「换背景图…」「打开背景图」两个按钮；进页就把规格落一份到 `stages\03-shot` |
| `Views\Step3RenderView` | 卡片改按 1920×1080 出、底图铺上（`background-size:cover`）；任务清单里带上 `engine / canvas / backgroundImage / templateDir`；产物是 MP4 不再是截图；项目里留一份 `stages\04-render\render-spec.json` + `render-tasks.json` |
| `ProjectStore.ProjectRef` | 新增 `RenderStageDir`（`stages\04-render`）、`AssembleDir`（`stages\05-assemble`） |

**换背景图**：分镜页工具条右边「换背景图…」—— 它是全局设置，换一次以后每支都跟着走；
每个项目仍会在 `stages` 下留一份 `render-spec.json`，那是"这次渲染到底用了什么"的凭证。

**回归**：一次性验证工程 **55 项全通过**（规格算/存/读 18 项、提示词 12 项、卡片 HTML 7 项、
真构造分镜页 + 渲染页 18 项），并专门断言"没往用户真实项目目录里写东西"。

***

## 一、这是什么

### 1.1 一句话定位

你在审阅 AI 剪辑版（或原始录音）时，觉得某段不好、想删掉，或者想单独调某句的音量、加个淡入淡出防爆音 —— 这个工具就是干这个的。它只认 **m4a / mp3 / wav**，只做**单声道**口播，界面极简，启动不到 1 秒。

编辑方式借鉴达芬奇：**时间轴是一排 "素材块"**，每段音频是一块，可以拖位置、拖边缘裁头尾、用剪刀切开，块之间自动填静音。

### 1.2 功能清单（当前版本）



| 功能      | 怎么用                                                |
| ------- | -------------------------------------------------- |
| 导入      | 顶部「导入」选文件 → 自动复制进素材库                               |
| 素材库     | 左侧面板，**双击**或**拖到右侧时间轴**即插入（总是插到**时间轴末尾**，不会叠在已有块上） |
| 播放 / 暂停 | 底部按钮，或按空格                                          |
| 定位      | 拖动红色播放线                                            |
| 选中      | 点块 = 选中；拖空白 = 框选；Ctrl + 点 = 切换；Ctrl+A = 全选         |
| 移动块     | 拖块中间，左右移动（不能移出 0 秒）                                |
| 裁切      | 拖块**左 / 右边缘**：只裁头尾；不能超过素材本身长度，也不能越过剪刀切出的相邻块        |
| 剪刀      | 播放线停在某位置 → 点「剪刀」把选中的块切开成两块                         |
| 删除块     | 选中后点「删除块」或按 Delete（不删源文件）                          |
| 音量调整    | 选中块 → 拖音量滑块（**松手生效**，-20 \~ +20 dB）                |
| 淡入 / 淡出 | 选中块 → 点「淡入」「淡出」（0.5 秒渐变防爆音）                        |
| 撤销 / 重做 | Ctrl+Z / Ctrl+Y（快照式，最多保留 50 步）                     |
| 缩放      | 滚轮以鼠标位置为中心缩放                                       |
| 滚动      | 底部水平滚动条；**播放中**红线跑出视野时视图自动跟随，未播放不跟随                |
| 导出      | 顶部「导出」，输出 48kHz m4a（块间空隙自动填静音）                     |
| 手机上传    | 手机 Safari 打开底部显示的地址，选音频上传，自动进素材库                   |
| 移除素材    | 素材库右键「删除」：只移除素材库条目（映射），磁盘文件不动，也不进回收站               |

### 1.3 明确不做（当前边界）

降噪、真正的多轨混音（块重叠时后放置的覆盖先放置的）、批量处理、效果器、格式转换、密码鉴权。以后想加，维护手册第六节教你怎么加。

### 1.4 使用流程

**本地剪辑**：导入 / 拖入素材 → 拖播放线定位 → 剪刀切掉不满意的段落 → 拖边缘微调头尾 → 空格播放确认 → 导出。

**iPhone 录音 → 电脑**：



1. 打开软件（上传服务自动启动，看底部状态栏地址）

2. 手机和电脑连同一个 WiFi

3. 手机 Safari 打开 `http://电脑IP:8124`

4. 点「选择音频」→ 选语音备忘录里的录音 → 上传

5. 电脑上素材库刷新，双击或拖入时间轴即可编辑



***

## 二、代码规模与文件地图

### 2.1 规模



* 源文件：15 个 `.cs` 文件 + 3 个 XAML（界面描述）

* 总代码量：约 3500 行 `.cs`（不含注释约三分之一是中文注释）

* 产物：单文件 `LiteCut.exe`，**约 140MB**（self-contained，把 .NET 运行时打包进了 exe，所以**不挑电脑**—— 拷到任何一台 Windows 10/11 都能双击直接跑，不用装任何环境）。这是 "双击即用" 和 "体积" 的取舍：以后想压到 1\~2MB 的瘦版，看第七节 "打包进阶"。

### 2.2 文件地图

**主流程（块轨道模型，学习按此顺序）**



| 文件                        | 职责                                            | 大约行数 | 你会改它的场景     |
| ------------------------- | --------------------------------------------- | ---- | ----------- |
| `TimelineClip.cs`         | 一个素材块的数据结构（引用源文件、入出点、音量、淡入淡出）                 | 63   | 给块加新属性      |
| `TimelineProject.cs`      | 时间轴数据中心：块列表、选中、增删 / 切开 / 移动 / 命中测试            | 220  | 改时间轴行为      |
| `UndoService.cs`          | 撤销 / 重做（快照模式，最多 50 步）                         | 137  | 调历史深度、改撤销策略 |
| `WaveformControl.cs`      | 时间轴绘制 + 鼠标交互（画块 / 播放线 / 刻度；拖移 / 裁切 / 框选 / 缩放） | 590  | 改选区交互、画法    |
| `AudioEngine.cs`          | 音频进出：读时长、按块拼流播放、导出 48kHz m4a                  | 355  | 改导出码率 / 采样率 |
| `FileService.cs`          | 打开 / 导出对话框 + 素材库列表                            | 154  | 改素材库规则      |
| `UploadServer.cs`         | 手机上传服务器（手写 HTTP，8124 端口起）                     | 387  | 改上传逻辑 / 端口  |
| `Utils.cs`                | 公用函数（全独立静态方法）                                 | 240  | 加通用小工具      |
| `Logger.cs`               | 写日志到 exe 同目录 logs 文件夹                         | 55   | 加日志级别       |
| `MainWindow.xaml` / `.cs` | 主窗口：界面 + 按钮转发 + 各模块接线（薄指挥中心）                  | 730  | 加按钮、改布局     |
| `App.xaml` / `.cs`        | 程序启动 + 全局配色                                   | 50   | 改主题颜色       |

**旧模型（保留中，已不在主流程，见 5.9 节）**



| 文件                           | 职责                            | 大约行数 |
| ---------------------------- | ----------------------------- | ---- |
| `Models.cs`                  | 波形编辑数据（AudioDocument / 选区）    | 105  |
| `EditService.cs`             | 命令模式编辑引擎（剪切 / 粘贴 / 音量 / 淡入淡出） | 367  |
| `AudioPeaks.cs`              | 波形峰值缓存（波形加速思路已被新实现继承）         | 127  |
| `LibraryWindow.xaml` / `.cs` | 独立素材库窗口（已被主窗口内置面板取代）          | 160  |

**音频这条链路（2026-09-29 傍晚重写，原理见 0.9 与 5.5）**



| 文件                                        | 职责                                    | 大约行数 |
| ----------------------------------------- | ------------------------------------- | ---- |
| `LiteCut.Core\Timeline\AudioRenderPlan.cs` | 渲染计划（纯数据："要拼什么"）+ 内容指纹                | 227  |
| `LiteCut.Core\Timeline\AudioPatchLayout.cs` | 增量重拼的判定与包络计算（纯算术，不碰音频库，可独立断言）         | 291  |
| `工作台\TimelineAudioRenderer.cs`             | 按计划渲染成样本数组 + 增量重拼执行                   | 371  |
| `工作台\AudioEngine.cs`                       | 播放 / 导出 / 指纹缓存 / 后台预热 / 设备复用           | 489  |
| `工具\音频探针\`                               | 回归验证小工具（**不参与产品打包**，只在需要时手动跑）           | 1187 |

### 2.3 调用关系（谁指挥谁）



```
MainWindow（指挥中心，只转发）

&#x20; ├─→ TimelineProject   （时间轴数据：块列表 / 选中 / 播放位置）

&#x20; │      └─→ TimelineClip（每一块）

&#x20; ├─→ UndoService      （撤销/重做：每次操作前拍快照）

&#x20; ├─→ WaveformControl  （画块 + 鼠标交互，直接读写 TimelineProject）

&#x20; ├─→ AudioEngine      （把时间轴拼成连续音频流 → 播放 / 导出）

&#x20; ├─→ FileService      （对话框 + 素材库列表）

&#x20; └─→ UploadServer     （手机传文件进素材库）

&#x20;        └─→ FileService（入库后通知刷新）
```

数据流（拖一个素材进时间轴后发生了什么）：



1. `MainWindow.InsertClipAtPlayhead()` 调 `AudioEngine.GetDurationSec()` 读素材时长

2. 创建 `TimelineClip`（记录源文件路径、入点 0、出点 = 时长、**时间轴位置 = 当前所有块的末尾**）

3. `_undo.CaptureSnapshot()` 拍撤销快照（操作前）

4. `_project.AddClip(clip)` → `NotifyChanged()` → 波形控件重绘

5. 播放 / 导出时：`AudioEngine` 把**所有块按时间轴位置拼成一条连续样本流**（空隙填静音、每块应用自己的音量 / 淡入淡出），然后播这条流或编码成 m4a



***

## 三、环境配置（读代码 / 改代码 / 编译）

### 3.1 本机现状

我（AI）已经在你电脑上装好了 **.NET SDK 8.0.425**（编译必需），位置在：

`C:\Users\wuhao\AppData\Local\Microsoft\dotnet\`

它自带运行时，所以打包出来的 exe 你机器上能直接跑；而且现在打的是 self-contained 包，运行时内嵌，拷到哪台机器都能跑。

### 3.2 方案 A：Visual Studio Community 2022（推荐）

这是 C# 最完整的免费开发环境，有可视化界面设计器，最适合学习。



1. 下载：浏览器打开 `https://visualstudio.microsoft.com/zh-hans/downloads/`，选 **Community 2022** 下载

2. 运行安装器，在「工作负载」勾选：**".NET 桌面开发"**（右侧会连带选上 WPF 支持）

3. 安装完成后：**文件 → 打开 → 项目 / 解决方案** → 选择 `工作台.sln`

4. 按 **F5** 直接运行调试；想打断点看变量：在代码行号左侧点一下（出现红点），运行到那里会暂停，鼠标悬停变量看值

5. 按 **Ctrl+Shift+B** 只编译不运行

> 安装包约 6~10GB，磁盘紧张可用方案 B。

### 3.3 方案 B：VS Code + 已装的 SDK（轻量）

只读代码、改逻辑够用，但没有 WPF 可视化设计器（XAML 只能手写）。



1. 下载 VS Code：`https://code.visualstudio.com/`

2. 打开 VS Code → 扩展市场搜索安装：**C# Dev Kit**（微软官方）

3. **文件 → 打开文件夹** → 选 `工作台` 文件夹

4. 打开 `.cs` 文件即可读代码，智能提示、跳转定义都可用

5. 运行：终端执行 `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project 工作台`

### 3.4 命令行编译（不需要任何 IDE）



```
\# 编译

& "\$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build 工作台\工作台.csproj

\# 打包单文件 exe（等价于运行 build.ps1）

.\build.ps1
```



***

## 四、C# 扫盲（从 Python 视角）

如果你会 Python，下面这张对照表能帮你快速上手 C#。

### 4.1 核心概念对照



| Python          | C#                | 说明                                                    |
| --------------- | ----------------- | ----------------------------------------------------- |
| `import xxx`    | `using xxx;`      | 引入别的命名空间。C# 没有 "文件头" 概念，`using` 就是告诉编译器 "我要用哪些库里的名字"  |
| 模块（.py 文件）      | 类 + 命名空间          | C# 里代码按 "类" 组织，一个文件可放一个或多个类                           |
| `def 函数()`      | `方法`（类里的函数）       | C# 一切都在类里，没有模块级函数；但 `static` 方法 ≈ 模块级函数               |
| `list`          | `List<T>`         | 泛型列表。`List<TimelineClip>` = 元素必须是 TimelineClip 的 list |
| `dict`          | `Dictionary<K,V>` | 字典                                                    |
| 函数参数默认值         | 可选参数 `= 值`        | 类似                                                    |
| `lambda x: ...` | `(x) => ...`      | 匿名函数，本项目大量用在事件订阅上                                     |
| 异常 `try/except` | `try/catch`       | 类似                                                    |
| 类和对象            | 类和对象              | 概念一样                                                  |
| 无               | `event`           | 事件：类 A 说 "有事发生了"，类 B 说 "我来处理"。本项目用它解耦模块               |
| 无               | `interface`       | 接口：只规定 "有什么方法"，不实现。多态和解耦的关键                           |

### 4.2 本项目的每个 `using` 是干嘛的（帮你认门）

以 `MainWindow.xaml.cs` 为例：



```
using System;                     // 基础类型：string、int、Math、EventArgs 等

using System.Collections.Generic; // List\<T>、Dictionary\<T>

using System.IO;                  // 文件操作：File、Path、Directory

using System.Windows;             // WPF 核心：Window、MessageBox、RoutedEventArgs

using System.Windows.Controls.Primitives; // Thumb：音量滑块的"拖柄"，用它检测拖完

using System.Windows.Input;       // 键盘/鼠标：Key、ModifierKeys、MouseButtonEventArgs
```

其它文件里可能出现的：



```
using System.Linq;                // 集合查询：Where（筛选）、OrderBy（排序）、Min/Max 等

using System.Text;                // 字符串编码：Encoding、StringBuilder

using System.Net;                 // 网络：IPAddress

using System.Net.Sockets;         // 网络连接：TcpListener、TcpClient

using System.Net.NetworkInformation; // 网卡信息：NetworkInterface（找局域网 IP）

using System.Threading;           // 线程：Thread

using System.Windows.Threading;   // 界面线程定时器：DispatcherTimer

using System.Windows.Media;       // 绘图：DrawingContext、Color、Brush（WaveformControl 用）

using Microsoft.Win32;            // 打开/保存文件对话框

using NAudio.Wave;                // 第三方音频库：播放、读写音频

using NAudio.Wave.SampleProviders;// 音频采样处理：重采样、立体声转单声道
```

### 4.3 几个关键语法（本项目实际用到的）

**类 = 数据类型 + 行为**。`TimelineClip` 就是 "时间轴上一块素材" 这种数据类型的定义：



```
public class TimelineClip

{

&#x20;   public string SourcePath { get; set; } = "";   // 自动属性：读写都开放，默认空字符串

&#x20;   public double SourceStartSec { get; set; }      // 从源素材第几秒开始取（入点）

&#x20;   public double TimelineStartSec { get; set; }    // 这块放在时间轴第几秒

&#x20;   // 只读计算属性（≈ Python 的 @property）：不另存，每次现算

&#x20;   public double SourceDurationSec => SourceEndSec - SourceStartSec;

}
```

**static 方法 ≈ Python 模块级函数**。`Utils.FormatTime()` 不需要对象直接调用，和 `import utils; utils.format_time()` 一个意思。

**event（事件）= 回调通知**。这是本项目解耦的关键：



```
// TimelineProject 里声明：时间轴内容变了会广播

public event EventHandler? Changed;

// 某个操作完成后，触发它：

Changed?.Invoke(this, EventArgs.Empty);

// MainWindow / WaveformControl 里订阅（说"我来处理"）：

\_project.Changed += (s, e) => InvalidateVisual(); // 每次变化后自动重绘
```

`?.Invoke` 意思是 "如果没人订阅就跳过"（Python 里没有对应，理解为 "安全调用"）。

**lambda（匿名函数）**：本项目大量用简写处理事件：



```
\_engine.PlaybackStopped += (s, e) => { PlayButton.Content = "▶ 播放"; };

// 等于一个只有一句逻辑的小函数
```

**可空类型&#x20;**`?`：`TimelineProject? _project` 意思是 "这个变量可能是 null"。C# 强制你意识到空值问题（避免 Python 里常见的 NoneType 报错）。

**元组返回**：一个方法可以一次返回多个值。`TimelineProject.HitTest()` 返回 `(TimelineClip? clip, string edge)`，调用方用 `var (clip, edge) = ...` 接住。

### 4.4 XAML 是什么

XAML 是 WPF 用来 "描述界面长什么样" 的语言，不是编程语言。一个按钮在 XAML 里是：



```
\<Button Content="剪刀" Click="OnScissorsClick" />
```

`Click="OnScissorsClick"` 的意思是：按钮被点击时，调用本类里的 `OnScissorsClick` 方法。所以 XAML 管 "长什么样"，`.cs` 文件管 "点了之后干嘛"。两者通过 `x:Class` 配对（比如 `MainWindow.xaml` 配对 `MainWindow.xaml.cs`）。

### 4.5 命名规则（本项目遵守）



| 东西      | 规则                    | 例子                              |
| ------- | --------------------- | ------------------------------- |
| 类名、方法名  | 每个单词首字母大写（PascalCase） | `SplitClipAt`、`CaptureSnapshot` |
| 局部变量、参数 | 首单词小写（camelCase）      | `newStart`、`clip`               |
| 常量      | 全大写                   | `LibraryDir`、`MaxHistory`       |
| 方法名     | 动词开头                  | `AddClip()`、`Undo()`、`Play()`   |



***

## 五、逐文件讲解（关键代码导读）

按 "从数据到界面" 的顺序讲，配合文件地图看。**阅读顺序建议：5.1 → 5.2 → 5.3 → 5.5 → 5.8 → 5.4**（数据先于界面）。

### 5.1 TimelineClip.cs —— 一个素材块长什么样（入门第一个文件）

只有 63 行，**这是最适合第一个读的代码文件**：全是纯 C# 语法（类、自动属性、计算属性），没有任何界面概念。

记住它的核心设计：**块里不存音频数据本身，只存 "引用"**——`SourcePath`（哪个文件）+ `SourceStartSec/SourceEndSec`（取哪一段）。音频数据在播放 / 导出时才从源文件读出来。这就是达芬奇里 "片段（Clip）" 的概念：**一个块 = 源文件的某一段 + 放在时间轴的某个位置**。



* `Id`：唯一 ID（`Guid`），即使两个块来自同一个素材也能区分

* `SourceStartSec/SourceEndSec`：入点 / 出点（裁切改的就是它）

* `TimelineStartSec`：块在时间轴上的起点（移动改的就是它）

* `VolumeDb`、`FadeInSec`、`FadeOutSec`：块级音量 / 淡入淡出

* `TimelineEndSec`、`SourceDurationSec`：计算属性，从上面推出来

### 5.2 TimelineProject.cs —— 时间轴数据中心（重点看）

时间轴的 "数据中心"，所有对时间轴的修改都通过它，不直接改 UI。



* `Clips`：所有块的列表（按时间轴位置排序）

* `AddClip / RemoveSelectedClips / SplitClipAt / MoveClip`：增删改。**剪刀**就是 `SplitClipAt`：把跨越播放线位置的块切成两块（第一块保留到切开点，第二块从切开点开始，源素材的入点同步换算）

* `SelectClipsInRange / SelectClip / SelectAll / ClearSelection`：选中管理（框选 / 点选 / 全选）

* `HitTest(时间秒, 容差)`：命中测试 —— 返回 `(命中的块, "left"/"right"/"body")`。**这是拖块移动和拖边缘裁切的判定基础**：点在块身上，边缘只认它自己的（缝左侧抓左块右边缘、缝右侧抓右块左边缘）；点在空白处才取"最近的边缘"。两遍扫描的原因见 0.10

* `TotalDurationSec / StartSec`：总时长 / 原点（第一块的起点）

* `Changed / SelectionChanged` 事件：内容变了通知界面重绘

关键设计：**任何修改最后都调&#x20;**`NotifyChanged()`，界面只订阅事件刷新，不需要知道谁改的。

### 5.3 UndoService.cs —— 撤销 / 重做（快照模式）

和旧模型 EditService 的 "命令模式" 不同，现在用的是**快照模式**：



* 原理：**每次修改时间轴之前**，把所有块的完整数据复制一份存进 `_undoStack`。撤销 = 弹出上一个快照，把 `_project.Clips` 整个恢复；重做 = 反向再弹。

* `CaptureSnapshot()`：**必须在修改前调用**（MainWindow 里所有操作都是 `_undo?.CaptureSnapshot()` → 改 → 完成），修改本身不用通知它

* 上限 50 步（`MaxHistory`），防内存爆

* 优点：简单可靠；缺点：每次存全量数据。素材量不大，完全够用

* 对比：旧模型 EditService 是 "命令模式"（每个操作是一个命令对象，记录 Old/New，执行 = 换、撤销 = 换回去）。两者思想不同，效果一样，本项目的当前方向是快照模式

### 5.4 WaveformControl.cs —— 时间轴绘制 + 鼠标交互（最复杂）

继承 `FrameworkElement` 的自绘控件，职责只有两个：**画** 和 **鼠标**。



* 画（`OnRender`）：背景 → 每个块画一个矩形（选中 = 金色，未选中 = 蓝色）+ 块内画波形 + 素材名标签 → 红色播放线（顶部带手柄）→ 时间刻度

* 播放线跟随：**播放中**红线跑出可视范围时，视图自动滚动把它拉回屏幕中间（`FollowPlayhead()`，由 MainWindow 在播放位置变化时调用）；**未播放时**拖播放线不会带动视图（修复：以前未播放也会跟随）

* 块内波形：`_peakCache` 缓存每个素材的峰值数组（每 100 样本取一个最大振幅），画的时候只按块的时间范围取峰值画竖线，所以不卡

* 鼠标（按下 → 移动 → 抬起）：


  * 点在播放线附近 → 拖播放线（改 `PlayheadSec`，发 `PlaybackPositionChanged`）

  * 点在块左 / 右边缘（8 像素容差）→ **拖边缘裁切**：只改入点 / 出点，另一头不动；源入点下限 = 0 / 前一块的源出点，源出点上限 = 素材总时长 / 后一块的源入点（剪刀切出的接缝不可越过，素材本身不可拉长）

  * 点在块内部 → **拖块移动**（记录鼠标和块起点的偏移 `_dragOffsetSec`）

  * 点空白 → 框选（`RubberBand`，实时调 `SelectClipsInRange`）

  * 滚轮 → 以鼠标位置为锚点缩放（0.01 \~ 2 秒 / 像素）

* 对外事件：`ClipChanged`（拖完块，MainWindow 据此拍撤销快照）、`ViewportChanged`（更新滚动条）、`PlaybackPositionChanged`、`SelectionChanged`

* 性能细节：`InvalidateVisual()` 是 WPF 的 "重画请求"，配合 `_peakCache` 缓存峰值，拖动 / 缩放不卡

### 5.5 音频播放与渲染

管两件事：**播放** 和 **导出**。核心思路是 "拼流"：把时间轴上的块按位置拼成一条
**连续样本数组**，然后播这个数组 / 导出这个数组 —— 播放和导出共用同一份逻辑。

这一块在 2026-09-29 傍晚重写过（原来的实现每按一次空格要 2.5 秒，见 0.9）。现在拆成三层：

| 文件 | 职责 |
| --- | --- |
| `LiteCut.Core\Timeline\AudioRenderPlan.cs` | **纯数据**："要拼什么"（每段取源文件哪一段、放哪、增益、淡化）+ 内容指纹。`Build()` 会把段拷成数组，天然是快照，界面线程调完可安全交给后台线程 |
| `工作台\TimelineAudioRenderer.cs` | **怎么拼**：一个源文件只开一次解码器、相邻段顺读不倒回、`float[]` 下标写。另有 `TryPatch()` 做增量重拼 |
| `工作台\AudioEngine.cs` | **播放/导出**：指纹缓存、后台预热、设备复用、取样本 |

关键点：

* `BuildPlan(project)`：把时间轴的块转成 `AudioRenderPlan`（顺手把 `VolumeDb` 经 `Utils.DbToGain` 换成线性增益）。
  **必须在界面线程调用** —— 它要读时间轴的块列表做快照。
* `Render(plan)`：整条重拼，约 1.5 秒（219 块 / 427 秒）。大头是解码，砍不掉。
* `TryPatch(oldPlan, oldSamples, newPlan)`：增量重拼，只重算改动过的段，其余整片 `Array.Copy`，约 16 ms。
  推不出来就返回 `null`，调用方退整条 —— **宁可慢一点，不能听错**。原理见 0.9 的"两个反直觉的技术点"。
* `AudioEngine.Play(project)`：缓存命中 → 直接用；没命中 → `TryPatch` → 还不行才 `Render`。
  设备还开着且喂的是同一份样本时，只挪读指针不重建（条分支管"暂停后接着播"和"从头再播"）。
* `Prewarm(project)`：**后台线程**把当前时间轴先拼好。必须在界面线程调（要先做快照）。
  连续多次调用只跑最后一个 —— 新请求顶掉还没开始跑的旧请求。
* `ExportM4a(project, path)`：走**同一份缓存**（刚放下鼠标就导出时不用再拼一遍）→
  重采样到 48kHz → 先写临时 WAV → 用 Windows 自带 AAC 编码器转 m4a → `finally` 保证临时文件必删。
* `GetDurationSec(path)`：静态方法，读素材时长（插入块时用）。
* 块间空隙 = 拼流时留下的静音，所以"删掉的段落"自动变成静音。

> 想改导出码率？找
>
> `MediaFoundationEncoder.EncodeToAac(..., 128000)`
>
> 把 128000 改成 96000 或 192000。

> **改了音量 / 淡化之后一定要发 `NotifyChanged()`，不能只 `Wave.Refresh()`。**
> 只刷波形的话画面变了、按空格听到的还是旧声音（这个坑 0.9 那轮一起修了）。

### 5.6 FileService.cs —— 文件与素材库



* 打开 / 导出对话框（`OpenFileDialog` / `SaveFileDialog`，WPF 自带）

* 素材库：列出目录里所有 m4a/mp3/wav，按修改时间倒序，包装成 `LibraryItem`（含大小、时间的友好显示文本）

* 素材库默认位置：`D:\视频创作\读书博主\音频素材库`（改法见第六节）

* **素材库条目 = "映射"（像达芬奇）**：删除素材库条目只把路径记入已移除清单（素材库目录下隐藏文件 `.litecut_removed.txt`），列表不再显示它；**磁盘文件永远不动、不进回收站**。手机上传的文件落盘即自动出现在列表（自动建映射），删除时同样不碰文件

### 5.7 UploadServer.cs —— 手机上传

自己用 `TcpListener` 写了一个**极简 HTTP 服务器**（为什么不用现成的 HttpListener：监听局域网地址需要管理员权限注册 URL 白名单，普通用户跑不了）。



* `GET /`：返回上传页面（HTML 是 `Utils.BuildUploadPageHtml()` 内嵌的）

* `POST /upload`：按 `Content-Length` 读完整数据 → 解析 multipart 报文（找 `boundary` 和 `\r\n\r\n` 分隔）→ 提取文件名和文件数据 → 存进素材库，文件名自动加时间戳（`20260919_203045_原名.m4a`）

* 端口从 8124 开始尝试，被占用自动顺延（避开 serve.py 的 8123）

* 单文件上限 200MB

* 上传成功触发 `FileUploaded` 事件 → MainWindow 刷新素材库

### 5.8 MainWindow.xaml/.cs —— 指挥中心

很薄：每个按钮的 Click 方法只有一两行，转交给对应服务。**它是所有模块的 "接线板"**，理解程序怎么运转，看 `MainWindow()` 构造函数：



1. 创建 `FileService` + `UploadServer`（启动上传，底部显示局域网地址）

2. 新建 `TimelineProject`，绑给 `WaveformControl`（`Wave.SetProject(_project)`）

3. 新建 `UndoService(_project)`（绑定时间轴）

4. 订阅各服务事件：拖完块→拍快照、上传完成→刷新素材库、播放位置变化→重绘、播放结束→按钮恢复

**所有编辑操作的固定套路**（看 `OnScissorsClick` / `OnDeleteClipClick` / `OnFadeInClick` 就懂）：



```
\_undo?.CaptureSnapshot();   // 1. 操作前拍快照

\_project.SplitClipAt(...);  // 2. 改时间轴数据（或改选中块的属性）

UpdateButtonStates();       // 3. 刷新按钮可用状态
```

**音量滑块的设计**（值得注意）：拖动时只改旁边的文字显示，**松手才真正应用**（`OnVolumeDragCompleted`）。一次拖动只产生一次修改，不会拖一下记几十次。增量计算：当前值 - 上次应用值。

快捷键在 `OnPreviewKeyDown`：Ctrl+Z / Ctrl+Y / Ctrl+A / Delete / 空格。

### 5.9 旧模型（已不在主流程，保留中）

这四个文件是早期 "波形编辑模型" 的遗留，**当前主流程（MainWindow）已经不引用它们**，保留它们是为了当 C# 教材素材和备用实现：



* `Models.cs`：`AudioDocument`（全部样本在内存里的列表）+ `AudioSelection`（选区）。曾经是唯一的数据中心，README 早期版本讲的 "所有编辑最终都是改 Samples 列表" 就指它

* `EditService.cs`：**命令模式**编辑引擎。每个编辑操作 = 一个命令对象（改哪段 + 旧数据 + 新数据），执行 = 换、撤销 = 换回。`IEditCommand` 接口、`ReplaceSegmentsCommand` 通用命令值得一读 —— 这是理解 "命令模式" 的最佳教材

* `AudioPeaks.cs`：波形峰值缓存（每 256 样本合成一个最大 / 最小值对）。它的 "预压缩再聚合" 思路已经被新模型继承（WaveformControl 的 `_peakCache`）

* `LibraryWindow.xaml/.cs`：独立的素材库窗口。已被主窗口左侧的内置素材库面板取代

> 维护提醒：
>
> **不要在新功能里引用旧模型**
>
> 。加功能一律走 TimelineProject + UndoService 的新套路（见 6.6）。旧文件如果确认没用了，可以删除（删除前先全局搜一下有没有引用）。



***

## 六、维护手册（怎么改）

### 6.1 改素材库位置

`MainWindow.xaml.cs` 顶部：



```
private const string LibraryDir = @"D:\视频创作\读书博主\音频素材库";
```

改成你想要的路径即可。`@` 前缀表示 "字符串里的反斜杠不用转义"。

### 6.2 改上传端口

`UploadServer.cs` 的 `Start()` 方法：`for (int p = 8124; p <= 8143; p++)`。把 8124 改成别的端口（比如 9000）即可；手机端访问地址会自动用新端口。

### 6.3 改淡入淡出时长

按钮给的时长在 `MainWindow.xaml.cs` 的 `OnFadeInClick` / `OnFadeOutClick`：



```
if (clip.IsSelected) clip.FadeInSec = 0.5;   // 0.5 秒 = 500ms
```

改成 `0.2` 就是 200ms。真正 "怎么渐变" 的渲染逻辑在 `AudioEngine.AppendClipSamples()`（按 `clip.FadeInSec` 换算成样本数做线性渐变）。

### 6.4 改导出码率 / 采样率

`AudioEngine.cs` 的 `ExportM4a`：



* 码率：`MediaFoundationEncoder.EncodeToAac(..., 128000)` 改数值

* 采样率：`new WdlResamplingSampleProvider(source, 48000)` 改 48000

### 6.5 加一个新按钮（完整步骤）

以加一个 "跳到开头" 按钮为例：



1. **改 XAML**（`MainWindow.xaml`）：在工具条里加一行



```
\<Button Content="回到开头" Click="OnJumpToStartClick" Margin="0,0,6,0" Padding="12,5" />
```



1. **改逻辑**（`MainWindow.xaml.cs`）：加一个方法



```
private void OnJumpToStartClick(object sender, RoutedEventArgs e)

{

&#x20;   if (\_project == null) return;

&#x20;   \_project.PlayheadSec = 0;

&#x20;   Wave.Refresh();

&#x20;   UpdateButtonStates();

}
```



1. 编译运行（F5 或 build.ps1）

### 6.6 加一个新的编辑功能（快照模式套路）

以 "把选中的块静音（音量设成 -100dB）" 为例，在 `MainWindow.xaml.cs` 加一个方法：



```
private void OnMuteClipClick(object sender, RoutedEventArgs e)

{

&#x20;   if (\_project == null || \_project.SelectedCount == 0) return;

&#x20;   \_undo?.CaptureSnapshot();                 // 1. 操作前拍快照

&#x20;   foreach (var clip in \_project.Clips)       // 2. 直接改选中块的属性

&#x20;   {

&#x20;       if (clip.IsSelected) clip.VolumeDb = -100;

&#x20;   }

&#x20;   Wave.Refresh();                            // 3. 刷新界面

}
```

然后在 XAML 加按钮调它（见 6.5）。**撤销 / 重做自动生效**，不用改任何别处 —— 这就是快照模式的好处：只要遵守 "操作前 CaptureSnapshot"，新功能自动获得撤销。

**规律总结**：改时间轴的 "结构"（增删 / 切开 / 移动 / 裁切）→ 走 `TimelineProject` 的方法 + 快照；改块的 "属性"（音量 / 淡入淡出 / 选择）→ 直接改 `clip.XXX` + 快照。两者都以 `CaptureSnapshot()` 开头。

### 6.7 常见问题排查



| 现象          | 原因 / 处理                                                                                                        |
| ----------- | -------------------------------------------------------------------------------------------------------------- |
| 导入文件报错      | 文件损坏或不是音频；确认是 m4a/mp3/wav                                                                                      |
| 手机打不开上传页面   | 手机和电脑不在同一 WiFi；或防火墙拦截（首次运行时 Windows 会弹防火墙提示，选 "允许"）                                                            |
| 上传页面能开，上传失败 | 看页面提示；确认电脑端软件还在运行                                                                                              |
| 导出失败        | 确认目标文件夹有写权限；检查磁盘空间                                                                                             |
| 导出出来的还是原素材  | 旧版 bug：插入素材会落在 "播放线位置"，第二次拖入的完整素材叠在剪辑块上，导出时后放置的块覆盖了剪辑结果。新版已改为**固定插到时间轴末尾**；若仍出现，检查时间轴上是否有两块叠在一起（重叠时后放置的覆盖先放置的） |
| 播放没声音       | 音量滑块是不是拖到 -20dB 了；块是不是被淡入淡出切没了                                                                                 |
| 开头留了空白，按播放没反应 | 已修（`AudioEngine.ComputeStartSample`）：播放线停在第一块前面的空地里时，起点会算成负数，取样本越界，声音出不来。现在会自动从第一块开头开始播（和导出行为一致 —— 导出同样不保留前导空白） |
| 界面文字模糊      | 说明 exe 没带清单（重新用 build.ps1 打包）                                                                                  |
| 找不到 dotnet  | 按第三节检查 SDK 是否安装                                                                                                |

### 6.8 改界面配色

`App.xaml` 顶部的 `<Application.Resources>` 里定义了所有颜色画刷（背景、面板、边框、文字、金色强调、波形蓝、播放线红）。改一处，全程序生效。波形控件里画块 / 播放线用的颜色在 `WaveformControl.OnRender()` 里（选中 = 金色 `#D4AF37`，未选中 = 蓝色 `#3A6BA5`）。



***

## 七、打包与发布

### 7.1 一键打包

在 `工作台` 文件夹打开 PowerShell，执行：



```
.\build.ps1
```

产物：`工作台\发布\LiteCut.exe`（单文件，双击即用）。

### 7.2 打包进阶：瘦版（1\~2MB，仅本机跑）

当前默认打的是 **self-contained**（约 140MB，运行时内嵌，拷到哪台机器都能跑）。

如果你只想在自己这台用、想把它压到 1\~2MB，把 `build.ps1` 里的 `--self-contained true` 改成 `--self-contained false` 再打包 —— 但这要求那台机器上装过 .NET 8 桌面运行时，否则双击会报 "You must install .NET Desktop Runtime"。

### 7.3 日常更新流程

改代码 → 跑 `build.ps1` → 把新的 `LiteCut.exe` 拷到常用位置（或直接覆盖旧的）。

**注意 `build.ps1` 的产物是 `发布\LiteCut.exe`，而实际在用的是 `发布\工作台\LiteCut.exe`** ——
所以覆盖的时候要认准"工作台"那一份，否则会出现两个 exe、双击到的还是旧的。

想一步到位（直接落到在用的位置），命令行跑：

```powershell
dotnet publish 工作台\工作台.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "发布\工作台"
```

**覆盖 exe 之前先确认程序没在跑**（文件被占用会写失败；更别去强杀用户正在剪的窗口）：

```powershell
tasklist /FI "IMAGENAME eq LiteCut.exe"
```

改完如果动的是音频那条链路，建议顺手跑一次回归验证（退出码 0 = 全通过，见 0.9）：

```powershell
cd 工具\音频探针
dotnet run -c Release
```



***

## 附：技术栈一览



| 项       | 选择                             | 为什么                    |
| ------- | ------------------------------ | ---------------------- |
| 语言 / 框架 | C# + WPF (.NET 8)              | Windows 原生、单文件小、你要学 C# |
| 音频库     | NAudio 2.2.1                   | 播放 / 解码 / 重采样 / 编码全都有  |
| m4a 解码  | Windows Media Foundation（系统自带） | 不装任何解码器                |
| m4a 编码  | Media Foundation AAC           | 系统自带，输出 48kHz          |
| 局域网传输   | 自写极简 HTTP（TcpListener）         | 免管理员权限、代码可控            |
| 打包      | dotnet publish 单文件             | 一个 exe 走天下             |