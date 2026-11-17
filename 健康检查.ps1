# ============================================================
#  健康检查.ps1 - 工作台代码健康度一键体检（只读，不发布）
#
#  用法（在工程根 D:\工作台工程\工作台 下执行）：
#      .\健康检查.ps1              # 人看的摘要，退出码 0=全过 / 1=有失败
#      .\健康检查.ps1 -Json        # 只输出一段 JSON（给脚本/Agent 读）
#      .\健康检查.ps1 -LogPath C:\某个\地方.log
#
#  它做五件事：
#      ① 关键文件在位（工程能编、文档在、视觉规范单一真源在）
#      ② 编译出货目标（工作台\工作台.csproj，Release）：警告数 / 错误数
#      ③ 跑自检（工具\自检）：通过几项、失败几项，失败项逐条列出
#      ④ 跑数据目录隔离探针（工具\隔离验证 --probe）：口径与本次接线对不对
#      ⑤ 仓库状态只看不判（提示今天有哪些改动还没提交）
#
#  ★ 三条红线（改这个脚本之前先读）：
#      1. 只 build，绝不 publish —— build.ps1 会把产物写进 D:\工作台
#         （真实运行实例），体检不许碰那里。想发布请单独跑 .\build.ps1。
#      2. 不写真实数据：不读也不写 D:\工作台\项目\ 里的任何文件，
#         只对目录做存在性判断。
#      3. 已有检查只增不减：加检查可以，删检查或放宽判据不行。
#
#  退出码：0 = 全过（失败 0 项）；1 = 有失败项；2 = 环境问题（找不到 dotnet）
# ============================================================

[CmdletBinding()]
param(
    [switch]$Json,
    [string]$LogPath = ""
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$checks = New-Object System.Collections.ArrayList
$logLines = New-Object System.Collections.ArrayList

# 控制台按 UTF-8 解码子进程输出（dotnet 吐的是 UTF-8，按 936 解会变乱码，
# 中文路径和「0 个警告」这类统计行都会读不出来）。
# 同时把 CLI 语言钉成英文：统计行固定是 "0 Warning(s) / 0 Error(s)"，
# 不受系统语言影响，正则不会因为换了台机器就失配。
$prevConsoleEncoding = [Console]::OutputEncoding
$prevCliLang = $env:DOTNET_CLI_UI_LANGUAGE
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

function Add-Log([string]$text) {
    if ($null -ne $text) { [void]$logLines.Add($text) }
}

function Add-Check([string]$name, [bool]$ok, [string]$detail, [string]$level) {
    if ([string]::IsNullOrEmpty($level)) { $level = 'gate' }   # gate = 计入失败；info = 只提示
    [void]$checks.Add([pscustomobject]@{ name = $name; ok = $ok; detail = $detail; level = $level })
    # 提示项（info）永远不写「[败]」：它不计入失败、也不影响退出码。
    # 以前日志里统一写 [败]，导致「结论：通过」与日志里的 [败] 互相打脸（2026-11-17 CEO 退单指出）。
    $mark = if ($ok) { '[过]' } elseif ($level -eq 'gate') { '[败]' } else { '[注意]' }
    Add-Log (("{0} {1} | {2}" -f $mark, $name, $detail))
}

# 调用外部程序：拿真实退出码 + 完整输出，不让 stderr 把脚本带走
function Invoke-Native([string]$exe, [string[]]$arguments) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = & $exe @arguments 2>&1 | Out-String
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $old
    }
    return [pscustomobject]@{ code = $code; text = $text }
}

function Get-LastCount([string]$text, [string]$patternEn, [string]$patternZh) {
    $m = [regex]::Matches($text, $patternEn)
    if ($m.Count -gt 0) { return [int]$m[$m.Count - 1].Groups[1].Value }
    $m2 = [regex]::Matches($text, $patternZh)
    if ($m2.Count -gt 0) { return [int]$m2[$m2.Count - 1].Groups[1].Value }
    return -1
}

# ---------- 0. 找 dotnet（与 build.ps1 同一口径：用户目录优先） ----------
$sdk = "$env:LOCALAPPDATA\Microsoft\dotnet"
$dotnet = Join-Path $sdk 'dotnet.exe'
$dotnetSource = '用户目录 .NET SDK'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source; $dotnetSource = '系统 PATH 里的 .NET SDK' }
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    if ($Json) {
        Write-Output (@{ ok = $false; reason = '找不到 dotnet.exe（请先装 .NET SDK 8）' } | ConvertTo-Json -Depth 3)
    } else {
        Write-Host '找不到 dotnet.exe：请先安装 .NET SDK 8（见 文档\维护说明.md）' -ForegroundColor Red
    }
    exit 2
}

