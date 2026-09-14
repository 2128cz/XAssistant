# 速记（Quick Note）Context

XAssistant 为 xapp「灵感速记」功能提供 OS 级入口与写库链路：注册全局热键、抓取前台窗口标题、弹原生速记窗、Npgsql 直插速记库。速记的列表管理、归档转 markdown（tRPC 接口、列表页）在 xapp 仓库，spec 见 xapp 仓库的 `docs/specs/global-quick-note.md`，写入归属决策见 xapp `docs/adr/0010`，本仓库负责唤起与写库。

## Language

**灵感速记（Quick Note）**:
用户在任意应用界面上 1 秒记一笔的即时记录功能：正文经速记库落 xapp 主库，可转 markdown 归档。本仓库提供其全局唤起入口与直插写库。
_Avoid_: 便签、临时笔记

**速记唤起（Quick-Note Invocation）**:
通过全局热键或托盘菜单触发速记窗打开/聚焦的动作；唤起瞬间抓取当前前台窗口标题作为来源（前台窗口是本应用自己时不算）。
_Avoid_: 弹窗、启动速记

**捕获窗（Capture Window）**:
XAssistant 原生无边框置顶小窗（`Views/QuickNoteWindow`，约 420×560），承载速记的正文输入、保存与失败重试；含标题栏与 ✕ 关闭按钮。
_Avoid_: 小窗页、便签窗

**来源（Source）**:
唤起速记窗那一刻当前前台窗口的标题，作为速记的独立一行上下文元信息；前台是本应用自己时来源为空。
_Avoid_: 上下文、窗口标题（特指来源字段时）

**全局热键（Global Hotkey）**:
唤起速记窗的全局组合键，默认 `Win+Numpad0`（小键盘 0，与任务栏固定的 `Win+0` 不冲突），可在配置中修改；由 XAssistant 常驻进程注册，注册失败时经托盘菜单兜底。
_Avoid_: 快捷键（泛指）、热键组合

**单实例复用（Single-instance Reuse）**:
速记窗已打开（且可能已输入内容）时再次唤起 → 仅置顶并聚焦输入框，不重开、不覆盖已输入内容、不覆盖来源；来源只在首次新开时写入。由 XAssistant 自持窗体引用判定，天然可靠。
_Avoid_: 去重、复用窗口

**连接串（Connection String）**:
速记库 PostgreSQL 连接串，优先取 `appsettings.json` 的 `QuickNote.ConnectionString`（复制自 xapp `.env` 的 `DATABASE_URL`，支持直接粘贴 URI）；未配置时自动读取 `QuickNote.DatabaseUrlEnvPath` 指向的 `.env`（留空即数据目录下的 `.env`，写相对路径也按数据目录解析）中的 `DATABASE_URL`。Npgsql 直插 `quick_notes` 表。
_Avoid_: 服务器地址、端口

**基址（Base URL）**:
xapp SPA 的访问基址：生产 `http://localhost:9009`、开发 `http://localhost:3009`，随构建配置（Release/Debug）默认、可在配置覆盖。仅「打开速记列表」需要。
_Avoid_: 服务器地址、端口
