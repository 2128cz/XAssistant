# 钩子接入 ↔ 模块开关 ↔ 消息播放 的联动（工作计划，2026-09-30）

## 0. 现象与根因（都实测过，不是推断）

用户报：面板上把 IDE 那张卡关掉，Qoder 还在放全屏消息，顶部消息栈也照收不误。

三层原因，没有一层怪面板：

1. **钩子不属于任何模块**。它是 `mcp/agent-hooks/install-agent-hooks.ps1` 独立写进 IDE 配置的
   （Qoder 是 `~\.qoder-cn\settings.json` 的 `hooks` 节点，Trae 是 `hooks.json`，DSH 是 profile 的
   `cordis.patch.yml`）。C# 侧**没有任何一行代码拥有它**（`grep install-agent-hooks Services/` 只命中
   `DshStatusModule` 的**说明文案**，不是调用）。所以模块开关与"IDE 会不会 spawn `agent-status.ps1`"无关。
2. **消息入口不看任何开关**。所有消息都汇到 `EffectQueue.Shared.Submit`（无头 `xa` 经命名管道、
   模块告警经 `EffectQueueWatchSink` 都是这一条），它只解析、不查这张卡允许不允许。
   而且 `agent-status.ps1` **现在根本不写 `-tag`**（实测 grep 零命中）——想按类挡也没有把手。
3. **卡关的是它自己那件事**。`qoder-watch` 关的是"尾随 agent.log 报 API 错误"，与钩子播放是两条路。

一条要紧的连带事实：`IdeProbe.Hooks()` 的措辞就是「已部署（**重启 IDE 生效**）」——
Qoder / Claude 的 hooks **不支持热重载**。⇒ 光把钩子卸掉，正在跑的那台 IDE 仍会按旧配置继续 spawn，
"关掉立刻不播"必须靠**应用侧的标签闸门**，不能只靠卸载。

## 1. 目标与不做什么

目标（用户原话拆成可验收项）：

- 模块开 → 注册钩子；模块关 → 去掉钩子；并且"钩子是否安装"在卡上是一个**开关行**（协议里 bool 行就是开关）。
- 消息带**多个 tag**：从哪儿来、什么类型、主对话还是子代理、报错还是状态——用来快速识别与针对性控制。
- 通过接口/面板**按类型控制播不播**：这张卡允许哪些类型的消息，逐个开关；没允许的就不该上屏。
- 三者必须**互相关联**，不再是"我随便写一条都能播、模块开关与它无关"。

不做什么：不改四段文案口径；不动 hook 的事件分档（黄/红/普通）；不把 hook 的分诊逻辑搬进 C#
（仍是一份脚本服务所有平台）；不新增前端控件类型（bool→勾选行、其余→输入行，协议已定）。

## 2. 设计：两层闸门 + 一份标签词表

| 层 | 谁生效 | 即时性 | 可逆性 |
|---|---|---|---|
| **L1 安装层** | 模块激活→装钩子；关闭→卸钩子；卡上「安装 hooks」开关行双向驱动 | 装/卸**立即改配置**，但 IDE 要重启才 spawn/停 spawn | 安装器只摘指向 `agent-status.ps1` 的条目，覆盖前 `.bak` |
| **L2 标签闸门** | `EffectQueue.Submit` 按「来源 + 类型」查这张卡的允许表，不允许就地丢弃 | **即时**（不依赖 IDE 重启，正好补 L1 的短板） | 纯内存表，改开关即变 |

**标签词表**（hook 侧写、面板侧读、`xa -k` 也能打）：

- 来源：`qoder` / `qoder-intl` / `trae` / `trae-intl` / `dsh` / `claude`（= `-from` 那个词，一个）
- 身份：`main` / `subagent`
- 类型：`ask`（请求人类介入/等待授权）、`done`（对话回合结束）、`tool-fail`（工具调用失败）、
  `interrupt`（意外中断对话）、`error`（模块尾随到的 API/配额错误）、`notice`（其它通知）

例：`-tag qoder,main,ask`、`-tag qoder,subagent,tool-fail`。

**多标签的落点**：`EffectCommand` 加 `Tags`（`-tag a,b,c` 逗号切；`Tag` 保留为第一个，
`xa -k -tag`、`GroupKey`、面板历史行都不必改口径）；`EffectSchedule.Kill` 的匹配改成"命中任一标签"。

## 2.5 一处必须先拆的雷：tag 现在同时被当成「身份」用（实测三处）