Add-Log ("健康检查开始 {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add-Log ("dotnet：{0}（来源：{1}）" -f $dotnet, $dotnetSource)
Add-Log ("工程根：{0}" -f $root)
Add-Log ''

# ---------- 1. 关键文件在位 ----------
$mustFiles = @(
    'README.md',
    'build.ps1',
    '健康检查.ps1',
    '工作台.sln',
    'LiteCut.Core\LiteCut.Core.csproj',
    '工作台\工作台.csproj',
    '文档\维护说明.md',
    '文档\更新日志.md',
    '文档\视觉规范.md',
    '工具\自检\SelfCheck.csproj',
    '工具\隔离验证\IsolationProbe.csproj',
    '工具\根目录守卫验证\RootGuardVerify.csproj'
)
$missing = @()
foreach ($rel in $mustFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $rel))) { $missing += $rel }
}
Add-Check '关键文件在位' ($missing.Count -eq 0) ($(if ($missing.Count -eq 0) { "$($mustFiles.Count) 个都在" } else { '缺：' + ($missing -join '、') }))

# 视觉规范单一真源：软件夹那份是用户改的生效版本；工程里那份是出厂底稿
$specLive = 'D:\工作台\视觉规范.md'
$specOk = Test-Path -LiteralPath $specLive
Add-Check '视觉规范单一真源在位（只读判断，不动它）' $specOk ($(if ($specOk) { $specLive } else { "没找到 $specLive（软件夹可能没装）" })) 'info'

# 真实数据目录存在性：只判断，绝不读内容
$dataDir = 'D:\工作台\项目'
$dataOk = Test-Path -LiteralPath $dataDir
Add-Check '真实数据目录在位（只做存在性判断）' $dataOk ($(if ($dataOk) { "$dataDir（本脚本不读不写）" } else { "没找到 $dataDir" })) 'info'

# ---------- 2. 编译出货目标 ----------
Add-Log ''
Add-Log '== 编译（工作台\工作台.csproj，Release）=='
$projPath = Join-Path $root '工作台\工作台.csproj'
$build = Invoke-Native $dotnet @('build', $projPath, '-c', 'Release', '--nologo')
Add-Log $build.text

$warnCount = Get-LastCount $build.text '(\d+)\s*Warning\(s\)' '(\d+)\s*个警告'
$errCount = Get-LastCount $build.text '(\d+)\s*Error\(s\)' '(\d+)\s*个错误'

Add-Check '编译：0 错误' (($errCount -eq 0) -and ($build.code -eq 0)) ("错误 {0} 个（退出码 {1}）" -f $errCount, $build.code)
Add-Check '编译：0 警告' ($warnCount -eq 0) ("警告 {0} 个" -f $warnCount)

# ---------- 3. 自检 ----------
Add-Log ''
Add-Log '== 自检（工具\自检，LiteCut.Core 纯逻辑）=='
$selfProj = Join-Path $root '工具\自检\SelfCheck.csproj'
$self = Invoke-Native $dotnet @('run', '--project', $selfProj, '-c', 'Release', '--nologo')
Add-Log $self.text

$passCount = -1
$failCount = -1
$m = [regex]::Match($self.text, '通过\s*(\d+)\s*项，失败\s*(\d+)\s*项')
if ($m.Success) {
    $passCount = [int]$m.Groups[1].Value
    $failCount = [int]$m.Groups[2].Value
}

Add-Check '自检：有结果输出' ($passCount -ge 0) ($(if ($passCount -ge 0) { "通过 $passCount 项" } else { '没看到「通过 N 项，失败 M 项」' }))
Add-Check '自检：失败 0 项' (($failCount -eq 0) -and ($self.code -eq 0)) ("失败 {0} 项（退出码 {1}）" -f $failCount, $self.code)

# 失败项逐条列出来，别只说「基本通过」
$failures = @()
foreach ($line in ($self.text -split "`r?`n")) {
    if ($line -match '^\s*\[败\]\s*(.+)$') { $failures += $Matches[1].Trim() }
}

