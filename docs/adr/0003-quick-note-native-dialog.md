# 速记捕获载体改为 XAssistant 原生对话框 + Npgsql 直插

xapp 侧 ADR 0010（2026-08-05）将速记捕获载体由「SPA 捕获视图（Chrome/Edge app 模式小窗）」改为「XAssistant 原生无边框对话框」，写库由 tRPC `create` 改为 Npgsql 直插主库 `quick_notes` 表（只写 `content`/`source`）。本仓库对应改造：`QuickNoteCaptureService` 从「启动 app 模式浏览器窗指向 xapp 捕获页」改为「弹原生速记窗 + Npgsql INSERT」；新增 `QuickNoteDatabaseService`、`Views/QuickNoteWindow`、`QuickNoteViewModel`；删除浏览器唤起、标题标记识别与 `Browser`/`TitleMarker` 配置；单实例复用退化为自持窗体引用（取代 `docs/adr/0002` 的标题标记匹配）。

- **Consequences**: 速记窗不再依赖 xapp 服务在线（直连 Postgres，Postgres 为 Windows 服务、开机即在）；xapp 侧已删除 SPA 捕获视图与 tRPC `create`，本仓库 `OpenList()` 仍用 SPA 基址打开列表页 `<基址>/quick-note`；写库契约 `INSERT INTO quick_notes (content, source)` 只写两列，`quick_notes` 表新增 `NOT NULL` 无默认值列会断直插且无即时报错，改表须同步本仓库（xapp `docs/domains/quick-note.md` 已知坑已记录）；连接串优先取 `AppSettings.QuickNote.ConnectionString`，未配置时自动读取 xapp `.env` 的 `DATABASE_URL`（路径经 `DatabaseUrlEnvPath` 可配，默认 `C:\xapp-2026-06-30\.env`），显式配置优先、保留 ADR 0010「与 xapp 解耦」意图；前台窗口为本应用自身时来源置空，避免来源误记为 "XAssistant"。
