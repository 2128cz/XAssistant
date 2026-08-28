# XAssistant

XAssistant 是一款 Windows 桌面活动监控应用（WPF / C# / .NET 10），在后台记录你的鼠标点击、键盘按键和各应用使用时长，并通过托盘常驻提供统计界面。

> ⚠️ 本软件会在本地统计键盘鼠标操作数据。**所有数据都保存在本机**，不会上传到任何服务器，请放心使用。

## 功能

| 功能 | 说明 |
| --- | --- |
| 🖱️ 鼠标点击统计 | 通过低级钩子记录左 / 中 / 右键点击次数 |
| ⌨️ 键盘按键统计 | 记录按键次数，区分左右修饰键（Ctrl / Shift / Alt / Win） |
| 📊 应用使用时长 | 通过 WMI 进程事件追踪每个应用的使用时长（按天分桶） |
| 💻 电脑使用时长 | 记录电脑每天的开机 / 使用时长（需配合后台服务 XAssistant.Service） |
| 📝 灵感速记唤起（可选） | 全局热键唤起速记输入窗，直写速记数据库（需自建配套 xapp 后端，见下文） |

应用最小化到系统托盘常驻，关闭窗口是隐藏而非退出，右下角托盘菜单提供"退出"。

## 系统要求

- Windows 10 / 11（64 位）
- 构建源码需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 直接运行 Release 程序包**不需要**安装 .NET（已自包含打包）

## 目录结构

```
XAssistant/
├─ XAssistant.csproj            # 主程序（WPF）
├─ XAssistant.Service/          # 后台服务（Windows Service，负责电脑使用时长记录）
│  └─ publish-and-install.ps1   # 服务构建 + 安装脚本（需管理员）
├─ Services/                    # 钩子、数据库、配置等业务服务
├─ ViewModels/  Views/          # MVVM 页面
└─ Models/      Converters/     # 数据模型与转换器
```

## 构建

```bash
# 构建整个解决方案（主程序 + 后台服务）
dotnet build XAssistant.sln -c Release

# 发布主程序（自包含，win-x64）
dotnet publish XAssistant.csproj -r win-x64 -c Release --self-contained true

# 发布后台服务（自包含，win-x64），输出到 XAssistant.Service\publish
dotnet publish XAssistant.Service\XAssistant.Service.csproj -c Release --self-contained true -r win-x64 -o XAssistant.Service\publish
```

## 运行 / 安装

1. **（可跳过但仍建议）安装后台服务**：以管理员身份打开 PowerShell，在仓库 `XAssistant.Service` 目录执行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File publish-and-install.ps1
   ```

   脚本会自动发布、注册并启动名为 `XAssistant.UsageTracker` 的 Windows 服务，启动类型为"自动"。该服务记录每天的电脑使用时长，主程序通过命名管道实时读取。

2. **运行主程序**：双击 `XAssistant.exe`，或发布后直接解压即用。

## 数据存储位置

- 主程序数据：`%APPDATA%\XAssistant`（鼠标 / 键盘 / 应用使用时长数据库和配置）
- 后台服务数据：`C:\ProgramData\XAssistant\UsageTracker`（电脑使用时长数据库和日志）
- 日志：`%APPDATA%\XAssistant\logs\`，滚动保留 31 天

所有数据库均为本地 SQLite 文件，未启用任何云端上报。

## 可选集成：灵感速记

`Win+Numpad0` 全局热键唤起速记输入窗（可在设置中修改）。该功能会按 `appsettings.json` 中 `QuickNote.ConnectionString` 的 PostgreSQL 连接串直插速记表；未配置时尝试读取系统环境对应路径（默认 `C:\xapp-2026-06-30\.env`）下的 `DATABASE_URL`。**该后端属于另一个独立项目，默认不随本仓库提供**，不使用该功能时完全无影响。

## 许可证

本项目基于 [MIT 许可证](./LICENSE) 开源，欢迎使用、修改与二次开发。