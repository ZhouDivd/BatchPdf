@echo off
chcp 65001 >nul
title BatchPdf 无头极速批量打印 (无需打开CAD)
rem BatchPdf 无头批量打印：双击选择文件/文件夹，或直接将 DWG 文件/文件夹拖拽到此图标上
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BatchPdfHeadless.ps1" %*
