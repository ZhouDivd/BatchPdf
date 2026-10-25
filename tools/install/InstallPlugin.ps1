# CADBPLOT 插件安装/更新脚本 (官方 ApplicationPlugins 目录 + 注册表双重保障)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  CADBPLOT 插件部署到 AutoCAD 默认插件目录" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host ""

$runningAcad = Get-Process -Name "acad", "accoreconsole" -ErrorAction SilentlyContinue
if ($runningAcad) {
    Write-Host "[警告] 检测到 AutoCAD 正在运行中！" -ForegroundColor Yellow
    Write-Host "如果插件文件被占用，复制可能会失败。建议先关闭 AutoCAD 再运行本脚本。" -ForegroundColor Yellow
    Write-Host ""
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bundleSrc = Join-Path $repoRoot "bin\Release\BatchPdf.bundle"
$targetDir = "$env:ProgramData\Autodesk\ApplicationPlugins\BatchPdf.bundle"

if (!(Test-Path $bundleSrc)) {
    Write-Host "[错误] 未找到本地插件包: $bundleSrc" -ForegroundColor Red
    Write-Host "请先编译项目生成 DLL 后再试。" -ForegroundColor Red
    exit 1
}

# 1. 同步最新的编译产物到本地 bundle 目录
$acad2020Dll = Join-Path $repoRoot "bin\Release\Acad2020\BatchPdf.Acad2020.dll"
$acad2022Dll = Join-Path $repoRoot "bin\Release\Acad2022\BatchPdf.Acad2022.dll"

if (Test-Path $acad2020Dll) {
    Copy-Item $acad2020Dll (Join-Path $bundleSrc "Contents\2020\BatchPdf.Acad2020.dll") -Force
}
if (Test-Path $acad2022Dll) {
    Copy-Item $acad2022Dll (Join-Path $bundleSrc "Contents\2022\BatchPdf.Acad2022.dll") -Force
}

# 2. 部署到官方 ApplicationPlugins 目录
Write-Host "1/2 复制插件到官方目录: $targetDir ..." -ForegroundColor Gray
if (!(Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

try {
    Copy-Item "$bundleSrc\*" $targetDir -Recurse -Force -ErrorAction Stop
    Write-Host "    已完成文件同步复制。" -ForegroundColor Green
} catch {
    Write-Host "    [失败] 复制文件失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    请确保以管理员权限运行，或确保 AutoCAD 完全退出后再重试。" -ForegroundColor Yellow
}

# 3. 写入/更新注册表按需与开机自动加载 (HKCU，双保险)
Write-Host "2/2 写入 AutoCAD 注册表自动加载项 (双保险)..." -ForegroundColor Gray
$commands = @('BPDF', 'BPLOT', 'BPDFSCAN', 'BPDFTEST', 'BPDFSET', 'BPDFLOG', 'BPPREVIEW', 'BPTEACH', 'BPTABLE', 'BPDFBATCH')
$root = 'HKCU:\Software\Autodesk\AutoCAD'
$written = 0

if (Test-Path $root) {
    foreach ($rel in Get-ChildItem $root) { # R23.0, R24.1, etc.
        $is2020 = ($rel.PSChildName -like 'R23*')
        $dllPath = if ($is2020) {
            "$targetDir\Contents\2020\BatchPdf.Acad2020.dll"
        } else {
            "$targetDir\Contents\2022\BatchPdf.Acad2022.dll"
        }
        $appName = if ($is2020) { 'BatchPdf2020' } else { 'BatchPdf2022' }

        foreach ($prod in Get-ChildItem $rel.PSPath) {
            $appKey = "$($prod.PSPath)\Applications\$appName"
            New-Item -Path $appKey -Force | Out-Null
            Set-ItemProperty -Path $appKey -Name 'DESCRIPTION' -Value "BatchPdf for $($rel.PSChildName)" -Type String -Force | Out-Null
            Set-ItemProperty -Path $appKey -Name 'LOADCTRLS'   -Value 6 -Type DWord -Force | Out-Null
            Set-ItemProperty -Path $appKey -Name 'MANAGED'     -Value 1 -Type DWord -Force | Out-Null
            Set-ItemProperty -Path $appKey -Name 'LOADER'      -Value $dllPath -Type String -Force | Out-Null
            
            $cmdsKey = "$appKey\Commands"
            New-Item -Path $cmdsKey -Force | Out-Null
            foreach ($c in $commands) {
                Set-ItemProperty -Path $cmdsKey -Name $c -Value $c -Type String -Force | Out-Null
            }
            $written++
            Write-Host "    已注册到: $($prod.PSChildName) ($($rel.PSChildName))" -ForegroundColor Gray
        }
    }
}

Write-Host ""
Write-Host "[成功] 插件已就绪！" -ForegroundColor Green
Write-Host ""
Write-Host "生效状态:" -ForegroundColor White
Write-Host "  1. 插件已置于: $targetDir"
Write-Host "  2. 启动 AutoCAD 将自动静默加载本插件，无需任何手动 NETLOAD。"
Write-Host "  3. 顶部菜单栏自动显示「CAD批量打印(&B)」菜单。"
Write-Host "  4. 命令直接可用: BPDF / BPLOT / BPPREVIEW / BPTEACH / BPTABLE 等。"
Write-Host ""