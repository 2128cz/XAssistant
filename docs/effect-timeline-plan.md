# 警告效果改造计划（时间轴 · 分组 · 背景立绘 · 入场动画）

> 立项：2026-09-22。需求来自实盘使用反馈，逐条对现有代码做过可行性核对（证据带 文件:行号）。
> 本文件是执行中的计划记录，做完一项就在 §6 打勾并留下验证数字；**不要**在实现完成后删掉本文件。

## 0. 现状基线（核对结论，先纠正几个前提）

| 以为 | 实际 |
|---|---|
| 没有队列，多条互相顶 | **已有**双通道 + tag 归并 + 抢占 + 重播配额：`Services/EffectSchedule.cs`（40 项断言在 `release/typing-games/effect-schedule-check`），200 ms tick |
| 队列已支持并排显示 | 队列是**单轨串行**：一条播完才播下一条（`EffectSchedule.cs:151-168`），所以"多条垂直排列"确实没做 |
| 彩蛋与警告同一套模板 | 同窗不同模板：彩蛋走 `EffectsWindow.xaml.cs:137 ShowBanner`，字号**硬编 46**、不呼吸、不入队、不进消息栈；命令行走 `:169 ShowCommand`（全参可控） |
| 需要手工做白色图标 | 不必：`OpacityMask = ImageBrush(icon)` + `Fill = tint` 可把任何带 alpha 的彩色图标变成可染色剪影（`extract-ide-icons.ps1:52-70` 抽的是彩色原图，无需重画） |
| `-from` 到处在用 | 只有消息卡消费（`EffectQueue.cs:135` → `MessageStackWindow.xaml.cs:155-216`）；**效果窗完全没用** |
| 斜线是静态的 | 斜线已是 `TileMode` 平铺 `DrawingBrush` + 绝对 `Viewport`（`EffectsWindow.xaml.cs:439-454`），**有 Viewport 但没有平移动画**；平移先例见 `Controls/KeyboardHeatmap.xaml.cs:684-690` |

## 1. P0 分组时间轴（结构性，先做）

**语义**：同组的多条消息**并排**显示，共享一套全屏 border 与呼吸；组内逐条入场，整组一起退场。

- 归组键：新增 `-group <名>`；没写时退到 `tag` → 再退到 `-from` → 再退到档位（`color+urgent`）。
- 组的 border 档位 = 组内**最高档**（error > warn > info），时长取组内最长单条。
- 时间轴：第 1 条入场 0.5 s → 第 2 条入场 0.5 s → …；每条驻留 `ScreenSeconds`（默认 3 s）。
  **组的到期时刻 = 最后一条的（入场完成 + 驻留）**，不是从第一条起算。
- 组显示期间**新加入**一条同类消息 → 插到组尾，并把**整组**的到期时刻按"从它加入时起算"重算（用户明确要求）。
- 队列改造点：`EffectJob` 增加 `Group` 与 `Entered`；`EffectSchedule.Tick/Pump` 从"选一条播"改为"选一组播 + 组内追加"；`Preempt`（紧急抢屏）保持整组级别。

**易用性调整（我的判断，需要你确认的在 §5）**

1. 一屏最多 **4 行**，第 5 条起继续排队（12 条堆满屏等于没有警告）。
2. 组总时长封顶 **12 s**（8 条 × 3 s = 24 s 会长期挡屏幕），封顶后按 FIFO 逐条退出而不是整组硬撑。
3. 「重新计时」只对**同组**生效，避免一个高频子代理把屏幕永久锁住。

## 2. P1 入场 / 驻留 / 退场动画（四步，按你给的顺序）

1. **粒子块扫入**：一条高亮斜线团从屏右向左快速平移（0.5 s，`TranslateTransform.X`）。
2. **警戒线跟着拉出**：斜线层套一个 `RectangleGeometry` clip，clip 的 `RectAnimation` 跟随粒子块右缘推进——扫到哪，线拉到哪。
3. **驻留**：斜线层 `Viewport.X` 缓慢循环平移（持续感），**文本保持居中不动**（文本不挂在平移层上）。
4. **退场**：clip 反向收 + 整层向右扫出；组的 border 同时结束。

可行性：全部是 WPF 标准动画能力；`Viewport` 是 DP 可用 `RectAnimationUsingKeyFrames`，平移层用 `TranslateTransform` 已有先例。**风险点**：`Tape` 现在是三列 Grid + 单一 `TapeText`，改成"多行 + 每行独立 clip/平移层"要把每行做成一个 `BannerRow` 控件（新建 `Views/BannerRow.xaml.cs` 或代码构建），这是 P0/P1 的共同前置。

## 3. P2 背景立绘（谁发的，一眼看见）

- 效果窗按 `-from` 取 `Assets/IdeIcons/<平台>.png`，以**剪影方式**（`OpacityMask`）铺在警告背后，颜色跟随当前档位 tint。
- 映射：`dsh → dsh.png`（DSH 小鲸鱼）、`qoder/claude → qoder.png`、`trae* → trae.png`；缺文件退化为无立绘（不画假样子）。
- 用户可配置：**新增模板基类** `Services/Modules/IconBackdropModule.cs`（照 `UpsModule` 的可编辑行 + `NumericOnly` 钳制，**不照** `IdeStatusModule`——它全是只读行）：
  参数行 = 开关、图标（平台名或 png 绝对路径）、尺寸 %、横向 %、纵向 %、不透明度 %。面板只有 CheckBox/TextBox 两种控件可用，所以位置尺寸一律用**百分比数字**，不做拖拽。
  另配命令行 `-icon on|off [尺寸] [x] [y] [alpha]`，两边同一套默认值。
- 加"试弹"按钮（`LogTailWatch.cs:66-84` 已有试弹先例与 tag 约定）。

## 4. P3 统一模板 + 加大日常警告的说服力

- 彩蛋的 banner 路径改为**走同一个 `BannerRow` 模板**（不再硬编 46），字号统一由 `-lable` 段/规则表给。
- 日常警告（hook/命令行）默认档比现在更重：字号 46 → **58**、驻留 5 s → **8 s**（`EffectCommand.cs:65` 的默认值），斜线瓦片高随字号走（已按 `字号*1.25` 联动，`xaml.cs:329`）。
- 换肤类彩蛋（`white/light`）**不**再顶掉正在播的警告：`ShowBanner` 改为入普通通道排队。

## 5. 需要你拍板的三处

1. 一屏最多几行、组时长封顶多少秒（我默认 4 行 / 12 s）。
2. "同类"默认按什么归组：`-group` 显式 > `tag` > `-from` > 档位（我按这个优先级）。
3. 日常警告默认字号/时长（我提议 58 / 8 s，会挡屏更多）。

## 6. 执行进度与验证

- [ ] P0 分组时间轴 —— 验证：`effect-schedule-check` 40 → ≥ 52 项（归组、组到期重算、4 行上限、12 s 封顶、整组退场）
- [ ] P1 四步动画 —— 验证：离屏逐帧采样（扫入中途 clip 右缘 ≈ 屏宽 50%、驻留期 Viewport 在动而文本 X 不动、退场后窗口自关）
- [ ] P2 背景立绘 + 模板基类 —— 验证：`module-protocol-check` 加参数行钳制断言；像素侧断言立绘非空且随档位变色
- [ ] P3 模板统一 + 默认档加粗 —— 验证：彩蛋与命令行两条路径字号一致；`selftest.ps1` 58 项不回归
- [ ] 文档：`CLAUDE.md` 效果段与 `README.md` 的 `xa` 语法同步新参数
