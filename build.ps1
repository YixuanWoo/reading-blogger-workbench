# ============================================================
# build.ps1 - 一键编译 + 打包单文件 exe
#
# 用法：右键"使用 PowerShell 运行"，或打开 PowerShell 后执行
#       .\build.ps1
# 产物：D:\工作台\工作台.exe（单文件，双击即用）
#       —— 即"程序文件夹"，与源码工程(D:\工作台工程\工作台)分离。
#
# 如果机器上装了 Visual Studio 的 .NET SDK（系统级），
# 直接把下面 $dotnet 改成 "dotnet" 即可。
# ============================================================

# --- 找到 dotnet（优先用用户目录里安装的） ---
$sdk = "$env:LOCALAPPDATA\Microsoft\dotnet"
$dotnet = "$sdk\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $system = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($system) { $dotnet = $system.Source } else {
        Write-Host "找不到 dotnet.exe，请先安装 .NET SDK 8（说明文档有步骤）" -ForegroundColor Red
        exit 1
    }
}

$proj = "工作台\工作台.csproj"
# 发布到"程序文件夹"(与源码工程分离)。注意：这是真实文件夹，不是旧 junction。
$outDir = "D:\工作台"

# --- 第 1 步：编译 ---
Write-Host "==> 编译中..." -ForegroundColor Cyan
& $dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败，请检查上面的错误信息" -ForegroundColor Red
    exit 1
}

# --- 第 2 步：打包单文件 exe ---
# --self-contained true  = 运行时内嵌（约 140MB，拷到任何 Windows 10/11 都能跑，双击即用）
# 想压到 1~2MB 的瘦版（仅本机、需装 .NET 8 运行时），把 true 改成 false
#
# 【顺手把调试符号挡在门外（T-12）】
#   .pdb / .xml 只在开发时有意义（IDE 悬停看注释），拷进"程序文件夹"纯占地：
#   以前每打一次包就吐一份 LiteCut.pdb / LiteCut.Core.pdb / LiteCut.Core.xml，
#   还得手动清。这三个开关让 publish 不再生成它们。
Write-Host "==> 打包单文件 exe..." -ForegroundColor Cyan
& $dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -p:GenerateDocumentationFile=false `
    -o $outDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "打包失败，请检查上面的错误信息" -ForegroundColor Red
    exit 1
}

# --- 第 3 步：把文件名改成中文名「工作台.exe」 ---
# 程序集名（csproj 的 <AssemblyName>）仍然是 LiteCut —— 那是代码里的标识，
# 动它会连带影响 WPF 资源基名等一堆东西；这里只是让"用户看到的文件名"是中文。
# 顺带说明：Windows 的图标缓存是按路径记的，改名 = 换个路径，
# 正好绕开旧缓存，新图标能立刻显示出来。
$built = Join-Path $outDir "LiteCut.exe"
$final = Join-Path $outDir "工作台.exe"

if (-not (Test-Path $built)) {
    Write-Host "没找到刚打出来的 LiteCut.exe，打包可能中途失败了" -ForegroundColor Red
    exit 1
}

# ★ 直接 Move 覆盖，不要"先删再移"。
# 以前先 Remove-Item 旧文件：删不掉就 exit 1，而新打出来的 LiteCut.exe 已经躺在软件夹里了
# → 用户会看到文件夹里同时有 LiteCut.exe 和 工作台.exe 两个，不知道该双击哪个
# （2026-10-03 真踩到过：旧 exe 删不动，新产物就以 LiteCut.exe 的名义留下了）。
# Move-Item -Force 自己会覆盖，一步到位，不存在中间状态。
try {
    Move-Item $built $final -Force -ErrorAction Stop
}
catch {
    # 覆盖失败几乎只有一个原因：软件还开着，旧 exe 被占用。
    # 这时新产物还在软件夹里，必须把它说明白，别让人对着两个 exe 发懵。
    Write-Host ""
    Write-Host "覆盖失败 —— 多半是软件还开着（$final 被占用）。" -ForegroundColor Red
    if (Test-Path $built) {
        Write-Host "★ 注意：$built 就是刚才打出来的新版，直接双击它也能用新版功能。" -ForegroundColor Yellow
        Write-Host "  想让文件名统一成「工作台.exe」，关掉软件后重新打包即可（这次的新产物会被覆盖掉）。" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host ""
Write-Host "==> 补种视觉规范（T-26①）..." -ForegroundColor Cyan
# 《视觉规范.md》：工作台每次组装任务包时读取 exe 旁边这份、注入给 Agent 的提示词。
# ★ 只在软件夹**没有**这份文件时才拷（补种出厂底稿）——绝不覆盖：
#   用户日常改标准就是改软件夹那份（改完即生效，不用重打包），
#   打包若无条件覆盖，会把他改过的标准静默冲回出厂版。
#   想重置成出厂底稿：删掉软件夹那份再打一次包。
$specSrc = Join-Path $PSScriptRoot "文档\视觉规范.md"
$specDst = Join-Path $outDir "视觉规范.md"
if (Test-Path $specDst) {
    Write-Host "软件夹已有视觉规范.md，保留用户版本不覆盖。" -ForegroundColor DarkGray
} elseif (Test-Path $specSrc) {
    Copy-Item $specSrc $specDst -Force
    Write-Host "已补种：$specDst" -ForegroundColor Green
} else {
    Write-Host "工程夹没找到 $specSrc，跳过（运行时走内置规则）。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "完成！产物：$final" -ForegroundColor Green
Write-Host "双击即可运行（程序文件夹：$outDir）。" -ForegroundColor Green
