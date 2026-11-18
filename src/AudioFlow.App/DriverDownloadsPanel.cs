using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AudioFlow.Core;
using Microsoft.Win32;

namespace AudioFlow.App;

internal sealed record DriverDownload(string Id, string Name, string Purpose, string RenderName, string CaptureName,
    string InstallerName, Uri DownloadUri, Uri WebsiteUri);

internal static class DriverDownloads
{
    public static IReadOnlyList<DriverDownload> All { get; } =
    [
        new("Cable", "VB-CABLE", "通常先选这款 · 单独安装是 1 路", "CABLE Input", "CABLE Output", "VBCABLE_Setup_x64.exe",
            new("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip"), new("https://vb-audio.com/Cable/")),
        new("HiFiCable", "Hi-Fi CABLE", "这款也可以 · 单独安装也是 1 路", "Hi-Fi Cable Input", "Hi-Fi Cable Output", "HiFiCableAsioBridgeSetup.exe",
            new("https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip"), new("https://vb-audio.com/Cable/#DownloadASIOBridge"))
    ];

    public static void OpenOfficialLink(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    public static string SelectInstaller(Window? owner)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择已解压的 VB-Audio 驱动安装程序",
            Filter = "驱动安装程序 (*.exe)|*.exe",
            CheckFileExists = true
        };
        bool? selected = owner == null ? picker.ShowDialog() : picker.ShowDialog(owner);
        return selected == true ? LaunchInstaller(picker.FileName) : "这次没有选择安装程序，需要时可以再装。";
    }

    internal static string LaunchInstaller(string path, Action<ProcessStartInfo>? launch = null)
    {
        if (!File.Exists(path) || !All.Any(driver => string.Equals(driver.InstallerName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("先把官方压缩包里的文件全部解压，再选择 VBCABLE_Setup_x64.exe 或 HiFiCableAsioBridgeSetup.exe。");
        var start = new ProcessStartInfo(Path.GetFullPath(path))
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!
        };
        try
        {
            if (launch == null) Process.Start(start); else launch(start);
            return "已打开安装程序。完成安装并按提示重启后，点击「重新检测设备」。";
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return "已取消安装确认。需要时可以再装。"; }
    }
}

internal static class GuideUi
{
    public static TextBlock Text(string text, string size = "Body", string color = "Text.Secondary")
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        Theme.Use(block, TextBlock.FontSizeProperty, "FontSize." + size);
        Theme.Use(block, TextBlock.ForegroundProperty, color);
        return block;
    }
    public static Border Card(UIElement child, string background = "Surface.Card")
    {
        var card = new Border { Child = child, Padding = new(18), BorderThickness = new(1) };
        Theme.Use(card, Border.BackgroundProperty, background);
        Theme.Use(card, Border.BorderBrushProperty, "Border.Default");
        card.SetResourceReference(Border.CornerRadiusProperty, "Radius.Card");
        return card;
    }
    public static Button Button(string text, string name, bool primary = false)
    {
        var button = new Button { Content = text, Name = name, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        return button;
    }
}

internal sealed class DriverDownloadsPanel : StackPanel
{
    private readonly MainViewModel model;
    private readonly Action<Uri> openLink;
    private readonly Func<Window?, string> installDriver;
    private readonly TextBlock feedback = GuideUi.Text("", "Caption", "Accent.Success");
    private readonly TextBlock capacity = GuideUi.Text("", "Body", "Accent.Output");
    private readonly Dictionary<DriverDownload, TextBlock> statuses = [];
    private bool subscribed;

    public DriverDownloadsPanel(MainViewModel model, Action<Uri>? openLink = null, Func<Window?, string>? installDriver = null)
    {
        this.model = model;
        this.openLink = openLink ?? DriverDownloads.OpenOfficialLink;
        this.installDriver = installDriver ?? DriverDownloads.SelectInstaller;
        var explanation = new StackPanel();
        explanation.Children.Add(GuideUi.Text("1 路、2 路是什么意思？", "CardTitle", "Text.Primary"));
        var one = GuideUi.Text("1 路 = 1 份声音。比如 QQ 和直播软件，都听到同一份“音乐 + 讲话”。");
        one.Margin = new(0, 8, 0, 7); explanation.Children.Add(one);
        explanation.Children.Add(GuideUi.Text("2 路 = 2 份可以不同的声音。比如 QQ 只听到讲话，直播软件听到“音乐 + 讲话”。"));
        var both = GuideUi.Text("下面两款单独安装都只有 1 路；两款都装，才有 2 路。一路也可以给多个软件听。", "Caption", "Accent.Output");
        both.Margin = new(0, 9, 0, 0); explanation.Children.Add(both);
        Children.Add(GuideUi.Card(explanation, "Surface.Panel"));
        var cards = new Grid { Margin = new(0, 12, 0, 12) };
        cards.ColumnDefinitions.Add(new()); cards.ColumnDefinitions.Add(new());
        int column = 0;
        foreach (var driver in DriverDownloads.All)
        {
            var content = new StackPanel();
            var icon = GuideUi.Text("↓  " + driver.Purpose, "Caption", "Accent.Output");
            content.Children.Add(icon);
            var title = GuideUi.Text(driver.Name, "Heading", "Text.Primary");
            title.FontWeight = FontWeights.SemiBold; title.Margin = new(0, 8, 0, 7); content.Children.Add(title);
            var purpose = GuideUi.Text(driver.Id == "Cable" ? "把音乐、游戏声和讲话传给其他软件。通常先装这款，设置更省事。" :
                "也能把声音传给其他软件。已经装了左边，想再传一份不同声音时，再装这款。");
            purpose.MinHeight = 58; content.Children.Add(purpose);
            var status = GuideUi.Text("", "Caption", "Accent.Success");
            status.Margin = new(0, 12, 0, 12); statuses.Add(driver, status); content.Children.Add(status);
            var download = GuideUi.Button("↓  下载驱动", "Download" + driver.Id, true);
            download.ToolTip = driver.DownloadUri.AbsoluteUri;
            download.Margin = new(0, 0, 0, 7);
            download.Click += (_, _) => Open(driver.DownloadUri, "官方下载已打开。下载后，请先把压缩包里的所有文件解压出来。");
            content.Children.Add(download);
            var website = GuideUi.Button("↗  官网与使用说明", "Website" + driver.Id);
            website.ToolTip = driver.WebsiteUri.AbsoluteUri; website.Margin = new(0);
            website.Click += (_, _) => Open(driver.WebsiteUri, "已打开 VB-Audio 官方网站。");
            content.Children.Add(website);
            var card = GuideUi.Card(content);
            card.Margin = column == 0 ? new(0, 0, 6, 0) : new(6, 0, 0, 0);
            Grid.SetColumn(card, column++); cards.Children.Add(card);
        }
        Children.Add(cards);
        Children.Add(GuideUi.Text("下载安装 · 三个小步骤", "CardTitle", "Text.Primary"));
        var steps = GuideUi.Text("1  下载压缩包，把里面的所有文件解压出来\n2  打开安装程序，Windows 询问时选择“是”\n3  按安装提示重启电脑，再点“重新检测设备”");
        steps.Margin = new(0, 8, 0, 10); steps.LineHeight = 23; Children.Add(steps);
        var bar = new WrapPanel();
        var install = GuideUi.Button("安装已下载的驱动…", "InstallDownloadedDriver");
        install.ToolTip = "先解压所有文件，再选择安装程序。Windows 弹出确认窗口时，选择“是”。";
        install.Margin = new(0, 0, 8, 8);
        install.Click += (_, _) =>
        {
            try { SetFeedback(this.installDriver(Window.GetWindow(this))); }
            catch (Exception error) { SetFeedback("安装程序未打开：" + UserError.Describe(error), true); }
        };
        bar.Children.Add(install);
        var refresh = GuideUi.Button("↻  重新检测设备", "RefreshDriverDevices");
        refresh.Margin = new(0, 0, 0, 8);
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try { await model.RefreshCatalogAsync(); UpdateStatus(); SetFeedback("已重新检查。请在接收声音的软件里选 CABLE Output 或 Hi-Fi Cable Output 作为麦克风。"); }
            catch (Exception error) { SetFeedback("检测未完成：" + UserError.Describe(error), true); }
            finally { refresh.IsEnabled = true; }
        };
        bar.Children.Add(refresh); Children.Add(bar);
        Children.Add(capacity);
        feedback.Margin = new(0, 8, 0, 0); Children.Add(feedback);
        var note = GuideUi.Text("下载由 VB-Audio 官方提供。通常先装 VB-CABLE 就够用，具体使用与捐赠说明见官网。", "Caption", "Text.Muted");
        note.Margin = new(0, 12, 0, 0); Children.Add(note);
        UpdateStatus();
        Loaded += (_, _) => { if (!subscribed) { model.PropertyChanged += ModelChanged; subscribed = true; } UpdateStatus(); };
        Unloaded += (_, _) => { if (subscribed) { model.PropertyChanged -= ModelChanged; subscribed = false; } };
    }
    private void Open(Uri uri, string message)
    {
        try { openLink(uri); SetFeedback(message); }
        catch (Exception error) { SetFeedback("链接未打开：" + UserError.Describe(error) + "；可从按钮提示中查看官方地址。", true); }
    }
    private void SetFeedback(string message, bool failed = false)
    {
        feedback.Text = message;
        Theme.Use(feedback, TextBlock.ForegroundProperty, failed ? "Accent.Error" : "Accent.Success");
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ChannelCapacity)) UpdateStatus();
    }
    private void UpdateStatus()
    {
        var connected = ChannelPool.Connected(model.Graph, model.Catalog.Endpoints);
        foreach (var (driver, text) in statuses)
        {
            bool detected = connected.Any(channel =>
                model.Catalog.Endpoints.Any(e => e.Id == channel.RenderEndpointId && e.Name.Contains(driver.RenderName, StringComparison.OrdinalIgnoreCase)) &&
                model.Catalog.Endpoints.Any(e => e.Id == channel.CaptureEndpointId && e.Name.Contains(driver.CaptureName, StringComparison.OrdinalIgnoreCase)));
            text.Text = detected ? "✓  设备已找到 · 不用重复安装" : "还没找到设备 · 需要时再安装";
            Theme.Use(text, TextBlock.ForegroundProperty, detected ? "Accent.Success" : "Text.Muted");
        }
        capacity.Text = model.ChannelCapacity;
    }
}
