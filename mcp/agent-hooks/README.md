# agent-hooks —— 多家 AI 编码代理的「会话状态 → xa 提醒」

一份状态脚本 + 一个安装器，把各家 AI 编码代理的**生命周期事件**接到 XAssistant 的 `xa` 命令上：
报错 / 等授权 / 这轮回复完成时，屏幕上直接给你一条对应颜色的提醒（全屏带 + 边框 + 顶部持久消息栈）。

```
mcp/agent-hooks/
  agent-status.ps1               平台无关：stdin 收事件 JSON → 分诊 → 拉 xa
  dsh-status-plugin.mjs          DSH 原生 Cordis 插件：会话 / 工具 / 授权 / 提问事件 → 状态 JSON
  install-agent-hooks.ps1        多平台安装器：装 / 拆 / 试算 / 看平台表
  dsh-status-plugin.selftest.mjs DSH 事件映射自测
```

## 为什么一份脚本够用

Claude Code 定下的 hooks 协议已被各家照抄成事实标准——**同一套 PascalCase 事件名 + stdin 收 JSON +
exit code 表达决定**，所以分诊逻辑没有平台分支，平台差异只剩「配置写在哪个文件」：

| 事件 | 分档 | 四段文案：机械回复 · 现况 · 来源 · 信息 |
|---|---|---|
| `PostToolUseFailure` / `PostToolUse` 结果异常 | 黄（工具挂了但对话还在跑，不必按红色惊动）；`is_interrupt` 升红 | `警告 · 工具调用失败 · “对话标题” · code 1`；被人为打断写成 `故障 · 意外中断对话 · …` |
| `StopFailure`（限流 / 配额 / API 错误打断整轮） | 红 | `故障 · 意外中断对话 · “对话标题” · ratelimit` |
| `PermissionRequest` | 黄 | `询问 · 等待授权 · “对话标题” · Bash` |
| `Notification`（确认 / 提问） | 黄 | `询问 · 请求人类介入 · “对话标题” · 要人回的那句话` |
| `Warning`（DSH token 上限 / 策略阻断） | 黄 | `警告 · 运行告警 · “对话标题” · 输出达到 token 上限` |
| `Stop` | 普通 | `回复 · 对话回合结束 · “对话标题”`（`stop_hook_active` 时静默，防 Stop→xa→Stop 死循环） |

四段的规矩（`selftest.ps1` 是按段拆开来断言的，不是匹配关键字）：

- **机械回复**是固定领词 `提醒 / 警告 / 故障 / 询问 / 回复`——颜色与分流按它走，别的系统也在认它，不许现场造词
  （旧写法把工具名塞进领词变成「授权(Bash)」，现在工具名归到信息段）。
- **现况**说清是哪一环：主对话用 `工具调用失败 / 意外中断对话 / 对话回合结束 / 等待授权 / 请求人类介入 / 运行告警 / 事件数据不完整`；
  事件带 `agent_id`/`agent_type` 的就是子实例，换成 `子代理失败 / 子代理成功`——两类消息一眼分得清该回哪一路。
- **来源**只有这场对话的名字（事件自带 `session_title` → IDE 的 vscdb 任务名 → 退回项目目录名，都没有就省这一段）。
  **对话内容一律不上屏**：`last_assistant_message` 那种原文既读不到重点又泄上下文。
  也不要拿 `parent_business_info.name` 当标题——那是 IDE 用**用户首句**自动起的会话名，上屏就等于对话内容
  （这条踩过一次：回放本机 12 条真事件，第 3 段全是「要，继续」「unity我关了，你」，用户点名禁止过）。
- **信息**是定位用的额外内容（错误码 / 工具名 / 退出码 / 子代理在干什么 / 要人回的那个问题），段内部用 ` / ` 分隔，
  ` · ` 只留给四段之间，正文按它才拆得出恰好四段。缺哪段省哪段。
  要人回的那句**问题原文**在 `details.input.questions[].question`（授权请求在 `tool_input.questions`），
  `message` 里的 `Tool AskUserQuestion requires confirmation` 只是确认 boilerplate，不许顶替问句；
  一次问好几句时只展首问并挂「/ 共 N 问」，问句太长先瘦身、计数留在结尾不被截掉。

