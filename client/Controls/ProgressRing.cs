using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hotix.InvoiceClient.Controls;

/// <summary>
/// A circular progress indicator with a thin arc stroke.
/// Replaces horizontal progress bars with a Suprematist/Bauhaus-inspired ring.
/// </summary>
public class ProgressRing : Control
{
    static ProgressRing()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ProgressRing),
            new FrameworkPropertyMetadata(typeof(ProgressRing)));
    }

    public ProgressRing()
    {
        SizeChanged += OnSizeChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    // ── Dependency Properties ──────────────────────────────

    public static readonly DependencyProperty PercentageProperty =
        DependencyProperty.Register(
            nameof(Percentage),
            typeof(double),
            typeof(ProgressRing),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPercentageChanged));

    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(ProgressRing),
            new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public static readonly DependencyProperty TrackColorProperty =
        DependencyProperty.Register(
            nameof(TrackColor),
            typeof(Brush),
            typeof(ProgressRing),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE9, 0xE7, 0xE2))));

    public Brush TrackColor
    {
        get => (Brush)GetValue(TrackColorProperty);
        set => SetValue(TrackColorProperty, value);
    }

    public static readonly DependencyProperty ProgressColorProperty =
        DependencyProperty.Register(
            nameof(ProgressColor),
            typeof(Brush),
            typeof(ProgressRing),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xD9, 0x47, 0x2B))));

    public Brush ProgressColor
    {
        get => (Brush)GetValue(ProgressColorProperty);
        set => SetValue(ProgressColorProperty, value);
    }

    // ── Rendering ──────────────────────────────────────────

    private static void OnPercentageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProgressRing ring)
            ring.AnimateTo((double)e.NewValue);
    }

    private void AnimateTo(double newPercentage)
    {
        double targetAngle = (newPercentage / 100.0) * 359.999; // keep a tiny gap at 100%
        var anim = new DoubleAnimation
        {
            To = targetAngle,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTargetProperty(anim, new PropertyPath("(0)", CurrentAngleProperty));
        var sb = new Storyboard();
        sb.Children.Add(anim);
        BeginStoryboard(sb);
    }

    private static readonly DependencyProperty CurrentAngleProperty =
        DependencyProperty.Register(
            "CurrentAngle",
            typeof(double),
            typeof(ProgressRing),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnCurrentAngleChanged));

    private static void OnCurrentAngleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Force redraw via InvalidateVisual
        (d as ProgressRing)?.InvalidateVisual();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        double stroke = StrokeThickness;
        double radius = (size - stroke) / 2.0;
        Point center = new(ActualWidth / 2.0, ActualHeight / 2.0);
        double angle = (Percentage / 100.0) * 359.999;

        // ── Track (full circle, light gray) ──
        var trackPen = new Pen(TrackColor, stroke) { StartLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        // ── Progress arc ──
        if (angle <= 0) return;

        var progressPen = new Pen(ProgressColor, stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        double radAngle = angle * (Math.PI / 180.0);
        double startRad = -Math.PI / 2.0; // start from top (12 o'clock)
        double endRad = startRad + radAngle;

        Point startPoint = new(
            center.X + radius * Math.Cos(startRad),
            center.Y + radius * Math.Sin(startRad));

        Point endPoint = new(
            center.X + radius * Math.Cos(endRad),
            center.Y + radius * Math.Sin(endRad));

        bool isLargeArc = angle > 180.0;

        var arcFigure = new PathFigure { StartPoint = startPoint, IsClosed = false };
        arcFigure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(radius, radius),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise,
            IsStroked = true,
        });

        var pathGeo = new PathGeometry();
        pathGeo.Figures.Add(arcFigure);

        dc.DrawGeometry(null, progressPen, pathGeo);
    }
}
