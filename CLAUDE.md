# CLAUDE.md

本文件为 Claude Code（claude.ai/code）在本仓库中编写代码时提供指引。

## 项目概述

XAssistant 是一款 Windows 桌面活动监控应用（WPF、C#、.NET 8），负责记录：

- **鼠标点击**（左/中/右键），通过低级 Win32 钩子实现
- **键盘按键**，通过低级 Win32 钩子实现
- **各应用使用时长**（哪些进程被使用了多久），通过 WMI 进程事件实现
- **电脑整体使用时长**（开机/睡眠/唤醒）——由同解决方案里的 `XAssistant.Service` 项目采集，以 Windows 服务 `XAssistant.UsageTracker` 独立进程运行，见下方「后台服务依赖」节
- **灵感速记唤起**（xapp 速记功能的 OS 级入口）——全局热键 `Win+Numpad0` 唤起原生速记窗并直插速记库，见下方「速记唤起」节

UI 采用基于 CommunityToolkit.Mvvm 的 WPF MVVM 架构。项目没有自动化测试。

## 常用命令

- 构建：`dotnet build`（需要 .NET 8 SDK；主程序与后台服务都是 `net8.0-windows`）
- 运行（调试）：`dotnet run`
- 发布（Release，win-x64）：`dotnet publish -r win-x64 -c Release` —— 输出位于 `bin\Release\net8.0-windows\win-x64\publish`（`build.bat` 封装了这条命令）
- 部署并重新启动：`powershell -ExecutionPolicy Bypass -File deploy.ps1` —— 默认部署到 `<系统盘>\XAssistant`（可用 `-TargetDir` 改），必须以管理员身份运行；该脚本会停止正在运行的实例、备份旧版本、把发布输出（自己发到 `bin\deploy\publish`，不依赖目标框架名）复制过去、启动应用，并把 `xa` 命令注册进用户 PATH（`-SkipXa` 跳过这步，注册脚本为根目录 `register-xa.ps1`）
- 格式化代码：`dotnet tool restore && dotnet csharpier .`（CSharpier 1.2.6 是 `dotnet-tools.json` 中固定的格式化工具）。注意仓库当前并未全量格式化（`dotnet csharpier check .` 在 102 个文件里报 65 个），直接跑 `csharpier .` 会产生大面积无关改动；新代码跟邻近文件的现有风格保持一致就好

## 数据存储

所有应用数据存放在 `%APPDATA%\XAssistant`（Release）或 `%APPDATA%\XAssistant_Dev`（Debug）。该区分由 `AppDataPathHelper` 中的 `#if DEBUG` 控制，**同时**影响数据文件夹和开机自启注册表键（开发版使用 `XAssistant_Dev`），因此 Debug 构建永远不会触碰生产数据。在该文件夹内：

- `click_data.db` —— 表 `ClickRecords(Id, Button, ClickTime)`
- `key_data.db` —— 表 `KeyPressRecords(Id, Key, PressTime)`
- `app_usage.db` —— 表 `ProcessSession(Id, ProcessName, StartTime, EndTime, WindowTitle, Date, AccumulatedSeconds, LastUpdateTime)`；WAL 模式；由 `ProcessUsageTracker` 写入
- `appsettings.json` —— `AppSettings`（录制自动启动开关、窗口大小、日志面板状态、速记唤起 `QuickNote` 段——含速记库连接串 `QuickNote.ConnectionString`），由 `ConfigurationService` 负责
- `logs/xassistant-<日期>.log` —— Serilog 滚动文件日志（保留 31 天）

时间戳以字符串形式存储：点击/键盘数据库使用 ISO 8601（`"o"`）格式，`ProcessSession` 使用 `yyyy-MM-dd HH:mm:ss.fff`（另有独立的 `Date` 列，格式为 `yyyy-MM-dd`，用于按天分桶）。

### 路径约定（仓库要推公网，不写本机布局）

- 主程序自己的数据一律由 `AppDataPathHelper.GetAppDataFolder()` 算，不在调用方拼绝对路径；设环境变量 `XASSISTANT_DATA_DIR` 可整体改道，便于冒烟测试起一个不碰正式库的实例。
- 配置项里需要填文件位置时（如 `QuickNote.DatabaseUrlEnvPath`）**默认按数据目录解析相对路径**，只有跨盘时才需写绝对路径；代码里的默认值不得出现某台机器上的目录。
- 唯一例外是下面那个跨进程的 `%ProgramData%` 目录：服务进程的当前目录是 System32、身份是 LocalSystem，只能用绝对路径，但也由 `CommonApplicationData` 推导而不写死盘符。
- `.env`、`*.db`、日志、夹具快照这类**本地数据与凭据文件一律由 `.gitignore` 挡住**，不进版本控制；`appsettings.json` 在数据目录而不在仓库里，原因同样是它可能带连接串。

## 后台服务依赖（重要）

“电脑使用统计”页面（`UsageViewModel`）**不读取**主程序自己的数据库。它通过命名管道 `UsageTrackerPipe` 和 `%ProgramData%\XAssistant\UsageTracker\pc_usage.db` 这份 SQLite 库来消费独立的 `XAssistant.UsageTracker` Windows 服务；服务未运行时回退到直接读库，库也不存在才显示“无法获取”。管道服务端与 `pc_usage.db` 的写入方都在本仓库的 `XAssistant.Service` 项目里，与主程序分进程、分装配：目录约定两边各写一份（主程序 `Services/AppDataPathHelper.GetUsageTrackerFolder()`、服务 `XAssistant.Service/UsagePaths.cs`），**两边必须算出同一路径，改一处要同步另一处**。

## 架构