各段能拿到什么，取决于平台愿意在事件里写多少（本机 Qoder 1.1.57 实测）：
9 个 `state.vscdb`（globalStorage + 8 个 workspaceStorage）里搜不到事件的 `session_id`，
`projects/<slug>/<会话>/state.json` 整份是密文，transcript 的顶层键也没有 title/name/summary——
所以 Qoder 的**来源段通常只有项目目录名**；DSH 的事件直接带 `session_title`，那一段才是引号里的会话名。

真机事件喂进脚本后跑出来的样子（前四条是把真事件回放进部署副本得到的原文）：

```
警告 · 工具调用失败 · XericRp · Exit code 1 ('file_lines', 3365) ('has_b…
询问 · 请求人类介入 · AI · 是否同意把本次安装的 Caddy 本地根证书导入你的 Windows 信任库？
询问 · 请求人类介入 · XericDesktop · 把 XericUIGen 三个包接进哪个工程给你手测？（… / 共 2 问
回复 · 对话回合结束 · com.lrss3.deconstruction
询问 · 等待授权 · “修复构建脚本的编码问题” · Bash
回复 · 子代理成功 · XericCICD · 子代理 Explore
警告 · 子代理失败 · XericCICD · 子代理 Explore / boom
```

后三条分别说明：DSH 那种带 `session_title` 的来源才加引号；子实例换成 `子代理成功 / 子代理失败`，
名字与「派下去干什么」进信息段（不占来源段）；拿不到会话标题时来源退回项目目录名（`XericCICD`）。

## 平台对照

| 平台 | 内核 | hooks 落地形态 | 装法 | 状态 |
|---|---|---|---|---|
| **Qoder CN**（桌面版 / IDE） | Electron 桌面应用 | `~/.qoder-cn/settings.json` 的 `hooks` 节点（三级合并） | `-Platform qoder` | **已实测**（另有独立 `QoderComputerUse.exe` 接管遮罩窗口，见同级报告） |
| **Claude Code** | Node CLI | `~/.claude/settings.json` 的 `hooks` 节点 | `-Platform claude` | 协议源头，结构同 Qoder，未本机实测 |
| **Trae CN** | **VS Code fork**（1.107.1） | **独立文件** `~/.trae-cn/hooks.json`（项目级 `<repo>/.trae/hooks.json`） | `-Platform trae [-ProjectPath <repo>]` | 路径与事件名已从安装包确证；schema 待用 Trae 的 Hooks 面板核对 |
| **Trae**（国际版） | VS Code fork | `~/.trae/hooks.json` | `-Platform trae-intl` | 同 trae（CN 与国际版互为 peer 目录） |
| **DeepSeek Harness** | Cordis agent harness | `$DSH_HOME/profiles/<profile>/cordis.patch.yml` + profile 内原生插件 | `-Platform dsh [-Profile web]` | **原生事件对接**：完成、会话错误、token 上限、工具失败、授权、结构化提问 |
| Cursor / Windsurf / Codex CLI | VS Code fork / CLI | 各自官方 hooks | 待校准 | 先照官方模板手动配一条，再回来装 |

> `AI.ide.computerUse.enable` 在 Trae 里是用户级开关，**Trae 的电脑控制没有独立进程**（追踪逻辑在
> icube agent 模块内部，安装目录里也没有第二个可执行文件）——所以 Trae 走 hooks 这条路，
> 别指望像 Qoder 那样靠 `QoderComputerUse*Overlay` 类名从窗口层检测接管。

## 装 / 拆

```powershell
# 看一眼平台表
.\install-agent-hooks.ps1 -List

# 装（Qoder CN）
.\install-agent-hooks.ps1 -Platform qoder

# Trae CN：先试算，确认写出来的结构对得上 Trae 的 Hooks 面板
.\install-agent-hooks.ps1 -Platform trae -DryRun
.\install-agent-hooks.ps1 -Platform trae

# 只想给某一个仓库装（Trae 的项目级配置，可跟着 Git 走）
.\install-agent-hooks.ps1 -Platform trae -ProjectPath D:\some\repo

# DSH Web GUI：装原生 Cordis 状态插件（用户 patch 支持热重载；未加载时重启 dsh web）
.\install-agent-hooks.ps1 -Platform dsh -Profile web -DryRun
.\install-agent-hooks.ps1 -Platform dsh -Profile web

# 拆（只摘安装器自己的标记块，现有 cordis.patch.yml 其它行原样保留）
.\install-agent-hooks.ps1 -Platform dsh -Profile web -Remove
.\install-agent-hooks.ps1 -Platform qoder -Remove
```

