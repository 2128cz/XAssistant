using System;

namespace XAssistant.Services;

/// <summary>
/// 一个粒子的运动状态：位置、速度、角度、角速度、寿命。纯数值，不含任何 WPF 类型。
/// 每个粒子各有一份，谁也不跟谁共用轨迹。
/// </summary>
/// <param name="X">画布横坐标（DIP）</param>
/// <param name="Y">画布纵坐标（DIP）</param>
/// <param name="Vx">横向速度（DIP/s），每帧被空气阻力削掉一点</param>
/// <param name="Vy">纵向速度（DIP/s），同上</param>
/// <param name="Angle">当前角度（度）</param>
/// <param name="SpinRate">角速度（度/s），跟着速度一起衰减，最后停转</param>
/// <param name="Age">已经活了多久（秒）</param>
/// <param name="Life">总共活多久（秒）：尾段是缩小消失</param>
public readonly record struct ParticleState(double X, double Y, double Vx, double Vy,
    double Angle, double SpinRate, double Age, double Life);

/// <summary>
/// 粒子的物理：一次 <see cref="Step"/> 就是把状态往前推一格 dt。
/// 拆成纯函数是为了能直接拿数验证（衰减、停住、缩小），不必开窗口、也不必等渲染帧；
/// 效果层只负责「每帧把每个粒子各算一次 + 把数写回视觉」。
///
/// 没有重力：粒子就沿自己那份随机矢量直行，轨迹不会往下弯；只有空气阻力把它越拉越慢，
/// 直到停在半空。之前加过一版小重力，看着仍然像「东西往下掉」，与「沿矢量播放」的意图不符。
/// </summary>
public static class ParticleMotion
{
    /// <summary>
    /// 空气阻力（1/s）。故意很轻：粒子要能沿自己的矢量飘到一千像素开外，
    /// 阻力再大一个量级就变成「飘两下就停了」。速度与角速度都按 e^(-k·t) 衰减，所以是越飞越慢而不是匀速。
    /// </summary>
    public const double Drag = 0.55;

    /// <summary>寿命最后这段用来缩小消失；前面那截只管飞与转。</summary>
    public const double ShrinkTail = 0.9;

    /// <summary>
    /// 按 dt 推进一格。指数衰减取闭式解（乘 <c>e^(-k·dt)</c>），不是一帧一帧乘固定系数——
    /// 后者会让帧率改变衰减快慢，掉帧时粒子会当场"冻住"，正是卡顿的观感来源。
    /// 位移与初速永远平行（每步乘同一个标量），所以轨迹是一条直线。
    /// </summary>
    public static ParticleState Step(ParticleState p, double dt)
    {
        if (dt <= 0) return p;
        double decay = Math.Exp(-Drag * dt);
        return p with
        {
            X = p.X + p.Vx * decay * dt,
            Y = p.Y + p.Vy * decay * dt,
            Vx = p.Vx * decay,
            Vy = p.Vy * decay,
            Angle = p.Angle + p.SpinRate * decay * dt,
            SpinRate = p.SpinRate * decay,
            Age = p.Age + dt,
        };
    }

    /// <summary>尾段的缩放：从 1 线性收到 0，配合 <see cref="Opacity"/> 一起淡掉。</summary>
    public static double Scale(ParticleState p) => Math.Clamp(Tail(p), 0, 1);

    /// <summary>尾段的透明度：与缩放同一段走完，所以是「边缩边淡」而不是缩到看不见才突然消失。</summary>
    public static double Opacity(ParticleState p) => Math.Clamp(Tail(p), 0, 1);

    /// <summary>该收掉了：寿命走满。</summary>
    public static bool Expired(ParticleState p) => p.Age >= p.Life;

    /// <summary>尾段进度：1 = 刚开始收尾，0 = 走完。</summary>
    private static double Tail(ParticleState p) => (p.Life - p.Age) / ShrinkTail;
}