### 启动引导与导航
- 所有装配都在 `App.xaml.cs` 的 `OnStartup` 中手动完成，使用 `Microsoft.Extensions.DependencyInjection`（不使用 generic host）。`LogBufferService` 在启动时先创建一个实例，同时供 Serilog 的 `UiLogSink` 和 DI 使用，这样在容器构建完成之前应用内日志面板就能工作。
- 常驻启动还会开 `NotificationPipeServer`（命名管道 `XAssistant.Notify`）：无头 xa 实例转来的整条命令在这里走 `EffectDispatch`——持久消息栈只有常驻进程养得住；管道被别的实例占着只记日志不报错。
- `ProcessUsageTracker` 在启动时立即启动，在关机、会话结束或托盘菜单退出（`App.IsShuttingDown`）时停止并保存。
- 主窗直接嵌入 `DashboardView`（`MainWindow.xaml` 里 `DataContext="{Binding Dashboard}"`），**没有页面切换**：顶部「实时概览 / 输入分析 / 会话记录」与页脚的 ⚙ 只经 `NavigateSection`（`MainWindow.xaml.cs`）调 `DashboardSurface.ScrollToSection` 做滚动定位。`MainWindowViewModel` 负责窗口大小持久化、开机自启开关、底部日志面板与导出记录。
- `DashboardViewModel` 聚合六个模块的 ViewModel（`Mouse` / `Keyboard` / `Usage` / `Apps` / `Settings` / `Practice` / `WordFrequency`），并把变化转发到自身属性供面板绑定。所有模块 ViewModel 都是单例，因此无论滚动到哪一屏，实时数据（钩子、定时器）都会持续流动。（旧版 `HomeViewModel` 与各模块独立 View 已是死代码，未注册、未引用。）

### 工作台「大嵌小」与逐行分栏
`DashboardView` 是一页到底的工作台，只有三块板（`Board` 样式，一块一个边框）：PART 1 概览（标语 + 四格累计）、PART 2 输入（节奏曲线带六格速率 / 热力图带排行与最近按键 / 短句练习带成绩与词频）、PART 3 记录（会话表 / 原始记录 / 设置）。三块板恒为通栏，**切换发生在每一行内部**：每一行都是「主体格 + 340 右栏」，够宽时子参数站在主体右边，不够宽才摞回主体下方。格与格之间靠 1 px 发丝线（`BoardRule`，或格子自身的 `BorderThickness`）分开，不靠空白分块——每卡 20 px 的沟就是松散的来源。

- 判据只看页面实际宽度，**不看显示器横竖屏**：`DashboardView.RefreshLayoutSignals` 按 `ActualWidth` 算 `IsRailSplit`（四行共用一档）与 `IsOverviewInRow`。屏幕比例一票否决曾把 1100 宽的窗口整页摊平（页面 1003 够放 340 的读数栏却不让放），这条已废；`SystemParameters.StaticPropertyChanged` 现在只用来重算窗口 `MinWidth`（竖屏 1080 放不下默认 1100 的窗口，`MainWindowViewModel.WindowMinWidth` 跟着 `DashboardViewModel.IsLandscapeScreen` 走）。
- 四行共用一个档，不再给热力图行单立更严的下限：`RailSplitMinWidth = 940` 在 `DashboardView` 与 `DashboardViewModel`（只为写进 `LayoutModeNote`）各存一份；`DashboardView` 另有两个常量——`CardsInRowMinWidth = 1000`（顶部四卡一排）与 `RailHardMinWidth = 700`（「横向 · 分栏」档的硬下限）。键盘被挤窄后的可读由控件自己补救：`KeyboardHeatmap.ApplyKeyCapFonts` 按 Viewbox 实际缩放反算键帽名字号（`MinLabelPx ÷ scale` 向上取整，clamp 到 16 号）；计数是次要信息，同样反算但 clamp 到 12 号，放大到上限还读不动（`12 × scale < 6.5`）就整行收掉，不糊在键帽上。
- 设置里的「界面布局」三档：「自动 · 按宽度」走 940 / 1000 两条下限；「横向 · 分栏」跳过可读性下限、只留硬下限 700（340 的右栏塞得进就拆，键帽小一点是用户自己的选择）；「纵向 · 单栏」一票否决。VM 侧只暴露两个布尔：`AllowsRailSplit`（是否否决）与 `PrefersWideRail`（是否只看塞得进），落盘仍是 `Auto` / `Wide` / `Tall`。
- 实测账（离屏夹具 `bin/boardshot`，真 View + 真 VM，只把钩子与数据库换成假的）：页面 1003（1100 宽窗口）下**四行全部并排**，混排 2089 px、单栏 2896 px，省 807；键盘实绘 629 px = 设计的 0.562，键帽名由 10 号提到 16 号、屏幕实落 9.0 px（下限 7.5，计数仍显示）；页面 943 以下计数收掉（12 号也只有 6.1 px）。页面 703 摊平，排行与最近按键在栏内再并排一次。夹具按「窗口宽 − 97」复现页面宽（边框 16 + 滚动条 17 + 页边距 64），拿窗口宽当页面宽会高估出一个不存在的档。
- 排布切换全部由 `DashboardView.xaml` 里的布局 `Style`（`OverviewCards` / `RhythmTiles` / `MainCell` / `RailCell` / `RailRanking` / `RailRecent`）的 `DataTrigger` 改 `Grid.Row` / `Grid.Column` / `Grid.ColumnSpan` / `Columns` / `BorderThickness` / `Margin` 完成。**同一棵树只有一份内容**——这些控件都带状态，复制两套布局就会变成双份订阅与双份焦点。热力图行与其它三行共用 `MainCell` / `RailCell`，不再单开 `Heat*` 样式（`HeatMainCell` / `HeatRailCell` 已删）：键盘挤窄后的可读由控件自己的字号补偿负责，与分栏档无关，样式只需一份。
- 那条分隔线在「并排」与「摞上下」之间切换时只换 `BorderThickness` 的朝向（左边线 ↔ 上边线），不额外留沟列：沟是空白，线不是空白。热力图行摊平时，栏内的排行与最近按键再并排一次（`RailRanking` / `RailRecent`），否则 34 号的读数会一路飘到板右缘。
- 因此受 `Style` 控制的属性（`Grid.Row`、`Grid.Column`、`Grid.ColumnSpan`、`UniformGrid.Columns`、`BorderThickness`、`Margin`）**绝不能再写本地值**：WPF 里本地值优先级高于 `Style` 触发器，写了触发器会静默失效。
- 子控件（`VersePractice` / `PracticeScoreboard` / `WordFrequencyPanel`）不再自带浮卡边框与外边距，贴板边由 `DashboardView` 给；否则一块小格里套两层卡、叠两份 20 px 留白。
- 两处随窄布局暴露出来的冗余：曲线被挤到 660 宽之后峰值线两端的标注会撞车，采样口径那串元信息（`CadenceSummary`）从峰值线右侧挪进下方的轴标注行（那里原来那句「每秒敲击 · 近 1 秒滚动窗口…」是同义重复，一并换掉）；时段选择器删掉了右栏里那排 `ListBox`：它与热力图标题行的 `ComboBox` 绑同一个属性、两处选互相覆盖，还白吃 70 px 行高（板 1 板头另留一个下拉作为全局快捷入口，同一属性两处入口已经嫌多，三处就只剩互相打架），连带 `DashboardStyles.xaml` 里的 `PeriodSelector` / `PeriodChoice` 两份死样式一起删了。

