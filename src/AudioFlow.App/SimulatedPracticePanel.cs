using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AudioFlow.App;

internal sealed class SimulatedPracticePanel : StackPanel
{
    private readonly Canvas diagram = new() { Height = 250, ClipToBounds = true };
    private readonly Border softwareCard, deviceCard;
    private readonly Ellipse sourcePort = new() { Width = 18, Height = 18 };
    private readonly Ellipse targetPort = new() { Width = 18, Height = 18 };
    private readonly System.Windows.Shapes.Path line = new() { StrokeThickness = 3, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path pending = new() { StrokeThickness = 3, IsHitTestVisible = false };
    private readonly Ellipse movingDot = new() { Width = 9, Height = 9, IsHitTestVisible = false };
    private readonly TextBlock softwareName = GuideUi.Text("", "Title", "Text.Primary");
    private readonly TextBlock progress = GuideUi.Text("", "Caption", "Accent.Output");
    private readonly TextBlock instruction = GuideUi.Text("", "CardTitle", "Text.Primary");
    private readonly TextBlock feedback = GuideUi.Text("", "Caption", "Accent.Input");
    private readonly ProgressBar softwareMeter = new() { Maximum = 1 }, deviceMeter = new() { Maximum = 1 };
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Value = .75 };
    private readonly ComboBox deviceChoice = new() { Name = "DemoDeviceChoice", ItemsSource = new[] { "请选择模拟设备", "耳机（模拟）", "扬声器（模拟）" }, SelectedIndex = 0 };
    private readonly Button add = GuideUi.Button("＋ 添加播放设备卡片", "DemoAddDevice");
    private readonly Button play = GuideUi.Button("开启声音连接", "DemoStart", true);
    private readonly Button chooseMusic = GuideUi.Button("♪ 音乐软件（模拟）", "DemoChooseMusic");
    private readonly Button chooseVideo = GuideUi.Button("▣ 视频软件（模拟）", "DemoChooseVideo");
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool softwareChosen, deviceAdded, connected, previewPlaying, dragging, resetting;
    private double phase;
    internal bool IsCompleted { get; private set; }
    internal int Stage => IsCompleted ? 6 : !softwareChosen ? 1 : !deviceAdded ? 2 : deviceChoice.SelectedIndex == 0 ? 3 : !connected ? 4 : 5;
    internal Point SourceAnchor { get; private set; }
    internal Point TargetAnchor { get; private set; }
    internal event EventHandler? ProgressChanged;

