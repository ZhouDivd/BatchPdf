<#
.SYNOPSIS
  BatchPdf 无头批量打印驱动脚本 (AutoCAD accoreconsole.exe，无需打开 CAD 界面)

.DESCRIPTION
  对一个或多个 DWG 文件 / 文件夹逐个启动 accoreconsole.exe（只读打开），
  加载 BatchPdf 插件并执行 BPDFBATCH：扫描图框 -> 匹配纸张 -> 打印 PDF -> 非标裁剪 -> (可选)合并。
  打印参数默认沿用 CAD 界面里保存的设置 (%APPDATA%\BatchPdf\settings.json)，可用参数临时覆盖。
  结束后输出汇总 CSV 报告与失败清单。

.EXAMPLE
  .\BatchPdfHeadless.ps1 "D:\项目\施工图"                         # 整个文件夹
  .\BatchPdfHeadless.ps1 "D:\项目\施工图" -Recurse -OutputDir "D:\PDF输出" -OutputMode merged
  .\BatchPdfHeadless.ps1 a.dwg b.dwg -Mode scan                   # 只扫描识别，不出 PDF（调试用）
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Paths,

    [switch]$Recurse,

    # 输出目录（不填 = 沿用界面设置：默认保存在 DWG 同目录）
    [string]$OutputDir,

    # single = 每张图一个 PDF；merged = 每个 DWG 合并为一个多页 PDF（不填 = 沿用界面设置）
    [ValidateSet('single', 'merged')]
    [string]$OutputMode = '',

    # plot = 打印；scan = 只扫描并报告（不出 PDF）
    [ValidateSet('plot', 'scan')]
    [string]$Mode = 'plot',

    # 非标图框自动裁白边：true / false（不填 = 沿用界面设置）
    [ValidateSet('true', 'false')]
    [string]$AutoCrop = '',

    # 裁剪留白(mm)，负数 = 沿用界面设置
    [double]$CropMarginMm = -1,

    # 打印样式表，如 monochrome.ctb（不填 = 沿用界面设置）
    [string]$PlotStyle,

    # AutoCAD 安装目录（不填 = 自动查找）
    [string]$AcadDir,

    # 插件 DLL（不填 = 脚本同目录下的 BatchPdf.Acad2022.dll / BatchPdf.Acad2020.dll）
    [string]$PluginDll,

    # 单个 DWG 超时（分钟）
    [int]$TimeoutMinutes = 20,

    # 保留临时工作目录（脚本/任务/控制台日志），调试用
    [switch]$KeepWork,

    # 结束后不暂停（被其它脚本调用时使用）
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

function Write-Info([string]$msg, [string]$color = 'Gray') { Write-Host $msg -ForegroundColor $color }

