# XAssistant

> ## ⚠️ 开篇避难声明（请先读，好吗）
>
> 本仓库是 **vibe coding** 的产物：代码 100% 由 AI 编写，作者本人对 C# / .NET 一窍不通，遇到 bug 只会双手合十念"重启一下"。项目只在作者自己的一台机器上验证过能跑动，**不保证任何其它环境下的兼容性**，**不承诺任何更新与维护**。
>
> 之所以发出来，纯粹是因为视频发出去之后有观众老爷想要。**作者本来是不想把这个 APP 拿出手的**——各位下载、尝鲜、拆解、魔改都行，但请对它的诞生过程保持宽容：它只是个摔不坏就行的玩具。
>
> 在这台机器上：Windows 10 专业版 22H2（build 19045，64 位）。

## 功能

| 功能 | 说明 |
| --- | --- |
| 💻 **电脑使用时长** | 记录电脑每天的开机 / 使用时长，通过系统托盘常驻，由随仓库提供的后台服务 `XAssistant.Service` 支撑（Windows 服务，自动启动） |
| 🖱️ 鼠标点击统计 | 低级钩子记录左 / 中 / 右键点击次数 |
| ⌨️ 键盘按键统计 | 按键计数，区分左右修饰键（Ctrl / Shift / Alt / Win） |
| 📊 应用使用时长 | WMI 追踪每个应用的使用时长——⚠️ **此功能不完善，统计可能有偏差**，作者自己都看不太懂，纯属图一乐 |
| 📝 灵感速记（可选） | 全局热键唤起速记输入窗，需要自行配置一个数据库连接串（见下文），不配也能正常使用其它功能 |

应用最小化到系统托盘常驻，关闭窗口只是隐藏，托盘菜单里才有"退出"。

## 支持环境

- ✅ **唯一亲测环境**：Windows 10 专业版 22H2（19045），64 位
- ✅ 运行 Release 程序包**不需要**额外装 .NET（已自包含打包）
- ❓ 其它系统或版本：不保证，出了事作者也只能摊手

## 安装（观众请从这里看起）

1. 到 Releases 页下载两个 zip：`XAssistant-win-x64-*.zip`（主程序）和 `XAssistant.Service-win-x64-*.zip`（后台服务）。
2. 解压**到同一个文件夹**下，目录结构保持：
   ```
   你的文件夹/
   ├─ XAssistant.exe                （来自主程序包）
   └─ XAssistant.Service/
      ├─ XAssistant.Service.exe     （来自服务包）
      └─ install-service.ps1
   ```
3. 右键 `install-service.ps1` → **使用 PowerShell 运行**（会弹出管理员授权），或在管理员 PowerShell 里执行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\XAssistant.Service\install-service.ps1
   ```
   脚本会注册并启动名为 `XAssistant.UsageTracker` 的自动启动服务，负责"电脑使用时长"数据。
4. 双击 `XAssistant.exe`，收工。

> 小提示：不装后台服务也能用鼠标 / 键盘 / 应用时长等功能，只有"电脑使用时长"会显示获取不到。

## 构建（只有开发者在意的部分）

```powershell
# 构建整个解决方案（主程序 + 后台服务）
dotnet build XAssistant.sln -c Release

# 发布主程序（自包含，win-x64）
dotnet publish XAssistant.csproj -r win-x64 -c Release --self-contained true

# 发布后台服务（自包含，win-x64）
dotnet publish XAssistant.Service\XAssistant.Service.csproj -c Release --self-contained true -r win-x64
```

需要安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

## 数据存储位置

- 主程序数据：`%APPDATA%\XAssistant`（点击 / 按键 / 应用使用时长数据库与配置 `appsettings.json`）
- 后台服务数据：`C:\ProgramData\XAssistant\UsageTracker`（电脑使用时长数据库与日志）
- 日志：`%APPDATA%\XAssistant\logs\`，滚动保留 31 天

所有数据均为**本地文件**，不联网、不上传。

## 可选功能：灵感速记（要自己配数据库）

全局热键（默认 `Win+Numpad0`）唤起速记输入窗，内容会写入你的 PostgreSQL 数据库。配置方法：编辑 `%APPDATA%\XAssistant\appsettings.json`，在 `QuickNote` 节点下填写一个连接串：

```json
{
  "QuickNote": {
    "ConnectionString": "Host=你的主机;Port=5432;Database=你的库名;Username=用户;Password=密码"
  }
}
```

未配置时该功能静默禁用，其它功能不受任何影响。作者自己的配置里还留着一套读取系统环境变量的兜底逻辑，一般情况用不上，就当作历史遗留吧。

## 架构坦言（高耦合低内聚预警，慎入）

- 三个钩子服务各写各的，靠 `DispatcherTimer` 轮询硬撑实时数据——没有事件总线，全靠"过一会儿再看一眼"。
- 主程序和后台服务之间用**硬编码的命名管道名 + 绝对路径**互相"神交"，改名全靠两边同时默念。
- `ProcessUsageTracker` 一个类近千行，是仓库里 bug 产出率最高的选手。
- 全局零测试。作者对"测试"的概念停留在"这功能我点过一下能跑"。
- 速记功能里还躺着对另一套系统的本地绝对路径引用——作者自己看着都心虚。

一句话总结：**高耦合、低内聚、靠运气运行**。属于 AI 圆梦 + 作者硬扛的产物。欢迎学习、拆解、魔改，生产环境慎用，报错归自己。

## 许可证

[MIT](./LICENSE)