using AudioFlow.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioFlow.App;

internal static class SettingsChecks
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await using var model = new MainViewModel(directory);
        await model.RefreshCatalogAsync();
        var window = new SettingsWindow(model) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Children(child)) yield return nested; }
        }
        static double Contrast(Color a, Color b)
        {
            static double L(Color c)
            {
                static double V(byte channel) { double s = channel / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
                return .2126 * V(c.R) + .7152 * V(c.G) + .0722 * V(c.B);
            }
            double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }
        try
        {
            window.Show(); window.UpdateLayout();
            var scroll = (ScrollViewer)window.Content;
            Check(((SolidColorBrush)window.Background).Color == ((SolidColorBrush)Theme.Brush("Surface.Window")).Color &&
                ((SolidColorBrush)scroll.Background).Color == ((SolidColorBrush)Theme.Brush("Surface.Window")).Color,
                "actual SettingsWindow and its scroll surface use the dark window theme");
            var texts = Children(window).OfType<TextBlock>().Where(t => !string.IsNullOrWhiteSpace(t.Text) && t.IsVisible).ToArray();
            foreach (var text in texts)
            {
                Brush background = window.Background;
                for (var ancestor = VisualTreeHelper.GetParent(text); ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
                {
                    Brush? candidate = ancestor is Border border ? border.Background : ancestor is Panel panel ? panel.Background :
                        ancestor is ScrollViewer viewer ? viewer.Background : null;
                    if (candidate is SolidColorBrush solid && solid.Color.A == 255) { background = candidate; break; }
                }
                Check(text.Foreground is SolidColorBrush foreground && Contrast(foreground.Color, ((SolidColorBrush)background).Color) >= 4.5,
                    "settings text remains readable: " + text.Text);
            }
            int playback = model.Catalog.Endpoints.Count(e => !e.Capture);
            int microphones = ChannelPool.Connected(model.Graph, model.Catalog.Endpoints).Count(c => !c.RequiresValidation);
            Check(texts.Any(t => t.Text.Contains($"找到 {playback} 个能播放声音的设备") && t.Text.Contains($"传声音的麦克风 {microphones} 个")),
                "settings separates actual playback endpoint count from independent virtual microphone count");
            Check(model.Catalog.Endpoints.Where(e => !e.Capture).All(e => texts.Any(t => t.Text.StartsWith(e.Name))),
                "settings lists every detected physical and virtual playback endpoint");
            UiChecks.SavePreview(window, Path.Combine(directory, "settings-dark.png"), 780, 740);
            scroll.ScrollToEnd(); window.UpdateLayout();
            UiChecks.SavePreview(window, Path.Combine(directory, "settings-dark-bottom.png"), 780, 740);
            File.WriteAllText(Path.Combine(directory, "settings-report.json"), JsonSerializer.Serialize(new
                { Passed = true, PlaybackDevices = playback, IndependentVirtualMicrophones = microphones, Checks = checks }, SceneStore.JsonOptions));
        }
        finally { window.Close(); }
    }
}
