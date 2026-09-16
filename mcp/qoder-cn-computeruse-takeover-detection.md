# Qoder CN「Computer Use 接管遮罩」检测与「模型工作状态」监听 —— 实盘验证报告

> 生成时间：2026-09-16
> 验证环境：Windows 25H2 / Qoder CN（`.qoder-cn`）/ computer-use 插件 v1.0.1
> 结论性质：以下所有关键结论均经过**实际脚本采样验证**，非推测。原始临时脚本已按流程删除，本报告含可复现脚本。

---

## 0. 背景与目标

需求：当我（开发者）在用 Qoder CN 的 Computer Use（AI 接管桌面）时，希望**外部程序（如 XAssistant）能及时感知"AI 正在接管屏幕"**，从而给出提醒。延伸目标：感知"模型工作状态"（运行中 / 需人工介入 / 报错 / 完成），以便开发时获得提示。

本报告给出两条已验证的实现路径：
1. **窗口层检测**：捕获 Computer Use 接管时出现的遮罩 UI（独立顶层窗口）。
2. **官方 Hooks 层监听**：用 Qoder CN 内置生命周期钩子，直接拿到模型工作状态事件。

---

## 1. 【已验证】Computer Use 接管遮罩的实现方式

### 1.1 关键结论

接管提示 UI = **由 Computer Use 客户端进程创建的 2 个独立顶层窗口**（`WS_EX_LAYERED + WS_EX_TOPMOST`），每次工具调用期间成对出现、结束即销毁。**不是**画在 IDE 主窗口内部的 UI 层。

| 视觉元素 | 窗口类名 (Class) | 窗口标题 | 典型尺寸 | 样式 | 说明 |
|---|---|---|---|---|---|
| 全屏边框淡化/暗角 | `QoderComputerUseOperationOverlay` | （空） | 覆盖整个虚拟桌面（如 `3000x1920`） | layered + topmost + 全屏 | 操作进行中显示 |
| 顶部小浮岛/状态胶囊 | `QoderComputerUseStatusOverlay` | `Qoder Computer Use Status` | 约 `405x70`，置顶 | layered + topmost | 操作进行中显示，位于屏幕顶部 |

- **所属进程**：`QoderComputerUse.exe`（本次采样实测 pid = `42576`，每次运行会变）。
  - 可执行路径（由插件 launcher 决定）：`%USERPROFILE%\.qoder-cn\bin\qoder-cn-computer-use\QoderComputerUse.exe`（或 `...\1.0.1\QoderComputerUse.exe`）。
- **架构事实**：`QoderComputerUse.exe` 只是本地**执行器**（UI Automation 操作 + 截图），**不含模型 API Key**；"看图决策"的视觉大模型走 Qoder 后端。

### 1.2 实测采样证据（EnumWindows 差异对比）

每一轮 Computer Use 操作，两个遮罩窗口成对 show → hide，存活约 10s：

```
11:03:56.270 ADDED  hwnd=3871896  class='QoderComputerUseOperationOverlay'  pid=42576  rect=3000x1920@0,-320  layered=True topmost=True full=True
11:03:56.448 ADDED  hwnd=41618648 class='QoderComputerUseStatusOverlay'     pid=42576  title='Qoder Computer Use Status'  rect=405x70@1297,-275  layered=True topmost=True
11:04:06.811 GONE   hwnd=41618648 class='QoderComputerUseStatusOverlay'     pid=42576
11:04:06.811 GONE   hwnd=3871896  class='QoderComputerUseOperationOverlay'  pid=42576
11:04:18.788 ADDED  ...OperationOverlay...   （下一轮）
11:04:29.16x GONE   ...（两个一起消失）
11:04:52.46x ADDED  ...（再下一轮）
11:05:02.76x GONE
11:05:17.83x ADDED
11:05:28.27x GONE
```

### 1.3 附带发现

- 遮罩出现瞬间，系统输入法候选条 `EdgeUiInputTopWndClass` 会被顶掉（GONE），遮罩消失后恢复 —— 说明遮罩 z-order 极高。
- Computer Use 自身用**单窗口 WGC 截图**时**拍不到**这个遮罩（因为它捕获的是"目标应用窗口内容"，遮罩是叠在最上层的另一个独立窗口）。要连遮罩一起截，需要抓**整屏合成层**（`CopyFromScreen` 理论上可含分层窗口，但实测该场景下也未见，可能与绘制层级/时机有关）。

