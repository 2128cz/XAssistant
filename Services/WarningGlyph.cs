using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

// 主工程开了 UseWindowsForms，隐式 using 里的 System.Drawing.Brush / Point 会跟 WPF 的撞名
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace XAssistant.Services;

/// <summary>
/// 警告标志：纯几何绘制的一圈描边三角 + 一根短柱 + 一颗点。
///
/// 为什么不走 ⚠ 字形：WPF 渲染不了彩色 emoji（只能单色轮廓），缺字体的机器还会掉成方框，
/// 而且字形的大小与描边粗细都不受控——全屏那四颗要「巨大到压住整屏」，只有画形状才说得上尺寸。
/// 三处共用这一个构造器：全屏四角、顶栏卡片的小徽标、需要时的行内提示。
/// </summary>
public static class WarningGlyph
{
    /// <summary>描边粗细占高度的比例：太小在大尺寸下看着像没描边。</summary>
    private const double StrokeRatio = 0.085;

    /// <summary>
    /// 画一个高 <paramref name="height"/> 的警告三角。颜色跟着效果色走（只改 alpha 不另发明颜色），
    /// 三角内部填 18% 同色淡底——全实心会把正文顶出一块白，纯描边又太轻。
    /// </summary>
    public static FrameworkElement Build(Brush accent, double height, HorizontalAlignment align = HorizontalAlignment.Center)
    {
        double stroke = Math.Max(1.5, height * StrokeRatio);
        var canvas = new Canvas
        {
            Width = height,
            Height = height,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        var triangle = new Polygon
        {
            Points = new PointCollection
            {
                new(height / 2, stroke * 0.7),
                new(stroke * 0.45, height - stroke * 0.45),
                new(height - stroke * 0.45, height - stroke * 0.45),
            },
            Stroke = accent,
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = new SolidColorBrush(WithAlpha(accent, 0.18)),
        };
        var bar = new Rectangle
        {
            Width = Math.Max(2, height * 0.085),
            Height = height * 0.33,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = accent,
        };
        Canvas.SetLeft(bar, height / 2 - bar.Width / 2);
        Canvas.SetTop(bar, height * 0.31);
        var dot = new Ellipse
        {
            Width = Math.Max(3, height * 0.1),
            Height = Math.Max(3, height * 0.1),
            Fill = accent,
        };
        Canvas.SetLeft(dot, height / 2 - dot.Width / 2);
        Canvas.SetTop(dot, height * 0.72);

        canvas.Children.Add(triangle);
        canvas.Children.Add(bar);
        canvas.Children.Add(dot);
        return canvas;
    }

    /// <summary>把一支画刷的颜色换个透明度重出一色；非纯色画刷退回中性灰。</summary>
    private static Color WithAlpha(Brush brush, double alpha)
    {
        Color color = brush is SolidColorBrush solid ? solid.Color : Colors.Gainsboro;
        return Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
    }
}
