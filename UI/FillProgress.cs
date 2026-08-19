using System;
using System.Windows;
using System.Windows.Media;

namespace EasyFM350.Wpf.UI;

public sealed class FillProgress : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(FillProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush? _fill;

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;

        _fill ??= TryFindResource("Accent") as Brush ?? Brushes.DodgerBlue;

        var fillW = Math.Max(0.0, Math.Min(1.0, Progress)) * w;
        if (fillW < 1) return;

        var radius = Math.Min(6.0, h / 2);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));
        dc.DrawRectangle(_fill, null, new Rect(0, 0, fillW, h));
        dc.Pop();
    }
}