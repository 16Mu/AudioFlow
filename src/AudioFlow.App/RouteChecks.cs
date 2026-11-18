using AudioFlow.Audio;
using AudioFlow.Core;
using Nodify;
using Nodify.Events;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AudioFlow.App;

internal static class RouteChecks
{
    public static async Task RunAsync(MainWindow window, MainViewModel model, string sourceId, string playbackId, string microphoneId, List<string> checks)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        NodeViewModel Node(string id) => model.Nodes.First(n => n.Id == id);
        var edge = model.Connections.First(c => c.Source.Owner.Id == sourceId && c.Target.Owner.Id == playbackId);
        var outputRow = Node(sourceId).OutputLevels.First(r => r.Connection == edge);
        outputRow.Volume = .45f;
        Check(Node(playbackId).InputLevels.Single(r => r.Connection == edge).Volume == .45f &&
            Node(sourceId).OutputLevels.First(r => r.Connection.Target.Owner.Id == microphoneId).Volume == 1,
            "one route gain synchronizes source and target cards without changing another output");
        var input = Node(sourceId).InputLevels.First(); input.Volume = .3f;
        Check(input.Connection.Source.Owner.OutputLevels.First(r => r.Connection == input.Connection).Volume == .3f,
            "input card route gain matches the corresponding source output");
        var speaker = Node(playbackId);
        using var windowsVolume = new EndpointVolumeBinding(speaker.Model.Key);
        var original = windowsVolume.State;
        try
        {
            speaker.DeviceVolume = .31f;
            Check(Math.Abs(windowsVolume.State.Volume - .31f) < .002f, "speaker card volume updates the real Windows endpoint");
            windowsVolume.SetVolume(.52f);
            var timer = Stopwatch.StartNew();
            while (Math.Abs(speaker.DeviceVolume - .52f) > .002f && timer.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(30);
            Check(Math.Abs(speaker.DeviceVolume - .52f) < .002f, "Windows endpoint volume notifications update the speaker card");
            speaker.DeviceMuted = !original.Muted;
            Check(windowsVolume.State.Muted == !original.Muted, "device mute also synchronizes with Windows");
        }
        finally { windowsVolume.SetVolume(original.Volume); windowsVolume.SetMuted(original.Muted); }

        var editor = (NodifyEditor)window.FindName("Editor");
        void Layout()
        {
            var content = (FrameworkElement)window.Content; content.Measure(new(1420, 920)); content.Arrange(new(0, 0, 1420, 920)); content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); content.UpdateLayout();
        }
        NodeInput Connector(string id)
        {
            Layout(); var container = (DependencyObject)editor.ItemContainerGenerator.ContainerFromItem(Node(id))!;
            return Children(container).OfType<NodeInput>().First();
        }
        var originalPort = Node(playbackId).Inputs[0];
        var connector = Connector(playbackId);
        var started = new PendingConnectionEventArgs(originalPort) { RoutedEvent = Nodify.Connector.PendingConnectionStartedEvent, Anchor = originalPort.Anchor };
        connector.RaiseEvent(started); Layout();
        Check(model.Reconnecting && model.Pending.Source?.Owner.Id == sourceId && !model.Graph.Connections.Any(c => c.SourceId == sourceId && c.TargetId == playbackId),
            "dragging a connected input starts a detached line from its original source");
        var pending = Children(editor).OfType<PendingConnection>().First();
        var oldTarget = pending.TargetAnchor;
        connector.RaiseEvent(new PendingConnectionEventArgs(originalPort) { RoutedEvent = Nodify.Connector.PendingConnectionDragEvent, Anchor = originalPort.Anchor, OffsetX = 85, OffsetY = 45 });
        Layout();
        Check((pending.TargetAnchor - oldTarget).Length > 1 && (pending.SourceAnchor - model.Pending.Source!.Anchor).Length < .01,
            $"detached connection keeps its source anchor and follows drag position: source={pending.SourceAnchor}, expected={model.Pending.Source!.Anchor}, target={pending.TargetAnchor}, old={oldTarget}");
        connector.RaiseEvent(new PendingConnectionEventArgs(originalPort) { RoutedEvent = Nodify.Connector.PendingConnectionCompletedEvent, Canceled = true });
        Layout();
        Check(!model.Reconnecting && model.Graph.Connections.Any(c => c.SourceId == sourceId && c.TargetId == playbackId && c.Volume == .45f),
            "canceling a connection drag restores its route and original gain");
        var alternative = model.AddBlank(NodeKind.Playback);
        var alternateDevice = model.Catalog.Endpoints.First(e => !e.Capture && e.Id != speaker.Model.Key);
        model.ChooseCard(alternative, new(alternateDevice.Name, alternateDevice.Id, NodeKind.Playback, "播放设备"));
        originalPort = Node(playbackId).Inputs[0]; connector = Connector(playbackId);
        connector.RaiseEvent(new PendingConnectionEventArgs(originalPort) { RoutedEvent = Nodify.Connector.PendingConnectionStartedEvent, Anchor = originalPort.Anchor });
        connector.RaiseEvent(new PendingConnectionEventArgs(originalPort) { RoutedEvent = Nodify.Connector.PendingConnectionCompletedEvent, TargetConnector = Node(alternative.Id).Inputs[0] });
        Check(model.Graph.Connections.Any(c => c.SourceId == sourceId && c.TargetId == alternative.Id && c.Volume == .45f),
            "reconnecting to another card preserves the individual route volume");
        model.UndoCommand.Execute(null);
        Check(model.Graph.Connections.Any(c => c.SourceId == sourceId && c.TargetId == playbackId && c.Volume == .45f),
            "one undo restores a moved connection");
        Layout();
        var restored = model.Connections.First(c => c.Source.Owner.Id == sourceId && c.Target.Owner.Id == playbackId);
        var line = Children(editor).OfType<Connection>().First(c => c.DataContext == restored);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.GetSetMethod(true)!.Invoke(click, [2]);
        line.RaiseEvent(click);
        Check(!model.Graph.Connections.Any(c => c.SourceId == sourceId && c.TargetId == playbackId), "double-clicking a connection removes the complete line");
        model.UndoCommand.Execute(null);
        model.DeleteCardCommand.Execute(Node(alternative.Id));
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
}
