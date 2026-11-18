using AudioFlow.Core;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioFlow.App;

internal static class OptimizationChecks
{
    public static async Task RunAsync(MainWindow window, MainViewModel model, string directory, List<string> checks)
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        var original = SceneStore.Clone(model.Graph);
        var focus = model.Workspace.FocusedApplication;
        bool global = model.GlobalView;
        try
        {
            await model.SwitchViewAsync(false);
            model.FocusApplication(Path.Combine(directory, "MemoryPreview.exe"));
            var input = model.CanvasNodes.Single(n => n.HasDefaultOutput).InputLevels.Single();
            var output = model.FocusedRoot!.OutputLevels.Single();
            var edge = model.CanvasConnections.Single();
            for (int i = 0; i < 25; i++) await model.RefreshCatalogAsync();
            Check(ReferenceEquals(edge, model.CanvasConnections.Single()) &&
                ReferenceEquals(input, model.CanvasNodes.Single(n => n.HasDefaultOutput).InputLevels.Single()) &&
                ReferenceEquals(output, model.FocusedRoot!.OutputLevels.Single()),
                "25 inventory refreshes reuse preview connections and volume rows without accumulating subscriptions");
            int retiredNotifications = 0;
            output.PropertyChanged += (_, _) => retiredNotifications++;
            model.FocusedRoot!.SetRouteLevels([]);
            edge.RefreshVolume();
            Check(retiredNotifications == 0, "removed volume rows unsubscribe and receive no further updates");
            model.FocusedRoot.SetRouteLevels([edge]);
            model.Arrange();
            var cards = model.CanvasNodes.Select(n => model.MeasureLayoutCard!(n)).ToArray();
            File.WriteAllText(Path.Combine(directory, "layout-measurements.json"), System.Text.Json.JsonSerializer.Serialize(new { Cards = cards,
                Positions = model.CanvasNodes.Select(n => new { n.Id, n.Model.X, n.Model.Y }).ToArray() }, SceneStore.JsonOptions));
            Check(cards.All(c => c.Width >= 326 && c.Height > 0) &&
                Math.Abs(model.FocusedRoot.Location.Y + cards.Single(c => c.Id == model.FocusedRoot.Id).OutputY -
                    model.CanvasNodes.Single(n => n.HasDefaultOutput).Location.Y - cards.Single(c => c.PrimaryOutput).InputY) < 1,
                "rendered canvas arrangement measures actual cards and aligns native output connectors");
            var target = model.CanvasNodes.Single(n => n.HasDefaultOutput);
            target.DefaultOutputEnabled = false;
            model.Arrange();
            UiChecks.SavePreview(window, Path.Combine(directory, "disconnected-output.png"));
            Check(model.CanvasConnections.Single().IsDisconnected, "No Output marks the saved default route disconnected");
            target = model.CanvasNodes.Single(n => n.HasDefaultOutput);
            target.DefaultOutputEnabled = true;
            Check(!model.CanvasConnections.Single().IsDisconnected, "reenabling the output clears the disconnected state");
            Check(model.VersionLabel == "v0.01", "the application displays the requested v0.01 version");
            CheckLineRendering(Check, directory);
        }
        finally
        {
            model.ImportConfiguration(original);
            model.Workspace.FocusedApplication = focus;
            await model.SwitchViewAsync(global);
            if (!global && focus != null) model.FocusApplication(focus);
        }
    }

    private static void CheckLineRendering(Action<bool, string> check, string directory)
    {
        var line = new FlowConnection { Width = 500, Height = 260, Source = new(30, 65), Target = new(470, 190),
            Stroke = Theme.Brush("Accent.Output"), StrokeThickness = 2.8 };
        RenderTargetBitmap Render()
        {
            line.Measure(new(500, 260)); line.Arrange(new(0, 0, 500, 260)); line.UpdateLayout();
            var bitmap = new RenderTargetBitmap(500, 260, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(line); return bitmap;
        }
        (int Count, double X) Red(RenderTargetBitmap bitmap)
        {
            var pixels = new byte[500 * 260 * 4]; bitmap.CopyPixels(pixels, 500 * 4, 0);
            int count = 0; long x = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 2] > 180 && pixels[i + 2] > pixels[i + 1] + 65 && pixels[i + 2] > pixels[i] + 55)
                { count++; x += i / 4 % 500; }
            return (count, count == 0 ? 0 : (double)x / count);
        }
        Render();
        line.IsDisconnected = true;
        var disconnected = Render(); var marker = Red(disconnected);
        check(line.StrokeDashArray is { Count: > 0 } && marker.Count > 20 && Math.Abs(marker.X - 250) < 8,
            "a line closed after first render becomes dashed and paints a red X at the curve midpoint");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(disconnected));
        using (var file = File.Create(Path.Combine(directory, "disconnected-line.png"))) encoder.Save(file);
        line.Source = new(80, 35); line.Target = new(420, 220);
        marker = Red(Render());
        check(marker.Count > 20 && Math.Abs(marker.X - 250) < 8, "moving a disconnected line keeps the red X centered on its new curve");
        line.IsDisconnected = false;
        check(line.StrokeDashArray == null && Red(Render()).Count == 0, "reenabling a line restores a solid stroke and removes its red X");
    }
}
