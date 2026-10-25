# CADBPLOT 插件卸载脚本
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  从 AutoCAD 默认插件目录卸载 CADBPLOT" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host ""

$targetDir = "$env:ProgramData\Autodesk\ApplicationPlugins\BatchPdf.bundle"

if (!(Test-Path $targetDir)) {
    Write-Host "[提示] 默认插件目录不存在该插件，无需卸载: $targetDir" -ForegroundColor Yellow
    Write-Host ""
    exit 0
}

try {
    Remove-Item -Path $targetDir -Recurse -Force -ErrorAction Stop
    Write-Host "[成功] 插件已从 AutoCAD 默认插件目录完全卸载。" -ForegroundColor Green
} catch {
    Write-Host "[失败] 卸载失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "请确保 AutoCAD 已退出且没有文件被占用。" -ForegroundColor Yellow
}

Write-Host ""