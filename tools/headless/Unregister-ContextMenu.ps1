Remove-Item -Path "HKCU:\Software\Classes\SystemFileAssociations\.dwg\shell\BatchPdfPlot" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Classes\Directory\shell\BatchPdfPlot" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Classes\Directory\Background\shell\BatchPdfPlot" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Success: Context menu removed." -ForegroundColor Green