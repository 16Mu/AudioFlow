using AudioFlow.Core;
using Nodify;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudioFlow.App;

internal static class ConnectionInteractionChecks
{
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        var window = new MainWindow(Path.Combine(directory, "profile-" + Guid.NewGuid().ToString("N")), true)
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        window.Show();
        await using var model = window.ViewModel;
        try
        {
            await model.RefreshCatalogAsync();
            var device = model.Catalog.Endpoints.First(e => !e.Capture && !e.Virtual);
            const string musicKey = @"C:\AudioFlowPreview\Music.exe";
            var graph = new RouteGraph
            {
                Channels = model.Catalog.SuggestedChannels.ToList(),
                Nodes =
                [
                    new() { Id = "music", Kind = NodeKind.Application, Key = musicKey, Name = "音乐软件", X = 80, Y = 100 },
                    new() { Id = "primary", Kind = NodeKind.Playback, PrimaryOutput = true, OwnerAppKey = musicKey, Key = AudioOutput.SystemDefault, Name = "主要播放", X = 570, Y = 70 },
                    new() { Id = "extra", Kind = NodeKind.Playback, Key = device.Id, Name = "第二个播放设备", X = 570, Y = 480 }
                ],
                Connections =
                [
                    new() { Id = "base", SourceId = "music", TargetId = "primary", Mode = RouteMode.Playback },
                    new() { Id = "copy", SourceId = "music", TargetId = "extra", Mode = RouteMode.Playback, Volume = .55f }
                ]
            };
            model.ImportConfiguration(graph); model.FocusApplication(musicKey);
            async Task Layout()
            {
                window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }
            async Task<FlowConnection> Line(string id)
            {
                await Layout();
                return Children((NodifyEditor)window.FindName("Editor")).OfType<FlowConnection>().Single(c => ((ConnectionViewModel)c.DataContext).Model.Id == id);
            }
            static void Click(FlowConnection line, MouseButton button)
            {
                line.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
                { RoutedEvent = button == MouseButton.Left ? UIElement.PreviewMouseLeftButtonDownEvent : UIElement.PreviewMouseRightButtonDownEvent });
            }
            async Task<ContextMenu> Menu(string id)
            {
                var line = await Line(id); Click(line, MouseButton.Right);
                var menu = line.ContextMenu; menu.PlacementTarget = line; menu.IsOpen = true;
                await Layout(); return menu;
            }
            static void Action(ContextMenu menu, int index)
            {
                var item = (MenuItem)menu.Items[index]; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item)); menu.IsOpen = false;
            }
            var copy = await Line("copy"); Click(copy, MouseButton.Left); await Layout();
            Check(model.SelectedConnections.Single().Model.Id == "copy" && copy.IsHighlighted && copy.Opacity == 1 && copy.StrokeThickness > 5,
                "clicking the real line selects and visibly highlights it even while audio is stopped");
            Check(((SolidColorBrush)copy.Stroke).Color == ((SolidColorBrush)Theme.Brush("Text.Primary")).Color,
                "the selected line uses a bright contrasting color");
            copy.IsFlowing = true; copy.HasSignal = true;
            Check(copy.IsHighlighted && copy.Opacity == 1 && copy.StrokeThickness > 5,
                "animation updates keep the selected line highlighted");
            copy.IsFlowing = false; copy.HasSignal = false;
            Click(await Line("base"), MouseButton.Left); await Layout();
            Check(!copy.IsHighlighted && model.SelectedConnections.Single().Model.Id == "base",
                "clicking a different line moves the highlight to that line");
            var menu = await Menu("copy");
            Check(model.SelectedConnections.Single().Model.Id == "copy" && ((MenuItem)menu.Items[0]).Header?.ToString() == "暂停这条连接" && ((MenuItem)menu.Items[2]).IsEnabled,
                "right-click selects the exact line and exposes pause and delete actions");
            UiChecks.SavePreview(window, Path.Combine(directory, "selected-line.png"));
            Action(menu, 0); await Layout();
            Check(model.Graph.Connections.Count == 2 && !model.Graph.Connections.Single(c => c.Id == "copy").Enabled && model.Graph.Connections.Single(c => c.Id == "copy").Volume == .55f,
                "the actual pause menu keeps the line and its volume");
            copy = await Line("copy");
            Check(copy.IsDisconnected && copy.StrokeDashArray?.Count > 0 && copy.DirectionalArrowsCount == 0 && copy.IsHighlighted,
                "a paused selected line is dashed with no flow arrows and remains highlighted");
            Check(GraphCompiler.Compile(model.Graph, model.Catalog.Endpoints).Sources.Single(s => s.Node.Id == "music").Destinations.All(d => d.EndpointId != device.Id || d.SystemDefault),
                "the audio plan excludes the paused additional destination");
            model.SaveNow();
            Check(!model.Store.LoadWorkspace().Configuration.Connections.Single(c => c.Id == "copy").Enabled,
                "the paused connection persists to disk");
            menu = await Menu("copy");
            Check(((MenuItem)menu.Items[0]).Header?.ToString() == "重新开启", "a paused line offers an explicit re-enable action");
            Action(menu, 0); await Layout();
            Check(model.Graph.Connections.Single(c => c.Id == "copy").Enabled && model.Graph.Connections.Single(c => c.Id == "copy").Volume == .55f,
                "re-enable restores the connection at the saved volume");
            menu = await Menu("base");
            Check(!((MenuItem)menu.Items[2]).IsEnabled && ((MenuItem)menu.Items[3]).Visibility == Visibility.Visible,
                "the protected primary line explains its deletion restriction and retains a pause action");
            string originalKey = model.Graph.Nodes.Single(n => n.Id == "primary").Key;
            Action(menu, 0);
            Check(!model.Graph.Connections.Single(c => c.Id == "base").Enabled && model.Graph.Nodes.Single(n => n.Id == "primary").Key == AudioOutput.None && model.Graph.Connections.Single(c => c.Id == "copy").Enabled,
                "pausing the primary line closes only its own output");
            menu = await Menu("base"); Action(menu, 0);
            Check(model.Graph.Connections.Single(c => c.Id == "base").Enabled && model.Graph.Nodes.Single(n => n.Id == "primary").Key == originalKey,
                "re-enabling the primary line restores its former device");
            menu = await Menu("copy"); Action(menu, 2);
            Check(model.Graph.Connections.All(c => c.Id != "copy") && model.Graph.Nodes.Count == 3 && model.Graph.Connections.Any(c => c.Id == "base"),
                "delete removes only the chosen line and preserves both cards and other connections");
            model.UndoCommand.Execute(null);
            Check(model.Graph.Connections.Single(c => c.Id == "copy").Enabled, "undo restores the deleted connection");

            Check(graph.Channels.Count >= 2, "the virtual-input verification has two independently installed channels");
            var first = graph.Channels[0];
            var virtualGraph = SceneStore.Clone(graph);
            virtualGraph.Nodes.Add(new() { Id = "virtual", Kind = NodeKind.VirtualMicrophone, ChannelId = first.Id, Key = first.RenderEndpointId, Name = "传声音的麦克风" });
            virtualGraph.Nodes.Add(new() { Id = "receiver", Kind = NodeKind.Application, Key = @"C:\AudioFlowPreview\Chat.exe", Name = "聊天软件", InputDeviceKey = first.CaptureEndpointId, ChannelId = first.Id });
            virtualGraph.Connections.Add(new() { Id = "feed", SourceId = "music", TargetId = "virtual", Mode = RouteMode.MicrophoneFeed });
            virtualGraph.Connections.Add(new() { Id = "receive", SourceId = "virtual", TargetId = "receiver", Mode = RouteMode.MicrophoneFeed });
            model.ImportConfiguration(virtualGraph); await model.SwitchViewAsync(true);
            model.SetConnectionEnabled(model.Connections.Single(c => c.Model.Id == "receive"), false);
            Check(!model.Graph.Connections.Single(c => c.Id == "receive").Enabled && model.Graph.Nodes.Single(n => n.Id == "receiver").InputDeviceKey == AudioOutput.None && model.Graph.Connections.Single(c => c.Id == "feed").Enabled,
                "pausing a shared virtual microphone stops this recipient without pausing the upstream sound");
            model.SetConnectionEnabled(model.Connections.Single(c => c.Model.Id == "receive"), true);
            Check(model.Graph.Nodes.Single(n => n.Id == "receiver").InputDeviceKey == first.CaptureEndpointId && model.Graph.Connections.Single(c => c.Id == "receive").Enabled,
                "re-enable restores the recipient's original virtual microphone");
            model.RemoveConnection(model.Connections.Single(c => c.Model.Id == "receive"));
            Check(model.Graph.Connections.All(c => c.Id != "receive") && model.Graph.Nodes.Single(n => n.Id == "receiver").InputDeviceKey == AudioOutput.None,
                "deleting a virtual microphone line also stops that recipient from hearing it");
            model.UndoCommand.Execute(null);
            Check(model.Graph.Connections.Any(c => c.Id == "receive") && model.Graph.Nodes.Single(n => n.Id == "receiver").InputDeviceKey == first.CaptureEndpointId,
                "one undo restores both the virtual line and its recipient microphone");
            var occupied = SceneStore.Clone(model.Graph);
            var second = occupied.Channels[1];
            occupied.Nodes.Add(new() { Id = "other-receiver", Kind = NodeKind.Application, Key = @"C:\AudioFlowPreview\OtherChat.exe", Name = "其他聊天软件", InputDeviceKey = second.CaptureEndpointId, ChannelId = second.Id });
            model.ImportConfiguration(occupied);
            model.SetConnectionEnabled(model.Connections.Single(c => c.Model.Id == "receive"), false);
            Check(model.Graph.Connections.Single(c => c.Id == "receive").Enabled && model.Graph.Nodes.Single(n => n.Id == "receiver").InputDeviceKey == first.CaptureEndpointId && model.Notice.Contains("腾出"),
                "without a free silence device, pause explains the problem and preserves the working connection");
            Check(!model.Running, "connection interactions do not start real audio during this fixture");
            File.WriteAllText(Path.Combine(directory, "connection-report.json"), JsonSerializer.Serialize(new { Passed = true, Count = checks.Count, Checks = checks }, SceneStore.JsonOptions));
        }
        finally { window.Close(); }
    }
}
