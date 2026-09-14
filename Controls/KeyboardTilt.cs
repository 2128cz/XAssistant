using System;
using System.Windows;
using System.Windows.Media;   // Matrix 在 Media 下，Point / Rect 仍在 System.Windows
// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Point 会跟板面上的点撞名
using Point = System.Windows.Point;

namespace XAssistant.Controls;

/// <summary>一组倾斜幅值。各值都是「敲到最边缘的键」时的上限，其余姿态按敲击距离线性缩放。</summary>
internal readonly record struct TiltLimits(
    double RollDegrees,
    double ShearDegrees,
    double ForeshortenDegrees,
    double SwingPixels,
    double HeadroomX,
    double HeadroomY)
{
    /// <summary>
    /// 选用的一组幅值：敲最左 / 最右的键时板子总共歪 6.2°（绕法线转 2.2° + 纵向切变 4.0°），
    /// 沿敲击方向压掉 cos 26° ≈ 10%，整块板朝敲击方向跟 6 个设计单位。
    /// 跟随量必须明显小于压缩量，否则上下敲击会把整块板推出原框，读出来不是「收进去」而是「探出去」。
    /// 余量是键盘框上下左右各允许越出的距离，超出的部分由 <see cref="KeyboardTilt.Build"/> 自动回收。
    /// </summary>
    public static readonly TiltLimits Default = new(2.2, 4.0, 26, 6, 26, 26);
}

/// <summary>
/// 键盘热力图的「敲击倾斜」模型：把整块 144 键布局看成一块悬在屏幕前方、由板心吊住的刚性板。
/// 敲哪个键，板就朝那个方向倾过去——敲左边左边沉，接着敲右边又荡回右边，停手之后回平。
///
/// 姿态矩阵只用仿射四件套拼成，每一项都有明确的几何身份（一切变换都绕板心做）：
///   平移 T 整块板朝敲击方向跟一点，让敲击点不脱手；
///   旋转 R 绕屏幕法线转，敲击侧下沉，就是肉眼说出来的「左倾 / 右倾」；
///   切变 K skewY，竖边仍然保持竖直而两端一高一低，读作「敲击那一侧转到后面去了」；
///   压缩 F 沿敲击方向按 cos α 缩短：把板绕「过板心且垂直于敲击方向」的轴倾转 α 之后做正交投影，
///          落在倾转轴上的分量不变、垂直分量恰好乘 cos α，四件套里只有这一项是物理推出来的。
/// 复合顺序 A = T · R · K · F，最后再按两个轴各回收一次，把倾斜后的外接框压回「键盘框 + 余量」以内，
/// 保证姿态再怎么荡都不会越出它所在的那块布局区域。
///
/// 本文件是这套动画的模型层，全部按 internal 收着：几何（<see cref="KeyboardTilt"/>）、
/// 单轴跟随（<see cref="TiltSpring"/>）、时间与渲染循环（<see cref="TiltDriver"/>）三层各管一段，
/// 都不对外暴露成控件的公开 API。离屏夹具与控件同装配，看得见 internal，验证不必为此开洞。
/// </summary>
internal static class KeyboardTilt
{
    /// <summary>按默认幅值算出某个敲击位置对应的姿态矩阵。(0,0) 即板心，返回单位矩阵。</summary>
    public static Matrix Build(double u, double v, double width, double height) =>
        Build(u, v, width, height, TiltLimits.Default);

    /// <summary>算出姿态矩阵。<paramref name="u"/> / <paramref name="v"/> 是敲击点相对板心的归一化偏移，板心 0、板边 ±1。</summary>
    public static Matrix Build(double u, double v, double width, double height, in TiltLimits limits)
    {
        var raw = BuildRaw(u, v, width, height, in limits);
        if (raw.IsIdentity) return raw;
        var box = TransformedBox(raw, width, height);
        // 越界回收：倾得越狠、外接框越大，就把两个轴各自压回去一点，直到刚好塞进「框 + 余量」。
        // 盯的是「最远端离板心多远」而不是框的边长：平移会把外接框推偏，按边长算是漏的。
        // 顺带补上了正交投影缺的那点纵深——实物倾过去本来就是会变扁的。
        double fitX = Math.Min(1, (width / 2 + limits.HeadroomX) / Math.Max(Math.Abs(box.Left), Math.Abs(box.Right)));
        double fitY = Math.Min(1, (height / 2 + limits.HeadroomY) / Math.Max(Math.Abs(box.Top), Math.Abs(box.Bottom)));
        if (fitX >= 1 && fitY >= 1) return raw;
        // 缩放同样绕板心、并且要乘在 raw 之后（q = S·(L·p + t) = (S·L)·p + S·t）。
        // WPF 的 Matrix 是行向量约定 p' = p·M + offset，逐项乘出来即是：
        return new Matrix(
            raw.M11 * fitX, raw.M12 * fitY,
            raw.M21 * fitX, raw.M22 * fitY,
            raw.OffsetX * fitX, raw.OffsetY * fitY);
    }

