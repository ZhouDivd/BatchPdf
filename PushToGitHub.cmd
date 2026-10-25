@echo off
chcp 65001 >nul
title 正在推送 BatchPdf 到 GitHub
echo ========================================================
echo  准备将代码推送到 GitHub: https://github.com/ZhouDivd/BatchPdf
echo ========================================================
echo.
cd /d "%~dp0"
git push -u origin main
echo.
if %errorlevel% equ 0 (
    echo [成功] 全部代码已成功推送到 GitHub 仓库！
    echo 仓库主页: https://github.com/ZhouDivd/BatchPdf
) else (
    echo [提示] 推送遇到权限验证。请在弹出的 GitHub 授权窗口中点击确认，或使用 Personal Access Token。
)
echo.
pause
