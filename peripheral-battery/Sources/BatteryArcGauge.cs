using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace PeripheralBattery;

/// <summary>
/// 270° 马蹄形圆弧电量表盘（参考 Apple Watch / iOS 电量组件风格）：
/// - 圆弧从左下（135°）顺时针扫到右下（405°），底部留出 90° 缺口放百分比数字；
/// - 颜色随电量变化：绿色（&gt; 50%）&gt; 琥珀色（阈值 ~ 50%）&gt; 红色（&lt;= 阈值）；
/// - 充电时在圆弧正上方（12 点钟方向）叠加带镂空描边的闪电图标；
/// - 圆弧正中央显示设备类型图标。
/// </summary>
internal sealed class BatteryArcGauge
{
    public static readonly Windows.UI.Color GreenColor = Windows.UI.Color.FromArgb(255, 48, 209, 88);   // #30D158
    public static readonly Windows.UI.Color AmberColor = Windows.UI.Color.FromArgb(255, 255, 159, 10);  // #FF9F0A
    public static readonly Windows.UI.Color RedColor = Windows.UI.Color.FromArgb(255, 255, 69, 58);     // #FF453A

    private const double StartAngleDeg = 135.0;
    private const double TotalSweepDeg = 270.0;

    private readonly double _diameter;
    private readonly double _strokeThickness;
    private readonly double _radius;
    private readonly double _cx;
    private readonly double _cy;

    private readonly XamlPath _trackPath;
    private readonly XamlPath _valuePath;
    private readonly FontIcon _centerIcon;
    private readonly TextBlock _percentText;
    private readonly Grid _boltHost;
    private readonly XamlPath _boltHalo;
    private readonly XamlPath _boltFill;

    private readonly SolidColorBrush _valueBrush = new(GreenColor);
    private readonly SolidColorBrush _boltFillBrush = new(GreenColor);
    private readonly SolidColorBrush _boltHaloBrush = new(Windows.UI.Color.FromArgb(255, 28, 28, 30));