# ---------- 3b. 数据目录隔离（LITECUT_DATA_DIR，T-4f1f38d8） ----------
# 只跑 --probe：纯口径 + 本次进程的接线断言，不写文件、不起界面（几十毫秒）。
# 真落盘那一半（--write）要设环境变量，属于手工验证，不进每次体检。
Add-Log ''
Add-Log '== 数据目录隔离（工具\隔离验证 --probe）=='
$probeProj = Join-Path $root '工具\隔离验证\IsolationProbe.csproj'
$probe = Invoke-Native $dotnet @('run', '--project', $probeProj, '-c', 'Release', '--', '--probe')
Add-Log $probe.text

$probePass = -1
$probeFail = -1
$pm = [regex]::Match($probe.text, '隔离验证结果：通过\s*(\d+)\s*项，失败\s*(\d+)\s*项')
if ($pm.Success) {
    $probePass = [int]$pm.Groups[1].Value
    $probeFail = [int]$pm.Groups[2].Value
}

Add-Check '数据目录隔离：探针有结果输出' ($probePass -ge 0) ($(if ($probePass -ge 0) { "通过 $probePass 项" } else { '没看到「隔离验证结果：通过 N 项，失败 M 项」' }))
Add-Check '数据目录隔离：探针失败 0 项' (($probeFail -eq 0) -and ($probe.code -eq 0)) ("失败 {0} 项（退出码 {1}）" -f $probeFail, $probe.code)