function Exit-Script([int]$code) {
    if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') {
        Write-Host ''
        Write-Host '按任意键退出...' -ForegroundColor DarkGray
        try { [void][Console]::ReadKey($true) } catch {}
    }
    exit $code
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------- 1. 查找已安装的 AutoCAD (accoreconsole.exe) ----------
function Find-AcadDirFast {
    param([string]$preferredVersion)

    $candidates = New-Object System.Collections.Generic.List[string]

    # a. Running processes
    Get-Process acad, accoreconsole -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Path) { $candidates.Add((Split-Path -Parent $_.Path)) }
    }

    # b. Registry HKLM / HKCU
    foreach ($hive in @('HKLM:\SOFTWARE\Autodesk\AutoCAD', 'HKCU:\SOFTWARE\Autodesk\AutoCAD')) {
        if (-not (Test-Path $hive)) { continue }
        Get-ChildItem $hive -ErrorAction SilentlyContinue | ForEach-Object {
            $relKey = $_.PSPath
            Get-ChildItem $relKey -ErrorAction SilentlyContinue | ForEach-Object {
                $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                if ($p) {
                    if ($p.AcadLocation) { $candidates.Add($p.AcadLocation) }
                    if ($p.Location) { $candidates.Add($p.Location) }
                    if ($p.'(default)') { $candidates.Add($p.'(default)') }
                }
            }
        }
    }

    # c. Common standard paths on C: and D:
    $years = @('2024', '2023', '2022', '2021', '2020', '2019', '2018', '2025', '2026')
    foreach ($drive in @('C:', 'D:')) {
        foreach ($y in $years) {
            $candidates.Add("$drive\Program Files\Autodesk\AutoCAD $y")
            $candidates.Add("$drive\Autodesk\AutoCAD $y")
            $candidates.Add("$drive\AutoCAD $y")
            $candidates.Add("$drive\AutoCAD$y")
        }
    }

    $valid = New-Object System.Collections.Generic.List[object]
    $seen = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($dir in $candidates) {
        if (-not $dir) { continue }
        $clean = $dir.Trim().TrimEnd('\', '/')
        if (-not $seen.Add($clean)) { continue }
        $exe = Join-Path $clean 'accoreconsole.exe'
        if (Test-Path -LiteralPath $exe) {
            $is2020 = ($clean -match '2020|2019')
            try {
                $vi = (Get-Item -LiteralPath $exe).VersionInfo
                if ($vi.ProductMajorPart -eq 23 -or $vi.FileMajorPart -eq 23) { $is2020 = $true }
                if ($vi.ProductMajorPart -ge 24 -or $vi.FileMajorPart -ge 24) { $is2020 = $false }
            } catch {}
            $valid.Add([PSCustomObject]@{
                Path   = $clean
                Exe    = $exe
                Is2020 = $is2020
            })
        }
    }

    if ($valid.Count -eq 0) { return $null }

    if ($preferredVersion -eq '2020') {
        $m = $valid | Where-Object { $_.Is2020 } | Select-Object -First 1
        if ($m) { return $m }
    } elseif ($preferredVersion -eq '2022') {
        $m = $valid | Where-Object { -not $_.Is2020 } | Select-Object -First 1
        if ($m) { return $m }
    }

    return $valid[0]
}

$detectedAcad = $null
if ($AcadDir) {
    $cleanAcadDir = $AcadDir.Trim().TrimEnd('\', '/')
    $testExe = Join-Path $cleanAcadDir 'accoreconsole.exe'
    if (Test-Path -LiteralPath $testExe) {
        $is2020 = ($cleanAcadDir -match '2020|2019')
        try {
            $vi = (Get-Item -LiteralPath $testExe).VersionInfo
            if ($vi.ProductMajorPart -eq 23 -or $vi.FileMajorPart -eq 23) { $is2020 = $true }
            if ($vi.ProductMajorPart -ge 24 -or $vi.FileMajorPart -ge 24) { $is2020 = $false }
        } catch {}
        $detectedAcad = [PSCustomObject]@{
            Path   = $cleanAcadDir
            Exe    = $testExe
            Is2020 = $is2020
        }
    } else {
        Write-Info ("[警告] 指定的 AutoCAD 目录中未找到 accoreconsole.exe: {0}" -f $AcadDir) Yellow
    }
}

if (-not $detectedAcad) {
    $pref = if ($PluginDll -and ($PluginDll -match '2020')) { '2020' } elseif ($PluginDll -and ($PluginDll -match '2022')) { '2022' } else { '' }
    $detectedAcad = Find-AcadDirFast -preferredVersion $pref
}

if (-not $detectedAcad) {
    Write-Info "[错误] 未找到 accoreconsole.exe，请用 -AcadDir 指定 AutoCAD 安装目录（例如 'D:\AutoCAD 2020' 或 'C:\Program Files\Autodesk\AutoCAD 2020'）。" Red
    Exit-Script 3
}

$AcadDir = $detectedAcad.Path
$accore = $detectedAcad.Exe
$isAcad2020Build = $detectedAcad.Is2020

# ---------- 2. 匹配插件 DLL (BatchPdf.Acad2020.dll / BatchPdf.Acad2022.dll) ----------
function Find-MatchingPluginDll([bool]$is2020) {
    $targetName = if ($is2020) { 'BatchPdf.Acad2020.dll' } else { 'BatchPdf.Acad2022.dll' }
    $subDirName = if ($is2020) { '2020' } else { '2022' }

    $candidates = @(
        (Join-Path $scriptDir "Contents\$subDirName\$targetName"),
        (Join-Path $scriptDir "$subDirName\$targetName"),
        (Join-Path $scriptDir $targetName),
        "$env:ProgramData\Autodesk\ApplicationPlugins\BatchPdf.bundle\Contents\$subDirName\$targetName",
        (Join-Path $scriptDir "..\..\bin\Release\Acad$subDirName\$targetName"),
        (Join-Path $scriptDir "..\..\bin\Debug\Acad$subDirName\$targetName")
    )

    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path }
    }
    return $null
}