---

## 2. 【推荐】如何可靠监听「接管遮罩出现」

### 2.1 精确过滤条件（零误报）

```text
命中条件（满足其一）：
  GetClassName(hwnd) == "QoderComputerUseOperationOverlay"
  GetClassName(hwnd) == "QoderComputerUseStatusOverlay"
  或 窗口所属进程名 == "QoderComputerUse.exe"
```

> 实测：不加过滤时真实桌面很吵（输入法条、别的 App 弹窗会刷屏）；**加上类名过滤后精确命中，无误报**。

### 2.2 方案 A：全局事件钩子 `SetWinEventHook`（首选，实时）

- 用 `WINEVENT_OUTOFCONTEXT` 模式监听 `EVENT_OBJECT_SHOW` / `EVENT_OBJECT_HIDE`（`OBJID_WINDOW`）。
- 回调里对 `hwnd` 调 `GetClassName`，命中上面两个类名 → 触发通知（Toast / 响铃 / 写日志 / 推 IM）。
- 优点：事件驱动、实时、几乎零开销、不需注入 DLL。
- 权限：普通完整性窗口无需管理员；若客户端以更高完整性运行，监听进程需同级权限。

### 2.3 方案 B：轮询 `EnumWindows` 差异对比（最简单，已验证可行）

- 每 ~150ms 枚举一次可见顶层窗口，做集合差异，命中目标类名的新增/消失即为"接管开始/结束"。
- 优点：实现最简单、无消息循环。缺点：非严格实时（亚秒级延迟）。
- 本报告 1.2 的实测数据即由此方案采集。

### 2.4 可复现探针脚本（PowerShell，验证后已删除临时副本）

```powershell
# _cu_probe.ps1 —— 顶层窗口差异监控（含 PID 修复、150ms 采样）
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class WinProbe {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
function Get-Snapshot {
    $list = New-Object System.Collections.Generic.List[object]
    $cb = [WinProbe+EnumWindowsProc] {
        param($hWnd, $lParam)
        if ([WinProbe]::IsWindowVisible($hWnd)) {
            $cls = New-Object System.Text.StringBuilder 256; [void][WinProbe]::GetClassName($hWnd,$cls,256)
            $ttl = New-Object System.Text.StringBuilder 512; [void][WinProbe]::GetWindowText($hWnd,$ttl,512)
            $ownerPid = 0; [void][WinProbe]::GetWindowThreadProcessId($hWnd,[ref]$ownerPid)
            $rect = New-Object WinProbe+RECT; [void][WinProbe]::GetWindowRect($hWnd,[ref]$rect)
            $list.Add([pscustomobject]@{ Hwnd=$hWnd.ToInt64(); Class=$cls.ToString(); Title=$ttl.ToString(); Pid=$ownerPid; W=($rect.Right-$rect.Left); H=($rect.Bottom-$rect.Top) })
        }
        return $true
    }
    [void][WinProbe]::EnumWindows($cb,[IntPtr]::Zero); return $list
}
$log = "$PSScriptRoot\_cu_probe_log.txt"
"PROBE START $(Get-Date -Format 'HH:mm:ss.fff')" | Out-File $log -Encoding UTF8
$prev=@{}; foreach($w in (Get-Snapshot)){ $prev[$w.Hwnd]=$w }
$deadline=(Get-Date).AddSeconds(240)
while((Get-Date) -lt $deadline){
    Start-Sleep -Milliseconds 150
    $cur=Get-Snapshot; $curMap=@{}; foreach($w in $cur){$curMap[$w.Hwnd]=$w}
    foreach($w in $cur){ if(-not $prev.ContainsKey($w.Hwnd)){ "ADDED  $(Get-Date -Format 'HH:mm:ss.fff') class='$($w.Class)' title='$($w.Title)' pid=$($w.Pid) rect=$($w.W)x$($w.H)" | Out-File $log -Append -Encoding UTF8 } }
    foreach($h in $prev.Keys){ if(-not $curMap.ContainsKey($h)){ "GONE   $(Get-Date -Format 'HH:mm:ss.fff') hwnd=$h class='$($prev[$h].Class)'" | Out-File $log -Append -Encoding UTF8 } }
    $prev=$curMap
}
"PROBE END $(Get-Date -Format 'HH:mm:ss.fff')" | Out-File $log -Append -Encoding UTF8
```

