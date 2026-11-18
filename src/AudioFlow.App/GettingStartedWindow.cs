using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace AudioFlow.App;

internal sealed class GettingStartedWindow : Window
{
    private readonly MainViewModel model;
    private readonly GettingStartedState state;
    private readonly Action<Uri>? openLink;
    private readonly Func<Window?, string>? installDriver;
    private readonly StackPanel page = new();
    private readonly ScrollViewer scroll;
    private readonly Button next = GuideUi.Button("下一步  →", "GuideNext", true);
    private readonly Button previous = GuideUi.Button("←  上一步", "GuidePrevious");
    private readonly Button skip = GuideUi.Button("跳过引导，先看看", "GuideSkip");
    private readonly Button[] steps = new Button[3];
    private readonly TextBlock progress = GuideUi.Text("", "Caption", "Text.Muted");
    internal int Step { get; private set; }

    public GettingStartedWindow(MainViewModel model, Action<Uri>? openLink = null, Func<Window?, string>? installDriver = null)
    {
        this.model = model; state = new(model.Store.DataDirectory);
        this.openLink = openLink; this.installDriver = installDriver;
        Title = "欢迎使用声流 · 新手引导"; MinWidth = 860; MinHeight = 640;
        Width = Math.Clamp(SystemParameters.WorkArea.Width - 40, MinWidth, 1040);
        Height = Math.Clamp(SystemParameters.WorkArea.Height - 40, MinHeight, 790);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        Theme.Use(this, BackgroundProperty, "Surface.Window"); Theme.Use(this, ForegroundProperty, "Text.Primary");
        next.IsDefault = true; skip.IsCancel = true;
        NameScope.SetNameScope(this, new NameScope());
        RegisterName(next.Name, next); RegisterName(previous.Name, previous); RegisterName(skip.Name, skip);
        var root = new Grid(); root.ColumnDefinitions.Add(new() { Width = new(220) }); root.ColumnDefinitions.Add(new());
        Theme.Use(root, Panel.BackgroundProperty, "Surface.Window");
        var rail = new DockPanel { Margin = new(0) };
        var railContent = new StackPanel { Margin = new(24, 30, 20, 0) };
        var brand = GuideUi.Text("声流\nAudioFlow", "Heading", "Text.Primary"); brand.FontWeight = FontWeights.SemiBold;
        railContent.Children.Add(brand);
        var strap = GuideUi.Text("把声音送到\n你想去的地方", "Body", "Accent.Output"); strap.Margin = new(0, 12, 0, 40); railContent.Children.Add(strap);
        string[] labels = ["认识声流", "学会连接声音", "按需准备驱动"];
        for (int i = 0; i < steps.Length; i++)
        {
            int selected = i;
            steps[i] = GuideUi.Button($"{i + 1:00}   {labels[i]}", "GuideStep" + i);
            steps[i].HorizontalContentAlignment = HorizontalAlignment.Left; steps[i].Margin = new(0, 0, 0, 12);
            steps[i].Click += (_, _) => { Step = selected; RenderStep(); };
            railContent.Children.Add(steps[i]);
        }
        var railNote = GuideUi.Text("随时可以跳过。\n之后在设置中重新查看。", "Caption", "Text.Muted");
        railNote.Margin = new(24, 16, 20, 28); DockPanel.SetDock(railNote, Dock.Bottom); rail.Children.Add(railNote); rail.Children.Add(railContent);
        var railSurface = new Border { Child = rail, BorderThickness = new(0, 0, 1, 0) };
        Theme.Use(railSurface, Border.BackgroundProperty, "Surface.Panel"); Theme.Use(railSurface, Border.BorderBrushProperty, "Border.Default");
        root.Children.Add(railSurface);
        var content = new Grid(); content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.Margin = new(32, 28, 32, 24);
        scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content.Children.Add(scroll);
        var footer = new Grid { Margin = new(24, 16, 24, 18) };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var left = new StackPanel(); skip.SetResourceReference(StyleProperty, "QuietButton"); skip.HorizontalAlignment = HorizontalAlignment.Left;
        left.Children.Add(skip); progress.Margin = new(10, 8, 0, 0); left.Children.Add(progress); footer.Children.Add(left);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(previous); actions.Children.Add(next); Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        var footerSurface = new Border { Child = footer, BorderThickness = new(0, 1, 0, 0) };
        Theme.Use(footerSurface, Border.BorderBrushProperty, "Border.Default"); Grid.SetRow(footerSurface, 1); content.Children.Add(footerSurface);
        Grid.SetColumn(content, 1); root.Children.Add(content); Content = root;
        previous.Click += (_, _) => { if (Step > 0) Step--; RenderStep(); };
        next.Click += (_, _) => { if (Step == 2) Close(); else { Step++; RenderStep(); } };
        skip.Click += (_, _) => Close();
        RenderStep();
    }
    private void Heading(string eyebrow, string title, string description)
    {
        page.Children.Add(GuideUi.Text(eyebrow, "Caption", "Accent.Output"));
        var heading = GuideUi.Text(title, "Hero", "Text.Primary"); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(0, 10, 0, 12);
        page.Children.Add(heading);
        var copy = GuideUi.Text(description); copy.LineHeight = 23; copy.Margin = new(0, 0, 0, 18); page.Children.Add(copy);
    }
    private void RenderStep()
    {
        page.Children.Clear(); scroll.ScrollToTop();
        for (int i = 0; i < steps.Length; i++)
        {
            Theme.Use(steps[i], Control.BackgroundProperty, i == Step ? "Surface.View.Application" : "Surface.Panel");
            Theme.Use(steps[i], Control.ForegroundProperty, i == Step ? "Accent.Output" : "Text.Secondary");
        }
        previous.Visibility = Step == 0 ? Visibility.Collapsed : Visibility.Visible;
        next.Content = Step == 2 ? "开始使用  →" : "下一步  →";
        skip.Content = Step == 2 ? "暂不安装，先看看" : "跳过引导，先看看";
        progress.Text = $"第 {Step + 1} 步，共 3 步 · 随时可以跳过";
        if (Step == 0)
        {
            Heading("01 / 认识声流", "让每个软件，拥有自己的声音路线", "声流是一个 Windows 音频路由器。像连接积木一样，把软件、麦克风和播放设备连起来，决定声音在哪里播放、送给谁。");
            var diagram = new Grid { Margin = new(0, 8, 0, 22) };
            diagram.ColumnDefinitions.Add(new()); diagram.ColumnDefinitions.Add(new() { Width = new(46) }); diagram.ColumnDefinitions.Add(new());
            var source = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            source.Children.Add(GuideUi.Text("♪  软件声音", "Section", "Accent.Output"));
            var sourceCopy = GuideUi.Text("音乐 · 游戏 · 浏览器", "Body", "Text.Primary"); sourceCopy.Margin = new(0, 10, 0, 0); source.Children.Add(sourceCopy);
            var sourceCard = GuideUi.Card(source); sourceCard.VerticalAlignment = VerticalAlignment.Center; diagram.Children.Add(sourceCard);
            var arrow = GuideUi.Text("→", "Hero", "Accent.Output"); arrow.VerticalAlignment = VerticalAlignment.Center; arrow.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(arrow, 1); diagram.Children.Add(arrow);
            var destinations = new StackPanel();
            foreach (var (name, detail) in new[] { ("耳机 / 扬声器", "一路声音，可以同时送到多个输出"), ("另一款软件的麦克风", "通过虚拟音频线，传递软件声音") })
            {
                var target = new StackPanel(); target.Children.Add(GuideUi.Text(name, "Title", "Text.Primary"));
                var targetCopy = GuideUi.Text(detail, "Caption"); targetCopy.Margin = new(0, 7, 0, 0); target.Children.Add(targetCopy);
                var card = GuideUi.Card(target); card.Margin = new(0, 0, 0, 10); destinations.Children.Add(card);
            }
            Grid.SetColumn(destinations, 2); diagram.Children.Add(destinations); page.Children.Add(diagram);
            AddFeature("播放更自由", "让不同软件使用不同的耳机或扬声器，也能把同一份声音复制到多个设备。");
            AddFeature("麦克风不止能收人声", "把音乐、游戏声和真实麦克风混合，送入聊天、录音或直播软件；不同内容使用不同虚拟通道。");
            var local = GuideUi.Text("音频在本机处理。你可以先浏览界面，等需要虚拟麦克风时再准备驱动。", "Caption", "Text.Muted");
            local.Margin = new(0, 12, 0, 0); page.Children.Add(local);
        }
        else if (Step == 1)
        {
            Heading("02 / 学会连接声音", "四步，连起第一条声音路线", "单软件配置适合专心设置一个应用；全局画布可以查看全部连接。两个视图共用一份配置，切换不会打断声音。");
            string[] titles = ["选择要配置的软件", "添加声音的目的地", "把输出端拖到输入端", "开启声音路由"];
            string[] details = ["先让软件播放声音，再从左侧选择它；没找到时点击「添加应用」搜索或手动选择。",
                "在画布空白处右键，添加播放设备或麦克风卡片，然后在卡片中选择具体设备。",
                "从软件右侧的「声音输出」拖到目标左侧的「播放输入 / 声音输入」。一份声音可以连接多个目标。",
                "点击右上角「开启声音路由」。之后的连线、音量与静音修改实时生效，配置自动保存。"];
            for (int i = 0; i < titles.Length; i++)
            {
                var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new(42) }); row.ColumnDefinitions.Add(new());
                var number = GuideUi.Text((i + 1).ToString("00"), "Title", "Accent.Output"); number.FontWeight = FontWeights.SemiBold; row.Children.Add(number);
                var text = new StackPanel(); text.Children.Add(GuideUi.Text(titles[i], "CardTitle", "Text.Primary"));
                var description = GuideUi.Text(details[i]); description.Margin = new(0, 7, 0, 0); text.Children.Add(description);
                Grid.SetColumn(text, 1); row.Children.Add(text); var card = GuideUi.Card(row); card.Margin = new(0, 0, 0, 10); page.Children.Add(card);
            }
            var tip = GuideUi.Text("小提示：默认输出卡片控制软件原声；新增播放设备连接用于复制声音。停止路由会还原声音设置，关闭窗口默认退出并还原。", "Caption", "Text.Muted");
            tip.Margin = new(0, 8, 0, 0); page.Children.Add(tip);
        }
        else
        {
            Heading("03 / 按需准备驱动", "先用起来，驱动可以稍后再装", "只调整软件到耳机或扬声器的播放去向，可以直接开始。要把软件声音送进另一款软件的麦克风输入，再安装虚拟音频线。");
            page.Children.Add(new DriverDownloadsPanel(model, openLink, installDriver));
            var tip = GuideUi.Text("安装后，接收软件的麦克风选 CABLE Output 或 Hi-Fi Cable Output。设置中会一直保留下载、安装和检测入口。", "Caption", "Text.Muted");
            tip.Margin = new(0, 12, 0, 0); page.Children.Add(tip);
        }
    }
    private void AddFeature(string title, string detail)
    {
        var feature = new StackPanel(); feature.Children.Add(GuideUi.Text(title, "CardTitle", "Text.Primary"));
        var description = GuideUi.Text(detail); description.Margin = new(0, 6, 0, 0); feature.Children.Add(description);
        var card = GuideUi.Card(feature, "Surface.Panel"); card.Margin = new(0, 0, 0, 10); page.Children.Add(card);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        try { state.Dismiss(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { model.Notify("引导偏好未保存，下次可能再次显示：" + error.Message); }
    }
}