### 键盘热力图的透视倾转
`Controls/KeyboardHeatmap` 把 144 键当成一块悬在屏幕前方的刚性板，敲击位置决定姿态。模型层全在 `Controls/KeyboardTilt.cs`，按三层拆开：`KeyboardTilt` 是纯几何（单应投影 + 逐键一阶仿射 + 越界回收）、`TiltSpring` 是单轴的二阶阻尼跟随、`TiltDriver` 管时间与渲染循环的挂卸；控件只负责两头——把键名换算成敲击向量、把算出的姿态写回视觉树。三层都是 `internal`（离屏夹具与控件同装配，看得见，验证不必为此开洞）。

- **仿射 + 斜切做不出透视，这不是调参能救的**：仿射矩阵只有一份线性部分，平行线映完还是平行线、等长的平行线段映完还是等长，所以「远边比近边短」永远画不出来；斜切只能把角歪掉，拉不开尺度差。真透视是射影变换（单应），必须让远端真的变小。
- 姿态的算法：把板面点 (x,y,0) 绕「过板心且垂直于敲击方向」的板内轴 â = (−Ny, Nx) 倾转 α 得到 3D 点（纵深 Z = −sin α·(n̂·p)），再过一道针孔相机 `w = 1 + sinα·along/Depth` 做透视除法，然后绕视线滚 γ、朝敲击方向跟 T、越界回收 S。α=0 或 Depth→∞ 时严格退回原来的正交模型。
- 但 WPF 的 `MatrixTransform` 只能装仿射，所以**单应要逐键压到每颗键帽上**：键帽中心走精确投影，中心处用中心差分求雅可比当线性部分（`KeyboardTilt.ApplyPose`）。键帽只几十设计单位宽，二阶残差≈½·|f″|·L² 随宽度平方增长，最宽的空格上约 2 个设计单位（屏幕上 1.5 px），看不出来；144 块拼起来就是完整的梯形轮廓。旧模型那块「整块板共用的仿射四件套」已整体删除，`TiltLimits` 里的 `ShearDegrees` 也删了——切变现在由透视自然产出。
- 变换因此挂在 144 个 `ContentPresenter` 容器的 `RenderTransform` 上（`KeyboardHeatmap.AttachKeyTilts` 走 `ItemContainerGenerator` 取容器，一次建好长期复用，每帧只改 `.Matrix`），键盘块 `ItemsControl` 本身不再挂变换。Canvas 面板不虚拟化，容器不会中途消失；但**容器没生成齐时一帧都不写**，否则写了一半会让剩下的键帽停在上一帧位置。键帽的 `RenderTransformOrigin` 必须留在默认 (0,0)：那块矩阵里已经把「从键帽左上角算起的平移」一并带上了。
- 越界回收把板框四角的外接框压回「框 + 余量」，余量在 XAML 里就是 Viewbox 的 `Margin`，改一处要同步 `TiltLimits.Default` 的 `HeadroomX/Y`。`DepthBoardWidths`（相机距离，以板宽为单位）决定近大远小有多狠，2.2 个板宽下敲最右时近端键帽比远端大约 21%。
- 三处跨文件同值的约定：键帽行高 43（XAML 的 `ContentPresenter Height` 与 `KeyboardHeatmap.KeyRowHeight`）、板框 1120×331（XAML 声明的尺寸与由 `Layout` 极值算出的 `BoardBox`）、WPF 的 `Matrix` 是行向量约定（存的是数学矩阵的转置，而屏幕 y 轴朝下，所以正角就是顺时针）。
- 高频节拍 DP 一律走 `RegisterTick`（`KeyStrikePulse` 与鼠标那一组共用同一个注册助手），绝不能挂 `VisualPropertyChanged`，否则每敲一键都重建 144 个键帽的着色；同一个键连击时键名不变，重播只能靠自增脉冲。
- `CompositionTarget.Rendering` 是静态事件，挂与卸都收在 `TiltDriver` 里：姿态回到水平就立刻退订，`Unloaded` 里也必须调 `Reset()` 退订并归位，否则换页卸掉的控件仍被渲染循环拽着。归位时 `ApplyPose(0,0,…)` 写的是**精确的** `Matrix.Identity`，不留浮点尾巴。
- 离屏夹具 `bin/tiltshot`（只 `Compile Include` 那两个源文件 + XAML，不引用主工程）拿两条「仿射做不到」的性质当判据：同一排键帽的竖边长度近端/远端 > 1.15，与各排上沿斜率沿纵向单调收拢（仿射下这个差恒为 0）；另外还比 576 个角点的二阶残差、全姿态 60×60 网格扫描的越出量、与单帧 144 块写回成本。

