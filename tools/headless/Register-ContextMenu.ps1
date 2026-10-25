$cmd = 'D:\01AIcreatevIDEO\CADBPLOT\tools\headless\BatchPlot_Headless.cmd'
$acadExe = 'C:\Program Files\Autodesk\AutoCAD 2022\acad.exe'

# 1. DWG files
$key1 = "HKCU:\Software\Classes\SystemFileAssociations\.dwg\shell\BatchPdfPlot"
New-Item -Path $key1 -Force | Out-Null
Set-ItemProperty -Path $key1 -Name "(Default)" -Value "使用 BatchPdf 批量打印 (免开CAD)"
if (Test-Path $acadExe) { Set-ItemProperty -Path $key1 -Name "Icon" -Value "$acadExe,0" }
$cmdKey1 = Join-Path $key1 "command"
New-Item -Path $cmdKey1 -Force | Out-Null
Set-ItemProperty -Path $cmdKey1 -Name "(Default)" -Value "`"$cmd`" `"%1`""

# 2. Directory
$key2 = "HKCU:\Software\Classes\Directory\shell\BatchPdfPlot"
New-Item -Path $key2 -Force | Out-Null
Set-ItemProperty -Path $key2 -Name "(Default)" -Value "使用 BatchPdf 批量打印文件夹 (免开CAD)"
if (Test-Path $acadExe) { Set-ItemProperty -Path $key2 -Name "Icon" -Value "$acadExe,0" }
$cmdKey2 = Join-Path $key2 "command"
New-Item -Path $cmdKey2 -Force | Out-Null
Set-ItemProperty -Path $cmdKey2 -Name "(Default)" -Value "`"$cmd`" `"%1`""

# 3. Directory Background
$key3 = "HKCU:\Software\Classes\Directory\Background\shell\BatchPdfPlot"
New-Item -Path $key3 -Force | Out-Null
Set-ItemProperty -Path $key3 -Name "(Default)" -Value "使用 BatchPdf 批量打印当前目录 (免开CAD)"
if (Test-Path $acadExe) { Set-ItemProperty -Path $key3 -Name "Icon" -Value "$acadExe,0" }
$cmdKey3 = Join-Path $key3 "command"
New-Item -Path $cmdKey3 -Force | Out-Null
Set-ItemProperty -Path $cmdKey3 -Name "(Default)" -Value "`"$cmd`" `"%V`""

Write-Host "Success: Context menu registered!" -ForegroundColor Green