if (-not $PluginDll -or -not (Test-Path -LiteralPath $PluginDll)) {
    $PluginDll = Find-MatchingPluginDll -is2020 $isAcad2020Build
    if (-not $PluginDll) {
        $PluginDll = Find-MatchingPluginDll -is2020 (-not $isAcad2020Build)
    }
}

if (-not $PluginDll -or -not (Test-Path -LiteralPath $PluginDll)) {
    Write-Info ("[错误] 未找到与当前 AutoCAD 相匹配的插件 DLL (需要 {0})，请确认插件完整性或用 -PluginDll 指定。" -f $(if ($isAcad2020Build) { 'BatchPdf.Acad2020.dll' } else { 'BatchPdf.Acad2022.dll' })) Red
    Exit-Script 2
}
$PluginDll = (Resolve-Path -LiteralPath $PluginDll).Path

# ---------- 3. 收集 DWG ----------
if (-not $Paths -or $Paths.Count -eq 0) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $topForm = New-Object System.Windows.Forms.Form
        $topForm.TopMost = $true
        $topForm.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen

        $openFile = New-Object System.Windows.Forms.OpenFileDialog
        $openFile.Filter = "AutoCAD 图纸 (*.dwg)|*.dwg|所有文件 (*.*)|*.*"
        $openFile.Multiselect = $true
        $openFile.Title = "选择要批量打印的 DWG 图纸 (可按住 Ctrl/Shift 多选；若取消则选择文件夹)"
        if ($openFile.ShowDialog($topForm) -eq [System.Windows.Forms.DialogResult]::OK -and $openFile.FileNames.Count -gt 0) {
            $Paths = $openFile.FileNames
        } else {
            $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
            $dlg.Description = '选择要批量打印的 DWG 所在文件夹'
            if ($dlg.ShowDialog($topForm) -eq [System.Windows.Forms.DialogResult]::OK) {
                $Paths = @($dlg.SelectedPath)
            }
        }
        $topForm.Dispose()
    } catch {}
}
if (-not $Paths -or $Paths.Count -eq 0) {
    Write-Info '[提示] 未选择任何 DWG 文件或文件夹。您也可以将 DWG 文件或文件夹直接拖拽到桌面快捷方式上。' Yellow
    Exit-Script 1
}

$dwgs = New-Object System.Collections.Generic.List[string]
foreach ($p in $Paths) {
    if ([string]::IsNullOrWhiteSpace($p)) { continue }
    $p = $p.Trim().Trim('"')
    if (Test-Path -LiteralPath $p -PathType Container) {
        $items = if ($Recurse) { Get-ChildItem -LiteralPath $p -Filter *.dwg -File -Recurse } else { Get-ChildItem -LiteralPath $p -Filter *.dwg -File }
        foreach ($f in ($items | Sort-Object FullName)) { $dwgs.Add($f.FullName) }
    } elseif ((Test-Path -LiteralPath $p -PathType Leaf) -and $p.ToLower().EndsWith('.dwg')) {
        $dwgs.Add((Resolve-Path -LiteralPath $p).Path)
    } else {
        Write-Info "[跳过] 不是 DWG 文件或文件夹: $p" Yellow
    }
}
# 去重 + 排除 CAD 自动备份/临时文件
$dwgs = @($dwgs | Where-Object { $_ -notmatch '\\~\$' -and $_ -notmatch '_recover\.dwg$' } | Select-Object -Unique)
if ($dwgs.Count -eq 0) {
    Write-Info '[提示] 没有找到任何 DWG 文件。' Yellow
    Exit-Script 1
}