写多标签之前先查了 `Tag` 都被谁当什么用，结果它身兼三职，直接改成语义上的"分类标签"会把刚修好的东西撞回去：

| 位置 | 今天的用法 | 多标签后的后果 | 处置 |
|---|---|---|---|
| `EffectSchedule.Submit` → `FindByTagKey` | 标签相同＝「同一条告警又喊了一遍」，归并到原行重新计时 | 两条不同对话的 `qoder,main,ask` 会被并成一条 —— **正是用户报的那个症状复发** | 归并身份从 tag 里**搬出去**：新开关 `-id <词>`，只有写了 `-id` 才归并（UPS 轮询那类模块改用它）；hook 只写 `-tag`，永不归并 |
| `EffectCommand.GroupKey = Group ?? Tag ?? Source` | 同键排成一叠 | 标签进组键后，`qoder,main,ask` 与 `qoder,main,done` 变成**两组** → 不再同屏堆叠，改成排队互相等 | 组键改成 `Group ?? Id ?? Source`，分类标签不参与分组 |
| `EffectSchedule.Kill(channel, tag, contains)` | 与单个 tag 精确相等 | 按类型收（`-k -tag ask`）打不中 | 改成"命中任一标签"；`-k -id X` 另走精确比 |

一句话：**`-id` 是"这是同一件事"，`-tag` 是"这是哪一类"，`-group` 是"排成一叠"**。三者分开，之前是 `-tag` 一词三义。

## 3. 执行计划

### 步骤 1：标签通道（纯逻辑，不碰窗口）
- 前置：`EffectCommand.Tag` 现在是单值（`tag[0]`）；`Kill(channel, tag, contains)` 用 `string.Equals` 精确比。
- 改动：`Tags` 列表 + `-tag a,b,c` 解析（非法字符/空段拒绝，整条不认）；`Tag => Tags.FirstOrDefault()`；
  `EffectJob.Tags` 透出；`Kill` 改成"任一标签相等"；`EffectLogEntry` 历史行写成 `tag=a|b|c`。
- 判据：`effect-schedule-check` 新增：逗号切分、单值兼容、`-k -tag ask` 命中 `qoder,main,ask`、
  `-k -tag qoder` 不误伤 `trae,...`、空/畸形 `-tag` 整条不认。

### 2. 闸门表与 Submit 拦截
- 前置：`EffectQueue.Shared.Submit` 是唯一入口（已核）；模块激活/关闭由 `WatchModuleRegistry` 统一走且每步包 try。
- 改动：新增 `Services/MessageGate.cs`（纯逻辑：`来源 → 允许的标签集合`，可注册/注销/查询，带"被挡下"计数）；
  `EffectQueue.Submit` 在入队前问一次：消息带来源标签且该来源**已登记但类型不在允许集** → 丢弃并记历史；
  **没登记的来源**（裸 `xa` 命令、别的脚本、打字彩蛋）一律放行——闸门只关"有主的消息"，不变成全局黑名单。
- 判据：夹具断言三种情形（登记且允许 / 登记但类型关着 / 根本没登记），并断言"关掉立刻不播"不依赖 IDE 重启。

### 3. hook 侧写标签
- 前置：`agent-status.ps1` 现在零 `-tag`；四段文案的领词/现况已经在脚本里分好类（`Notice`/`StateFail`/`StateDone`）。
- 改动：在 `Show-Effect` 统一追加 `-tag <source>,<main|subagent>,<类型>`（类型从已有的分档处推导，不另起一套判断）；
  保持 UTF-8-BOM、永远 `exit 0`；改完**重装**三处部署副本。
- 判据：`selftest.ps1` 每条真事件的 `-tag` 都在词表内、子代理事件写 `subagent`、演练仍走独立 tag；
  真事件回放（`scratch/replay-emitted.ps1`）产物里能看到标签段。

### 4. 安装/卸载接线
- 改动：新增 `Services/AgentHooksInstaller.cs`——以**子进程**调仓库里的 `install-agent-hooks.ps1`
  （`-Platform <key>` / `-Remove` / `-Root`），回读 `IdeProbe.Hooks(路径)` 更新卡面状态；
  未校准平台（`codex`/`vscode`/`zcode`/`cursor`…）**不提供安装开关**，只显示状态与原因。
- 判据：`module-protocol-check` 用 `-Root` 指临时目录跑装→查→卸→查四步，断言只动我们自己的条目、
  用户已有条目原样保留、`.bak` 生成；不碰真 IDE 配置。

