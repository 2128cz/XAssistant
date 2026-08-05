# 速记（Quick Note）Context

XAssistant 为 xapp「灵感速记」功能提供的 OS 级入口：注册全局热键、抓取前台窗口标题、唤起 app 模式捕获小窗。速记的本体（数据表、tRPC 接口、捕获/列表视图）在 xapp 仓库，spec 见 `C:\xapp-2026-06-30\docs\specs\global-quick-note.md`，本仓库只负责唤起链路。

## Language

**灵感速记（Quick Note）**:
用户在任意应用界面上 1 秒记一笔的即时记录功能：正文经速记页落 xapp 主库，可转 markdown 归档。本仓库提供其全局唤起入口，不负责速记的数据存储。
_Avoid_: 便签、临时笔记

**速记唤起（Quick-Note Invocation）**:
通过全局热键或托盘菜单触发捕获窗打开/聚焦的动作；唤起瞬间抓取当前前台窗口标题作为来源。
_Avoid_: 弹窗、启动速记

**捕获窗（Capture Window）**:
以 Chrome/Edge app 模式启动的浏览器小窗（无地址栏、约 420×560、置顶），承载 xapp 的速记捕获视图。
_Avoid_: 小窗页、便签窗

**来源（Source）**:
唤起捕获窗那一刻当前前台窗口的标题，作为速记的独立一行上下文元信息。
_Avoid_: 上下文、窗口标题（特指来源字段时）

**全局热键（Global Hotkey）**:
唤起速记捕获窗的全局组合键，默认 `Win+Numpad0`（小键盘 0，与任务栏固定的 `Win+0` 不冲突），可在配置中修改；由 XAssistant 常驻进程注册，注册失败时经托盘菜单兜底。
_Avoid_: 快捷键（泛指）、热键组合

**单实例复用（Single-instance Reuse）**:
捕获窗已打开（且可能已输入内容）时再次唤起 → 仅置顶并聚焦输入框，不重开、不覆盖已输入内容、不覆盖来源；来源只在首次新开时写入。
_Avoid_: 去重、复用窗口

**标题标记（Title Marker）**:
捕获视图页面标题的固定前缀（「灵感速记」），C# 侧据此在可见顶层窗口中识别捕获窗是否已打开。这是与 xapp 捕获视图的契约。
_Avoid_: 窗口标题（与来源混淆）、标识符

**基址（Base URL）**:
xapp SPA 的访问基址：生产 `http://localhost:9009`、开发 `http://localhost:3009`，随构建配置（Release/Debug）默认、可在配置覆盖。
_Avoid_: 服务器地址、端口