    public BatteryArcGauge(
        double diameter,
        double strokeThickness,
        double iconFontSize,
        double percentFontSize,
        Brush textBrush,
        Brush trackBrush)
    {
        _diameter = diameter;
        _strokeThickness = strokeThickness;

        // 顶部留出少量余量给闪电尖端，防止被裁切
        var inset = strokeThickness / 2.0 + 2.0;
        _radius = Math.Max(4, diameter / 2.0 - inset);
        _cx = diameter / 2.0;
        _cy = diameter / 2.0 + 0.5;

        _trackPath = new XamlPath
        {
            Width = diameter,
            Height = diameter,
            Stretch = Stretch.None,
            Stroke = trackBrush,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = BuildArcGeometry(_cx, _cy, _radius, StartAngleDeg, TotalSweepDeg),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _valuePath = new XamlPath
        {
            Width = diameter,
            Height = diameter,
            Stretch = Stretch.None,
            Stroke = _valueBrush,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = BuildArcGeometry(_cx, _cy, _radius, StartAngleDeg, TotalSweepDeg),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _centerIcon = new FontIcon
        {
            Glyph = "\uE83F",
            FontSize = iconFontSize,
            Foreground = textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // 视觉中心略微上移，给底部缺口处的百分比留出呼吸空间
            Margin = new Thickness(0, 0, 0, Math.Round(diameter * 0.08)),
        };

        _percentText = new TextBlock
        {
            Text = "—",
            FontSize = percentFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, diameter <= 38 ? -1 : 0),
        };

        // 12 点钟方向的充电闪电（双层矢量 Path：底下一层用背景色加粗描边做镂空效果，上面一层填亮绿色）
        var boltHeight = Math.Max(9.5, strokeThickness * 2.15);
        var boltWidth = boltHeight * (12.0 / 14.0);
        var boltTop = Math.Max(0, (_cy - _radius) - boltHeight / 2.0);

        _boltHalo = new XamlPath
        {
            Width = boltWidth,
            Height = boltHeight,
            Stretch = Stretch.None,
            Data = BuildBoltGeometry(boltWidth, boltHeight),
            Fill = _boltHaloBrush,
            Stroke = _boltHaloBrush,
            StrokeThickness = Math.Max(2.0, strokeThickness * 0.45),
            StrokeLineJoin = PenLineJoin.Round,
        };

        _boltFill = new XamlPath
        {
            Width = boltWidth,
            Height = boltHeight,
            Stretch = Stretch.None,
            Data = BuildBoltGeometry(boltWidth, boltHeight),
            Fill = _boltFillBrush,
        };

        _boltHost = new Grid
        {
            Width = boltWidth,
            Height = boltHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, boltTop, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _boltHost.Children.Add(_boltHalo);
        _boltHost.Children.Add(_boltFill);

        Root = new Grid
        {
            Width = diameter + 4,
            Height = diameter,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Root.Children.Add(_trackPath);
        Root.Children.Add(_valuePath);
        Root.Children.Add(_boltHost);
        Root.Children.Add(_centerIcon);
        Root.Children.Add(_percentText);
    }

    public Grid Root { get; }

    /// <summary>按电量与阈值计算圆弧颜色：绿色 &gt; 琥珀色 &gt; 红色。</summary>
    public static Windows.UI.Color EvaluateArcColor(DeviceBattery? device, int threshold)
    {
        if (device?.Level is not { } level)
        {
            return Windows.UI.Color.FromArgb(140, 150, 150, 150);
        }

        if (level <= threshold) return RedColor;

        var amberCeiling = Math.Max(50, threshold + 15);
        if (level <= amberCeiling) return AmberColor;

        return GreenColor;
    }

    public void SetHaloColor(Windows.UI.Color haloColor)
    {
        _boltHaloBrush.Color = haloColor;
    }

    public void Update(DeviceBattery? device, int threshold, bool useTypeIcons, string fallbackGlyph)
    {
        if (device is null)
        {
            _centerIcon.Glyph = fallbackGlyph;
            _percentText.Text = "—";
            _valuePath.Opacity = 0;
            _boltHost.Visibility = Visibility.Collapsed;
            return;
        }

        _centerIcon.Glyph = useTypeIcons ? device.Glyph : fallbackGlyph;
        _percentText.Text = device.LevelText;

        var arcColor = EvaluateArcColor(device, threshold);
        _valueBrush.Color = arcColor;
        _boltFillBrush.Color = GreenColor;

        if (device.Level is { } lv && lv > 0)
        {
            var fraction = Math.Clamp(lv / 100.0, 0.02, 1.0);
            _valuePath.Opacity = 1;
            _valuePath.Data = BuildArcGeometry(_cx, _cy, _radius, StartAngleDeg, TotalSweepDeg * fraction);
        }
        else if (device.Level == 0)
        {
            _valuePath.Opacity = 1;
            _valuePath.Data = BuildArcGeometry(_cx, _cy, _radius, StartAngleDeg, TotalSweepDeg * 0.015);
        }
        else
        {
            _valuePath.Opacity = 0;
        }

        _boltHost.Visibility = device.Charging == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static PathGeometry BuildArcGeometry(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        var clampedSweep = Math.Clamp(sweepDeg, 0.5, 359.9);
        var startRad = startDeg * Math.PI / 180.0;
        var endRad = (startDeg + clampedSweep) * Math.PI / 180.0;

        var startPt = new Windows.Foundation.Point(
            cx + r * Math.Cos(startRad),
            cy + r * Math.Sin(startRad));
        var endPt = new Windows.Foundation.Point(
            cx + r * Math.Cos(endRad),
            cy + r * Math.Sin(endRad));

        var arc = new ArcSegment
        {
            Point = endPt,
            Size = new Windows.Foundation.Size(r, r),
            IsLargeArc = clampedSweep > 180.0,
            SweepDirection = SweepDirection.Clockwise,
        };

        var figure = new PathFigure
        {
            StartPoint = startPt,
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(arc);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static PathGeometry BuildBoltGeometry(double w, double h)
    {
        Windows.Foundation.Point Pt(double nx, double ny) =>
            new(nx / 12.0 * w, ny / 14.0 * h);

        var figure = new PathFigure
        {
            StartPoint = Pt(7.3, 0.4),
            IsClosed = true,
            IsFilled = true,
        };

        var poly = new PolyLineSegment();
        poly.Points.Add(Pt(1.4, 7.8));
        poly.Points.Add(Pt(5.5, 7.8));
        poly.Points.Add(Pt(4.4, 13.6));
        poly.Points.Add(Pt(10.6, 5.9));
        poly.Points.Add(Pt(6.5, 5.9));
        figure.Segments.Add(poly);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
