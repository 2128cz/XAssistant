# 各 AI IDE / Agent 平台的「Hooks 会话钩子」与「Computer Use 接管检测」横向验证报告

> 生成时间：2026-09-16
> 上游文档：`mcp/qoder-cn-computeruse-takeover-detection.md`（Qoder CN 实盘验证）
> 性质：本文为**桌面调研结论**（官方文档 + 社区证据交叉），其中 Qoder CN 一条为实盘验证，其余平台的「窗口类名/进程特征」列为待实测项（需要目标平台真实运行后按 qoder 报告 §2 的 EnumWindows 探针采样）。
>
> **2026-09-16 装机现场实测补充**（本机装有 Trae CN、Qoder CN、VS Code、Visual Studio）：Trae 与 Qoder **不是同类结构**——
> Trae CN 是 VS Code 1.107.1 的 fork，hooks 写在**独立的 `hooks.json`**（不是 settings.json 里的节点），
> 且它的电脑控制**没有独立进程、也没有独立遮罩窗口**（追踪逻辑在 icube agent 模块内部）。
> 据此，hooks 那条腿已经落成多平台安装器：`mcp/agent-hooks/`（`-Platform qoder|claude|trae|trae-intl`）。

---

## 0. 结论速览

| 平台 | 内核 | 类 Claude Code Hooks | Computer Use / 桌面接管 | 外部检测可行性 |
|---|---|---|---|---|
| **Claude Code**（Anthropic） | Node CLI（无内核关系） | ✅ **协议源头** `~/.claude/settings.json` | 无遮罩（跑在终端里） | Hooks 直接拿全状态 |
| **Qoder CN**（桌面版） | Electron 桌面应用（非 VS Code） | ✅ 12 事件，`~/.qoder/settings.json`（**实盘验证**） | ✅ `QoderComputerUse*Overlay` 独立顶层窗口（**实测类名已知**） | Hooks + 窗口双通道，均已打通 |
| **Qoder（qoder.com）/ Qoder CN IDE** | 待确认（有官方 docs.qoder.com） | ✅ 官方 computer-use 文档在案 | ✅ 官方 computer-use 文档在案 | 方法同上，类名待实测 |
| **Trae CN / Trae 国际版**（字节） | **VS Code 1.107.1 fork**（实测 `product.json.vscodeVersion`，`packageType: TRAE_CN`） | ✅ **独立文件** `.trae/hooks.json`（项目级）/ `hooks.json`（全局级，CN 版根为 `~/.trae-cn`）；事件名与 Claude Code 同构（`PreToolUse` / `Notification` / `Stop` / `PermissionRequest`，安装包字符串实证） | ⚠️ **无独立进程、无独立遮罩窗口**：电脑控制由 icube agent 模块内部追踪（`_computerUseTracking`），安装目录里没有第二个可执行文件 | hooks 可用（`-Platform trae`）；**窗口层检测不适用**，只能走 hooks / IDE 内状态 |
| **Trae Work**（= Trae Solo 升级） | 独立 product line（agent 工作台形态） | 待确认（与其 IDE hooks 是否打通） | ✅ 官方 `work_computer-use` 文档 | 同上 |
| **Cursor**（Anysphere） | **VS Code fork** | ✅ 官方 `cursor.com/docs/hooks.md` | 无桌面接管（agent 只在 IDE 内跑） | Hooks 直接可用 |
| **Windsurf**（Cognition） | **VS Code fork** | ✅ 官方「Cascade Hooks」文档 | 无桌面接管 | Hooks 直接可用 |
| **Codex CLI**（OpenAI） | Rust CLI | ✅ 社区 hooks（`.codex/hooks`） | CLI 形态，无遮罩屏 | Hooks 可用 |
| **Kiro / OpenCode / Gemini CLI** | CLI | ✅ 被 `weykon/agent-hooks` 统一覆盖 | 无 | Hooks 可用 |
| **VS Code 本体** | **内核源头**（Electron + Monaco） | ❌ 无官方 hooks 协议 | ❌（Copilot 行为在编辑器内） | 只能写扩展（`onDidChangeWindowState` 等） |
| **GitHub Copilot（VS Code）** | VS Code 扩展 | ❌ | ❌ | 扩展 API 层观察，无生命周期钩子 |
| **通义灵码 IDE（Lingma）** | **VS Code fork**（独立 IDE） | 待确认 | 待确认 | 视 hooks 支持而定 |
| **JetBrains AI Assistant** | IntelliJ 平台插件 | ❌ | ❌ | UI Automation / 日志层 |
| **Visual Studio** | 微软原生 IDE | ❌ | ❌ | UI Automation / 日志层 |

