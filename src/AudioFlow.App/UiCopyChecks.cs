using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioFlow.Core;

namespace AudioFlow.App;

internal static class UiCopyChecks
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var nested in Children(child)) yield return nested;
            }
        }
        var main = new MainWindow(Path.Combine(directory, "profile-" + Guid.NewGuid().ToString("N")), true)
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000
        };
        main.Show();
        await using var model = main.ViewModel;
        try
        {
            await model.RefreshCatalogAsync();
            var second = model.Catalog.Endpoints.FirstOrDefault(e => !e.Capture && !e.Default && !e.Virtual)
                ?? model.Catalog.Endpoints.First(e => !e.Capture);
            const string musicKey = @"C:\AudioFlowPreview\Music.exe";
            var example = new RouteGraph
            {
                Channels = model.Catalog.SuggestedChannels.ToList(),
                Nodes =
                [
                    new() { Id = "music", Kind = NodeKind.Application, Name = "音乐软件", Key = musicKey, Volume = .8f },
                    new() { Id = "main-output", Kind = NodeKind.Playback, Name = "主要播放", Key = AudioOutput.SystemDefault, PrimaryOutput = true, OwnerAppKey = musicKey },
                    new() { Id = "extra-output", Kind = NodeKind.Playback, Name = second.Name, Key = second.Id }
                ],
                Connections =
                [
                    new() { SourceId = "music", TargetId = "main-output", Mode = RouteMode.Playback, Volume = 1 },
                    new() { SourceId = "music", TargetId = "extra-output", Mode = RouteMode.Playback, Volume = .55f }
                ]
            };
            model.ImportConfiguration(example); model.FocusApplication(musicKey); model.Search = "音乐";
            UiChecks.SavePreview(main, Path.Combine(directory, "main-before-arrange.png"));
            model.Arrange();
            UiChecks.SavePreview(main, Path.Combine(directory, "main-window.png"));
            Check(!model.Running, "the documentation example renders real controls without starting audio routing");
            Check(model.Graph.Nodes.Count == 3 && model.Graph.Connections.Count == 2, "the preview shows one software connected to two playback destinations");
            Check(main.FindName("RunAction") is Button run && run.Content is string label && label == model.RunActionLabel,
                "the actual start button displays the current readable action label");
            main.Width = 1120; main.Height = 740; main.UpdateLayout();
            UiChecks.SavePreview(main, Path.Combine(directory, "main-compact.png"), 1120, 740);
            var content = (FrameworkElement)main.Content;
            foreach (var button in Children(main).OfType<Button>().Where(b => b.Name is "RunAction" or "RestoreOriginal"))
            {
                var bounds = button.TransformToAncestor(content).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
                Check(bounds.Left >= 0 && bounds.Right <= content.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= content.ActualHeight,
                    "the compact window keeps " + button.Name + " fully visible");
            }
            Check(!UserError.Describe(new JsonException("Unmapped member Configuration")).Contains("Unmapped"),
                "an unreadable settings file shows an actionable explanation rather than a serializer error");
            Check(!UserError.Describe(new System.Runtime.InteropServices.COMException("HRESULT 0x80004005")).Contains("HRESULT"),
                "a Windows device failure does not expose a hexadecimal implementation message");
            Check(UserError.Describe(new FileNotFoundException()).Contains("重新选择"), "a missing file tells the user how to proceed");
            var validation = new GraphValidationException("不能把一张卡片连回自己。");
            Check(UserError.Describe(validation) == validation.Message, "a clear connection validation explanation is preserved");
            File.WriteAllText(Path.Combine(directory, "copy-report.json"), JsonSerializer.Serialize(new { Passed = true, Count = checks.Count, Checks = checks }, SceneStore.JsonOptions));
        }
        finally { main.Close(); }
    }
}