# ---------- 4. 工作目录 ----------
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$work = Join-Path $env:TEMP "BatchPdfHeadless\$stamp"
New-Item -ItemType Directory -Force -Path $work | Out-Null
if ($OutputDir) {
    $OutputDir = [IO.Path]::GetFullPath($OutputDir)
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

Write-Info '================ BatchPdf 无头批量打印 ================' Cyan
Write-Info ("AutoCAD 内核 : {0}" -f $accore)
Write-Info ("插件         : {0}" -f $PluginDll)
Write-Info ("DWG 数量     : {0}" -f $dwgs.Count)
Write-Info ("模式         : {0}{1}" -f $Mode, $(if ($OutputMode) { " / $OutputMode" } else { ' / (沿用界面设置)' }))
Write-Info ("输出目录     : {0}" -f $(if ($OutputDir) { $OutputDir } else { '(沿用界面设置)' }))
Write-Info '======================================================' Cyan

$dllForLisp = $PluginDll.Replace('\', '/')
$ansi = [System.Text.Encoding]::Default
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$allRows = New-Object System.Collections.Generic.List[object]
$failList = New-Object System.Collections.Generic.List[string]
$totalOk = 0; $totalFail = 0; $totalSkip = 0; $totalNeed = 0
$sw = [Diagnostics.Stopwatch]::StartNew()

for ($i = 0; $i -lt $dwgs.Count; $i++) {
    $dwg = $dwgs[$i]
    $n = $i + 1
    $tag = '{0:D3}' -f $n
    $jobFile = Join-Path $work "job_$tag.json"
    $resFile = Join-Path $work "result_$tag.json"
    $scrFile = Join-Path $work "run_$tag.scr"
    $logFile = Join-Path $work "console_$tag.log"

    $job = [ordered]@{
        Mode       = $Mode
        DwgPath    = $dwg
        ResultPath = $resFile
    }
    if ($OutputDir) { $job.OutputDirectory = $OutputDir }
    if ($OutputMode) { $job.OutputMode = $OutputMode }
    if ($AutoCrop) { $job.AutoCrop = ($AutoCrop -eq 'true') }
    if ($CropMarginMm -ge 0) { $job.CropMarginMm = $CropMarginMm }
    if ($PlotStyle) { $job.PlotStyle = $PlotStyle }
    [IO.File]::WriteAllText($jobFile, ($job | ConvertTo-Json -Depth 4), $utf8NoBom)

    $scr = @(
        '(setq bpdf_sl (getvar "SECURELOAD"))',
        '(setvar "SECURELOAD" 0)',
        ('(command "_.NETLOAD" "{0}")' -f $dllForLisp),
        '(setvar "SECURELOAD" bpdf_sl)',
        'BPDFBATCH',
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($scrFile, $scr, $ansi)

    Write-Host ("[{0}/{1}] {2}" -f $n, $dwgs.Count, $dwg) -ForegroundColor White
    $t0 = $sw.Elapsed.TotalSeconds

    $env:BPDF_JOB = $jobFile
    $argLine = ('/i "{0}" /s "{1}" /readonly' -f $dwg, $scrFile)
    $timedOut = $false
    try {
        $proc = Start-Process -FilePath $accore -ArgumentList $argLine -NoNewWindow -PassThru `
            -RedirectStandardOutput $logFile -RedirectStandardError ($logFile + '.err')
        if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
            $timedOut = $true
            try { $proc.Kill() } catch {}
        }
    } catch {
        Write-Info ("    [错误] 启动 accoreconsole 失败: {0}" -f $_.Exception.Message) Red
    } finally {
        Remove-Item Env:\BPDF_JOB -ErrorAction SilentlyContinue
    }
    $secs = [math]::Round($sw.Elapsed.TotalSeconds - $t0, 1)

    $res = $null
    if (Test-Path $resFile) {
        try { $res = [IO.File]::ReadAllText($resFile, [Text.Encoding]::UTF8) | ConvertFrom-Json } catch {}
    }

    if ($timedOut) {
        $failList.Add("$dwg`t超时(>${TimeoutMinutes}分钟)，已强制结束")
        Write-Info ("    [超时] 超过 {0} 分钟，已强制结束 ({1}s)" -f $TimeoutMinutes, $secs) Red
        continue
    }
    if (-not $res) {
        $failList.Add("$dwg`t未生成结果文件（插件未加载或 accoreconsole 异常），见日志: $logFile")
        Write-Info ("    [失败] 未生成结果文件，控制台日志: {0}" -f $logFile) Red
        $KeepWork = $true
        continue
    }

    $totalOk += [int]$res.Succeeded; $totalFail += [int]$res.Failed; $totalSkip += [int]$res.Skipped; $totalNeed += [int]$res.NeedsConfirmationCount
    foreach ($s in $res.Sheets) {
        $allRows.Add([pscustomobject][ordered]@{
            'DWG序号'  = $n
            'DWG文件'  = $dwg
            '图框序号' = $s.Index
            '图号'     = $s.DrawingNumber
            '图名'     = $s.DrawingName
            '比例'     = $s.Scale
            '纸张'     = $s.Paper
            '纸张尺寸' = ('{0}x{1}' -f $s.PaperWidthMm, $s.PaperHeightMm)
            '非标'     = $(if ($s.NonStandard) { '是' } else { '' })
            '状态'     = $s.Status
            '输出文件' = $s.OutputFile
            '错误'     = $s.Error
            '提示'     = (($s.Warnings | Where-Object { $_ }) -join '; ')
        })
    }

    $line = if ($Mode -eq 'scan') {
        "    识别图框 {0} | 可打印 {1} | 需确认 {2} ({3}s)" -f $res.FrameCount, $res.ReadyCount, $res.NeedsConfirmationCount, $secs
    } else {
        "    图框 {0} | 成功 {1} | 失败 {2} | 跳过 {3} | 需确认 {4} ({5}s)" -f $res.FrameCount, $res.Succeeded, $res.Failed, $res.Skipped, $res.NeedsConfirmationCount, $secs
    }
    $color = if ($res.Error -or [int]$res.Failed -gt 0) { 'Yellow' } else { 'Green' }
    Write-Info $line $color
    if ($res.MergedPdf) { Write-Info ("    合并 PDF: {0}" -f $res.MergedPdf) DarkCyan }
    if ($res.Error) {
        Write-Info ("    提示: {0}" -f $res.Error) Yellow
        $failList.Add("$dwg`t$($res.Error)")
    }
    foreach ($s in ($res.Sheets | Where-Object { $_.Status -eq 'Failed' })) {
        $failList.Add("$dwg`t图框#$($s.Index) $($s.DrawingNumber) $($s.DrawingName): $($s.Error)")
    }
}

# ---------- 5. 汇总报告 ----------
$reportDir = if ($OutputDir) { $OutputDir } else { Split-Path -Parent $dwgs[0] }
$csvPath = Join-Path $reportDir "BatchPdf_无头打印报告_$stamp.csv"
try {
    if ($allRows.Count -gt 0) {
        $allRows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    } else {
        [IO.File]::WriteAllText($csvPath, "无结果`r`n", (New-Object System.Text.UTF8Encoding($true)))
    }
} catch {
    $csvPath = Join-Path $work "BatchPdf_无头打印报告_$stamp.csv"
    $allRows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
}
$failPath = $null
if ($failList.Count -gt 0) {
    $failPath = [IO.Path]::ChangeExtension($csvPath, $null).TrimEnd('.') + '_失败清单.txt'
    [IO.File]::WriteAllLines($failPath, $failList, (New-Object System.Text.UTF8Encoding($true)))
}

Write-Info '======================================================' Cyan
if ($Mode -eq 'scan') {
    Write-Info ("扫描完成：DWG {0} 个，用时 {1:N1}s，需确认图框 {2} 个" -f $dwgs.Count, $sw.Elapsed.TotalSeconds, $totalNeed) Green
} else {
    Write-Info ("全部完成：DWG {0} 个 | PDF 成功 {1} | 失败 {2} | 跳过 {3} | 需确认(未打印) {4} | 用时 {5:N1}s" -f `
        $dwgs.Count, $totalOk, $totalFail, $totalSkip, $totalNeed, $sw.Elapsed.TotalSeconds) $(if ($failList.Count -gt 0) { 'Yellow' } else { 'Green' })
}
Write-Info ("汇总报告: {0}" -f $csvPath)
if ($failPath) { Write-Info ("失败清单: {0}" -f $failPath) Yellow }

if ($KeepWork) {
    Write-Info ("工作目录(脚本/日志): {0}" -f $work) DarkGray
} else {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

Exit-Script $(if ($failList.Count -gt 0) { 10 } else { 0 })