# ---------- 3c. 根目录守卫（改「项目根目录」的提示与取消语义，T-4c84816f） ----------
# 它**真的会写 config.json**（要断言"取消后文件哈希/mtime 没变"就必须有一份真文件）。
# 所以必须先把它钉到 %TEMP% 下的临时目录 —— 不设变量就等于写进用户的真实配置。
# 跑完只删自己建的那一个目录（路径已绑定，不许通配符）。
Add-Log ''
Add-Log '== 根目录守卫（工具\根目录守卫验证）=='
$guardProj = Join-Path $root '工具\根目录守卫验证\RootGuardVerify.csproj'
$guardDir = Join-Path $env:TEMP ("wb-rootguard-health-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$prevDataDir = $env:LITECUT_DATA_DIR
$env:LITECUT_DATA_DIR = $guardDir
try {
    $guard = Invoke-Native $dotnet @('run', '--project', $guardProj, '-c', 'Release', '--nologo')
} finally {
    if ($null -eq $prevDataDir) {
        Remove-Item Env:\LITECUT_DATA_DIR -ErrorAction SilentlyContinue
    } else {
        $env:LITECUT_DATA_DIR = $prevDataDir
    }
}
Add-Log $guard.text

$guardPass = -1
$guardFail = -1
$grd = [regex]::Match($guard.text, '根目录守卫验证结果：通过\s*(\d+)\s*项，失败\s*(\d+)\s*项')
if ($grd.Success) {
    $guardPass = [int]$grd.Groups[1].Value
    $guardFail = [int]$grd.Groups[2].Value
}

Add-Check '根目录守卫：有结果输出' ($guardPass -ge 0) ($(if ($guardPass -ge 0) { "通过 $guardPass 项" } else { '没看到「根目录守卫验证结果：通过 N 项，失败 M 项」' }))
Add-Check '根目录守卫：失败 0 项' (($guardFail -eq 0) -and ($guard.code -eq 0)) ("失败 {0} 项（退出码 {1}）" -f $guardFail, $guard.code)

# 清掉本次的临时数据目录（只删这一个已绑定的路径）
try {
    if (Test-Path -LiteralPath $guardDir) { Remove-Item -LiteralPath $guardDir -Recurse -Force }
} catch { }

# ---------- 4. 仓库状态（只看不判） ----------
Add-Log ''
Add-Log '== 仓库状态（只提示，不算失败）=='
$git = Invoke-Native 'git' @('-C', $root, 'status', '--porcelain')
$gitLines = @()
if ($git.code -eq 0) {
    foreach ($line in ($git.text -split "`r?`n")) {
        if ($line.Trim().Length -gt 0) { $gitLines += $line.Trim() }
    }
}
Add-Check '仓库工作区是否干净（提示项）' (($git.code -eq 0) -and ($gitLines.Count -eq 0)) ($(if ($git.code -ne 0) { 'git 不可用' } elseif ($gitLines.Count -eq 0) { '干净' } else { "$($gitLines.Count) 个文件有改动（提交前请确认是不是本次要交的）" })) 'info'

# ---------- 汇总 ----------
$gate = @($checks | Where-Object { $_.level -eq 'gate' })
$gateFail = @($gate | Where-Object { -not $_.ok })
$info = @($checks | Where-Object { $_.level -ne 'gate' })
$allOk = ($gateFail.Count -eq 0)

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:TEMP ("workbench-health-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
Add-Log ''
Add-Log ("结论：{0}（门禁 {1} 项，失败 {2} 项；自检通过 {3} 项、失败 {4} 项；隔离探针通过 {5} 项、失败 {6} 项；根目录守卫通过 {7} 项、失败 {8} 项）" -f $(if ($allOk) { '通过' } else { '不通过' }), $gate.Count, $gateFail.Count, $passCount, $failCount, $probePass, $probeFail, $guardPass, $guardFail)
Add-Log ("判据：门禁项（[过]/[败]）计入失败并决定退出码；提示项（[注意]）共 {0} 项，只提醒，不计入失败、不影响退出码。" -f $info.Count)
try {
    [System.IO.File]::WriteAllLines($LogPath, $logLines, (New-Object System.Text.UTF8Encoding($true)))
} catch { }

# 收尾：把控制台编码与 CLI 语言还原，别污染调用者
try { [Console]::OutputEncoding = $prevConsoleEncoding } catch { }
if ($null -eq $prevCliLang) { Remove-Item Env:\DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue } else { $env:DOTNET_CLI_UI_LANGUAGE = $prevCliLang }

if ($Json) {
    $result = [ordered]@{
        ok            = $allOk
        checkedAt     = (Get-Date).ToString('o')
        root          = $root
        dotnet        = $dotnet
        build         = [ordered]@{ exitCode = $build.code; warnings = $warnCount; errors = $errCount }
        selftest      = [ordered]@{ exitCode = $self.code; passed = $passCount; failed = $failCount; failures = $failures }
        isolation     = [ordered]@{ exitCode = $probe.code; passed = $probePass; failed = $probeFail }
        rootGuard     = [ordered]@{ exitCode = $guard.code; passed = $guardPass; failed = $guardFail }
        gates         = [ordered]@{ total = $gate.Count; failed = $gateFail.Count }
        hints         = [ordered]@{ total = $info.Count; appliesToExitCode = $false }
        checks        = @($checks)
        gitDirtyFiles = $gitLines
        logPath       = $LogPath
    }
    Write-Output ($result | ConvertTo-Json -Depth 6)
} else {
    Write-Host ''
    Write-Host '════════ 工作台健康检查 ════════' -ForegroundColor Cyan
    foreach ($c in $checks) {
        $mark = if ($c.ok) { '[过]' } else { if ($c.level -eq 'gate') { '[败]' } else { '[注意]' } }
        $color = if ($c.ok) { 'Green' } elseif ($c.level -eq 'gate') { 'Red' } else { 'Yellow' }
        Write-Host ("  {0} {1} — {2}" -f $mark, $c.name, $c.detail) -ForegroundColor $color
    }
    Write-Host ''
    if ($failures.Count -gt 0) {
        Write-Host '  自检失败项：' -ForegroundColor Red
        foreach ($f in $failures) { Write-Host ("    · {0}" -f $f) -ForegroundColor Red }
        Write-Host ''
    }
    Write-Host ("  编译：警告 {0} 个 / 错误 {1} 个｜自检：通过 {2} 项 / 失败 {3} 项｜隔离探针：通过 {4} 项 / 失败 {5} 项｜根目录守卫：通过 {6} 项 / 失败 {7} 项" -f $warnCount, $errCount, $passCount, $failCount, $probePass, $probeFail, $guardPass, $guardFail) -ForegroundColor Gray
    if ($allOk) {
        Write-Host ("  结论：通过（门禁 {0} 项全过；提示项 {1} 项只提醒、不计入失败）" -f $gate.Count, $info.Count) -ForegroundColor Green
    } else {
        Write-Host ("  结论：不通过（{0}/{1} 项失败；提示项 {2} 项不计入失败）" -f $gateFail.Count, $gate.Count, $info.Count) -ForegroundColor Red
    }
    Write-Host ("  日志：{0}" -f $LogPath) -ForegroundColor DarkGray
    Write-Host '════════════════════════════════' -ForegroundColor Cyan
}

if ($allOk) { exit 0 } else { exit 1 }