### 5. 卡面接线
- 改动：`IconBackdropModule` 这一族加三类行：`hooksOn`（安装开关）、`play-ask` / `play-done` / `play-tool-fail` /
  `play-interrupt` / `play-error`（类型开关，bool 行）、`hooksState`（只读状态行，含"重启 IDE 才生效"提示）。
  激活：登记闸门 + 装钩子；关闭：注销闸门 + 卸钩子 + 把该来源**在屏与在栈**的消息收掉（`xa -k -tag <source>`）。
- 判据：`module-protocol-check` 断言激活→临时根里出现条目、关闭→条目消失且 `.bak` 在；
  关掉卡之后同一来源的消息进不了 `EffectQueue`。

### 6. 文档与回归
- 同步 `CLAUDE.md`、`README.md`、`mcp/agent-hooks/README.md`、技能卡 `xassistant-watch-module`、本文件进度；
  跑 `run-gates.ps1` 六段 + `selftest.ps1`，分步提交（不推送）。

## 4. 风险与已定/待定

- **写用户 IDE 配置是机器级动作**。用户已明确要"打开模块就注册钩子"，所以默认**激活即装**；
  同时卡上给一个可取消的开关（"这张卡开着，但别碰我的 IDE 配置"是合法诉求），卸载只摘我们自己的条目并留 `.bak`。
- 卸载后 IDE 不重启 = 旧配置仍会 spawn：**L2 兜住"立刻不播"**，卡面状态行明说这一点。
- 词表要不要更多类型（如 `notice`）：先按现有分档来，缺了再加，别提前造。
- 待定 Q1：关掉卡时是否**顺手收掉该来源在屏/在栈的消息**（我倾向"是"，但它会改变现有 `off` 的语义边界）。
- 待定 Q2：`claude` 平台（协议源头、未本机实测）要不要一并给安装开关。

## 5. 进度（2026-10-02）

| 步骤 | 状态 | 证据 |
|---|---|---|
| 1 标签通道 | ✅ | `EffectCommand.Tags/-id/TagKey/HasTag/Producer`；`EffectSchedule` 按 `Id` 归并、`Kill` 命中「身份或任一标签」；`effect-schedule-check` 140 → 164 全绿 |
| 2 闸门 | ✅ | `Services/MessageGate.cs`（三条口径 + 计数）+ `EffectQueue.Submit` 入队前拦截、挡下记历史（新结局「被模块挡下」）；夹具 12 条闸门判据 |
| 3 hook 写标签 | ✅ | `Show-Effect` 统一追加 `-tag <来源>,<main\|subagent>,<类型>`；三处部署副本 md5 一致；`selftest.ps1` 185 → 195 |
| 4 安装器接线 | ✅ | `Services/AgentHooksInstaller.cs`（Platforms/Find/Status/Install/Remove/ScriptPath）；夹具在临时根上真跑装→查→卸→查 |
| 5 卡面接线 | ✅ | `IconBackdropModule` 追加安装开关 + 五个类型开关 + 只读状态行；激活＝登记闸门＋装钩子（后台线程）、关闭＝注销＋卸钩子＋收消息；`module-protocol-check` 74 → 86 |
| 6 文档与门禁 | ✅ | 文档已同步（CLAUDE / README / hooks README / 技能卡）；`run-gates` 六段全绿（1/2/3 一段一跑、4/5/6 一段一跑各 `VERDICT PASS`，`shots/suite-123c.txt` 与 `shots/suite-6b.txt`） |

**已知取舍**：类型词表在 C# 与 PowerShell 各有一份（跨语言没法共享常量），两边各配一条判据钉住它——
`MessageGate.Kinds` 的夹具判据 + `selftest.ps1` 的聚合判据；改词表要两边一起改。

## 6. 验收（观感与"真的不播了"归用户）

1. 关掉 Qoder 卡 → 立刻再触发一条对话消息 → 屏幕与顶部栈都不该出现；历史里能看到"被挡下"。
2. 重新打开卡 → 恢复播放；卡上"安装 hooks"取消勾选 → IDE 配置里我们的条目消失（用户自己的条目还在）。
3. 重启 IDE 后，未安装的钩子确实不再 spawn（这才是 L1 生效的时刻）。
4. 类型开关逐个试：只关 `done` 时，回合结束不再上屏，但"请求人类介入"照播。