### 四个模块
每个模块 = 钩子服务 + SQLite 仓库 + ViewModel + View：
1. **鼠标点击** —— `MouseClickHookService`（WH_MOUSE_LL）→ `ClickDatabaseService` → `ClickCounterViewModel`
2. **键盘** —— `KeyboardHookService`（WH_KEYBOARD_LL，长按去重按物理键即扫描码 + 扩展位判定，修饰键与 Shift 状态实时取自系统键态；按键名去重会让主键盘 4 与小键盘 4、两个 `.` 互相吞掉，丢过一次 key-up 还会让某个字符从此打不出来）→ `KeyDatabaseService` → `KeyCounterViewModel`
3. **各应用使用时长** —— `ProcessUsageTracker` → `app_usage.db` → `AppUsageViewModel`（2 秒 `DispatcherTimer` 轮询）
4. **电脑使用时长** —— 外部服务（见上文）→ `UsageViewModel`（5 秒 `DispatcherTimer` 轮询）

实时 UI 更新全部通过 `DispatcherTimer` 轮询或事件驱动计数器实现——没有发布/订阅总线。

### 随机打字练习的字符口径
题库按 `Assets/Practice/<语言>/<句式>.json` 两级目录加载（`PracticeScoreStore.LoadCatalogTree`），要敲的是 `text + suffix`，`annotation` 只是卡片上方给人读的。出题约束与验证场景见 `docs/typing-practice.md`，这里只记三条容易踩的：

- **不可见字符靠黑名单，不靠人工发现**：`PracticeText.Sanitize` 在加载时逐字段洗一遍——控制字符整段按 `char.IsControl` 认（含 JSON 转义混进来的 `\n` `\r` `\t` 与 C1 段），零宽 / 方向标记 / 软连字符 / BOM 按码位列出来，一律剔除；NBSP 这类怪空白折成半角空格（直删会把两个词粘成一个）。它们留在正文里就是一格永远敲不上的死位（进度卡在 N-1/N 那类死锁的根子），留在注音里则会把「这是音标还是要读的正文」判定带偏。
- **洗不能静默**：码位记到 `PracticeCatalog.IgnorableFindings`，`PracticeViewModel.CatalogWarning` 在练习块顶部念出来（`HasCatalogWarning` 控制那一行的显隐）；`Validate` 里这条检查排在「单词缺少文本」前面，因为 `\n` 也是空白，不排队就会被误报成缺字段。
- **注音不参与统计，显示判定走排除法**：进度分母与逐格计数只数 `text + suffix`，全角折叠（`Normalize` / `Fold`）也只作用在要敲的正文上。包不包 `/…/` 由 `PracticeText.IsReadingScript`（假名 / 汉字 / 谚文 / CJK 标点 / 长音符）与「整串有没有字母」决定；写成「整串是不是 ASCII」的白名单会把英语 IPA 的 `ː ð ʌ ə æ` 当成假名，一下弄掉 614 个注音的斜杠。`bin/practicheck` 把这两种极性逐个断言，改回白名单当场就红。

### 打字关键词彩蛋（换肤 + 浮岛粒子 + 警告带）
入口在 `Services/Keywords/`（`KeywordRule` / `KeywordCatalog` / `KeywordWatcher`）、`Services/ParticleMotion.cs` 与 `Views/EffectsWindow`。

