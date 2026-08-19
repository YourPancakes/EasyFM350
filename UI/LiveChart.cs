using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EasyFM350.Wpf.Backend.Config;

namespace EasyFM350.Wpf.UI;

public class LiveChart : FrameworkElement
{
    private const int MaxSamples = 240;
    private const int TopDb = -60, BottomDb = -125;

    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x25, 0x30));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0x63, 0x6D));
    private static readonly Typeface AxisFont = new("Consolas");
    private static readonly Typeface UiFont = new("Segoe UI");
    private static readonly Pen GridPen = new(GridBrush, 1);
    private static ChartBrushes? _brushes;
    private readonly int[] _ring = new int[MaxSamples];

    private FormattedText[]? _axisLabels;
    private double _axisLabelsDpi;
    private int _count;
    private int _ringHead;

    static LiveChart()
    {
        GridBrush.Freeze();
        AxisBrush.Freeze();
        GridPen.Freeze();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    public void Add(int rsrp)
    {
        if (_count < MaxSamples)
        {
            _ring[(_ringHead + _count) % MaxSamples] = rsrp;
            _count++;
        }
        else
        {
            _ring[_ringHead] = rsrp;
            _ringHead = (_ringHead + 1) % MaxSamples;
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _count = 0;
        _ringHead = 0;
        InvalidateVisual();
    }

    private int SampleAt(int i)
    {
        return _ring[(_ringHead + i) % MaxSamples];
    }

    private static ChartBrushes GetBrushes()
    {
        if (_brushes != null) return _brushes;
        var accent = (SolidColorBrush)Application.Current.Resources["Accent"];
        var lineBrush = new SolidColorBrush(Color.FromArgb(208,
            (byte)(accent.Color.R * 3 / 4), (byte)(accent.Color.G * 3 / 4), (byte)(accent.Color.B * 3 / 4)));
        lineBrush.Freeze();
        var linePen = new Pen(lineBrush, 2)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(56, accent.Color.R, accent.Color.G, accent.Color.B),
            Color.FromArgb(0, accent.Color.R, accent.Color.G, accent.Color.B),
            new Point(0, 0), new Point(0, 1));
        fillBrush.Freeze();
        _brushes = new ChartBrushes(linePen, fillBrush);
        return _brushes;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 46 || h < 30) return;
        var brushes = GetBrushes();

        double x0 = 36, y0 = 8, gw = w - 44, gh = h - 26;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var axisLabels = _axisLabels;
        if (axisLabels == null || _axisLabelsDpi != dpi)
        {
            _axisLabelsDpi = dpi;
            var labels = new List<FormattedText>();
            for (var db = TopDb; db >= BottomDb + 5; db -= 10)
                labels.Add(new FormattedText(db.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, AxisFont, 10, AxisBrush, dpi));
            axisLabels = labels.ToArray();
            _axisLabels = axisLabels;
        }

        var li = 0;
        for (var db = TopDb; db >= BottomDb + 5; db -= 10)
        {
            var y = y0 + (TopDb - db) * gh / (TopDb - BottomDb);
            dc.DrawLine(GridPen, new Point(x0, y), new Point(x0 + gw, y));
            dc.DrawText(axisLabels[li++], new Point(2, y - 6));
        }

        if (_count < 2)
        {
            var t = new FormattedText(Lang.T("empty_hint").Replace("\r\n", " "), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, UiFont, 12, AxisBrush, dpi);
            dc.DrawText(t, new Point(x0 + Math.Max(0, (gw - t.Width) / 2), y0 + gh / 2 - 8));
            return;
        }

        var lineGeo = new StreamGeometry();
        var fillGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        using (var fctx = fillGeo.Open())
        {
            var lastX = x0;
            for (var i = 0; i < _count; i++)
            {
                var x = x0 + (MaxSamples - _count + i) * gw / (MaxSamples - 1);
                var y = y0 + (TopDb - Clamp(SampleAt(i))) * gh / (TopDb - BottomDb);
                if (i == 0)
                {
                    ctx.BeginFigure(new Point(x, y), false, false);
                    fctx.BeginFigure(new Point(x, y0 + gh), true, true);
                    fctx.LineTo(new Point(x, y), true, false);
                }
                else
                {
                    ctx.LineTo(new Point(x, y), true, false);
                    fctx.LineTo(new Point(x, y), true, false);
                }

                lastX = x;
            }

            fctx.LineTo(new Point(lastX, y0 + gh), true, false);
        }

        dc.DrawGeometry(brushes.FillBrush, null, fillGeo);
        dc.DrawGeometry(null, brushes.LinePen, lineGeo);
    }

    private static int Clamp(int v)
    {
        return v > TopDb ? TopDb : v < BottomDb ? BottomDb : v;
    }

    private sealed record ChartBrushes(Pen LinePen, LinearGradientBrush FillBrush);
}