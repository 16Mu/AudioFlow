using AudioFlow.Audio;
using AudioFlow.Core;
using System.Diagnostics;
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
    public SettingsWindow(MainViewModel model)
    {
        this.model = model;
        SetResourceReference(StyleProperty, typeof(Window));
        Theme.Use(this, BackgroundProperty, "Surface.Window"); Theme.Use(this, ForegroundProperty, "Text.Primary");
        Title = "声流 · 音频设备与设置"; Width = 780; Height = 740; MinWidth = 620; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        Theme.Use(panel, TextElement.ForegroundProperty, "Text.Primary");
        panel.Children.Add(new TextBlock { Text = "音频设备与虚拟麦克风", FontSize = 23, FontWeight = FontWeights.SemiBold });
        Theme.Use(counts, TextBlock.ForegroundProperty, "Accent.Output"); panel.Children.Add(counts);
        panel.Children.Add(new TextBlock { Text = "播放设备用于把声音送到耳机、扬声器或虚拟播放端；独立虚拟麦克风必须有配对的播放端和录音端。播放输出数量不等于虚拟麦克风路数。", Margin = new(0, 0, 0, 16), Foreground = Theme.Brush("Text.Secondary") });
        panel.Children.Add(new TextBlock { Text = "独立虚拟麦克风", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(channels);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 12) };
        var refresh = new Button { Content = "重新检测设备" };
        refresh.Click += async (_, _) => { await model.RefreshCatalogAsync(); FillChannels(); };
        bar.Children.Add(refresh);
        var add = new Button { Content = "添加已有虚拟设备对" };
        add.Click += (_, _) => AddManualPair();
        bar.Children.Add(add);
        panel.Children.Add(bar);
        panel.Children.Add(result);
        panel.Children.Add(new TextBlock { Text = "可用播放设备", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 14, 0, 8) });
        panel.Children.Add(playbackDevices);
        panel.Children.Add(new TextBlock { Text = "获取免费虚拟音频设备", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 20, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "当前已安装的独立设备对决定路数。新增虚拟驱动后重新检测，已知设备自动加入，未知设备可手动绑定并测试；没有额外录音端时，增加播放设备不能增加独立麦克风。驱动安装和许可遵循原厂要求。", Foreground = Theme.Brush("Text.Secondary"), FontSize = 12 });
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 16) };
        LinkButton(links, "VB-Audio 官方下载", "https://vb-audio.com/Cable/");
        LinkButton(links, "Voicemeeter Banana", "https://vb-audio.com/Voicemeeter/banana.htm");
        panel.Children.Add(links);
        var close = new CheckBox { Content = "关闭窗口时保留在托盘并继续路由（默认关闭窗口会退出并恢复声音）", IsChecked = model.CloseToTray };
        close.Checked += (_, _) => model.CloseToTray = true; close.Unchecked += (_, _) => model.CloseToTray = false;
        panel.Children.Add(close);
        var resume = new CheckBox { Content = "重开软件时恢复上次配置和开启／停止状态", IsChecked = model.ResumeOnLaunch };
        resume.Checked += (_, _) => model.ResumeOnLaunch = true; resume.Unchecked += (_, _) => model.ResumeOnLaunch = false;
        panel.Children.Add(resume);
        panel.Children.Add(new TextBlock { Text = "音频在本机实时处理，场景保存到本机。部分独占、ASIO 或受保护音频无法按应用捕获。", Foreground = Theme.Brush("Text.Muted"), FontSize = 11, Margin = new(0, 18, 0, 0) });
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
        counts.Text = $"当前检测到 {outputs} 个播放设备；" + model.ChannelCapacity;
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
            box.Children.Add(new TextBlock { Text = "接收软件选择：" + (capture?.Name ?? "录音设备未连接"), Foreground = Theme.Brush("Text.Primary"), Margin = new(0, 7, 0, 0), FontSize = 12 });
            box.Children.Add(new TextBlock { Text = "发送端：" + (render?.Name ?? "播放设备未连接"), Foreground = Theme.Brush("Text.Secondary"), FontSize = 11, Margin = new(0, 4, 0, 10) });
            box.Children.Add(new TextBlock { Text = channel.Id == model.Graph.SilentInputChannelId ? "录音关闭专用 · 已预留" : capture == null || render == null ? "设备未连接" : channel.RequiresValidation ? "待验证 · 通过独立信号测试后可分配" : "已连接 · " + model.ChannelOwners(channel), Foreground = Theme.Brush("Text.Secondary"), FontSize = 11, Margin = new(0, 0, 0, 8) });
            var test = new Button { Content = channel.Id == model.Graph.SilentInputChannelId ? "录音关闭专用 · 静音输入" : "测试此通道", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = render != null && capture != null && channel.Id != model.Graph.SilentInputChannelId };
            test.Click += async (_, _) =>
            {
                testing = true; test.IsEnabled = false; result.Text = "正在发送短测试音并检查录音端…";
                try
                {
                    if (capture!.SampleRate != render!.SampleRate)
                    { AudioDeviceSettings.SetSampleRate(capture.Id, 48000); AudioDeviceSettings.SetSampleRate(render.Id, 48000); }
                    var check = await ChannelTest.RunIsolationAsync(channel, ChannelPool.Connected(model.Graph, model.Catalog.Endpoints));
                    if (check.Passed) { channel.RequiresValidation = false; model.Edited(true); }
                    result.Text = check.Passed ? channel.Name + "：独立信号测试通过。" : check.Crosstalk.Count > 0 ?
                        channel.Name + "：与 " + string.Join("、", check.Crosstalk) + " 存在串音，暂不计作独立通道。请检查混音器设置。" :
                        channel.Name + "：尚未收到测试信号，请检查驱动。";
                }
                catch (Exception ex) { result.Text = "测试未完成：" + ex.Message; }
                finally { testing = false; FillChannels(); }
            };
            box.Children.Add(test);
            channels.Children.Add(new Border { Child = box, Background = Theme.Brush("Surface.Card"), BorderBrush = Theme.Brush("Border.Default"), BorderThickness = new(1), CornerRadius = new(12), Margin = new(0, 0, 0, 9) });
        }
        if (channels.Children.Count == 0) channels.Children.Add(new TextBlock { Text = "尚未发现虚拟音频设备。安装驱动后点击「重新检测设备」。", Foreground = Theme.Brush("Text.Secondary") });
        playbackDevices.Children.Clear();
        foreach (var device in model.Catalog.Endpoints.Where(e => !e.Capture))
            playbackDevices.Children.Add(new TextBlock { Text = device.Name + (device.Default ? " · Windows 默认" : "") + (device.Virtual ? " · 虚拟播放端" : " · 播放输出"), Foreground = Theme.Brush("Text.Primary"), Margin = new(0, 0, 0, 7), FontSize = 12 });
    }
    private static void LinkButton(Panel parent, string text, string url)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        parent.Children.Add(button);
    }
    private void AddManualPair()
    {
        var dialog = new Window { Title = "添加虚拟麦克风设备对", Width = 560, Height = 340, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(StyleProperty, typeof(Window));
        var panel = new StackPanel { Margin = new(22) };
        var name = new TextBox { Text = "虚拟麦克风 " + (model.Graph.Channels.Count + 1) };
        var render = new ComboBox { ItemsSource = model.Catalog.Endpoints.Where(e => !e.Capture).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new(0, 8, 0, 10) };
        var capture = new ComboBox { ItemsSource = model.Catalog.Endpoints.Where(e => e.Capture).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new(0, 8, 0, 12) };
        panel.Children.Add(new TextBlock { Text = "通道名称" }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "虚拟播放端", Margin = new(0, 14, 0, 0) }); panel.Children.Add(render);
        panel.Children.Add(new TextBlock { Text = "同一条虚拟音频线的录音端" }); panel.Children.Add(capture);
        var save = new Button { Content = "测试并添加" }; panel.Children.Add(save);
        save.Click += async (_, _) =>
        {
            if (render.SelectedItem is not AudioEndpoint r || capture.SelectedItem is not AudioEndpoint c) return;
            var pair = new VirtualChannel { Id = Guid.NewGuid().ToString("N"), Name = name.Text, RenderEndpointId = r.Id, CaptureEndpointId = c.Id };
            save.IsEnabled = false;
            try
            {
                var check = await ChannelTest.RunIsolationAsync(pair, ChannelPool.Connected(model.Graph, model.Catalog.Endpoints));
                if (!check.Passed) { MessageBox.Show(dialog, check.Crosstalk.Count > 0 ? "这组设备与 " + string.Join("、", check.Crosstalk) + " 存在串音，不能计作独立通道。" : "没有检测到设备之间传递的测试音，未添加这组设备。", "通道未通过"); return; }
                if (model.Graph.Channels.Any(ch => ch.CaptureEndpointId == c.Id || ch.RenderEndpointId == r.Id))
                { MessageBox.Show(dialog, "这个设备已属于现有通道，不能计作另一路独立麦克风。", "通道已存在"); return; }
                model.Graph.Channels.Add(pair); await model.RefreshCatalogAsync(); FillChannels(); model.SaveNow(); dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "测试未完成"); }
            finally { save.IsEnabled = true; }
        };
        dialog.Content = panel; dialog.ShowDialog();
    }
}