- 切词直接复用 `WordFrequencyStore.DecodeKey`（字母数字算词、其它键是分隔符、退格撤销一个字符）——「词频面板里看到的词」与「能触发彩蛋的词」必须同一批。中文输入法下拿到的是拼音字母，所以词表只收 ASCII 词。
- 命中是**边打边判**：缓冲区一等于某个词就触发，不等空格；同一个词没被分隔符冲掉前只算一次。代价是 white 也是 whiteboard 的前缀，会提前掉一次。
- 主题色是程序化的：`ThemeManager.ApplyPalette(base, name, colors)` 在主题字典之上叠一层覆盖，只认 `BrushKeys` 列出的画刷键，认不出的键与写错的颜色逐条忽略（换肤是彩蛋，不该有让界面崩掉的可能）；`Apply(theme)` 会连带抹掉覆盖层，所以设置里那两个主题按钮就是「回到原色」的出口。关键词换肤**不落盘**（误触一个词不该改掉用户持久化的偏好），落盘只走 `SettingsViewModel`。
- 粒子一次只给一颗（`Particles` 默认 1、上限 4，同屏上限 12）。每颗一份 `ParticleState`，`CompositionTarget.Rendering` 上各算一次 `ParticleMotion.Step`：速度与角速度按 e^(-k·dt) 取闭式解衰减。**没有重力**——上一版加过一点，看着仍然像「东西往下掉」；现在粒子就沿自己那份随机矢量直行（每步只乘同一个标量，所以位移与初速永远平行，夹具就断言这一条），方向来自下半圆 180° 扇面（`ParticleMotion.ScatterDirection`：从正右经正下到正左，比上一版 60° 窄锥洒得开），越飞越慢、停在半空，尾段 0.9 s 边缩边淡。渲染循环的挂卸跟着「还有没有粒子、还有没有警告语在淡」，全空就 `Close`。
- **位置走布局、缩放旋转走 RenderTransform**：每帧把「中心 − 半边长」写进 `Canvas.Left/Top`，变换组里绝不出现 `TranslateTransform`。上一版把平移也塞进变换组，收尾缩放到 0 时组合矩阵把粒子拽回原点，看着就是「跳回左上角再原地消失」；夹具现在直接断言组里没有平移。
- 警告语 + 边缘高亮是**一个动作**：触发时屏幕中间一句大字（`KeywordRule.Banner`，例：AI接管中），左右两道斜线各占一半屏宽、一直铺到屏幕边（斜线是 `Hatch()` 生成的平铺画刷，瓦片高 = 字号×1.25，所以与文字等高），同时屏幕四边亮一圈向内渐隐的渐变带（四条 `Rectangle` 各铺一支按颜色现配的透明度梯度画刷，主带宽 / 淡出延伸带宽 / 呼吸周期就是 `-border` 段的三个参数）。边框与四角**不属于任何一条消息**：`RefreshBorder` 每次从「此刻还没开始退场的那些行」里挑最高一档
（紧急 > 普通，同档取带宽更大、字更大的一条），换级时四支渐变画刷的色标与四条边的长度各挂一条
0.5 s 动画自己挪过去（`MorphBands`；画刷刻意不 Freeze——冻住的 Freezable 挂不上动画，改色就成硬切），
背景立绘的填充色同理。亮度呼吸挂在四边的内层 `EdgePulse` 上：外层 `Edge` 已被淡入淡出占着，两条动画不能抢同一个属性。
每一行有**自己的时间表**（`EffectJob.RowUntil`）：自己上屏、等满自己那一段、自己擦出去，新来的一条只给自己上表。文字默认 **58 号**加粗（`EffectCommand.DefaultFontSize`，彩蛋与命令行共用一份）、字号可由 `-lable` 段改，**背后可不垫背景框**（垫了就像贴了块便利贴，跟这套 1 px 发丝线的界面不合）。颜色取当前 `AccentBrush` 或颜色词，换肤后跟着变。只贴着文字写四个斜杠不叫警告——列覆盖率（亮着的列数/总列数）现在低于 90% 夹具就红。
- 同时只能挂一条警告语：新一条上来先 `_bannerStory?.Stop()` 再开新的。曾经踩过两个坑：两条动画同时抢 `Tape.Opacity` 时 `HideBanner` 归零会被旧动画抬回去；而用「当前有没有在闪」这个标志位当闸门，它一旦被上一句的 Completed 抢先清掉，收起就变成空操作、窗口永远关不掉。现在 `HideNow` 无条件执行，并且用 `BeginAnimation(OpacityProperty, null)` 把属性交回本地值——光 `story.Stop()` 不够。
- **斜杠命令**（`SlashParser`）：`/warn 3 AI Computer Use`、`/e 1.5-3 编译失败`、`/i 跑完了`。三段是「类型 → 显示时间-闪烁次数 → 正文」；次数段只写一个数（`3`）就是闪 3 下、用默认时长，带横杠（`1.5-3`）才是时长+次数。类型→颜色：error/err/e/r 红、warn/warning/w 黄（主题里没现成警告色，给一支固定琥珀）、info/log/i/l 用当前强调色。`/` 或 `/off` 收起当前那句。词表是**封闭**的：斜杠后第一个词不在表里就整条丢掉，后面的字只吞不重试，等下一个 `/` 进入；第一个词一碰到空格就定性，不会拖到回车。命令走 `TextInput`（只有它带得出 `/`），且**收命令期间词流暂停匹配**，否则敲 `/info white` 会中途把界面切成浅色。
- **无头命令**（`EffectCli` + `EffectCommand`）：新语法 `XAssistant.exe -s info 8 0.5 0.5 -border on 60 30 1 -lable on 24 "AI 接管中" -group ai-hooks -from qoder` —— `-s` 段给颜色（颜色名 / `#RRGGBB` / info·warn·error 类型色）与持续·淡入·淡出三段节奏（默认 8 / 0.5 / 0.5；淡入秒数同时就是**扫描头擦边的时长**，夹在 0.15–1.5 s），`-border` 段给两条渐变带宽度与亮度呼吸周期（0 = 静态），`-lable` 段第一个数字永远是字号、剩下的是文案（off 或没文案就只剩边框）；`-group <词>` 是**组合键**（同键的多条在屏上竖着排成一叠、边框取组内最高档、新的进来整组重新计时，上限 4 行），优先级 `-group` > `-tag` > `-from`，三者都不写就不成组、照旧「一条播完才播下一条」；`-icon <路径>` 指定这一条的背景立绘（不写按 `-from` 去 `Assets/IdeIcons/<平台>.png` 取同名图）。解析全部在 `Services/EffectCommand.cs`（纯函数、不碰窗口，颜色词表在类里），有段开关就走新语法、否则整条按旧语法读。旧写法照旧兼容：`--fx warn 3 "..."` / `--fx confetti` / `--fx off` 与裸词 `confetti` / `off`（旧语法总时长换算成三段：首尾各 `min(1, 总长/3)`，7 秒时正好 1/5/1）。入口识别只看第一个词（`--fx` 允许不在第一位），命中就 `return`：不建容器、不装钩子、不开主窗、不碰数据库，`OnExit` 也要先看 `_headlessEffect` 否则去容器取服务只会抛空引用。`register-xa.ps1` 把 `xa`（`%LOCALAPPDATA%\XAssistant\bin\xa.cmd`）注册进用户 PATH，`deploy.ps1` 第 6 步自动调用（`-SkipXa` 跳过）。存在的理由是让别的进程（cmd、脚本、快捷键、CI、MCP）能触发同一套效果，而不必再多装一个客户端。执行第一步是转发：常驻主程序在跑时整条命令经命名管道 `NotificationPipe` 交给它（效果窗与消息栈只归一份，放完就退的无头进程养不住持久窗），没人接手才本地自己放；两条路径的语义统一在 `Services/EffectDispatch.cs`（解析→收起/撒花/上屏+入栈，返回占屏秒数），改行为只改这一处。
- **顶部持久消息栈**（`Views/MessageStackWindow`）：带文字的命令经 `EffectDispatch` 同时钉一张卡片在屏幕顶居中（文案 + HH:mm 时间戳 + 单条 ✕ + 清空全部），新消息插最前、旧的往下排着可回看，不自动消失；同屏上限 10 张淘汰最旧，栈空窗口自关。卡片头部是**来源徽章**：`-from <平台>` 非空时贴 28px 圆徽章（淡化 tint 圆底 + `Assets/IdeIcons/<平台>.png` 的 256px IDE 图标，图标由 `mcp/agent-hooks/extract-ide-icons.ps1` 从各家 exe 抽；没图标退首字母，没来源退 3px 素色条），多平台聚合也能认出谁发的。`off` 只收全屏带**不清栈**——历史回看是它存在的理由。无头实例转发失败时（主程序没跑）持久栈丢弃，全屏效果照放。
- **AI 编码代理的对话状态提醒**（`mcp/agent-hooks/`）：`agent-status.ps1` 是平台无关的生命周期 hook（Claude Code hooks 协议，stdin 收事件 JSON，`-Platform` 只影响日志前缀与 idle_prompt 语义）。分档：`PostToolUseFailure`→红（故障/中断）、`StopFailure`→红（整轮回复被 API 错误打断：限流/配额溢出/token limit，Claude 系事件 Trae 没有）、`PermissionRequest`→黄（授权(工具)）、`Notification`→黄（提问/接管；**未知的通知类型宁可多报不漏**）、`PostToolUse`→结果字段（error/exit_code/tool_response.is_error）异常才红（Trae 没失败事件的兜底通道），`Stop`→info（回复 · 已完成；`stop_hook_active` 静默防死循环），永远 `exit 0`；**stdin 必须用 StreamReader(OpenStandardInput, UTF8) 直读**——`[Console]::In` 按系统 GBK 码页解码 IDE 写来的 UTF-8，Stop 带的中文 `last_assistant_message` 一花 JSON 就碎，曾把正常结束误报成红档「事件数据不完整」（自测同步：管道喂方也要改 `$OutputEncoding` 为 UTF-8）；真截断时正则捞回事件名与 cwd 报红档而非静默吞，Show-Effect 统一给命令追加 `-from <平台>`（generic 不挂）。**各家事件集合不同是实踩的坑**：Trae 官方只支持 6 事件（无 PermissionRequest/PostToolUseFailure，挂上去永不触发），它的「等待确认」与「任务完成」都用 `Notification(idle_prompt)`——所以安装器按 kind 落地不同事件集（settings-hooks 5 事件含 StopFailure / hooks-file 即 Trae 只 3 个），idle_prompt 在 trae 系当完成静默（交给 Stop）、在其他平台当空闲等人归黄；摘旧条目用全集，升级时能把历史误挂的收回来。文案格式固定「事件词 · 请求人类介入：项目 · “对话标题” · 细节」：项目名取 `cwd` 叶目录；标题拿 `session_id`（剥 `.session.*` 后缀）去 IDE 的 `%APPDATA%\<App>\User\globalStorage\state.vscdb` 查 tasks 里同对象的 `name`/`title` 真实任务名——**绝不用 transcript 首句当标题，那会把聊天原文晒上屏**；vscdb 被 IDE 独占，用 FileShare.ReadWrite 开流拷字节 + Latin1 映射 IndexOf 查（中文值还原字节再 UTF8 解码），查不到整段省略；报错只留定位信息（`code = NNNNN` 错误码优先，其次错误首句截 40），**不写颜色词，颜色由效果表达**；错误文本里出现 ≥2 个 U+FFFD 就整段当不可信丢掉（`Trustworthy`），退回错误码/工具名——子进程按 GBK 写 stderr、上层按 UTF-8 解码时这是不可逆的，晒上去就是乱码。**来源段**：Qoder 事件带 `agent_id`/`agent_type` 即子代理（主对话没这两个字段，实测才敢标「主对话」，Claude 未验证故不标），并拿 `agent_id` 去 `~\.qoder-cn\projects\<slug>\<会话>\subagents\agent-<id>.meta.json` 取 `invocationName`/`description`/`color`（档案目录与同名 jsonl **平级**，先按 transcript 去扩展名拼、再退回 `dirname/subagents`）；`color` 只有内置类型带（59 份里 16 份，全是 Explore），不参与配色。写这段时踩到：**PS 5.1 命令模式下方法调用必须整体加括号**（`Join-Path $dir ([IO.Path]::GetFileNameWithoutExtension($leaf))`），漏了会被当成两个错位参数、异常再被外层 `catch { return $null }` 静默吞掉，表现就是"路径明明存在却读不到"；同理 `Split-Path -LeafBase` 是 PS 6+ 才有的。`install-agent-hooks.ps1` 多平台装/拆（`-List`/`-Platform`/`-Remove`/`-DryRun`/`-Root`），settings.json 型只合并 `hooks` 节点，Trae 型写独立 `hooks.json` 并按已有结构自适应（nested/flat），未校准平台拒写；DSH 走 `dsh-status-plugin.mjs` 原生监听 `turn/end` / `tools/result` / `approval/request` / `ask_user_question`，安装器只维护 profile `cordis.patch.yml` 的标记块，默认过滤子代理；修改这些 PowerShell 脚本后必须恢复 UTF8-BOM，并跑 `selftest.ps1`，断言数不写死。日志在 `%LOCALAPPDATA%\XAssistant\agent-hooks\agent-status.log`（字段校准依据；旧版 qoder-status.log 在 `~\.qoder-cn\hooks\`）。检测实盘报告：`qoder-cn-computeruse-takeover-detection.md`（Qoder 遮罩类名与 12 事件表）与 `ide-hooks-takeover-detection.md`（跨平台对照：Trae 是 VS Code fork、hooks 走独立 hooks.json、电脑控制无独立遮罩窗）。
- **IDE 接入状态模块族**（`Services/Modules/IdeStatusModule.cs` 基类 + `IdeProbe` 探测件 + `TraeWatchModule`/`ZCodeWatchModule`/`CodexWatchModule`/`VsCodeWatchModule`/`DshStatusModule` 五子类）：只读读出（Editable=false 行：目标程序 / 对话数据 / 事件接入 / 最近真实事件 + 能力与待办）与「立即刷新」按钮；占位平台（Codex 本机未装、VS Code 官方无 hooks）如实报告现状与可行路径，不装样子。`IdeProbe`：程序态先查进程（GetProcessesByName，异常当没跑）再看安装目录；hooks 态看配置里有没有 agent-status.ps1；真实事件取 `agent-status.log` 里带 `session_id` 的最后一条时间戳。子类可注入根（userProfile/appData/localAppData）供夹具用假目录验证（`IdeStatus()` 段六断言）。Qoder 版无 hooks 接入的平台（ZCode）拿到报错样本后照 `QoderWatchModule` + `LogTailer` 补尾随告警。
- **对话报错监视（模块）**（`Services/Modules/QoderWatchModule.cs`）：quota/限流这类**模型层错误不在 hook 事件流里**（Qoder 12 事件无 StopFailure，transcript 也不落；且各家策略不同——CN 自动重试自愈，国际版卡住等人工），但 IDE 自己的日志会写状态机迁移 `State transition: prompting -> error, trigger: chat_finish:{"code":100400,...}`。原 `Services/AgentErrorWatch.cs` 服务已迁成监视模块（面板「PART 4 / MODULES」开关与调参、激活态自动持久化）：尾随 `%APPDATA%\QoderCN|Qoder\logs\<会话>\questWindow\agent.log` 最新一份，从上次偏移增量读（文件轮换/截断归零；**首见文件只记游标不回放历史旧错**），命中走 sink（`IWatchAlertSink`→EffectQueue，与 hook 同一条上屏链路）发红档命令；同平台同码按参数冷却压重试连刷；带试弹按钮（独立 tag、不回重播）。共享件在 `Services/Modules/LogTailWatch.cs`（`LogTailer` + 告警 sink）——Trae/Codex/ZCode 等同类监视以后各写各的模块；模块写法见仓库附带的 skill `.qoder/skills/xassistant-watch-module/`。构造命令时进程内直调文本**不加引号**（引号是命令行语法，OS 才剥）。夹具 `ErrorSentinel()` 走真注册表 Register + 假 sink 验证命令内容与试弹复位；模块 Submit 走 BeginInvoke，断言前要放行一次派发队列（Pump 200）。
- **MCP 入口**：`mcp/xassistant_fx_server.py`（纯标准库的 stdio JSON-RPC 垫片）把 `xassistant_banner` / `xassistant_confetti` / `xassistant_off` 三个工具翻译成上面的 `--fx` 命令。它不自己渲染也不常驻服务：对话进行中闪一条、做完了撒一把，都是客户端 hook 调一下这个工具。可执行文件位置优先取环境变量 `XASSISTANT_EXE`；那边的颜色档 `TONES` 与 `SlashParser.Vocabulary` 分组对应，改产品要同步。
- 浮岛（`ToastWindow`）必须复用同一个窗口：每次 `Show` 新建一个，连击关键词会在屏幕顶上叠出一摞提示条并互相遮挡（上一版的事故）。`Reset` 换文案重计时，并用 generation 号让在飞的淡出动画作废，否则旧动画到点会把刚复用的窗口关掉。
- 词表：代码内置 + `Assets/Keywords/keywords.json`（用户入口，人手写的坏条目只能丢掉并记进 `LoadNotes`，引擎启动时进日志）。效果窗三条硬约束：`ShowActivated=False`、`Focusable=False`、`WS_EX_TRANSPARENT` 点击穿透；`App.OnStartup` 里 `ShutdownMode = OnExplicitShutdown`——不然粒子窗一关就成了「最后一个窗口」，会把整个常驻程序带走。
- 离屏夹具 `bin/keywordcheck`（五段）：物理用纯数值对 e^(-k·t) 的解析解、「位移与初速平行」（无重力）、「真的停住」（推 4 秒后一帧只挪出生那一帧的 11%）与「能飘过一千像素」（200 个方向×初速样本里最近的一例 1228 DIP）；斜杠命令逐条核语法、颜色档、上限夹逼与「表外整条跳过 + 收命令期间词流暂停」；xa 命令行逐条核 `-s`/`-border`/`-lable` 的解析（默认值、颜色词、旧语法换算）与 `ShowCommand` 上屏（字号文案按参数、上边渐变带像素量且带外为空、1 秒呼吸循环的极差、`-lable off` 条带收起、收起后窗口自己关）；接管提醒段断管道回环（有人听交接一字不差、没人接快速报 false）与消息栈行为（带文字才入栈、新插最前、上限淘汰、时间戳、`off` 不清栈、✕ 单条与清空后栈空自关，堆叠可能被常驻实例占管道时回环断言自动跳过）；真窗口那侧拍像素数非背景点与列覆盖率（斜线 98% 列覆盖、左右各 1384 DIP、斜线高 58 vs 文字高 54），并断言自定义 banner 原样上屏、`/` 能当场收起、同屏粒子数有顶、浮岛只有一个。注意快照拍的是窗口内容，内容坐标从虚拟屏左上角起算：拿屏幕 DIP 直接裁会整块偏一个 `VirtualScreenTop`。

### 速记唤起（灵感速记的 OS 级入口）
本仓库为 xapp「灵感速记」功能提供全局唤起与写库链路（速记表、tRPC 接口、列表页都在 xapp 仓库，spec 见 xapp 仓库的 `docs/specs/global-quick-note.md`，写入归属决策见 xapp `docs/adr/0010`）。相关文件在 `Services/QuickNote/` 与 `Views/QuickNoteWindow`：

- `GlobalHotkeyService` —— 经隐藏 `NativeWindow` 注册 `RegisterHotKey`（默认 `Win+Numpad0`，配置在 `AppSettings.QuickNote.HotKey`），`WM_HOTKEY` 触发事件；注册失败降级为托盘唤起，仅记日志不中断。
- `QuickNoteCaptureService` —— `InvokeCapture()`：速记窗已开则置顶聚焦（自持窗体引用单实例复用，不覆盖已输入内容与来源），未开则抓当前前台窗口标题作来源、弹原生无边框置顶小窗（`Views/QuickNoteWindow`，420×560，落在前台窗口所在屏居中）；`OpenList()` 普通标签打开列表页 `<基址>/quick-note`。
- `QuickNoteDatabaseService` —— Npgsql 直插 xapp 主库 `quick_notes` 表，只写 `content`/`source`，时间戳靠 DB 默认；连接串优先用 `AppSettings.QuickNote.ConnectionString`（复制自 xapp `.env` 的 `DATABASE_URL`，支持直接粘贴 URI），未配置时自动读取 `DatabaseUrlEnvPath` 指向的 `.env`（留空即数据目录下的 `.env`，写相对路径也按数据目录解析）的 `DATABASE_URL`。空内容由 UI 侧拦截；保存失败文字保留可重试。
- `Views/QuickNoteWindow` + `QuickNoteViewModel` —— 原生速记窗：多行输入自动聚焦，`Ctrl+Enter` 保存关窗、`Esc` 取消不保存、保存失败显示错误并保留内容；保存成功弹右下角 toast（`Services/ToastService` + `Views/ToastWindow`）确认落库。
- 与 xapp 的契约：列表路由 `/quick-note`、直插 `INSERT INTO quick_notes (content, source) VALUES ($1, $2)`。`quick_notes` 表新增 `NOT NULL` 无默认值列会断直插写入且无即时报错，改表须同步本仓库（xapp `docs/domains/quick-note.md` 已知坑已记录）。
- 基址默认随构建配置：Debug `http://localhost:3009`（开发版）、Release `http://localhost:9009`（生产版），可经 `AppSettings.QuickNote.SpaBaseUrl` 覆盖。决策见 `docs/adr/0001`、`docs/adr/0003`，词汇见 `CONTEXT.md`。