Qoder / Claude / Trae 安装时，安装器会把 `agent-status.ps1` 拷到 `<根>/hooks/`（带 BOM，Windows PowerShell 5.1 才读得动中文）、备份目标 JSON，并只维护指向该脚本的条目。**幂等**——重复装不会重复挂。

DSH 例外：它现有的 Claude/Codex 兼容桥只暴露 `Stop` / `PostToolUse` 等子集，拿不到 DSH 原生的 `approval/asked`、结构化提问和完整失败结果。安装器因此把 `dsh-status-plugin.mjs` 与状态脚本复制到目标 profile，并只在 `cordis.patch.yml` 里维护 `# xassistant-agent-hooks:dsh begin/end` 标记块；用户已有 YAML 不解析、不重排。插件默认只提醒顶层对话，子代理静默，避免一项任务扇出十几条完成卡。

### DSH 插件的发布仓库（找不到插件时看这里）

- 独立仓库：**http://192.168.1.111:3000/AI/DSH-XAssistant-AIHook.git** （`main` 分支：插件 + 映射自测 + 面向 DSH 用户的装/验/拆说明）
- 装好后的落点：`$DSH_HOME/profiles/<profile>/xassistant-agent-hooks/`（`dsh-status-plugin.mjs` + `agent-status.ps1`），
  profile 的 `cordis.patch.yml` 里是那段带 `begin/end` 的标记块；`DSH_HOME` 默认 `~\.dsh`。
- **开发处仍是本仓库 `mcp/agent-hooks/`**：插件仓库是发布镜像，改逻辑改这里再同步过去；状态脚本
  （分档、文案、来源徽章、乱码过滤）不在插件仓库复制第二份，避免两套判据漂移。
- 方向是**单向推**：插件自己 spawn `agent-status.ps1 -Platform dsh`，XAssistant 侧不需要为 DSH 写任何
  监听或轮询；工作台「PART 4 / MODULES」的 **DSH 接入状态**卡（`Services/Modules/DshStatusModule.cs`）
  只如实报「profile 装没装、patch 有没有标记块、插件文件在不在位、最近一条真机事件」。

## 来源段：主对话还是子代理

Qoder 的事件里带 `agent_id` + `agent_type` 的就是**子代理**，主对话没有这两个字段（本机 1.1.57 实测）。
`agent_id` 还能直接对上 IDE 落盘的子代理档案：

```
~\.qoder-cn\projects\<slug>\<会话>\subagents\agent-<agent_id>.meta.json
{"agentType":"Explore","toolUseId":"call_…","description":"盘点前端数据需求","invocationName":"Explore","color":"cyan"}
```

布局注意：`<会话>.jsonl` 与同名**目录**平级，档案在目录里的 `subagents/` 下，所以脚本按
`dirname(transcript)/basename(去扩展)/subagents/` 找，再退回 `dirname/subagents/`（兼容 transcript 指到会话目录本身）。

子实例的名字与「派下去干什么」进**信息段**（不占来源段，来源仍是那场对话的标题），效果是：

```
警告 · 子代理失败 · AI · 子代理 general-purpose：Probe NAS-adjacent hosts / Exit code 1
回复 · 子代理成功 · HotRollingDigitalTwin · 子代理 Explore：盘点前端数据需求
```

`color` 只有内置类型带（本机 59 份档案里 16 份有，全是 `Explore`；`general-purpose` 一律没有），
所以**没拿它参与配色**——屏幕颜色由档位表达（红=对话断了要人回来、黄=要你回一句或先看一眼、info=这一轮完了）。
项目级的小图标 / 颜色本机拿不到：global state 里 `"color":"…"` 零命中，`questWorkspaceHistory` 只有
`uri/label/normalizedPath/lastOpenedAt/workspaceKind`，8 个 `workspaceStorage` 也没有——真在项目上打了标记后
再扫一次才能定位。

