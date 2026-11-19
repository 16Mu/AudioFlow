using AudioFlow.Audio;
using AudioFlow.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AudioFlow.App;

public sealed class SettingsWindow : Window
{
    private readonly MainViewModel model;
    private readonly StackPanel channels = new();
    private readonly StackPanel playbackDevices = new();
    private readonly TextBlock counts = new() { Margin = new(0, 10, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock result = new() { Margin = new(0, 12, 0, 0), Foreground = Theme.Brush("Accent.Success") };
    private bool testing;
    public SettingsWindow(MainViewModel model) : this(model, null, null, null) { }
    internal SettingsWindow(MainViewModel model, Action<GettingStartedWindow>? prepareGuide, Action<Uri>? openLink, Func<Window?, string>? installDriver)
    {
        this.model = model;
        SetResourceReference(StyleProperty, typeof(Window));
        Theme.Use(this, BackgroundProperty, "Surface.Window"); Theme.Use(this, ForegroundProperty, "Text.Primary");
        Title = "声流 · 设备与设置"; Width = 780; Height = 740; MinWidth = 620; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        Theme.Use(panel, TextElement.ForegroundProperty, "Text.Primary");
        panel.Children.Add(new TextBlock { Text = "声音设备与设置", FontSize = 23, FontWeight = FontWeights.SemiBold });
        Theme.Use(counts, TextBlock.ForegroundProperty, "Accent.Output"); panel.Children.Add(counts);
        panel.Children.Add(new TextBlock { Text = "耳机和扬声器用来听声音。想把音乐或游戏声交给聊天、录音软件，需要能传声音的麦克风。多接一个耳机，不会多出一个这样的麦克风。", Margin = new(0, 0, 0, 16), Foreground = Theme.Brush("Text.Secondary") });
        var driverDownloads = new DriverDownloadsPanel(model, openLink, installDriver) { Name = "SettingsDriverDownloads" };
        var guideSection = new StackPanel();
        guideSection.Children.Add(GuideUi.Text("第一次用？从这里开始", "Title", "Text.Primary"));
        var guideCopy = GuideUi.Text("了解怎么用、学会连线，或准备把声音传给其他软件。"); guideCopy.Margin = new(0, 8, 0, 12);
        guideSection.Children.Add(guideCopy);
        var guideActions = new WrapPanel();
        var reopenGuide = GuideUi.Button("查看新手引导", "ReopenGettingStarted");
        reopenGuide.Click += (_, _) =>
        {
            var guide = new GettingStartedWindow(model, openLink, installDriver) { Owner = this };
            prepareGuide?.Invoke(guide); guide.ShowDialog();
        };
        var jumpToDrivers = GuideUi.Button("↓  驱动下载与安装", "ShowDriverDownloads");
        jumpToDrivers.Click += (_, _) => driverDownloads.BringIntoView();
        guideActions.Children.Add(reopenGuide); guideActions.Children.Add(jumpToDrivers); guideSection.Children.Add(guideActions);
        var guideCard = GuideUi.Card(guideSection); guideCard.Margin = new(0, 0, 0, 20); panel.Children.Add(guideCard);
        panel.Children.Add(new TextBlock { Text = "可以传不同声音的麦克风", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(channels);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 12) };
        var refresh = new Button { Content = "重新检测设备" };
        refresh.Click += async (_, _) => { await model.RefreshCatalogAsync(); FillChannels(); };
        bar.Children.Add(refresh);
        var add = new Button { Content = "添加已安装的传声音设备" };
        add.Click += (_, _) => AddManualPair();
        bar.Children.Add(add);
        panel.Children.Add(bar);
        panel.Children.Add(result);
        panel.Children.Add(new TextBlock { Text = "可以播放声音的设备", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 14, 0, 8) });
        panel.Children.Add(playbackDevices);
        panel.Children.Add(new TextBlock { Text = "驱动下载与安装", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 20, 0, 8) });
        panel.Children.Add(GuideUi.Text("只调整播放去向无需安装。需要把软件声音送入其他软件的麦克风时，再选择下面的官方驱动；已有设备会自动检测。"));
        driverDownloads.Margin = new(0, 0, 0, 22); panel.Children.Add(driverDownloads);
        var close = new CheckBox { Content = "关闭窗口后继续运行，可从任务栏右下角打开（不勾选时，关闭就退出并恢复声音）", IsChecked = model.CloseToTray };
        close.Checked += (_, _) => model.CloseToTray = true; close.Unchecked += (_, _) => model.CloseToTray = false;
        panel.Children.Add(close);
        var startup = new CheckBox { Name = "StartWithWindows", Content = "开机后自动打开声流", IsChecked = model.StartWithWindows };
        bool changingStartup = false;
        void ChangeStartup(bool enabled)
        {
            if (changingStartup) return;
            model.StartWithWindows = enabled;
            changingStartup = true; startup.IsChecked = model.StartWithWindows; changingStartup = false;
        }
        startup.Checked += (_, _) => ChangeStartup(true); startup.Unchecked += (_, _) => ChangeStartup(false);
        panel.Children.Add(startup);
        var startupHint = GuideUi.Text("登录 Windows 后自动打开。勾选下面的选项，还能接着使用上次的连接与开启／停止状态。", "Caption", "Text.Muted");
        startupHint.Margin = new(23, 2, 0, 9); panel.Children.Add(startupHint);
        var resume = new CheckBox { Content = "下次打开声流，接着用上次的设置", IsChecked = model.ResumeOnLaunch };
        resume.Checked += (_, _) => model.ResumeOnLaunch = true; resume.Unchecked += (_, _) => model.ResumeOnLaunch = false;
        panel.Children.Add(resume);
        panel.Children.Add(new TextBlock { Text = "声音和设置都留在这台电脑上。少数软件有自己的声音播放方式，可能无法在声流中转给别处。", Foreground = Theme.Brush("Text.Muted"), FontSize = 11, Margin = new(0, 18, 0, 0) });
        var scroll = new ScrollViewer { Content = panel };
        Theme.Use(scroll, Control.BackgroundProperty, "Surface.Window"); Theme.Use(scroll, Control.ForegroundProperty, "Text.Primary");
        Content = scroll;
        FillChannels();
        model.PropertyChanged += ModelChanged;
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; model.SaveNow(); };
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ChannelCapacity)) return;
        UpdateCounts(); if (!testing) FillChannels();
    }
    private void UpdateCounts()
    {
        int outputs = model.Catalog.Endpoints.Count(e => !e.Capture);
        counts.Text = $"找到 {outputs} 个能播放声音的设备；" + model.ChannelCapacity;
    }
    private void FillChannels()
    {
        UpdateCounts();
        channels.Children.Clear();
        foreach (var channel in model.Graph.Channels)
        {
            var box = new StackPanel { Margin = new(12) };
            var capture = model.Catalog.Endpoints.FirstOrDefault(e => e.Id == channel.CaptureEndpointId);
            var render = model.Catalog.Endpoints.FirstOrDefault(e => e.Id == channel.RenderEndpointId);
            box.Children.Add(new TextBlock { Text = channel.Name, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Accent.Output") });
            box.Children.Add(new TextBlock { Text = "请在接收声音的软件里选这个麦克风：" + (capture?.Name ?? "麦克风未连接"), Foreground = Theme.Brush("Text.Primary"), Margin = new(0, 7, 0, 0), FontSize = 12 });
            box.Children.Add(new TextBlock { Text = "声流往这个设备送声音：" + (render?.Name ?? "播放设备未连接"), Foreground = Theme.Brush("Text.Secondary"), FontSize = 11, Margin = new(0, 4, 0, 10) });
            box.Children.Add(new TextBlock { Text = channel.Id == model.Graph.SilentInputChannelId ? "用于关闭录音 · 已留给相应软件" : capture == null || render == null ? "设备未连接" : channel.RequiresValidation ? "还要测试 · 确认声音能正确传过去后再用" : "已连接 · " + model.ChannelOwners(channel), Foreground = Theme.Brush("Text.Secondary"), FontSize = 11, Margin = new(0, 0, 0, 8) });
            var test = new Button { Content = channel.Id == model.Graph.SilentInputChannelId ? "用于关闭录音 · 不传声音" : "试试能不能传声音", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = render != null && capture != null && channel.Id != model.Graph.SilentInputChannelId };
            test.Click += async (_, _) =>
            {
                testing = true; test.IsEnabled = false; result.Text = "正在播放一小段测试声音，看看能否传到这个麦克风…";
                try
                {
                    if (capture!.SampleRate != render!.SampleRate)
                    { AudioDeviceSettings.SetSampleRate(capture.Id, 48000); AudioDeviceSettings.SetSampleRate(render.Id, 48000); }
                    var check = await ChannelTest.RunIsolationAsync(channel, ChannelPool.Connected(model.Graph, model.Catalog.Endpoints));
                    if (check.Passed) { channel.RequiresValidation = false; model.Edited(true); }
                    result.Text = check.Passed ? channel.Name + "：声音能正确传过去，可以使用。" : check.Crosstalk.Count > 0 ?
                        channel.Name + "：与 " + string.Join("、", check.Crosstalk) + " 同时收到了这段声音，暂时不能分别传不同声音。请检查这些设备的设置。" :
                        channel.Name + "：还没听到测试声音，请检查驱动是否装好。";
                }
                catch (Exception ex) { result.Text = "测试未完成：" + UserError.Describe(ex); }
                finally { testing = false; FillChannels(); }
            };
            box.Children.Add(test);
            channels.Children.Add(new Border { Child = box, Background = Theme.Brush("Surface.Card"), BorderBrush = Theme.Brush("Border.Default"), BorderThickness = new(1), CornerRadius = new(12), Margin = new(0, 0, 0, 9) });
        }
        if (channels.Children.Count == 0) channels.Children.Add(new TextBlock { Text = "还没有能传声音的麦克风。可以先使用其他功能，需要时再安装驱动并重新检测。", Foreground = Theme.Brush("Text.Secondary") });
        playbackDevices.Children.Clear();
        foreach (var device in model.Catalog.Endpoints.Where(e => !e.Capture))
            playbackDevices.Children.Add(new TextBlock { Text = device.Name + (device.Default ? " · Windows 当前选择" : "") + (device.Virtual ? " · 用来把声音传给软件" : " · 用来听声音"), Foreground = Theme.Brush("Text.Primary"), Margin = new(0, 0, 0, 7), FontSize = 12 });
    }
    private void AddManualPair()
    {
        var dialog = new Window { Title = "添加已安装的传声音设备", Width = 560, Height = 340, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(StyleProperty, typeof(Window));
        var panel = new StackPanel { Margin = new(22) };
        var name = new TextBox { Text = "传声音的麦克风 " + (model.Graph.Channels.Count + 1) };
        var render = new ComboBox { ItemsSource = model.Catalog.Endpoints.Where(e => !e.Capture).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new(0, 8, 0, 10) };
        var capture = new ComboBox { ItemsSource = model.Catalog.Endpoints.Where(e => e.Capture).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new(0, 8, 0, 12) };
        panel.Children.Add(new TextBlock { Text = "给它起个名字" }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "声流把声音送到哪里", Margin = new(0, 14, 0, 0) }); panel.Children.Add(render);
        panel.Children.Add(new TextBlock { Text = "接收软件要选哪个麦克风" }); panel.Children.Add(capture);
        var save = new Button { Content = "测试并添加" }; panel.Children.Add(save);
        save.Click += async (_, _) =>
        {
            if (render.SelectedItem is not AudioEndpoint r || capture.SelectedItem is not AudioEndpoint c) return;
            var pair = new VirtualChannel { Id = Guid.NewGuid().ToString("N"), Name = name.Text, RenderEndpointId = r.Id, CaptureEndpointId = c.Id };
            save.IsEnabled = false;
            try
            {
                var check = await ChannelTest.RunIsolationAsync(pair, ChannelPool.Connected(model.Graph, model.Catalog.Endpoints));
                if (!check.Passed) { MessageBox.Show(dialog, check.Crosstalk.Count > 0 ? "这组设备与 " + string.Join("、", check.Crosstalk) + " 收到了同一段声音，不能用它们分别传不同声音。" : "测试声音没有传过去，这次没有添加。请检查是否选对了设备。", "声音没能正确传过去"); return; }
                if (model.Graph.Channels.Any(ch => ch.CaptureEndpointId == c.Id || ch.RenderEndpointId == r.Id))
                { MessageBox.Show(dialog, "这个设备已经添加过了，请使用原来的那一个。", "这个设备已经添加过了"); return; }
                model.Graph.Channels.Add(pair); await model.RefreshCatalogAsync(); FillChannels(); model.SaveNow(); dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, UserError.Describe(ex), "测试未完成"); }
            finally { save.IsEnabled = true; }
        };
        dialog.Content = panel; dialog.ShowDialog();
    }
}
