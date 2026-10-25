@echo off
rem BatchPdf headless batch plot: drag DWG files or folders onto this file.
rem Double-click to choose a folder. Extra options: see BatchPdfHeadless.ps1 header.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BatchPdfHeadless.ps1" %*