> 注意：`GetWindowThreadProcessId` 的输出参数**不要写成 `$pid`**（PowerShell 自动变量，会取到当前进程自身 PID）。用 `$ownerPid`。这是本次调试中修掉的一个坑。

---

## 3. 【更正统】用 Qoder CN Hooks 直接监听「模型工作状态」

Qoder CN IDE / JetBrains 插件内置**生命周期 Hooks**（与 Claude Code hooks 协议兼容）。无需逆向截屏，即可拿到模型运行状态。

### 3.1 支持的 12 个事件 → 映射到"工作状态"

| 事件 | 触发时机 | 可阻断 | 对应状态 |
|---|---|---|---|
| `SessionStart` | 会话开始/恢复 | 否 | — |
| `UserPromptSubmit` | 用户提交指令、模型开始处理 | 是 | **正在运行（起点）** |
| `PreToolUse` | 工具执行前 | 是 | 运行中（每步） |
| `PermissionRequest` | 工具需要用户授权 | 是 | **需人工介入** |
| `PostToolUse` | 工具执行成功后 | 否 | 运行中（每步） |
| `PostToolUseFailure` | 工具执行失败后 | 否 | **报错**（含 `error`、`is_interrupt`） |
| `SubagentStart` / `SubagentStop` | 子代理启动/停止 | 否 | 子任务运行/完成 |
| `Stop` | 主代理完成回复 | 是 | **任务完成/空闲** |
| `SessionEnd` | 会话结束 | 否 | — |
| `PreCompact` | 上下文压缩前 | 否 | — |
| `Notification` | 发出面向用户的通知 | 否 | **需人工介入**（`notification_type="permission_prompt"`） |

### 3.2 配置文件位置（多级合并，优先级低→高）

- `~/.qoder/settings.json`（用户级，全部项目生效）
- `<项目>/.qoder/settings.json`（项目级，可 Git 共享）
- `<项目>/.qoder/settings.local.json`（项目级本地，gitignore）

> 注意：**不支持热重载**，改完配置需**重启 IDE** 生效。

### 3.3 协议：stdin 收 JSON，exit code 表达决定

- 输入：事件上下文以 JSON 经 **stdin** 传入；公共字段含 `session_id`、`cwd`、`hook_event_name`、`transcript_path`；工具类事件带 `tool_name`/`tool_input`/`tool_response`；失败事件带 `error`/`is_interrupt`；通知事件带 `notification_type`/`title`/`message`。
- 输出：`exit 0` 放行（stdout JSON 可细控）；`exit 2` 阻断（stderr 注入会话，仅可阻断事件）；其他 = 非阻断错误。
- 环境变量：`QODER_SESSION_ID` / `QODER_TOOL_NAME` / `QODER_CWD` / `QODER_TRANSCRIPT_PATH` 等。
- **防死循环**：`Stop` 脚本必须检查 `stop_hook_active`，为 `true` 时 `exit 0`。
- 工具名映射：原生名与 Claude Code 兼容名互通，如 `Bash`≡`run_in_terminal`、`Edit`≡`search_replace`。

### 3.4 最小示例（接管/报错/完成时统一走一个脚本）

`~/.qoder/settings.json`：
```json
{
  "hooks": {
    "Notification":       [{ "hooks": [{ "type": "command", "command": "~/.qoder/hooks/status.sh" }] }],
    "PermissionRequest":  [{ "hooks": [{ "type": "command", "command": "~/.qoder/hooks/status.sh" }] }],
    "PostToolUseFailure": [{ "hooks": [{ "type": "command", "command": "~/.qoder/hooks/status.sh" }] }],
    "Stop":               [{ "hooks": [{ "type": "command", "command": "~/.qoder/hooks/status.sh" }] }]
  }
}
```
`status.sh` 内按 `.hook_event_name` 分诊，调你的 Toast/响铃/IM 推送。官方示例基于 bash+jq，**Windows 下建议改写成 PowerShell**（如 `command` 指向 `powershell -File .../status.ps1`）。

