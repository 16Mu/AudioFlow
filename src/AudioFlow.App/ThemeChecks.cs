using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioFlow.Core;

namespace AudioFlow.App;

internal static class ThemeChecks
{
    public static void Run(string directory, List<string> checks)
    {
        var backgrounds = new[] { "Window", "Title", "Panel", "Canvas", "Card", "Card.Hover", "Card.Selected", "Control", "Control.Hover", "Disabled", "Popup", "Popup.Hover", "Popup.Selected", "Button", "Button.Hover", "Button.Pressed", "Tag", "Overlay", "View.Application", "View.Global" };
        var comparisons = new List<object>();
        double minimum = double.MaxValue;
        void Compare(string foreground, string background)
        {
            double ratio = Contrast(((SolidColorBrush)Theme.Brush(foreground)).Color, ((SolidColorBrush)Theme.Brush(background)).Color);
            if (ratio < 4.5) throw new InvalidOperationException($"Insufficient contrast: {foreground} on {background}, {ratio:F2}:1.");
            minimum = Math.Min(minimum, ratio); comparisons.Add(new { Foreground = foreground, Background = background, Ratio = ratio });
        }
        foreach (var text in new[] { "Text.Primary", "Text.Secondary", "Text.Muted", "Text.Disabled" })
            foreach (var background in backgrounds) Compare(text, "Surface." + background);
        foreach (var button in new[] { "Accent.Primary", "Accent.Primary.Hover", "Accent.Primary.Pressed" }) Compare("Text.OnAccent", button);
        Compare("Text.OnLogo", "Surface.Logo");
        checks.Add("normal, selected, hover and disabled text contrast meets 4.5 to 1");

        string root = AppContext.BaseDirectory;
        for (int level = 0; level < 9 && !Directory.Exists(Path.Combine(root, "src", "AudioFlow.App")); level++)
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Source workspace not found.");
        var appDirectory = Path.Combine(root, "src", "AudioFlow.App");
        var sourceFiles = Directory.GetFiles(appDirectory, "*", SearchOption.AllDirectories).Where(file =>
            Path.GetExtension(file) is ".cs" or ".xaml" && !Path.GetRelativePath(appDirectory, file).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"));
        foreach (var file in sourceFiles)
        {
            if (Path.GetFileName(file) == "Tokens.xaml") continue;
            var source = File.ReadAllText(file);
            if (Regex.IsMatch(source, @"#[A-Fa-f0-9]{6,8}\b|\bBrushes\.[A-Za-z]+|\bColor\.From(?:Rgb|Argb)"))
                throw new InvalidOperationException("UI color must use a theme token: " + Path.GetRelativePath(appDirectory, file));
            foreach (Match reference in Regex.Matches(source, @"\{DynamicResource\s+([^}\s]+)\}"))
                if (Application.Current.TryFindResource(reference.Groups[1].Value) == null)
                    throw new InvalidOperationException("Undefined theme token: " + reference.Groups[1].Value);
        }
        checks.Add("application views contain no hardcoded colors and every theme token resolves");

        var menu = new ContextMenu();
        foreach (var header in new[] { "添加软件卡片", "添加麦克风卡片", "添加播放设备卡片" }) menu.Items.Add(new MenuItem { Header = header });
        menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "复制声音输入设备名", IsEnabled = false });
        menu.Items.Add(new MenuItem { Header = "删除此卡片" });
        var menuHost = new StackPanel { Margin = new(18) };
        menuHost.Children.Add(new TextBlock { Text = "右键菜单", FontSize = 18, Margin = new(0, 0, 0, 12) });
        menuHost.Children.Add(Snapshot(menu, 300, 400));
        var controls = new StackPanel { Margin = new(18) };
        controls.Children.Add(new TextBlock { Text = "统一控件状态", FontSize = 18, Margin = new(0, 0, 0, 14) });
        controls.Children.Add(new TextBox { Text = "搜索应用名称", Margin = new(0, 0, 0, 12) });
        controls.Children.Add(new ComboBox { ItemsSource = new[] { "耳机 / 扬声器", "虚拟麦克风 1" }, SelectedIndex = 0, Margin = new(0, 0, 0, 12) });
        var editable = new ComboBox { ItemsSource = new[] { "接收软件" }, IsEditable = true, Text = "可编辑的选择框", Margin = new(0, 0, 0, 12) }; controls.Children.Add(editable);
        controls.Children.Add(new Button { Content = "运行中的操作", Style = (Style)Application.Current.FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left });
        controls.Children.Add(new Button { Content = "暂不可用的操作", IsEnabled = false, Margin = new(0, 12, 0, 12), HorizontalAlignment = HorizontalAlignment.Left });
        controls.Children.Add(new CheckBox { Content = "可用选项", IsChecked = true });
        controls.Children.Add(new CheckBox { Content = "禁用选项", IsEnabled = false });
        controls.Children.Add(new Slider { Minimum = 0, Maximum = 1, Value = .6, Margin = new(0, 14, 0, 16) });
        var tooltip = new ToolTip { Content = "提示框也使用深色背景和清晰的文字。" }; controls.Children.Add(Snapshot(tooltip, 470, 100));
        var grid = new Grid { Margin = new(12) }; grid.ColumnDefinitions.Add(new() { Width = new(330) }); grid.ColumnDefinitions.Add(new());
        grid.Children.Add(menuHost); Grid.SetColumn(controls, 1); grid.Children.Add(controls);
        var window = new Window { Content = grid, Width = 880, Height = 560 };
        grid.Measure(new(880, 560)); grid.Arrange(new(0, 0, 880, 560)); grid.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); grid.UpdateLayout();
        menu.ApplyTemplate(); tooltip.ApplyTemplate(); editable.ApplyTemplate();
        if (menu.Background is not SolidColorBrush menuBackground || menuBackground.Color != ((SolidColorBrush)Theme.Brush("Surface.Popup")).Color)
            throw new InvalidOperationException("Context menu did not inherit the application theme.");
        var disabled = menu.Items.OfType<MenuItem>().First(m => !m.IsEnabled);
        if (Contrast(((SolidColorBrush)disabled.Foreground).Color, menuBackground.Color) < 4.5)
            throw new InvalidOperationException("Disabled context menu text is unreadable.");
        editable.Text = "新软件名称";
        if (editable.Template.FindName("PART_EditableTextBox", editable) is not TextBox editBox || editBox.Text != editable.Text)
            throw new InvalidOperationException("Editable combo input stopped working after theming.");
        foreach (var button in controls.Children.OfType<Button>())
            if (button.Template.FindName("ButtonSurface", button) is not Border surface || surface.CornerRadius != Theme.GetCornerRadius(button))
                throw new InvalidOperationException("Button corner token was not applied to its template.");
        checks.Add("context menus, tooltips and editable selectors inherit shared theme styles");
        var bitmap = new RenderTargetBitmap(880, 560, 96, 96, PixelFormats.Pbgra32); bitmap.Render(grid);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, "theme-controls.png"))) encoder.Save(stream);
        window.Close();
        File.WriteAllText(Path.Combine(directory, "theme-report.json"), JsonSerializer.Serialize(new { Passed = true, Pairs = comparisons.Count, MinimumTextContrast = minimum, Comparisons = comparisons }, SceneStore.JsonOptions));
    }
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    private static Image Snapshot(FrameworkElement control, int width, int maximumHeight)
    {
        control.ApplyTemplate(); control.Measure(new(width, maximumHeight));
        int height = Math.Max(1, (int)Math.Ceiling(control.DesiredSize.Height));
        control.Arrange(new(0, 0, width, height)); control.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
        return new Image { Source = bitmap, Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left };
    }
}