    /// <summary>整块板经过变换后的外接框，坐标以板心为原点。仿射把矩形映成平行四边形，极值必在四角，故只取四角。</summary>
    public static Rect TransformedBox(Matrix matrix, double width, double height)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (int corner = 0; corner < 4; corner++)
        {
            var point = matrix.Transform(new Point(
                (corner & 1) == 0 ? -width / 2 : width / 2,
                (corner & 2) == 0 ? -height / 2 : height / 2));
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>把键帽中心换算成归一化敲击向量：板心 (0,0)，左右上下四边 ±1。</summary>
    public static (double U, double V) PressVector(Point keyCenter, Rect board)
    {
        if (board.Width <= 0 || board.Height <= 0) return (0, 0);
        return (
            (keyCenter.X - board.X - board.Width / 2) * 2 / board.Width,
            (keyCenter.Y - board.Y - board.Height / 2) * 2 / board.Height);
    }

    /// <summary>四件套依次相乘，不做越界回收；单独留给验证用。</summary>
    private static Matrix BuildRaw(double u, double v, double width, double height, in TiltLimits limits)
    {
        u = Math.Clamp(u, -1, 1);
        v = Math.Clamp(v, -1, 1);
        // 敲击距离：板心附近几乎不倾，角落键与边缘中点一样封顶，不让斜向敲击额外疯
        double distance = Math.Min(1, Math.Sqrt(u * u + v * v));
        if (distance < 0.001) return Matrix.Identity;

        // 压缩方向取板面像素方向上的单位向量：敲上边压纵向、敲左边压横向、敲角落压斜向
        double offsetX = u * width / 2, offsetY = v * height / 2;
        double length = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        double nx = offsetX / length, ny = offsetY / length;
        double cos = Math.Cos(DegreesToRadians(limits.ForeshortenDegrees) * distance);
        // F = I + (cos α − 1)·n̂n̂ᵀ，行主序 [a b; c d]
        var squash = (
            1 + (cos - 1) * nx * nx, (cos - 1) * nx * ny,
            (cos - 1) * nx * ny, 1 + (cos - 1) * ny * ny);

        // 屏幕 y 轴朝下，所以数学上的正角在屏幕上就是顺时针：敲右边 u>0 → 右边下沉，敲左边反号
        double roll = DegreesToRadians(limits.RollDegrees) * u;
        var rotate = (Math.Cos(roll), -Math.Sin(roll), Math.Sin(roll), Math.Cos(roll));
        // y' = y + x·tan β：竖边保持竖直，横边歪斜，正是绕竖直轴转身在仿射下的骨架
        double shear = DegreesToRadians(limits.ShearDegrees) * u;
        var skew = (1d, 0d, Math.Tan(shear), 1d);

        var linear = Multiply(Multiply(rotate, skew), squash);
        // WPF 存的是数学矩阵的转置：x' = x·M11 + y·M21，y' = x·M12 + y·M22
        return new Matrix(linear.a, linear.c, linear.b, linear.d,
            limits.SwingPixels * u, limits.SwingPixels * v);
    }

    /// <summary>行主序 2×2 相乘，列向量约定 p' = L·p，因此越靠左的矩阵越晚施加。</summary>
    private static (double a, double b, double c, double d) Multiply(
        (double a, double b, double c, double d) left, (double a, double b, double c, double d) right) =>
        (
            left.a * right.a + left.b * right.c,
            left.a * right.b + left.b * right.d,
            left.c * right.a + left.d * right.c,
            left.c * right.b + left.d * right.d);

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