一句话结论：**2025–2026 年「类 Claude Code hooks 协议」已成为跨厂商事实标准**——同一份 `status.ps1 + settings.json 模板` 可以直接部署到 Claude Code / Qoder CN / Trae / Cursor / Windsurf / Codex 等 8+ 个平台；「独立 overlay 窗口检测」是 Computer Use 类功能的通用结构（Qoder CN 已实测，Trae 系已确认功能存在、类名待采）。

---

## 1. 内核到底相不相似

这是「能不能用同一套做法」的底层问题，分两类回答：

**IDE 类 —— 同为 VS Code fork（Electron + Monaco），扩展机制同源：**
- [字节 Trae IDE 真相：并非从零自研，而是 VSCode 内核的“进阶定制版”](https://www.cndba.cn/article/13761)—— Trae 官方承认基于 VS Code
- Cursor / Windsurf 为 VS Code fork 是公开共识（市场宣传与官方文档均如此）
- [阿里云灵码 IDE 技术测评](https://developer.aliyun.com/article/1686817)（Lingma IDE）同为 VS Code 系独立 IDE
- 推论：它们继承了 VS Code 的 settings.json/扩展/密钥链等机制，但 **hooks 能力是各家自己加的一层，互不保证**——Trae/Cursor/Windsurf 加了（下文证据），VS Code 本体反而没有。

**桌面/CLI 类 —— 与 VS Code 无内核关系，但 hooks 协议同款：**
- Qoder CN 桌面版：Electron 桌面应用（`.qoder-cn` 目录布局 + computer-use 插件 launcher）
- Claude Code / Codex CLI / Kiro / OpenCode / Gemini CLI：Node/Rust 终端 agent

> 结论：**内核不统一，但「会话钩子」的语言是统一的**——这才是能不能复用同一套 XAssistant 接入的关键。

### 1.1 本机实测：Trae CN 到底是谁（2026-09-16）

从装机目录里挖出来的硬证据（`%LOCALAPPDATA%\Programs\Trae CN\resources\app`）：

| 事实 | 证据 |
|---|---|
| Trae CN = VS Code 1.107.1 fork | `product.json` 的 `"version"` 与 `"vscodeVersion"` 都是 `1.107.1`；目录就是 `out/` + `extensions/` + `product.json` 那套 |
| 产品标识 | `packageType: TRAE_CN`、`provider: Yinli`、`providerCode: cn`、`soloUrl: https://work.trae.cn` |
| CN 版与国际版互为 peer 目录 | 设置项 `ENABLE_PEER_PRODUCT_SKILLS`：“Load skills from the peer product global directory (e.g. .trae ↔ .trae-cn)” |
| hooks 是**独立资产文件** | `out/vs/workbench/workbench.desktop.main.js` 里 `[{assetType:"hook",scope:3,projectRelPath:".trae/hooks.json",globalRelPath:"hooks.json"},{assetType:"command",…projectRelPath:".trae/commands/"}]`，全局那份由 `joinPath(<peer 全局目录>, "hooks.json")` 拼出 |
| 事件名与 Claude Code 同构 | `node_modules/@byted-icube/ai-modules-chat/dist/index.mjs` 里 `hook_event_name` ×12、`"PreToolUse"` / `"Notification"` / `"Stop"` 字面量，以及 `deny_message` / `stop_type: Blocked\|Stopped`（就是 exit 2 阻断那一套） |
| 其中一半是**企业 hooks** | 同一模块里大量 `is_enterprise` + `reason === Hooks` 分支（管理员下发的强制 hook），与企业下发共用同一批事件名 |
| **电脑控制没有独立进程** | 安装目录里可执行文件只有 `Trae CN.exe` / `aha_doctor.exe` / `task_host.exe` / `xperf.exe` / `inno_updater.exe`；CU 追踪在 icube 模块内部（`_computerUseTracking.reportSessionStart / reportSessionEnd`，字段 `step_count`、`end_reason: session_finished\|session_error\|session_stopped`）——对比 Qoder CN 的独立 `QoderComputerUse.exe` + 两个 topmost overlay 窗口 |
| CU 开关分两级 | `product.json` 的 `bootConfig.computerUse.enable: false`（产品级灰度）之上，还有用户设置 `AI.ide.computerUse.enable: true` 才真正生效 |

> 这条差异直接改变落地方式：**Qoder 可以靠窗口类名从外面看穿「AI 正在动我的屏幕」，Trae 不行**——
> 它没有那道独立遮罩，只能在 hooks 事件层（或 IDE 内 UI / 状态栏）判断。跨平台统一监听器的
> 「遮罩类名」那条腿，目前只有 Qoder 一家吃得下。

---

## 2. Hooks 协议：事实标准长什么样

所有兼容平台共用同一范式（Qoder CN 报告 §3 已详述，此处给跨平台版）：

- **配置**：`<家目录或项目>/.<厂商名>/settings.json` 里的 `"hooks": { "<事件>": [{...command...}] }`，多级合并（用户级 ← 项目级 ← 本地）
- **触发**：事件发生时 IDE/CLI spawn 配置的脚本，事件上下文 JSON 走 **stdin**，另注入环境变量（`*_SESSION_ID` / `*_TOOL_NAME` / `*_CWD` / `*_TRANSCRIPT_PATH`）
- **决定**：`exit 0` 放行；`exit 2` 阻断并把 stderr 注入会话（仅可阻断事件）；其他 = 非阻断错误
- **事件集**（各厂商大同小异）：`SessionStart / UserPromptSubmit / PreToolUse / PostToolUse / PostToolUseFailure / PermissionRequest / SubagentStart / SubagentStop / Stop / SessionEnd / PreCompact / Notification`

**逐平台证据：**
| 平台 | 官方/可靠来源 | 部署路径 | 备注 |
|---|---|---|---|
| Claude Code | anthropics/claude-code（协议源头，[hooks 输入参考 #11891](https://github.com/anthropics/claude-code/issues/11891)） | `~/.claude/settings.json` + 项目 `.claude/settings.json` | 只有它有 `stop_hook_active` 死循环闸门语义（Qoder 兼容） |
| Qoder CN | **用户实盘验证**（本仓库 qoder 报告 §3） | `~/.qoder/settings.json` 三级合并 | **不支持热重载**，改完要重启 IDE；工具名与 Claude Code 互通（`Bash`≡`run_in_terminal`） |
| Trae CN/国际 | [Trae 官方：通过 Hook 实现自动化](https://docs.trae.cn/ide_automate-actions-with-hooks) + [Hook 配置详解](https://docs.trae.cn/ide_hook-configuration-reference)（[论坛详解帖](https://forum.trae.cn/t/topic/180270/2)）；**装机实测见 §1.1** | **独立文件**：项目级 `.trae/hooks.json`、全局级 `~/.trae-cn/hooks.json`（国际版 `~/.trae/hooks.json`） | 与 settings.json 型**不同**：hooks 不在 settings.json 里；外层带不带 `hooks` 键待用 Trae 自带的 Hooks 面板核对（安装器已按目标文件现有结构自适应） |
| Cursor | [cursor.com/docs/hooks.md](https://cursor.com/docs/hooks.md?raw=1) | `~/.cursor/` 下 hooks 段 | Hooks 2.0 加入，Agent 循环 + Canvas 用同一机制（[中文详解](https://blog.csdn.net/qq_15071263/article/details/161776873)） |
| Windsurf | [Windsurf 官方：Cascade Hooks](https://docs.windsurf.com/zh/windsurf/cascade/hooks) | Cascade 配套 hooks | 与 Cascade 会话生命周期绑定 |
| Codex CLI | [codex-cli-hooks（社区反向工程）](https://raw.githubusercontent.com/shanraisshan/codex-cli-hooks/main/.codex/hooks/HOOKS-README.md) | `.codex/hooks/` | 事件驱动脚本式接入 |
| 统一注册 | [weykon/agent-hooks：一个注册器覆盖 Claude Code/Cursor/Codex/Windsurf/Kiro/OpenCode/Gemini](https://github.com/weykon/agent-hooks) | 各家映射表 | **「同协议、多厂商」的直接证据** |

> 对 XAssistant 的含义：写一份 `status.ps1`（按 `.hook_event_name` 分诊 → 调 `xa -s warn ... / -s info ...` 或托盘提醒），再为每个平台生成一份对应的 `settings.json`，就是全部成本。Qoder CN 那份最小示例（报告 §3.4）逐字可复用。

---

## 3. Computer Use / 桌面接管的「遮罩窗口」横向清单

Qoder CN 实盘结论：接管提示 = **computer-use 客户端进程创建的独立顶层窗口**（`WS_EX_LAYERED + WS_EX_TOPMOST`），工具调用期间成对出现、结束即销毁，非 IDE 内部 UI。

| 平台 | 功能是否在案 | 已知窗口/进程特征 | 状态 |
|---|---|---|---|
| Qoder CN | ✅ 用户实测 | `QoderComputerUseOperationOverlay`（全屏暗角）+ `QoderComputerUseStatusOverlay`（顶部胶囊）；进程 `QoderComputerUse.exe` | **已验证，零误报** |
| Qoder（qoder.com） | ✅ [官方 Computer Use 文档](https://docs.qoder.com/qoder/computer-use) | 未知 | 待实测（同法 EnumWindows 采样） |
| Trae CN IDE | ✅ [官方电脑控制文档](https://docs.trae.cn/ide_computer-use) | **没有独立窗口，也没有独立进程**：追踪在 `@byted-icube/ai-modules-chat` 内部（`_computerUseTracking`，带 `step_count` / `end_reason`）；开关是用户级的 `AI.ide.computerUse.enable` | **已实测：窗口层检测对 Trae 不成立**，改走 hooks |
| Trae Work | ✅ [官方 work 电脑控制文档](https://docs.trae.cn/work_computer-use) | 未知 | 待实测 |
| Cursor / Windsurf | 无桌面接管（agent 只在 IDE 内） | — | 不需要窗口检测，hooks 足矣 |
| Claude Code / Codex CLI | CLI 形态无遮罩 | — | hooks 足矣 |
| ChatGPT 桌面 / Operator 类 | 有（第三方） | 非本报告范围 | 可另采样 |

> XAssistant 落地的「接管提醒」其实是两件事：**物理接管**（遮罩窗口出现，跨 IDE 通用、语义就是「AI 正在动我的屏幕」）与**工作状态**（hooks 事件流，语义细粒度）。两者合并成一张映射表——「平台 → 遮罩类名/进程 + hooks 配置路径」就是 qoder 报告 §6 建议的统一产出，下面 §5 给雏形。

---

## 4. 没有 hooks 的平台怎么办

- **VS Code 本体 / GitHub Copilot**：无生命周期 hooks。替代路线是**写一个 VS Code 扩展**：`window.onDidChangeWindowState`（窗口激活/失焦）、status bar、`chat.request` 进度事件都能观察到 agent 活动，够做「agent 运行中」的粗粒度提醒，但拿不到「完成/报错/需授权」这类语义，也没有外部配置入口。
- **JetBrains AI Assistant / Visual Studio**：无 hooks 协议。只能走 UI Automation（识别 AI 面板/运行指示器）或日志/网络层旁路，成本高、易碎。
- 这两类**不能**用「同一份 settings.json」接入，需要单独工程。

---

## 5. XAssistant 统一接入：映射表与落地状态

```text
平台            hooks 配置                                  遮罩特征
Claude Code     ~/.claude/settings.json（hooks 节点）        —
Qoder CN        ~/.qoder-cn/settings.json（hooks 节点）      QoderComputerUse{Operation,Status}Overlay + QoderComputerUse.exe   ← 目前唯一有独立遮罩的
Trae CN         ~/.trae-cn/hooks.json（独立文件）             无（CU 在 icube 模块内，没有独立进程）
Trae 国际版      ~/.trae/hooks.json（独立文件）                无
Cursor          ~/.cursor/…（待校准）                         —
Windsurf        Cascade hooks（待校准）                       —
Codex CLI       .codex/hooks（待校准）                        —
VS Code         （无 hooks，只能写扩展）                       —
```

**已经落地的部分**（`mcp/agent-hooks/`）：

- `agent-status.ps1` —— 平台无关的状态脚本：stdin 收事件 JSON → 分诊 → 异步拉 `xa`（文案与颜色档跟 Qoder 版那套一致），脚本自身永远 `exit 0`；
- `install-agent-hooks.ps1` —— 多平台安装器：`-List` / `-Platform <名字>` / `-ProjectPath`（Trae 的项目级）/ `-Remove` / `-DryRun`，按目标文件的**已有结构自适应**（带 `hooks` 层就写里层，顶层直接是事件名就写顶层），只摘自己挂的条目；
- `selftest.ps1` —— 30 项离屏断言（装 / 幂等 / flat 自适应 / 拆卸不伤用户自己的条目 / 拒写未校准平台 / 事件分诊文案与静默）。

**监听器两条腿的现状**：

1. 遮罩窗口腿：`SetWinEventHook(EVENT_OBJECT_SHOW/HIDE)` + 类名/进程过滤（qoder 报告 §2.2 方案 A）——**目前只有 Qoder 一家有得吃**（Trae 没有独立遮罩，安装目录里也没有第二个进程）；
2. hooks 腿：共享 `agent-status.ps1`——已覆盖 Qoder / Claude Code / Trae（+ 待校准的 Cursor / Windsurf / Codex）。

---

## 6. 待办与待实测清单

**已在本次装机现场结清的**：

- [x] **Trae CN 是什么内核** —— VS Code 1.107.1 fork（`product.json` 实证，见 §1.1）
- [x] **Trae 的 hooks 落在哪** —— 独立 `hooks.json`（项目级 `.trae/hooks.json`、全局 `~/.trae-cn/hooks.json`）
- [x] **Trae 的事件集** —— `PreToolUse` / `Notification` / `Stop` / `PermissionRequest` 与 Claude Code 同构（安装包字符串实证）
- [x] **Trae 有没有独立遮罩** —— 没有：无独立进程、无独立窗口，CU 追踪在 icube 模块内部
- [x] **统一产出的雏形落地** —— `mcp/agent-hooks/`（状态脚本 + 多平台安装器 + 30 项自测）

**还需要真实运行才能定的**：

- [ ] 在 Trae 里打开 Hooks 面板加一条命令 hook，比对本仓库安装器写出的 `hooks.json`（外层到底带不带 `hooks` 键）
- [ ] 给 Trae CN 装上后真触发一次，核对事件 JSON 的真实字段名（`agent-status.log` 就是校准依据）
- [ ] Trae 的 hooks 配置是否热重载（Qoder 已知不支持，必须重启）
- [ ] 装/跑 **Trae Work**（`work.trae.cn`，即 Trae Solo 的升级形态）：确认它与 IDE 是否共用同一套 hooks
- [ ] 装/跑 **Qoder（qoder.com 国际版）**，确认 computer-use 的 overlay 类名是否与 Qoder CN 相同
- [ ] **Cursor / Windsurf / Codex** 各部署一次，核对事件集与配置路径（安装器现在默认拒写，先手动配一条看模板）
- [ ] **Lingma IDE（通义灵码）**：确认是否有 hooks 与 computer use
- [ ] Trae 的「电脑控制进行中」怎么从 IDE 外部看出来（没有遮罩可测，考虑状态栏 / 窗口标题 / UI Automation）

---

## 参考来源

- [Qoder 官方：Computer Use](https://docs.qoder.com/qoder/computer-use)
- [Trae CN 官方：通过 Hook 实现自动化](https://docs.trae.cn/ide_automate-actions-with-hooks) / [Hook 配置详解](https://docs.trae.cn/ide_hook-configuration-reference)
- [Trae CN 官方：电脑控制（Computer Use）](https://docs.trae.cn/ide_computer-use) / [Trae Work 电脑控制](https://docs.trae.cn/work_computer-use)
- [Cursor 官方：Hooks](https://cursor.com/docs/hooks.md?raw=1)
- [Windsurf 官方：Cascade Hooks](https://docs.windsurf.com/zh/windsurf/cascade/hooks)
- [字节 Trae IDE 真相：VS Code 内核的进阶定制版（cndba）](https://www.cndba.cn/article/13761)
- [品玩：TRAE SOLO 正式升级 TRAE Work](https://www.pingwest.com/a/314523)
- [品玩：2025-2026 AI 代码编辑器生态解析](https://www.pingwest.com/a/316737)
- [阿里云：Qoder CN 智能编程平台全解（Quest2.0/沙箱/Hook/CLI）](https://developer.aliyun.com/article/1759418)
- [阿里云：灵码 IDE 技术测评](https://developer.aliyun.com/article/1686817)
- [weykon/agent-hooks：统一 hooks 注册（Claude Code/Cursor/Codex/Windsurf/Kiro/OpenCode/Gemini）](https://github.com/weykon/agent-hooks)
- [codex-cli-hooks：Codex CLI hooks 社区文档](https://raw.githubusercontent.com/shanraisshan/codex-cli-hooks/main/.codex/hooks/HOOKS-README.md)
- [anthropics/claude-code：PermissionRequest hooks 细节 #11891](https://github.com/anthropics/claude-code/issues/11891)