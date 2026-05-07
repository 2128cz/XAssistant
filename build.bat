@echo off
echo ===== 开始发布 XAssistant =====
dotnet publish -r win-x64 -c Release
if %errorlevel% equ 0 (
    echo ===== 发布成功！输出在 .\publish 文件夹 =====
) else (
    echo ===== 发布失败，请检查错误 =====
)
pause