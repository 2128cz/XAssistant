using System;
using System.Windows;
using System.Windows.Media;   // Matrix 在 Media 下，Point / Rect 仍在 System.Windows
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Point 会跟板面上的点撞名
using Point = System.Windows.Point;

namespace XAssistant.Controls;

/// <summary>一组倾斜幅值。各值都是「敲到最边缘的键」时的上限，其余姿态按敲击距离线性缩放。</summary>
internal readonly record struct TiltLimits(
    double RollDegrees,
    double ForeshortenDegrees,
    double DepthBoardWidths,
    double SwingPixels,
    double HeadroomX,
    double HeadroomY)
{
    /// <summary>
    /// 选用的一组幅值：敲最左 / 最右的键时，板子绕视线滚 2.2°（敲击侧在屏幕上沉下去），
    /// 再绕板内轴倾转 26°（敲击侧退到远处去，正交分量就是压掉 cos 26° ≈ 10%）。
    /// 相机放在 2.2 个板宽之外，这个距离决定近大远小有多明显：敲最右时退远的右缘纵向尺度只剩约 0.90 倍、
    /// 就近的左缘涨到约 1.09 倍（夹具实测量到两头差 21%），板子从平行四边形收成梯形。整块板还朝敲击方向跟 6 个设计单位。
    /// 跟随量必须明显小于压缩量，否则上下敲击会把整块板推出原框，读出来不是「收进去」而是「探出去」。
    /// 余量是键盘框上下左右各允许越出的距离，超出的部分由 <see cref="KeyboardTilt.BuildPose"/> 自动回收。
    /// </summary>
    public static readonly TiltLimits Default = new(2.2, 26, 2.2, 6, 26, 26);
}

/// <summary>
/// 键盘热力图的「敲击倾斜」模型：把整块 144 键布局看成一块悬在屏幕前方、由板心吊住的刚性板，板前有一个真的相机。
/// 敲哪个键，那个方向就转到远处去——退远的那一侧键帽变小、行距变窄，近大远小把整块板拉成梯形。
///
/// 为什么不能只用一块仿射矩阵：仿射保持平行性，矩形怎么转都是平行四边形，「远边比近边短」它永远画不出来；
/// 斜切只能把角歪掉，不能把尺度拉开。所以姿态改成先算单应（3D 倾转 + 透视除法），再把它压到每个键帽自己那一小块上：
///   键帽中心走精确投影，键帽内部用该处的雅可比做一阶仿射——键帽才几十设计单位宽，
///   二阶残差不到半个百分点，肉眼不可见，144 块拼起来就是完整的透视轮廓。
///
/// 姿态本身只有三个物理量（一切变换都绕板心做）：
///   倾转 α 绕「过板心且垂直于敲击方向」的板内轴 â 转，敲击侧退远：
///          落在 â 上的分量不变、沿敲击方向的分量乘 cos α（这就是正交投影下看得到的那截压缩），
///          而 Z = −sin α·(n̂·p) 正是透视除法要用的纵深。
///   滚转 γ 绕视线转，敲击侧在屏幕上再沉一点，就是肉眼说出来的「左倾 / 右倾」。
///   跟随 T 整块板朝敲击方向平移一点，让敲击点不脱手。
/// 最后按两个轴各回收一次，把投影后的外接框压回「键盘框 + 余量」以内，
/// 保证姿态再怎么荡都不会越出它所在的那块布局区域。
///
/// 本文件是这套动画的模型层，全部按 internal 收着：几何（<see cref="KeyboardTilt"/>）、
/// 单轴跟随（<see cref="TiltSpring"/>）、时间与渲染循环（<see cref="TiltDriver"/>）三层各管一段，
/// 都不对外暴露成控件的公开 API。离屏夹具与控件同装配，看得见 internal，验证不必为此开洞。
/// </summary>
internal static class KeyboardTilt
{
    /// <summary>雅可比的中心差分步长（设计单位）。相对 1120 的板宽足够小，又远大于浮点噪声。</summary>
    private const double JacobianStep = 1;

