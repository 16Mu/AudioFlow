using AudioFlow.Core;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioFlow.App;

internal static class FeatureUiChecks
{
    public static async Task RunAsync(MainWindow window, MainViewModel model, List<string> checks, string directory)
    {
        var original = SceneStore.Clone(model.Graph); bool global = model.GlobalView; string? focus = model.Workspace.FocusedApplication;
        string? path = model.Workspace.ActiveConfigurationPath;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Children(child)) yield return nested; }
        }
        void Layout() { window.UpdateLayout(); var root = (FrameworkElement)window.Content; root.Measure(new(1420, 920)); root.Arrange(new(0, 0, 1420, 920)); root.UpdateLayout(); }
        try
        {
            string key = Path.Combine(directory, "RecordingConfigurationPreview.exe");
            model.ImportConfiguration(new() { Channels = model.Catalog.SuggestedChannels.ToList(), Nodes = [new() {
                Id = "feature-source", Kind = NodeKind.Application, Key = key, Name = "录音设备验证", RecordingConfigured = true, InputDeviceKey = AudioOutput.SystemDefault }] });
            await model.SwitchViewAsync(false); model.FocusApplication(key); model.Arrange(); Layout();
            var input = Children(window).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "默认录音设备选择" &&
                c.DataContext is NodeViewModel node && node.Id == "feature-source");
            input.SelectedItem = model.FocusedRoot!.RecordingChoices.First(c => c.Key == AudioOutput.None);
            Check(!model.FocusedRoot.RecordingEnabled && model.FocusedRoot.RecordingChoice?.Key == AudioOutput.None && model.Graph.SilentInputChannelId != null,
                "the rendered recording device picker synchronizes No Input, its checkbox, and one silent-channel reservation");
            var rootNode = model.FocusedRoot;
            var output = model.Nodes.Single(n => n.HasDefaultOutput);
            output.Choice = output.Choices.First(c => c.Key == AudioOutput.None);
            Check(!output.DefaultOutputEnabled && output.Model.Key == AudioOutput.None,
                "the rendered No Output dropdown closes its device checkbox");
            UiChecks.SavePreview(window, Path.Combine(directory, "input-output-disabled.png"));
            var fileA = Path.Combine(directory, "声音配置-A.json"); var fileB = Path.Combine(directory, "声音配置-B.json");
            await model.SaveConfigurationAsAsync(fileA);
            Check(model.ConfigurationName == "声音配置-A.json" && File.Exists(fileA), "Save As selects and displays the current named configuration file");
            output.DefaultOutputEnabled = true; rootNode.RecordingEnabled = true;
            await Task.Delay(180);
            var savedA = model.Store.Load(fileA);
            Check(savedA.Nodes.Single(n => n.PrimaryOutput).Key == AudioOutput.SystemDefault && savedA.Nodes.Single(n => n.Kind == NodeKind.Application).InputDeviceKey == AudioOutput.SystemDefault &&
                savedA.SilentInputChannelId == null && model.SaveState.StartsWith("已保存"),
                "automatic named-file save restores both remembered devices and releases the unused silent channel");
            await model.SaveConfigurationAsAsync(fileB);
            model.Nodes.Single(n => n.HasDefaultOutput).DefaultOutputEnabled = false;
            model.FocusedRoot!.RecordingEnabled = false;
            await Task.Delay(180);
            Check(model.Store.Load(fileA).Nodes.Single(n => n.PrimaryOutput).Key != AudioOutput.None && model.Store.Load(fileB).Nodes.Single(n => n.PrimaryOutput).Key == AudioOutput.None,
                "editing configuration B does not overwrite configuration A");
            await model.OpenConfigurationAsync(fileA);
            Check(model.Nodes.Single(n => n.HasDefaultOutput).DefaultOutputEnabled && model.FocusedRoot?.RecordingEnabled == true && model.ConfigurationName == "声音配置-A.json",
                "opening a named file restores its recording and playback device choices in both views");
            var beforeMissing = SceneStore.Clone(model.Graph);
            await model.OpenConfigurationAsync(Path.Combine(directory, "missing-file.json"));
            Check(model.Graph.Nodes.Count == beforeMissing.Nodes.Count && model.ConfigurationName == "声音配置-A.json",
                "opening a missing configuration preserves the current graph and filename");
            File.WriteAllText(fileA, "invalid-json"); model.SaveConfigurationCommand.Execute(null);
            Check(model.SaveState.StartsWith("保存失败") && File.ReadAllText(fileA) == "invalid-json" &&
                model.Store.LoadWorkspace().Configuration.Nodes.Count == model.Graph.Nodes.Count,
                "a corrupted active file is preserved while the latest configuration survives in its local snapshot");
            model.Store.Save(beforeMissing, fileA); model.SaveNow();
            var line = new FlowConnection { IsFlowing = true, HasSignal = false, Source = new(30, 45), Target = new(410, 160), Stroke = Theme.Brush("Accent.Output") };
            var preview = new Window { Width = 460, Height = 230, Content = line, Background = Theme.Brush("Surface.Canvas"), ShowActivated = false, Opacity = 0, Left = -32000, Top = -32000 };
            preview.Show(); preview.UpdateLayout(); await Task.Delay(80);
            double first = line.DirectionalArrowsOffset; await Task.Delay(100);
            Check(line.DirectionalArrowsCount == 2 && line.DirectionalArrowsOffset != first && line.Opacity == .5,
                "enabled silent routes display two moving directional arrows at the dim idle state");
            line.HasSignal = true; await Task.Delay(60);
            Check(line.Opacity == 1 && line.StrokeThickness > 3, "audio activity changes moving arrows to the bright signal state");
            for (int frame = 0; frame < 14; frame++)
            {
                UiChecks.SavePreview(preview, Path.Combine(directory, $"flow-{frame:00}.png"), 460, 230);
                await Task.Delay(100);
            }
            line.IsFlowing = false; await Task.Delay(60);
            Check(line.DirectionalArrowsCount == 0 && !line.HasAnimatedProperties,
                "stopping a route detaches its flow animation");
            preview.Close();
            model.Notify("设备选择与配置已保存，录音和播放开关与下拉框同步。");
            model.Arrange(); UiChecks.SavePreview(window, Path.Combine(directory, "recording-device-card.png"));
        }
        finally
        {
            model.Workspace.ActiveConfigurationPath = path; model.ImportConfiguration(original);
            await model.SwitchViewAsync(global); if (!global && focus != null) model.FocusApplication(focus); model.SaveNow();
        }
    }
}