## 验证

1. 装完**重启对应 IDE**（Qoder / Claude Code 的 hooks 不支持热重载）。
2. 在对话里触发一次：让它跑个命令失败、或等一次授权。
3. 看 `%LOCALAPPDATA%\XAssistant\agent-hooks\agent-status.log` —— 每次触发写一行原始事件 JSON，
   **字段名以这份日志为准**（各平台实现有出入时，按它校准脚本）。
4. 屏幕应出现对应颜色的提醒；没出现就看日志里 `[dry-run]`/`ERROR` 行与 `xa` 路径是否找对。

自测（不打扰桌面）：

```powershell
# 用假 xa 记录参数，喂一条假事件进 stdin
$fake = "$env:TEMP\fake-xa.cmd"
'@echo off', 'echo %* >> "%TEMP%\fake-xa.log"' | Set-Content $fake -Encoding ASCII
$env:XASSISTANT_XA = $fake
'{"hook_event_name":"PermissionRequest","tool_name":"Bash","cwd":"D:\\repo"}' |
  powershell -NoProfile -File .\agent-status.ps1 -Platform trae
Get-Content "$env:TEMP\fake-xa.log"     # 应看到 -s warn 8 1 1 -border on 60 30 1 -lable on 26 "询问 · 等待授权 · repo · Bash"
```

## 自测

改完脚本跑一次：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\selftest.ps1
```

自测全部在临时目录里跑、不碰真实 IDE / DSH 配置：装（JSON 两种落地形态 + DSH profile 标记块）/ 幂等 / flat 自适应 +
**不删用户自己的条目** / 拆卸 / 未校准平台拒写 / DSH 事件映射 / 事件分诊（颜色档、文案、静默规则、来源标记、空 stdin 与坏 JSON 永远 `exit 0`）。
它用一个假 `xa`（`XASSISTANT_XA` 指向把参数记进文件的 cmd）断言参数拼得对，不会真的弹窗。

> 两个坑记在这里：脚本**必须带 UTF8-BOM 保存**（Windows PowerShell 5.1 会把无 BOM 的中文按 ANSI 解读，直接语法报错）；
> 自测里等 `xa` 落盘不能只等「行数变多」——`Start-Process` 是异步的，上一条迟到的写入会把你提前放行，
> 整排断言于是整齐错位一行（`Wait-Quiet` 就是为此存在的）。

## 迁移（旧的 `mcp/qoder-hooks` 已并入这里）

原来 Qoder 专用的 `mcp/qoder-hooks/`（`qoder-status.ps1` + `install-takeover-hooks.ps1`）已并入本目录：
脚本改名 `agent-status.ps1` 并去掉 Qoder 专有假设（日志统一落到 `%LOCALAPPDATA%\XAssistant\agent-hooks\`），
安装器换成多平台的这一份。**装过旧版的重跑一次即可**：

```powershell
.\install-agent-hooks.ps1 -Platform qoder
```

安装器会先摘掉指向旧 `qoder-status.ps1` 的条目（两个脚本名都在清理名单里）再挂新的，不会重复挂；
旧脚本文件本身还留在 `~\.qoder-cn\hooks\` 下，可以手动删。

## 待校准

- [ ] **Trae `hooks.json` 的准确结构**：在 Trae 里打开 Hooks 面板加一条命令 hook，把生成的文件内容比对一遍
      （安装器会**沿用目标文件已有的结构**：带 `hooks` 层就写 `hooks` 层，顶层直接是事件名就写顶层）。
- [ ] Trae 的事件集完整性（`PreToolUse` / `Notification` / `Stop` 已在安装包里确证，`PostToolUseFailure` 未见到）。
- [ ] Cursor / Windsurf / Codex 的 hooks 文件位置与字段（`-Force` 之前先手动配一条看模板）。
- [ ] Trae 电脑控制的接管信号：无独立进程，需研究 IDE 内状态（状态栏 / webview / hooks 事件）替代窗口检测。