    /// <summary>
    /// 一次姿态的全部投影参数：一个敲击位置算一份，整帧共用（三角函数只在这里求一次，
    /// 每个键帽只跑代数）。越界回收的两个系数也在里面，<see cref="Map"/> 顺手带出去。
    /// </summary>
    private struct Pose
    {
        /// <summary>敲击方向的板面单位向量，倾转轴就是它的垂直方向 â = (−Ny, Nx)。</summary>
        public double Nx, Ny;

        /// <summary>倾转角的 cos / sin。</summary>
        public double Cos, Sin;

        /// <summary>绕视线滚转的 cos / sin。屏幕 y 轴朝下，所以正角就是顺时针。</summary>
        public double RollCos, RollSin;

        /// <summary>相机到板心的距离（设计单位）。越近透视越狠。</summary>
        public double Depth;

        /// <summary>整块板朝敲击方向的跟随平移。</summary>
        public double SwingX, SwingY;

        /// <summary>越界回收：两个轴各一个，1 表示没碰到余量上限。第一轮算 CornerBox 时先置 1，量完再写回真值。</summary>
        public double FitX, FitY;

        /// <summary>板心坐标（以板心为原点）→ 屏幕坐标，含回收后的最终尺度。</summary>
        public readonly Point Map(double x, double y)
        {
            // 1) 绕板内轴 â 倾转 α：沿敲击方向的分量退到屏幕里（Z 为负即更远），轴上分量不变
            double along = Nx * x + Ny * y;
            double axis = -Ny * x + Nx * y;
            double keep = 1 - Cos;
            double px = x * Cos - Ny * axis * keep;
            double py = y * Cos + Nx * axis * keep;
            // 2) 透视除法：相机在板心正前方 Depth 处，Z 为正即朝镜头，分母变小就是放大
            double w = 1 + Sin * along / Depth;
            px /= w;
            py /= w;
            // 3) 绕视线滚一点，4) 朝敲击方向跟随，5) 越界回收
            double rx = px * RollCos - py * RollSin;
            double ry = px * RollSin + py * RollCos;
            return new Point((rx + SwingX) * FitX, (ry + SwingY) * FitY);
        }
    }

    /// <summary>算出姿态的投影参数，并把四角落到的外接框回收进「板框 + 余量」。</summary>
    private static Pose BuildPose(double u, double v, double width, double height, in TiltLimits limits)
    {
        u = Math.Clamp(u, -1, 1);
        v = Math.Clamp(v, -1, 1);
        var pose = new Pose
        {
            Depth = limits.DepthBoardWidths * width,
            SwingX = limits.SwingPixels * u,
            SwingY = limits.SwingPixels * v,
            FitX = 1,
            FitY = 1,
        };
        double roll = DegreesToRadians(limits.RollDegrees) * u;
        pose.RollCos = Math.Cos(roll);
        pose.RollSin = Math.Sin(roll);
        // 敲击距离：板心附近几乎不倾，角落键与边缘中点一样封顶，不让斜向敲击额外疯
        double distance = Math.Min(1, Math.Sqrt(u * u + v * v));
        double offsetX = u * width / 2, offsetY = v * height / 2;
        double length = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        if (distance >= 0.001 && length > 0)
        {
            pose.Nx = offsetX / length;
            pose.Ny = offsetY / length;
            double alpha = DegreesToRadians(limits.ForeshortenDegrees) * distance;
            pose.Cos = Math.Cos(alpha);
            pose.Sin = Math.Sin(alpha);
        }
        else
        {
            pose.Cos = 1;
            pose.Sin = 0;
        }
        // 越界回收：盯的是「最远端离板心多远」而不是框的边长——平移会把外接框推偏，按边长算是漏的。
        // 键帽铺满整块板，所以拿板框四角投影就是保守的外接框，不会算少。
        var box = CornerBox(pose, width, height);
        pose.FitX = Math.Min(1, (width / 2 + limits.HeadroomX) / Math.Max(Math.Abs(box.Left), Math.Abs(box.Right)));
        pose.FitY = Math.Min(1, (height / 2 + limits.HeadroomY) / Math.Max(Math.Abs(box.Top), Math.Abs(box.Bottom)));
        return pose;
    }

