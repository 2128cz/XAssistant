@echo off
echo ===== 开始发布 XAssistant =====
dotnet publish -r win-x64 -c Release
if %errorlevel% equ 0 (
    echo ===== 发布成功！输出路径见上方构建日志（bin\Release\^<目标框架^>\win-x64\publish） =====
) else (
    echo ===== 发布失败，请检查错误 =====
)
pause