### ProcessUsageTracker（复杂部分）
最大的服务（约 950 行），也是 bug 最多的地方。关键设计要点：

- 按**规范化后的进程名**（小写、去除 `.exe` 后缀）分组跟踪应用会话，因此一个应用的所有窗口/实例共享一个会话；`ProcessCount` 记录该会话中有多少个进程。
- 通过两种方式发现进程：初始的 `Process.GetProcesses()` 枚举，外加 WMI `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` 监视器。
- 过滤系统进程：Session 0、位于 Windows 目录下的可执行文件、硬编码的 `SystemProcessNames` 集合，以及应用自身（按名称——`SelfProcessName`）。
- 一个 5 秒 `Timer` 累计每个会话的经过时长（写入 `AccumulatedSeconds`/`LastUpdateTime`）并刷新窗口标题。
- 时长低于 `MinimumSessionSeconds`（1 秒）的会话会被删除，而不是存储。
- `RecoverUnfinishedSessions` 修复崩溃或重启后遗留的 `EndTime IS NULL` 会话，包括跨天处理——跨午夜仍在运行的会话会被拆成两段：前一天在 23:59:59.999 结束，新会话从 00:00 开始。
- 窗口尚未出现的进程会暂存在 `_pendingProcesses` 中，一旦 `MainWindowHandle` 变为非零就会晋升为正式会话。
- 所有状态都保存在以 PID/应用名为主键的 `ConcurrentDictionary` 中——这种线程安全设计是刻意的。

### 日志
Serilog 同时写入滚动文件日志和 `UiLogSink`，后者把 `LogEntry` 推入 `LogBufferService` 的 `ObservableCollection`（通过 `BindingOperations.EnableCollectionSynchronization` 保证线程安全）。`MainWindowViewModel` 将过滤后的日志暴露给应用内面板。`ILogger<T>` 注入到所有地方。

## 约定

- 代码注释一律用**中文**编写——新注释请保持一致。
- 录制自动启动开关（鼠标/键盘）通过 `ConfigurationService` 在重启后保持。
- 应用最小化到系统托盘（Windows Forms `NotifyIcon`；项目在 WPF 之外启用了 `UseWindowsForms`）。关闭窗口是隐藏而不是退出——只有托盘“退出”菜单才会真正退出。
- `temp/` 目录是已加入 gitignore 的临时空间。