    /// <summary>把板框四角投影后的外接框，坐标以板心为原点。单应把矩形映成梯形，极值仍在四角。</summary>
    private static Rect CornerBox(in Pose pose, double width, double height)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (int corner = 0; corner < 4; corner++)
        {
            var point = pose.Map((corner & 1) == 0 ? -width / 2 : width / 2, (corner & 2) == 0 ? -height / 2 : height / 2);
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>默认幅值下整块板的外接框。</summary>
    public static Rect PoseBox(double u, double v, double width, double height) =>
        PoseBox(u, v, width, height, TiltLimits.Default);

    /// <summary>
    /// 把画布坐标下的一个点按姿态精确投影，结果仍回到画布坐标。留给离屏夹具：
    /// 逐键一阶仿射实绘出的角点与这个精确投影之间的差就是二阶残差，它必须小到屏幕上看不出来。
    /// 每次调用重算一份姿态，只在验证里跑，不进渲染帧。
    /// </summary>
    public static Point Project(double u, double v, double width, double height, Point canvasPoint)
    {
        var mapped = BuildPose(u, v, width, height, TiltLimits.Default)
            .Map(canvasPoint.X - width / 2, canvasPoint.Y - height / 2);
        return new Point(mapped.X + width / 2, mapped.Y + height / 2);
    }

    /// <summary>姿态下整块板的外接框（已含越界回收），坐标以板心为原点。离屏夹具拿它对量实绘位置。</summary>
    public static Rect PoseBox(double u, double v, double width, double height, in TiltLimits limits) =>
        CornerBox(BuildPose(u, v, width, height, limits), width, height);

    /// <summary>按默认幅值把姿态压到每个键帽上。</summary>
    public static void ApplyPose(double u, double v, double width, double height,
        ReadOnlySpan<Rect> slots, MatrixTransform[] transforms) =>
        ApplyPose(u, v, width, height, slots, transforms, TiltLimits.Default);

    /// <summary>
    /// 把姿态压到每个键帽上，结果写进 <paramref name="transforms"/>（与 <paramref name="slots"/> 同序）。
    /// <paramref name="slots"/> 是键帽在画布坐标里的槽位（Canvas.Left / Top 加自身宽高），与板框同一原点。
    /// RenderTransform 不改布局槽位，所以平移量要算成「目标位置 − 槽位左上角」。
    /// </summary>
    public static void ApplyPose(double u, double v, double width, double height,
        ReadOnlySpan<Rect> slots, MatrixTransform[] transforms, in TiltLimits limits)
    {
        if (u == 0 && v == 0)
        {
            // 静置：精确写回单位矩阵，不留浮点尾巴（下次加载进来是平的、夹具判归位都靠这条）
            for (int i = 0; i < transforms.Length; i++) transforms[i].Matrix = Matrix.Identity;
            return;
        }
        var pose = BuildPose(u, v, width, height, in limits);
        const double step = JacobianStep;
        for (int i = 0; i < slots.Length && i < transforms.Length; i++)
        {
            var slot = slots[i];
            double qx = slot.Width / 2, qy = slot.Height / 2;
            // 键帽中心的板心坐标
            double cx = slot.X + qx - width / 2, cy = slot.Y + qy - height / 2;
            var center = pose.Map(cx, cy);
            // 键帽很小，把它那一块的单应压成一阶仿射：中心走精确投影，四周用中心处的雅可比
            var right = pose.Map(cx + step, cy);
            var left = pose.Map(cx - step, cy);
            var down = pose.Map(cx, cy + step);
            var up = pose.Map(cx, cy - step);
            double j11 = (right.X - left.X) / (2 * step), j12 = (down.X - up.X) / (2 * step);
            double j21 = (right.Y - left.Y) / (2 * step), j22 = (down.Y - up.Y) / (2 * step);
            // WPF 存的是数学矩阵的转置（x' = x·M11 + y·M21），而平移要相对键帽左上角
            transforms[i].Matrix = new Matrix(
                j11, j21, j12, j22,
                center.X + width / 2 - slot.X - (j11 * qx + j12 * qy),
                center.Y + height / 2 - slot.Y - (j21 * qx + j22 * qy));
        }
    }

    /// <summary>把键名换算成归一化敲击向量：板心 (0,0)，左右上下四边 ±1。</summary>
    public static (double U, double V) PressVector(Point keyCenter, Rect board)
    {
        if (board.Width <= 0 || board.Height <= 0) return (0, 0);
        return (
            (keyCenter.X - board.X - board.Width / 2) * 2 / board.Width,
            (keyCenter.Y - board.Y - board.Height / 2) * 2 / board.Height);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}

/// <summary>
/// 姿态向量朝目标靠拢的二阶阻尼跟随，敲击向量的 x、y 各配一个（由 <see cref="TiltDriver"/> 持有）。
/// 换目标时速度不清零，所以「先敲左、紧接着敲右」是带着左倾的势头往右荡过去、略微过冲再收回来，
/// 连续敲击的倾斜效果因此是叠加着播完的，而不是每键重启一次独立动画。
/// </summary>
internal sealed class TiltSpring
{
    /// <summary>固有频率（Hz）：决定荡一次多快，2.6 Hz 约等于一个来回 380 ms。</summary>
    public const double NaturalFrequencyHz = 2.6;

    /// <summary>阻尼比：小于 1 才会荡过界再回来，0.42 大致让一次阶跃过冲 22%。</summary>
    public const double DampingRatio = 0.42;

    /// <summary>
    /// 目标预缩系数：响应本来就会过冲两成，把目标按同比例收一点，
    /// 荡到最外圈刚好落在最大姿态上，而不是早早撞上钳位、在最大倾斜上磨蹭一段。
    /// </summary>
    public const double TargetScale = 0.82;

    /// <summary>积分步长。远小于 1/ω，显式积分稳定；同时保证一帧里最多迭代几十次。</summary>
    private const double StepSeconds = 1d / 240d;

    /// <summary>单帧最长推进的时间。切窗口、断点、卡顿回来时按这个封顶，免得一步跨过头。</summary>
    private const double MaxFrameSeconds = 0.05d;

    private double _value;
    private double _velocity;
    private double _target;

    public double Value => _value;
    public double Velocity => _velocity;

    /// <summary>当前目标姿态（已经乘过 <see cref="TargetScale"/>）。</summary>
    public double Target => _target;

    /// <summary>换一个敲击目标。速度不清零，所以后一次敲击是接着前一次的势头走的。</summary>
    public void Aim(double target) => _target = Math.Clamp(target, -1, 1) * TargetScale;

    /// <summary>是否已经贴住目标：速度也归零才算停，否则渲染循环不能收摊。</summary>
    public bool Settled() => Math.Abs(_velocity) < 0.001 && Math.Abs(_value - _target) < 0.001;

    /// <summary>推进 <paramref name="deltaSeconds"/> 秒，把姿态拉向当前目标。</summary>
    public void Step(double deltaSeconds)
    {
        double remaining = Math.Clamp(deltaSeconds, 0, MaxFrameSeconds);
        double omega = 2 * Math.PI * NaturalFrequencyHz;
        while (remaining > 0)
        {
            double h = Math.Min(StepSeconds, remaining);
            double acceleration = -omega * omega * (_value - _target) - 2 * omega * DampingRatio * _velocity;
            // 半隐式欧拉：先更新速度再更新位移，长期摆动的能量不会自己往上涨
            _velocity += acceleration * h;
            _value += _velocity * h;
            remaining -= h;
        }
        if (Settled())
        {
            _value = _target;
            _velocity = 0;
        }
    }

    /// <summary>直接落到某个姿态并把目标也定在那里（控件重新加载时回平），不带速度，免得凭空甩一下。</summary>
    public void Snap(double value)
    {
        _value = value;
        _target = value;
        _velocity = 0;
    }
}

/// <summary>
/// 倾斜动画的时间驱动：把「敲一下」变成一段会自己收尾的动画，并负责渲染循环的挂与卸。
/// 三层分工是这套东西能复用的关键——<see cref="KeyboardTilt"/> 只管几何（无状态，随时可算），
/// <see cref="TiltSpring"/> 只管单轴跟随，本类只管时间（停留计时、步进、收敛、退订）；
/// 控件则只管两头：把键名换算成敲击向量喂进来，把算出的姿态写回视觉树。
/// 换一个想倾斜的视觉块，把写矩阵的回调交给它就行。
/// </summary>
internal sealed class TiltDriver
{
    /// <summary>姿态在最倾斜处停留这么久才让弹簧把板子送回水平；期间再敲一下则重新计时。</summary>
    public const double PoseHoldSeconds = 0.14;

    private readonly TiltSpring _x = new();
    private readonly TiltSpring _y = new();
    private readonly Action<double, double> _apply;
    private double _holdRemaining;
    private TimeSpan? _lastFrame;
    private bool _loopAttached;

    /// <param name="apply">每帧把当前姿态 (u, v) 落到视觉树上的回调。</param>
    public TiltDriver(Action<double, double> apply) => _apply = apply;

    /// <summary>
    /// 敲一下：只换弹簧的目标，不算矩阵——一秒能敲十来下，姿态每帧算一次就够。
    /// 弹簧换目标时不清速度，于是后一下带着前一下的余势荡过去，几次敲击就叠成一段连续的动作。
    /// </summary>
    public void Press(double u, double v)
    {
        _x.Aim(u);
        _y.Aim(v);
        _holdRemaining = PoseHoldSeconds;
        Attach();
    }

    /// <summary>
    /// 渲染帧回调。<see cref="CompositionTarget.Rendering"/> 是静态事件，而且声明成普通 <see cref="EventHandler"/>，
    /// 帧时刻只装在传进来的 RenderingEventArgs 里。
    /// </summary>
    private void Frame(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        // 取渲染时钟的差值而不是墙上时间：长空档交给弹簧自己按 50 ms 封顶，掉帧不会把板子甩飞
        Step(_lastFrame is { } last ? (time - last).TotalSeconds : 0);
        _lastFrame = time;
    }

    /// <summary>
    /// 按给定步长推进一帧，并把姿态交出去。离屏夹具也走这条：它能按固定步长把整段动画走完，不必等真渲染帧。
    /// </summary>
    public void Step(double deltaSeconds)
    {
        if (_holdRemaining > 0)
            _holdRemaining = Math.Max(0, _holdRemaining - deltaSeconds);
        else
        {
            _x.Aim(0);
            _y.Aim(0);
        }
        _x.Step(deltaSeconds);
        _y.Step(deltaSeconds);
        if (_holdRemaining <= 0 && _x.Settled() && _y.Settled()) Reset();
        else _apply(_x.Value, _y.Value);
    }

    /// <summary>
    /// 归位：退订渲染循环、抹掉停留计时、把姿态写成精确的 0（收敛判据留了千分之一的尾巴）。
    /// 停稳时由 <see cref="Step"/> 自己调；换页卸掉控件时必须由外面调——静态渲染事件不能留，
    /// 姿态也得写回单位矩阵，否则下次加载进来是歪的。
    /// </summary>
    public void Reset()
    {
        Detach();
        _holdRemaining = 0;
        _lastFrame = null;
        _x.Snap(0);
        _y.Snap(0);
        _apply(0, 0);
    }

    private void Attach()
    {
        if (_loopAttached) return;
        _loopAttached = true;
        _lastFrame = null;
        CompositionTarget.Rendering += Frame;
    }

    private void Detach()
    {
        if (!_loopAttached) return;
        _loopAttached = false;
        CompositionTarget.Rendering -= Frame;
    }
}
