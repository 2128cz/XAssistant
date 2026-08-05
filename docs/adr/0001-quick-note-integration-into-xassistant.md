# 速记唤起能力并入 XAssistant，而非独立托盘程序

xapp 速记 spec 原将「托盘程序（C#）」规划为独立 dotnet 程序，自带托盘图标、自启与配置。实际落地改为并入现有 XAssistant 应用：XAssistant 已开机自启、常驻后台，并已具备托盘菜单（`NotifyIcon`）、Run 键自启（`StartupService`）、dev/prod 数据隔离（`AppDataPathHelper`）、JSON 配置（`ConfigurationService`）与 Serilog 日志等全部底座，独立程序只会带来第二个托盘图标、第二份自启与配置文件。速记唤起作为 XAssistant 的一个新服务（全局热键 + 捕获窗唤起）落地，配置并入 `AppSettings`，托盘菜单新增「速记」项，自启与退出沿用 XAssistant 现有语义。

- **Consequences**: 全局热键注册在 XAssistant 进程内，XAssistant 未运行时速记不可唤起（自启由用户在设置页开关）；托盘「退出」即退出整个 XAssistant，满足 spec「退出后热键失效」验收项；配置随 `AppSettings` 走 `%APPDATA%\XAssistant[_Dev]\appsettings.json`。
