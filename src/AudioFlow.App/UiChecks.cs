using AudioFlow.Core;
using AudioFlow.Audio;
using Nodify;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AudioFlow.App;

internal static class UiChecks
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var window = new MainWindow(directory, true) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show();
        await using var model = window.ViewModel;
        var checks = new List<string>();
        model.PickSharedChannel = _ => null;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        Check(!model.Notice.StartsWith("工作区读取失败", StringComparison.Ordinal), "workspace loads without a JSON mapping error");
        await model.InitializeAsync();
        ThemeChecks.Run(directory, checks);
        var apps = model.Catalog.Applications.Where(a => !a.Name.Contains("Verification", StringComparison.OrdinalIgnoreCase)).Take(3).ToArray();
        Check(apps.Length >= 3, "running application selectors populated");
        Check(model.Catalog.SuggestedChannels.Count >= 2, "two virtual input channels available");
        var manualKeys = model.Workspace.ManualApplications.Select(a => AppIdentity.Normalize(a.Path)).ToHashSet();
        var configuredKeys = model.Graph.Nodes.Where(n => n.Kind == NodeKind.Application).Select(n => AppIdentity.Normalize(n.Key)).ToHashSet();
        Check(model.Applications.Where(a => !manualKeys.Contains(AppIdentity.Normalize(a.Key)) && !configuredKeys.Contains(AppIdentity.Normalize(a.Key))).All(a =>
            model.Catalog.Applications.Any(c => c.Key == AppIdentity.Normalize(a.Key) && (c.HasAudioSession ||
                model.Workspace.KnownAudioApplications.Any(k => AppIdentity.Normalize(k.Path) == c.Key)))),
            "automatic sidebar uses current or previous audio session evidence");
        var picker = new ApplicationPickerWindow(model.GetApplicationChoices());
        SavePreview(picker, Path.Combine(directory, "application-picker.png"), 980, 680);
        Check(Descendants((DependencyObject)picker.Content).OfType<System.Windows.Controls.Primitives.UniformGrid>().Any(g => g.Columns >= 2),
            "application picker uses a multi-column tile grid");
        var searchBox = (TextBox)picker.FindName("ApplicationSearch");
        searchBox.Text = apps[0].Name;
        Check(picker.ViewModel.Choices.Any(a => a.Key == apps[0].Path), "application name search filters picker tiles");
        searchBox.Text = "";
        var applicationGrid = (ListBox)picker.FindName("ApplicationGrid");
        applicationGrid.SelectedItem = picker.ViewModel.Choices.First(a => a.Key == apps[0].Path);
        var confirmApplication = (Button)picker.FindName("ConfirmApplication");
        confirmApplication.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, confirmApplication));
        Check(picker.SelectedApplication?.Key == apps[0].Path, "application grid selection confirms the chosen app");
        int navigationNodes = model.Graph.Nodes.Count;
        await model.AddManualApplicationAsync(picker.SelectedApplication!);
        Check(model.Graph.Nodes.Count == navigationNodes && model.Applications.Any(a => a.Key == apps[0].Path),
            "manual add preserves navigation without creating a route");
        Check(model.Applications.Any(a => a.Detail.Contains("使用系统默认输出")), "unconfigured applications explicitly show system output");
        picker.Close();
        model.ImportConfiguration(new RouteGraph { Channels = model.Catalog.SuggestedChannels.ToList() });
        await model.SwitchViewAsync(true);
        void Layout()
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1420, 920)); content.Arrange(new Rect(0, 0, 1420, 920)); content.UpdateLayout();
        }
        ComboBox Picker(NodeViewModel node)
        {
            Layout();
            var editor = (NodifyEditor)window.FindName("Editor");
            var container = (DependencyObject?)editor.ItemContainerGenerator.ContainerFromItem(node)
                ?? throw new InvalidOperationException("node template was not created");
            return Descendants(container).OfType<ComboBox>().First();
        }
        void Select(NodeViewModel node, Func<CardChoice, bool> predicate)
        {
            Picker(node).SelectedItem = node.Choices.First(predicate);
            Check(node.IsConfigured, "inline selector configured " + node.Model.Kind);
        }
        var sourceA = model.AddBlank(NodeKind.Application);
        Select(sourceA, c => c.Key == apps[0].Path);
        Check(model.Graph.Connections.Any(c => c.SourceId == sourceA.Id && c.Automatic &&
            model.Graph.Nodes.Any(n => n.Id == c.TargetId && n.Key == AudioOutput.SystemDefault)),
            "selecting an application automatically connects its Windows default output");
        var sourceB = model.AddBlank(NodeKind.Application);
        Select(sourceB, c => c.Key == apps[1].Path);
        Check(model.Graph.Nodes.Count(n => n.PrimaryOutput) == 2,
            "applications have independent default output cards");
        var receiver = model.AddBlank(NodeKind.Application);
        Select(receiver, c => c.Key == apps[2].Path);
        var microphone = model.AddBlank(NodeKind.Microphone);
        Select(microphone, c => c.Kind == NodeKind.Microphone);
        var virtualMic = model.AddBlank(NodeKind.Microphone);
        Select(virtualMic, c => c.Kind == NodeKind.VirtualMicrophone && c.ChannelId == model.Graph.Channels[1].Id);
        var playback = model.AddBlank(NodeKind.Playback);
        Select(playback, c => c.Kind == NodeKind.Playback && c.Name.Contains("Senary", StringComparison.OrdinalIgnoreCase));
        NodeViewModel Node(string id) => model.Nodes.First(n => n.Id == id);
        void Connect(string source, string target)
        {
            model.Connect(Node(source).Outputs[0], Node(target).Inputs[0]);
            Check(model.Graph.Connections.Any(c => c.SourceId == source && c.TargetId == target && !c.Automatic),
                "directional connection " + Node(source).Model.Kind + " -> " + Node(target).Model.Kind);
        }
        Connect(sourceB.Id, sourceA.Id);
        Connect(microphone.Id, sourceA.Id);
        Connect(sourceA.Id, playback.Id);
        Check(model.Graph.Connections.Any(c => c.SourceId == sourceA.Id && c.Automatic),
            "adding a playback copy preserves the protected default output link");
        Connect(sourceA.Id, virtualMic.Id);
        Connect(virtualMic.Id, receiver.Id);
        await RouteChecks.RunAsync(window, model, sourceA.Id, playback.Id, virtualMic.Id, checks);
        await DefaultOutputChecks.RunAsync(window, model, sourceA.Id, sourceB.Id, checks, directory);
        await FeatureUiChecks.RunAsync(window, model, checks, directory);
        await OptimizationChecks.RunAsync(window, model, directory, checks);
        Check(Node(sourceA.Id).Receiving && Node(sourceA.Id).Outputs.Count == 1, "software card has separate input and output");
        int edgeCount = model.Graph.Connections.Count;
        model.Connect(Node(sourceA.Id).Outputs[0], Node(sourceB.Id).Inputs[0]);
        Check(model.Graph.Connections.Count == edgeCount, "feedback rejected without losing existing configuration");
        model.Arrange();
        var unrelated = model.AddBlank(NodeKind.Playback);
        Select(unrelated, c => c.Kind == NodeKind.Playback && c.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase));
        SavePreview(window, Path.Combine(directory, "global-canvas.png"));
        var sharedGraph = model.Graph;
        var originalRoot = Node(sourceA.Id);
        await model.SwitchViewAsync(false);
        Check(ReferenceEquals(sharedGraph, model.Graph), "view switching retains one shared routing graph");
        var manualOnly = new AvailableItem("手动测试应用", Path.Combine(directory, "ManualOnly.exe"), NodeKind.Application, "手动添加");
        int initialNodes = model.Graph.Nodes.Count;
        await model.AddManualApplicationAsync(manualOnly);
        Check(model.Graph.Nodes.Count == initialNodes && model.FocusedRoot?.IsDraft == true, "application navigation does not add to a canvas");
        model.FocusApplication(apps[0].Path);
        Check(ReferenceEquals(model.FocusedRoot, originalRoot), "focused view reuses the global application card");
        Check(model.CanvasNodes.All(n => n.Id != unrelated.Id) && model.Nodes.Any(n => n.Id == unrelated.Id),
            "single application view filters unrelated cards without deleting their configuration");
        Layout();
        var root = model.FocusedRoot!;
        var editorControl = (NodifyEditor)window.FindName("Editor");
        var rootContainer = (DependencyObject)editorControl.ItemContainerGenerator.ContainerFromItem(root)!;
        var border = Descendants(rootContainer).OfType<Border>().First(b => b.Name == "CardSurface");
        var menu = border.ContextMenu!;
        menu.PlacementTarget = border; menu.DataContext = root;
        var addMic = (MenuItem)menu.Items[1];
        addMic.DataContext = root;
        addMic.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, addMic));
        Check(model.Graph.Nodes.Count == initialNodes + 1, "card context-menu adds a related microphone to the shared graph");
        var focused = model.FocusedRoot!;
        var singleMic = model.Nodes.Last(n => n.Model.Kind == NodeKind.Microphone);
        Select(singleMic, c => c.Kind == NodeKind.Microphone);
        Connect(singleMic.Id, focused.Id);
        Check(model.Graph.Connections.Count == edgeCount + 1, "focused view adds to the existing shared connections");
        var gainConnection = model.Connections.First(c => c.Source.Owner.Id == sourceA.Id && c.Target.Owner.Id == playback.Id);
        string gainId = gainConnection.Model.Id;
        float previousGain = gainConnection.Volume;
        gainConnection.Volume = .61f;
        await model.SwitchViewAsync(true);
        Check(Math.Abs(model.Connections.First(c => c.Model.Id == gainId).Volume - .61f) < .001 &&
            model.Nodes.Any(n => n.Id == singleMic.Id), "focused view cards, connections and volumes appear in the global canvas");
        model.UndoCommand.Execute(null);
        Check(Math.Abs(model.Connections.First(c => c.Model.Id == gainId).Volume - previousGain) < .001,
            "global view can undo a focused view edit");
        await model.SwitchViewAsync(false);
        model.FocusApplication(apps[0].Path);
        model.RedoCommand.Execute(null);
        Check(Math.Abs(model.Connections.First(c => c.Model.Id == gainId).Volume - .61f) < .001,
            "redo history is shared across canvas views");
        model.StartPendingConnection(Node(playback.Id).Inputs[0]);
        await model.SwitchViewAsync(true);
        Check(!model.Reconnecting && model.Graph.Connections.Any(c => c.Id == gainId),
            "switching views cancels a reconnect and restores its original connection");
        await model.SwitchViewAsync(false);
        model.FocusApplication(apps[0].Path);
        SavePreview(window, Path.Combine(directory, "single-application.png"));
        await model.SwitchViewAsync(true);
        Check(model.Graph.Connections.Count == edgeCount + 1, "both views retain the same complete connection set");
        model.SaveNow();
        var saved = model.Store.LoadWorkspace();
        Check(saved.Configuration.Connections.Count == edgeCount + 1 &&
            saved.Configuration.Connections.Any(c => c.Id == gainId && Math.Abs(c.Volume - .61f) < .001),
            "shared configuration and cross-view volume edit persist together");
        Check(saved.ManualApplications.Any(a => AppIdentity.Normalize(a.Path) == AppIdentity.Normalize(apps[0].Path)),
            "manually added application persisted");
        Check(!Descendants(window).OfType<FrameworkElement>().Any(e => e.Name == "Inspector"), "right sidebar removed");
        await CheckSharedRouting(window, model, directory, checks);
        await StartupChecks.RunAsync(directory, checks);
        await RecoveryChecks.RunAsync(window, model, directory, checks);
        await model.DisposeAsync();
        window.Close();
        File.WriteAllText(Path.Combine(directory, "ui-report.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }, SceneStore.JsonOptions));
    }
    private static async Task CheckSharedRouting(MainWindow window, MainViewModel model, string directory, List<string> checks)
    {
        var originalConfiguration = SceneStore.Clone(model.Graph);
        var originalView = model.Workspace.View;
        var originalFocus = model.Workspace.FocusedApplication;
        bool originalPaused = model.Paused;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Until(Func<bool> predicate, string failure)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(40);
            if (!predicate()) throw new InvalidOperationException(failure);
        }
        string runtime = VerificationRuntime.CopyRuntime(Path.Combine(directory, "tone-runtime"));
        string firstPath = Path.Combine(runtime, "SharedToneA.exe"), secondPath = Path.Combine(runtime, "SharedToneB.exe");
        File.Copy(Path.Combine(runtime, "AudioFlow.Verification.exe"), firstPath, true);
        File.Copy(Path.Combine(runtime, "AudioFlow.Verification.exe"), secondPath, true);
        Process Start(string path, int tone)
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--tone"); info.ArgumentList.Add(tone.ToString()); info.ArgumentList.Add("120");
            return Process.Start(info)!;
        }
        using var first = Start(firstPath, 523);
        using var second = Start(secondPath, 787);
        try
        {
            await first.StandardOutput.ReadLineAsync(); await second.StandardOutput.ReadLineAsync();
            await model.RefreshCatalogAsync();
            bool Listed(string path) => model.Applications.Any(a => AppIdentity.Normalize(a.Key) == AppIdentity.Normalize(path));
            await Until(() => Listed(firstPath) && Listed(secondPath), "Active tone applications missing from sidebar.");
            Check(Listed(firstPath) && Listed(secondPath), "real active audio applications appear automatically in the sidebar");
            var channels = model.Catalog.SuggestedChannels.Take(2).ToArray();
            model.ImportConfiguration(new()
            {
                Nodes =
                [
                    new() { Id = "source-A", Kind = NodeKind.Application, Name = "测试音 A", Key = firstPath },
                    new() { Id = "source-B", Kind = NodeKind.Application, Name = "测试音 B", Key = secondPath },
                    new() { Id = "input-A", Kind = NodeKind.VirtualMicrophone, Name = channels[0].Name, ChannelId = channels[0].Id },
                    new() { Id = "input-B", Kind = NodeKind.VirtualMicrophone, Name = channels[1].Name, ChannelId = channels[1].Id }
                ],
                Channels = channels.ToList(),
                Connections =
                [
                    new() { SourceId = "source-A", TargetId = "input-A", Mode = RouteMode.MicrophoneFeed },
                    new() { SourceId = "source-B", TargetId = "input-B", Mode = RouteMode.MicrophoneFeed }
                ]
            });
            await model.SwitchViewAsync(true);
            foreach (var pair in new[] { (Key: firstPath, Channel: channels[0]), (Key: secondPath, Channel: channels[1]) })
            {
                var output = model.Nodes.First(n => n.Model.PrimaryOutput && AppIdentity.Normalize(n.Model.OwnerAppKey ?? "") == AppIdentity.Normalize(pair.Key));
                model.ChooseCard(output, new(pair.Channel.Name, pair.Channel.RenderEndpointId, NodeKind.Playback, "播放设备"));
            }
            model.Arrange(); await model.StartAsync();
            string? Output(Process process) => AudioPolicy.Unpack(AudioPolicy.GetOutput(process.Id, 1));
            float SessionVolume(Process process) => new AudioCatalog().GetApplicationVolume([process.Id])?.volume ?? -1;
            NodeViewModel Node(string id) => model.Nodes.First(n => n.Id == id);
            await Until(() => model.Running && Output(first) == channels[0].RenderEndpointId && Output(second) == channels[1].RenderEndpointId,
                "The shared graph did not activate both application outputs.");
            Check(model.LiveSummary.Contains("声音路由运行中") && !model.LiveSummary.Contains("全局画布") && !model.LiveSummary.Contains("单软件配置"),
                "routing status is independent of the selected canvas view");
            SavePreview(window, Path.Combine(directory, "running-controls.png"));
            var runButton = (Button)window.FindName("RunAction");
            Check(runButton.Command == model.RunActionCommand && window.FindName("PauseAction") == null && model.RunActionLabel == "停止声音路由",
                "running state exposes only Start Stop while view selection remains separate");
            await Task.Delay(200);
            double beforeSwitch = model.Engine.Snapshot().RunningSeconds;
            model.ApplicationViewCommand.Execute(null);
            await Until(() => !model.GlobalView && !model.Busy, "The running switch action did not select the application view.");
            model.FocusApplication(firstPath);
            Check(Output(first) == channels[0].RenderEndpointId && Output(second) == channels[1].RenderEndpointId &&
                model.Engine.Snapshot().RunningSeconds >= beforeSwitch && model.RunActionLabel == "停止声音路由",
                "the running switch action changes only the view and preserves both audio outputs");
            Check(model.CanvasNodes.Any(n => n.Id == "source-A") && model.CanvasNodes.All(n => n.Id != "source-B") &&
                model.Nodes.Any(n => n.Id == "source-B"),
                "focused view hides unrelated applications while all shared routes remain active");
            SavePreview(window, Path.Combine(directory, "shared-focused.png"));

            Node("source-A").Volume = .37f;
            await Until(() => Math.Abs(SessionVolume(first) - .37f) < .01, "Focused source volume did not apply immediately.");
            model.Connections.First(c => c.Model.SourceId == "source-A").Volume = .4f;
            await Until(() => Math.Abs(SessionVolume(first) - .148f) < .01 && Math.Abs(SessionVolume(second) - 1) < .01,
                "Focused connection volume did not update its own output independently.");
            Check(Math.Abs(SessionVolume(first) - .148f) < .01, "source and connection volume edits apply immediately in the focused view");
            await model.SwitchViewAsync(true);
            Check(Math.Abs(model.Connections.First(c => c.Model.SourceId == "source-A").Volume - .4f) < .001 &&
                Math.Abs(Node("source-A").Volume - .37f) < .001, "focused volume edits appear unchanged in the global canvas");
            Node("source-B").Volume = .21f;
            model.Connections.First(c => c.Model.SourceId == "source-B").Volume = .75f;
            await Until(() => Math.Abs(SessionVolume(second) - .1575f) < .01 && Math.Abs(SessionVolume(first) - .148f) < .01,
                "Global edits did not update B while preserving A.");
            Check(Math.Abs(SessionVolume(second) - .1575f) < .01, "global view edits apply to the same running graph without re-enabling");

            model.Connections.First(c => c.Model.SourceId == "source-A").Volume = .3f;
            model.RunActionCommand.Execute(null);
            await Until(() => !model.Running && !model.Busy, "Start Stop button did not stop routing.");
            Check(!model.Running && model.Paused && Output(first) == null && Output(second) == null && model.RunActionLabel == "开启声音路由",
                "pause restores both original outputs and exposes one shared resume action");
            SavePreview(window, Path.Combine(directory, "paused-controls.png"));
            await model.SwitchViewAsync(false); model.FocusApplication(firstPath);
            Node("source-A").Volume = .58f;
            await Task.Delay(160);
            Check(!model.Running && model.Paused && Output(first) == null && Output(second) == null && Math.Abs(SessionVolume(first) - 1) < .01,
                "switching views and editing while paused preserves the common pause state");
            model.RunActionCommand.Execute(null);
            await Until(() => model.Running && !model.Busy && Output(first) == channels[0].RenderEndpointId &&
                Output(second) == channels[1].RenderEndpointId && Math.Abs(SessionVolume(first) - .174f) < .01 &&
                Math.Abs(SessionVolume(second) - .1575f) < .01, "Resume did not apply the current shared configuration to both applications.");
            Check(model.Running && !model.Paused, "resume from either view applies the latest shared edits to all applications");

            var defaultA = model.Nodes.First(n => n.Model.PrimaryOutput && AppIdentity.Normalize(n.Model.OwnerAppKey ?? "") == AppIdentity.Normalize(firstPath));
            model.ChooseCard(defaultA, new("Windows 默认输出", AudioOutput.SystemDefault, NodeKind.Playback, "系统默认"));
            model.DeleteCardCommand.Execute(Node("input-A"));
            await Until(() => Output(first) == null && Math.Abs(SessionVolume(first) - .174f) < .01 &&
                Output(second) == channels[1].RenderEndpointId, "Changing A's default output did not preserve its route gain while leaving B unchanged.");
            Check(Output(first) == null && model.Connections.Any(c => c.IsPrimary && c.Source.Owner.Id == "source-A"),
                "changing the default device preserves its gain and protected line while unrelated outputs remain active");
            model.Connections.Single(c => c.IsPrimary && c.Source.Owner.Id == "source-A").Volume = 1;
            await Until(() => Math.Abs(SessionVolume(first) - .58f) < .01, "The default output gain did not update the running application.");
            defaultA.DefaultOutputEnabled = false;
            await Until(() => Math.Abs(SessionVolume(first)) < .001 && Math.Abs(SessionVolume(second) - .1575f) < .01,
                "Disabling the sole default output did not silence only its own running application.");
            Check(!defaultA.DefaultOutputEnabled && Math.Abs(SessionVolume(first)) < .001,
                "default-output switch silences the real application without muting another application or the Windows device");
            defaultA.DefaultOutputEnabled = true;
            await Until(() => Math.Abs(SessionVolume(first) - .58f) < .01,
                "Reenabling default playback did not restore the saved gain.");
            Check(Math.Abs(SessionVolume(first) - .58f) < .01, "default-output switch restores real playback at the saved volume");
            var speaker = model.Catalog.Endpoints.First(e => !e.Capture && e.Name.Contains("Senary", StringComparison.OrdinalIgnoreCase));
            var playback = model.AddBlank(NodeKind.Playback);
            model.ChooseCard(playback, new(speaker.Name, speaker.Id, NodeKind.Playback, "播放设备"));
            model.Connect(Node("source-A").Outputs[0], Node(playback.Id).Inputs[0]);
            model.ChooseCard(model.Nodes.First(n => n.Model.PrimaryOutput && AppIdentity.Normalize(n.Model.OwnerAppKey ?? "") == AppIdentity.Normalize(firstPath)),
                new(speaker.Name, speaker.Id, NodeKind.Playback, "默认原声输出"));
            await Until(() => Output(first) == speaker.Id && Output(second) == channels[1].RenderEndpointId,
                "A focused speaker connection did not apply immediately.");
            Check(Output(first) == speaker.Id, "new focused connections execute immediately without affecting other applications");
            var speakerConnection = model.Connections.First(c => c.Model.SourceId == "source-A");
            string speakerConnectionId = speakerConnection.Model.Id;
            speakerConnection.Volume = .62f;
            await Until(() => Math.Abs(SessionVolume(first) - .3596f) < .01, "The new output's independent volume did not apply.");
            await model.SwitchViewAsync(true);
            Check(model.Connections.Any(c => c.Model.Id == speakerConnectionId && Math.Abs(c.Volume - .62f) < .001),
                "the global canvas includes focused connections and their current gains");
            model.UndoCommand.Execute(null);
            await Until(() => Math.Abs(SessionVolume(first) - .58f) < .01, "Cross-view undo did not update running audio.");
            await model.SwitchViewAsync(false); model.FocusApplication(firstPath);
            model.RedoCommand.Execute(null);
            await Until(() => Math.Abs(SessionVolume(first) - .3596f) < .01, "Cross-view redo did not update running audio.");
            Check(Output(second) == channels[1].RenderEndpointId, "shared undo and redo update live audio while preserving unrelated routes");
            model.StartPendingConnection(Node(playback.Id).Inputs[0]);
            await model.SwitchViewAsync(true);
            await Until(() => Output(first) == speaker.Id && Math.Abs(SessionVolume(first) - .3596f) < .01,
                "Switching views during reconnect lost the original live connection.");
            Check(!model.Reconnecting && model.Graph.Connections.Any(c => c.Id == speakerConnectionId),
                "view switching restores a pending reconnect with its original volume");

            await model.PauseAsync(); model.SaveNow();
            var saved = model.Store.LoadWorkspace();
            Check(saved.RoutingPaused && saved.Configuration.Nodes.Any(n => n.Id == "source-A" && Math.Abs(n.Volume - .58f) < .001) &&
                saved.Configuration.Connections.Any(c => c.Id == speakerConnectionId && Math.Abs(c.Volume - .62f) < .001),
                "one saved workspace retains both applications, connection gains and common pause state");
            var savedViewDirectory = Path.Combine(directory, "saved-view");
            new SceneStore(savedViewDirectory).SaveWorkspace(saved);
            await using (var reopened = new MainViewModel(savedViewDirectory))
            {
                await reopened.SwitchViewAsync(false); reopened.FocusApplication(firstPath);
                Check(reopened.Paused && reopened.Graph.Nodes.Any(n => n.Id == "source-B") && reopened.FocusedRoot?.Id == "source-A",
                    "reopening in the focused view retains the complete shared configuration");
            }
            Check(Output(first) == null && Output(second) == null, "shared routing verification restores original Windows settings");
        }
        finally
        {
            await model.PauseAsync();
            if (!first.HasExited) first.Kill();
            if (!second.HasExited) second.Kill();
            model.ImportConfiguration(originalConfiguration);
            model.Workspace.FocusedApplication = originalFocus;
            model.Workspace.RoutingPaused = originalPaused;
            await model.SwitchViewAsync(originalView == CanvasView.Global);
            model.SaveNow();
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        {
            var child = VisualTreeHelper.GetChild(value, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    internal static void SavePreview(Window window, string path, int width = 1420, int height = 920)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        CommandManager.InvalidateRequerySuggested();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); content.UpdateLayout();
        if (window.FindName("Editor") is NodifyEditor editor) { editor.FitToScreen(); content.UpdateLayout(); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