    public SimulatedPracticePanel()
    {
        var prompt = new StackPanel(); prompt.Children.Add(progress);
        instruction.Margin = new(0, 8, 0, 8); prompt.Children.Add(instruction); prompt.Children.Add(feedback);
        var promptCard = GuideUi.Card(prompt, "Surface.Panel"); promptCard.Margin = new(0, 0, 0, 12); Children.Add(promptCard);
        var choices = new WrapPanel();
        chooseMusic.Click += (_, _) => ChooseSoftware("音乐软件（模拟）");
        chooseVideo.Click += (_, _) => ChooseSoftware("视频软件（模拟）");
        chooseMusic.Margin = chooseVideo.Margin = new(0, 0, 8, 8); choices.Children.Add(chooseMusic); choices.Children.Add(chooseVideo);
        Children.Add(choices);
        var sourceContent = new StackPanel();
        sourceContent.Children.Add(softwareName);
        var sourceDescription = GuideUi.Text("这张卡片发出声音", "Caption"); sourceDescription.Margin = new(0, 7, 0, 18); sourceContent.Children.Add(sourceDescription);
        sourceContent.Children.Add(GuideUi.Text("练习音量", "Caption")); volume.Margin = new(0, 7, 0, 12); sourceContent.Children.Add(volume);
        sourceContent.Children.Add(softwareMeter); softwareCard = GuideUi.Card(sourceContent);
        var targetContent = new StackPanel();
        targetContent.Children.Add(GuideUi.Text("播放设备（模拟）", "Title", "Text.Primary"));
        deviceChoice.Margin = new(0, 10, 0, 18); targetContent.Children.Add(deviceChoice);
        targetContent.Children.Add(GuideUi.Text("这里显示收到的模拟声音", "Caption"));
        deviceMeter.Margin = new(0, 12, 0, 0); targetContent.Children.Add(deviceMeter); deviceCard = GuideUi.Card(targetContent);
        Theme.Use(diagram, Panel.BackgroundProperty, "Surface.Canvas");
        var surface = GuideUi.Card(diagram, "Surface.Canvas"); surface.Padding = new(0); surface.Margin = new(0, 0, 0, 12); Children.Add(surface);
        Theme.Use(line, System.Windows.Shapes.Shape.StrokeProperty, "Accent.Output");
        Theme.Use(pending, System.Windows.Shapes.Shape.StrokeProperty, "Accent.Output");
        pending.StrokeDashArray = new([4, 3]);
        Theme.Use(sourcePort, System.Windows.Shapes.Shape.FillProperty, "Accent.Output");
        Theme.Use(targetPort, System.Windows.Shapes.Shape.FillProperty, "Accent.Input");
        Theme.Use(movingDot, System.Windows.Shapes.Shape.FillProperty, "Accent.Success");
        sourcePort.ToolTip = "按住这个圆点，拖到右边设备的圆点";
        targetPort.ToolTip = "把软件右侧圆点拖到这里";
        diagram.Children.Add(line); diagram.Children.Add(pending);
        diagram.Children.Add(softwareCard); diagram.Children.Add(deviceCard);
        diagram.Children.Add(sourcePort); diagram.Children.Add(targetPort); diagram.Children.Add(movingDot);
        diagram.SizeChanged += (_, _) => ArrangeCards();
        sourcePort.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (BeginDrag()) { sourcePort.CaptureMouse(); e.Handled = true; }
        };
        sourcePort.LostMouseCapture += (_, _) =>
        {
            if (!dragging) return;
            dragging = false; pending.Visibility = Visibility.Collapsed; feedback.Text = "拖动已取消，可以重新按住圆点再试。";
        };
        diagram.PreviewMouseMove += (_, e) => { if (dragging) { MoveDrag(e.GetPosition(diagram)); e.Handled = true; } };
        diagram.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            FinishDrag(e.GetPosition(diagram));
            if (sourcePort.IsMouseCaptured) sourcePort.ReleaseMouseCapture();
            e.Handled = true;
        };
        var menu = new ContextMenu();
        var menuAdd = new MenuItem { Header = "添加播放设备卡片", Name = "DemoAddDeviceMenu" };
        menuAdd.Click += (_, _) => AddDevice(); menu.Items.Add(menuAdd); diagram.ContextMenu = menu;
        var actions = new WrapPanel();
        add.Margin = new(0, 0, 8, 8); add.Click += (_, _) => AddDevice(); actions.Children.Add(add);
        play.Margin = new(0, 0, 8, 8); play.Click += (_, _) => TogglePreview(); actions.Children.Add(play);
        var reset = GuideUi.Button("重新练习", "DemoReset"); reset.Margin = new(0, 0, 0, 8); reset.Click += (_, _) => Reset(); actions.Children.Add(reset);
        Children.Add(actions);
        deviceChoice.SelectionChanged += (_, _) => { if (!resetting) { connected = false; previewPlaying = false; timer.Stop(); Update(); } };
        timer.Tick += (_, _) =>
        {
            phase += .055;
            double level = (.6 + .2 * Math.Sin(phase * 6)) * volume.Value;
            softwareMeter.Value = level; deviceMeter.Value = connected ? level : 0;
            var point = CurvePoint(phase % 1, SourceAnchor, TargetAnchor);
            Canvas.SetLeft(movingDot, point.X - 4.5); Canvas.SetTop(movingDot, point.Y - 4.5);
        };
        Loaded += (_, _) => { ArrangeCards(); if (previewPlaying) timer.Start(); };
        Unloaded += (_, _) => Stop();
        Reset();
    }
    private void ChooseSoftware(string name)
    {
        softwareChosen = true; softwareName.Text = name; feedback.Text = ""; Update();
    }
    private void AddDevice()
    {
        if (!softwareChosen) { feedback.Text = "先点选一个模拟软件。"; return; }
        deviceAdded = true; feedback.Text = ""; Update();
    }
    internal bool BeginDrag()
    {
        if (!softwareChosen || !deviceAdded || deviceChoice.SelectedIndex == 0) { feedback.Text = "先选软件，再添加设备，并在卡片里选好模拟耳机或扬声器。"; return false; }
        dragging = true; feedback.Text = ""; pending.Visibility = Visibility.Visible;
        pending.Data = Curve(SourceAnchor, SourceAnchor); return true;
    }
    internal void MoveDrag(Point position) { if (dragging) pending.Data = Curve(SourceAnchor, position); }
    internal void FinishDrag(Point position)
    {
        if (!dragging) return;
        dragging = false; pending.Visibility = Visibility.Collapsed;
        if ((position - TargetAnchor).Length <= 26)
        {
            connected = true; feedback.Text = "连好了！现在点“开启声音连接”。"; Update();
        }
        else feedback.Text = "还没对上。请把圆点拖到设备左边发亮的圆点，再松开。";
    }
    private void TogglePreview()
    {
        if (!connected) { feedback.Text = "先把两个圆点连起来。"; return; }
        previewPlaying = !previewPlaying;
        if (previewPlaying) { IsCompleted = true; timer.Start(); feedback.Text = "练习完成！移动的小点表示模拟声音正在传过去。"; }
        else { timer.Stop(); softwareMeter.Value = deviceMeter.Value = 0; feedback.Text = "模拟声音已停止，你画的线仍然保留。"; }
        Update();
    }
    private void Reset()
    {
        resetting = true;
        timer.Stop(); softwareChosen = deviceAdded = connected = previewPlaying = dragging = IsCompleted = false;
        softwareName.Text = ""; deviceChoice.SelectedIndex = 0; volume.Value = .75; phase = 0;
        softwareMeter.Value = deviceMeter.Value = 0; feedback.Text = ""; resetting = false;
        Update();
    }
    private void Update()
    {
        progress.Text = Stage == 6 ? "✓  练习完成，可以进入下一步" : $"动手练习 · 第 {Stage} 步，共 5 步";
        instruction.Text = Stage switch
        {
            1 => "点选下面的“音乐软件”或“视频软件”。",
            2 => "在练习区空白处点右键，添加播放设备卡片。也可以点下面的“＋”按钮。",
            3 => "点设备卡片里的选择框，选模拟耳机或扬声器。",
            4 => "按住软件右边的圆点，拖到设备左边的圆点，再松开。",
            5 => "最后点下面的“开启声音连接”。",
            _ => "你已经选好软件、添加设备、连线并开启了模拟声音。"
        };
        softwareCard.Visibility = sourcePort.Visibility = softwareChosen ? Visibility.Visible : Visibility.Collapsed;
        deviceCard.Visibility = targetPort.Visibility = deviceAdded ? Visibility.Visible : Visibility.Collapsed;
        line.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        pending.Visibility = dragging ? Visibility.Visible : Visibility.Collapsed;
        movingDot.Visibility = previewPlaying ? Visibility.Visible : Visibility.Collapsed;
        add.IsEnabled = softwareChosen && !deviceAdded;
        play.IsEnabled = connected; play.Content = previewPlaying ? "停止声音连接" : "开启声音连接";
        sourcePort.StrokeThickness = targetPort.StrokeThickness = Stage == 4 ? 3 : 0;
        Theme.Use(sourcePort, System.Windows.Shapes.Shape.StrokeProperty, "Text.Primary");
        Theme.Use(targetPort, System.Windows.Shapes.Shape.StrokeProperty, "Text.Primary");
        ArrangeCards(); ProgressChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ArrangeCards()
    {
        double width = Math.Max(360, diagram.ActualWidth);
        double cardWidth = Math.Min(232, (width - 80) / 2);
        softwareCard.Width = deviceCard.Width = cardWidth;
        Canvas.SetLeft(softwareCard, 16); Canvas.SetTop(softwareCard, 35);
        Canvas.SetLeft(deviceCard, width - cardWidth - 16); Canvas.SetTop(deviceCard, 35);
        SourceAnchor = new(16 + cardWidth, 120); TargetAnchor = new(width - cardWidth - 16, 120);
        Canvas.SetLeft(sourcePort, SourceAnchor.X - 9); Canvas.SetTop(sourcePort, SourceAnchor.Y - 9);
        Canvas.SetLeft(targetPort, TargetAnchor.X - 9); Canvas.SetTop(targetPort, TargetAnchor.Y - 9);
        line.Data = Curve(SourceAnchor, TargetAnchor);
    }
    private static PathGeometry Curve(Point start, Point end)
    {
        double offset = Math.Max(35, Math.Abs(end.X - start.X) / 2);
        return new([new PathFigure(start, [new BezierSegment(new(start.X + offset, start.Y), new(end.X - offset, end.Y), end, true)], false)]);
    }
    private static Point CurvePoint(double t, Point start, Point end)
    {
        double offset = Math.Max(35, Math.Abs(end.X - start.X) / 2), u = 1 - t;
        return new(u * u * u * start.X + 3 * u * u * t * (start.X + offset) + 3 * u * t * t * (end.X - offset) + t * t * t * end.X,
            u * u * u * start.Y + 3 * u * u * t * start.Y + 3 * u * t * t * end.Y + t * t * t * end.Y);
    }
    internal void Stop()
    {
        timer.Stop(); dragging = false; pending.Visibility = Visibility.Collapsed;
        if (sourcePort.IsMouseCaptured) sourcePort.ReleaseMouseCapture();
    }
}
