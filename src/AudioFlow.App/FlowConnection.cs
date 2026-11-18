using Nodify;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace AudioFlow.App;

public sealed class FlowConnection : Connection
{
    private static readonly AnimationClock IdleClock = (AnimationClock)new DoubleAnimation(0, 1, TimeSpan.FromSeconds(3.2)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock(true);
    private static readonly AnimationClock SignalClock = (AnimationClock)new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock(true);
    private static int runningConnections;
    private bool registered;
    private Point disconnectedMiddle, lastSource, lastTarget;
    private bool middleValid;
    private static readonly DoubleCollection DisconnectedDashes = new([4d, 3d]);
    public static readonly DependencyProperty IsDisconnectedProperty = DependencyProperty.Register(nameof(IsDisconnected), typeof(bool), typeof(FlowConnection), new PropertyMetadata(false, Refresh));
    public bool IsDisconnected { get => (bool)GetValue(IsDisconnectedProperty); set => SetValue(IsDisconnectedProperty, value); }
    public static readonly DependencyProperty IsFlowingProperty = DependencyProperty.Register(nameof(IsFlowing), typeof(bool), typeof(FlowConnection), new PropertyMetadata(false, Refresh));
    public static readonly DependencyProperty HasSignalProperty = DependencyProperty.Register(nameof(HasSignal), typeof(bool), typeof(FlowConnection), new PropertyMetadata(false, Refresh));
    public bool IsFlowing { get => (bool)GetValue(IsFlowingProperty); set => SetValue(IsFlowingProperty, value); }
    public bool HasSignal { get => (bool)GetValue(HasSignalProperty); set => SetValue(HasSignalProperty, value); }
    public FlowConnection()
    {
        Loaded += (_, _) => UpdateFlow(); Unloaded += (_, _) => Detach(); IsVisibleChanged += (_, _) => UpdateFlow();
    }
    private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs _) => ((FlowConnection)sender).UpdateFlow();
    private void UpdateFlow()
    {
        StrokeDashArray = IsDisconnected ? DisconnectedDashes : null;
        if (!IsLoaded || !IsVisible || !IsFlowing || IsDisconnected) { Detach(); Opacity = IsDisconnected ? 1 : .35; DirectionalArrowsCount = 0; InvalidateVisual(); return; }
        if (!registered)
        {
            registered = true;
            if (runningConnections++ == 0) { IdleClock.Controller!.Begin(); SignalClock.Controller!.Begin(); }
        }
        DirectionalArrowsCount = 2; Opacity = HasSignal ? 1 : .5; StrokeThickness = HasSignal ? 3.2 : 2.4;
        ApplyAnimationClock(DirectionalArrowsOffsetProperty, HasSignal ? SignalClock : IdleClock);
    }
    private void Detach()
    {
        ApplyAnimationClock(DirectionalArrowsOffsetProperty, null);
        if (registered && --runningConnections == 0) { IdleClock.Controller!.Stop(); SignalClock.Controller!.Stop(); }
        registered = false;
    }
    protected override ((Point ArrowStartSource, Point ArrowStartTarget), (Point ArrowEndSource, Point ArrowEndTarget)) DrawLineGeometry(StreamGeometryContext context, Point source, Point target)
    {
        var result = base.DrawLineGeometry(context, source, target);
        if (!middleValid || source != lastSource || target != lastTarget)
        {
            var geometry = new StreamGeometry();
            using (var copy = geometry.Open()) base.DrawLineGeometry(copy, source, target);
            geometry.Freeze();
            PathGeometry.CreateFromGeometry(geometry).GetPointAtFractionLength(.5, out disconnectedMiddle, out _);
            lastSource = source; lastTarget = target; middleValid = true;
        }
        return result;
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!IsDisconnected || !middleValid) return;
        var background = Theme.Brush("Surface.Canvas");
        drawingContext.DrawEllipse(background, null, disconnectedMiddle, 11, 11);
        var pen = new Pen(Theme.Brush("Accent.Disconnected"), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawLine(pen, disconnectedMiddle + new Vector(-5, -5), disconnectedMiddle + new Vector(5, 5));
        drawingContext.DrawLine(pen, disconnectedMiddle + new Vector(-5, 5), disconnectedMiddle + new Vector(5, -5));
    }
}