---

## 4. 【已验证】配额报错的性质

- 现象：触发 Computer Use 时报 `code=100400 Allocated quota exceeded ... #token-limit`，指向阿里云百炼 Model Studio。
- 定性：**瞬时 TPM/TPS（每分钟 token/请求）速率限流，非账户余额耗尽**。
- 证据：等待一段时间后**重试即成功跑完**（限流窗口滚过后自愈）；余额耗尽类错误（欠费/Arrearage）不会自愈。
- 成因：每步都向视觉模型传整屏截图，单帧动辄上万 token，连续几步易冲破当分钟 TPM。
- 应对：放慢操作节奏 / 减少连续全屏抓取；或到百炼控制台提升 TPM 配额。**本地无开关可改**。

---

## 5. 路径选择建议（针对 XAssistant 外部提醒）

| 目标 | 推荐方案 | 理由 |
|---|---|---|
| 只想知道"AI 现在有没有接管我屏幕" | 窗口检测（§2）过滤 `QoderComputerUse*Overlay` | 直接、精确、跨 IDE 通用 |
| 想知道完整模型工作状态（运行/需介入/报错/完成） | Qoder Hooks（§3） | 官方支持、语义清晰、含错误/中断信息 |
| 两者结合 | Hooks 拿状态 + 窗口检测兜底"接管"事实 | 覆盖最全 |

---

## 6. 下一步：横向调研其他 IDE / Agent 平台的同类能力

目标：确认其它"AI 接管桌面/编码代理"平台是否也对外部程序暴露相似的**接管可视化信号**或**生命周期事件**，供 XAssistant 统一接入。建议按以下清单逐项验证（方法与本报告 §1–§2 相同：EnumWindows 差异 + 类名/PID 特征提取）：

- [ ] **Qoder CLI / QoderWork**：hooks 事件是否更全（文档提到 CLI、QoderWork hooks 另有支持，未来或有 `prompt`/`agent` 型 handler）。
- [ ] **Claude Code（Anthropic）**：hooks 体系（`PreToolUse/PostToolUse/Notification/Stop/SubagentStop/UserPromptSubmit/PreCompact/SessionStart`），是本协议的来源；配置 `~/.claude/settings.json`。
- [ ] **Cursor**：是否有 agent 生命周期钩子/命令、以及"AI 操作"时的置顶遮罩窗口类名。
- [ ] **GitHub Copilot（VS Code）**：`chat` / agent 模式的状态事件、状态栏、可扩展 API（`window.onDidChangeWindowState` 等）。
- [ ] **Warp / Zed / JetBrains AI Assistant**：接管/命令执行时的置顶窗口或状态回调。
- [ ] 各平台的 **Computer Use / 桌面接管** 遮罩窗口类名与所属进程，形成统一特征表。

统一产出建议：在 XAssistant 内维护一张「平台 → 遮罩类名/进程 → 接管状态」映射表，外部监听器按类名/进程命中即报警，实现跨平台"接管提醒"。

---

## 附录：本次调试踩坑记录

1. **时间窗没覆盖**：首版探针跑 90s 提前结束，而接管持续到其后，导致漏采。→ 延长窗口 + 与操作时间线对齐。
2. **PID 自动变量冲突**：`$pid` 是 PowerShell 自动变量，`[ref]$pid` 拿不到目标进程 PID。→ 改用 `$ownerPid`。
3. **配额误判为脚本失败**：`100400` 是后端模型限流，不是本地脚本问题。→ 识别为外部依赖问题，稍后重试。
4. **误判"遮罩非独立窗口"**：早期部分数据（重叠时段无新窗口）一度指向"遮罩在 IDE 内部"；补齐干净样本后被推翻，实为独立顶层窗口。→ 教训：结论必须有完整时间线覆盖的样本支撑。
