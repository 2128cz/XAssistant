# XAssistant

**XAssistant**（界面署名 **KeyScope**）是一款 Windows 桌面活动统计工具（WPF / C# / .NET 8），常驻系统托盘，记录键盘敲击、鼠标点击与移动、各应用使用时长及电脑使用时长。**所有数据仅保存在本机，不联网上传。**

![KeyScope 工作台 · 实时概览（深色主题）](docs/mainpic.png)

*顶部四张统计卡 → 输入节奏曲线 → 键盘热力图与按键排行：一页滚到底，不切页。*

## 说明

这是一个 **vibe coding** 产物：代码由 **0731 之前的 DeepSeek Flash 网页版**编写，由 **DeepSeek Flash 0731** 发布，作者本人对 C# / .NET 并不熟悉。项目仅在作者自己的机器上验证可用（Windows 10 专业版 22H2 build 19045，64 位），**不保证其它环境兼容**。不懂的地方可以直接问 AI，或者干脆让AI写一个更好。

## 安装

1. 从 Releases 下载两个压缩包：主程序 `XAssistant-win-x64-*.zip` 与后台服务 `XAssistant.Service-win-x64-*.zip`。
2. 解压到同一文件夹，保持目录结构：

   ```
   你的文件夹/
   ├─ XAssistant.exe
   └─ XAssistant.Service/
      ├─ XAssistant.Service.exe
      └─ install-service.ps1
   ```

3. 右键 `install-service.ps1` → **使用 PowerShell 运行**（会请求管理员授权），或在管理员 PowerShell 中执行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\XAssistant.Service\install-service.ps1
   ```

4. 双击 `XAssistant.exe` 即可。
5. 默认**不会**开机自启动。需要常驻统计的话，在界面**最底部**那一栏打开「**开机自启动**」开关，之后随系统登录自动运行。

> 不装后台服务也能用键盘 / 鼠标 / 应用时长等功能，仅"电脑使用时长"无法获取。

## 功能

主界面是一页可滚动的工作台，顶部导航（实时概览 / 习惯分析 / 设备管理）只做定位跳转。

| 区块 | 内容 |
| --- | --- |
| ⌨️ 键盘统计 | 逐键计数，区分左右修饰键（Ctrl / Shift / Alt / Win）；累计值过万缩写成 `12.3k` / `1.2M`，悬停可看准确数 |
| 🔥 键盘热力图 | 144 键扩展布局，按当前时段的最少 / 最多次数归一化着色，描边标出最近输入；时段可选今天 / 1、6、12 小时 / 总计 / 昨天 / 前天 |
| 📈 输入节奏 | 60 秒滚动曲线（采样 250 ms、重绘 100 ms、连续左移）配峰值示意线；速率取近 1 秒窗口，另有距上次按下、平均间隔、今日平均、最快速率、间隔速率 |
| 🖱️ 鼠标统计 | 左 / 中 / 右键点击数；位移窗里的小鼠标图标随手移动偏转，下方叠 X / Y 偏移与滚轮速率三条轨迹；另有 Δx·Δy、px/s 与折算 m/s、今日与累计移动里程（米）、滚轮格数 |
| 🏃 应用使用时长 | WMI 追踪各进程会话：首次启动、最近活动、落盘累计、实时补算、校正后时长与占比。进行中的会话按最后落盘时刻补算未写库的那段 |
| 💻 电脑使用时长 | 每天的开机 / 使用时长，依赖后台服务 XAssistant.Service；原始时长与服务校正时长不一致时标红，并给出修正原因与服务起止、休眠唤醒的会话事件明细 |
| 📝 灵感速记（可选） | 全局热键唤起速记窗，需自备数据库连接串，见下文 |
| ⚙️ 其它 | 明暗主题、悬浮键盘动画窗口、事件日志（按日期与级别筛选、导出）、记录导出、开机自启、系统托盘常驻 |

词频统计目前只是界面上的占位，尚未实现：程序只记录按键名称，不采集输入的文本内容。

关闭窗口只是隐藏到托盘，托盘菜单中才有「退出」；左键单击托盘图标可重新唤出主窗口。

## 支持环境

- 亲测环境：Windows 10 专业版 22H2（19045），64 位
- 直接运行 Release 包**无需安装 .NET**（已自包含打包）
- 其它环境不保证兼容

## 数据存储

- 主程序数据：`%APPDATA%\XAssistant`
  - `key_data.db` 按键计数 / `click_data.db` 鼠标计数与移动里程 / `app_usage.db` 应用会话
  - `appsettings.json` 配置
- 后台服务数据：`C:\ProgramData\XAssistant\UsageTracker\pc_usage.db`（电脑使用时长）
- 日志：`%APPDATA%\XAssistant\logs\`（按天滚动，保留 31 天）

均为本地文件，不联网上传。

## 灵感速记（可选）

全局热键默认 `Win+Numpad0`。编辑 `%APPDATA%\XAssistant\appsettings.json`，在 `QuickNote` 节点填写 PostgreSQL 连接串即可启用：

```json
{
  "QuickNote": {
    "ConnectionString": "Host=主机;Port=5432;Database=库名;Username=用户;Password=密码"
  }
}
```

未配置时该功能禁用，不影响其它功能。也可以只填 `DatabaseUrlEnvPath` 指向一个 `.env`，程序会读取其中的 `DATABASE_URL`。

## 构建

```powershell
# 构建整个解决方案（主程序 + 后台服务）
dotnet build XAssistant.sln -c Release

# 发布主程序（自包含，win-x64）
dotnet publish XAssistant.csproj -r win-x64 -c Release --self-contained true

# 发布后台服务（自包含，win-x64）
dotnet publish XAssistant.Service\XAssistant.Service.csproj -c Release --self-contained true -r win-x64
```

需安装 [.NET 8 SDK](https://dotnet.microsoft.com/download)（主程序与后台服务都是 `net8.0-windows`）。

调试构建（`DEBUG`）的数据写在 `%APPDATA%\XAssistant_Dev`，与正式版分开，方便两个版本同时跑。


## 许可证

[MIT](./LICENSE)