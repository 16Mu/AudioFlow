using AudioFlow.Core;
using Nodify;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioFlow.App;

internal static class DefaultOutputChecks
{
    public static async Task RunAsync(MainWindow window, MainViewModel model, string firstId, string secondId, List<string> checks, string directory)
    {
        var original = SceneStore.Clone(model.Graph);
        bool global = model.GlobalView;
        var focus = model.Workspace.FocusedApplication;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        NodeViewModel Node(string id) => model.Nodes.First(n => n.Id == id);
        void Layout() { var content = (FrameworkElement)window.Content; content.Measure(new(1420, 920)); content.Arrange(new(0, 0, 1420, 920)); content.UpdateLayout(); }
        IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Children(child)) yield return item; }
        }
        ComboBox Picker(NodeViewModel node)
        {
            Layout();
            var editor = (NodifyEditor)window.FindName("Editor");
            var container = (DependencyObject)editor.ItemContainerGenerator.ContainerFromItem(node)!;
            return Children(container).OfType<ComboBox>().First();
        }
        try
        {
            await model.SwitchViewAsync(false); model.FocusApplication(Node(firstId).Model.Key);
            var root = Node(firstId);
            var output = model.Nodes.Single(n => n.Model.PrimaryOutput && ApplicationOutputs.IsPrimary(root.Model, n.Model));
            var otherOutput = model.Nodes.Single(n => n.Model.PrimaryOutput && ApplicationOutputs.IsPrimary(Node(secondId).Model, n.Model));
            string otherKey = otherOutput.Model.Key;
            Check(Picker(root).Visibility == Visibility.Collapsed && !root.CanChooseCard,
                "focused software selector is removed from the rendered card");
            string rootKey = root.Model.Key;
            model.ChooseCard(root, new(Node(secondId).Name, Node(secondId).Model.Key, NodeKind.Application, "软件"));
            Check(root.Model.Key == rootKey && !root.CanDelete && !output.CanDelete,
                "focused software identity and both base cards are protected");
            int nodeCount = model.Graph.Nodes.Count, edgeCount = model.Graph.Connections.Count;
            var primary = model.Connections.Single(c => c.Source.Owner.Id == root.Id && c.IsPrimary);
            model.SelectedNodes.Clear(); model.SelectedNodes.Add(root); model.SelectedNodes.Add(output);
            model.SelectedConnections.Clear(); model.SelectedConnections.Add(primary);
            model.DeleteCommand.Execute(null); model.DeleteCardCommand.Execute(root); model.DeleteCardCommand.Execute(output);
            model.RemoveConnection(primary);
            model.StartPendingConnection(output.Inputs[0]); model.CompleteReconnect(null, false);
            Check(model.Graph.Nodes.Count == nodeCount && model.Graph.Connections.Count == edgeCount && !model.Reconnecting,
                "Delete, card deletion, disconnection and reconnect cannot remove the default pair");
            Layout();
            var line = Children((NodifyEditor)window.FindName("Editor")).OfType<Connection>().First(c => c.DataContext == primary);
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.GetSetMethod(true)!.Invoke(click, [2]);
            line.RaiseEvent(click);
            Check(model.Graph.Connections.Count == edgeCount, "double-clicking the default line leaves it connected");
            var ownInput = model.Graph.Channels.FirstOrDefault(c => c.CaptureEndpointId == root.RecordingKey)?.RenderEndpointId;
            var device = model.Catalog.Endpoints.First(e => !e.Capture && e.Id != ownInput && e.Id != AudioOutput.Resolve(output.Model.Key, model.Catalog.Endpoints)?.Id);
            Picker(output).SelectedItem = output.Choices.First(c => c.Key == device.Id);
            Check(output.Model.Key == device.Id && otherOutput.Model.Key == otherKey,
                "default output dropdown changes only its own application's native device");
            var extra = model.AddBlank(NodeKind.Playback);
            var copyDevice = model.Catalog.Endpoints.First(e => !e.Capture && e.Id != device.Id);
            model.ChooseCard(extra, new(copyDevice.Name, copyDevice.Id, NodeKind.Playback, "播放设备"));
            model.Connect(Node(firstId).Outputs[0], Node(extra.Id).Inputs[0]);
            var reversed = SceneStore.Clone(model.Graph); reversed.Connections.Reverse();
            var compiled = GraphCompiler.Compile(reversed, model.Catalog.Endpoints).Sources.First(s => s.Node.Id == firstId);
            Check(compiled.Primary is { Native: true } && compiled.Primary.EndpointId == device.Id &&
                compiled.Mirrors.Any(d => d.EndpointId == copyDevice.Id),
                "default output remains native and extra playback is copied regardless of line order");
            output = Node(output.Id);
            primary = model.Connections.Single(c => c.IsPrimary && c.Source.Owner.Id == firstId);
            Layout();
            var outputContainer = (DependencyObject)((NodifyEditor)window.FindName("Editor")).ItemContainerGenerator.ContainerFromItem(output)!;
            var toggle = Children(outputContainer).OfType<CheckBox>().Single(c => Equals(c.Content, "输出到所选设备"));
            float savedGain = primary.Volume;
            toggle.IsChecked = false;
            Check(!output.DefaultOutputEnabled && model.Graph.Connections.Count == edgeCount + 1 &&
                !model.Connections.Single(c => c.IsPrimary && c.Source.Owner.Id == firstId).Model.Enabled,
                "the rendered default-output checkbox disables its protected line without deleting cards or connections");
            Check(output.Model.Key == AudioOutput.None && output.Choice?.Key == AudioOutput.None && output.Model.LastOutputKey == device.Id,
                "switching off the output synchronizes the dropdown to No Output and remembers the previous device");
            compiled = GraphCompiler.Compile(model.Graph, model.Catalog.Endpoints).Sources.First(s => s.Node.Id == firstId);
            Check(compiled.Destinations.Any(d => d.EndpointId == copyDevice.Id) && compiled.Destinations.All(d => !d.Native),
                "switching off default playback leaves the added output audible");
            model.Store.SaveWorkspace(model.Workspace);
            var saved = model.Store.LoadWorkspace();
            Check(!saved.Configuration.Connections.Single(c => c.TargetId == output.Id).Enabled,
                "the default-output checkbox state is saved in the workspace");
            await model.SwitchViewAsync(true); await model.SwitchViewAsync(false); model.FocusApplication(rootKey);
            Check(!Node(output.Id).DefaultOutputEnabled, "switching canvas views preserves a disabled default output");
            model.Arrange(); UiChecks.SavePreview(window, Path.Combine(directory, "default-output-disabled.png"));
            output.DefaultOutputEnabled = true;
            Check(model.Connections.Single(c => c.IsPrimary && c.Source.Owner.Id == firstId).Volume == savedGain && output.Model.Key == device.Id,
                "reenabling the default output preserves its device and gain");
            var additional = model.AddBlank(NodeKind.Application);
            model.ChooseCard(additional, new("附加软件", Path.Combine(directory, "AdditionalApplication.exe"), NodeKind.Application, "软件"));
            Check(additional.IsConfigured && additional.CanChooseCard && additional.CanDelete,
                "right-click additional software cards remain freely configurable");
            model.DeleteCardCommand.Execute(additional);
            Check(!model.Graph.Nodes.Any(n => n.Id == additional.Id) && !model.Graph.Nodes.Any(n => n.PrimaryOutput && n.OwnerAppKey == additional.Model.Key),
                "deleting an additional software also removes only its own default card");
            model.FocusApplication(rootKey); model.Arrange();
            UiChecks.SavePreview(window, Path.Combine(directory, "protected-default-output.png"));
            string draftPath = Path.Combine(directory, "UnconfiguredApplication.exe");
            int beforeDraft = model.Graph.Nodes.Count;
            model.FocusApplication(draftPath);
            Check(model.Graph.Nodes.Count == beforeDraft && model.CanvasNodes.Count == 2 && model.CanvasConnections.Single().IsPrimary,
                "an unconfigured software preview contains both base cards and their default line without saving a route");
            UiChecks.SavePreview(window, Path.Combine(directory, "new-application-default.png"));
            var draftRoot = model.FocusedRoot!;
            var draftOutput = model.CanvasNodes.Single(n => n.Model.PrimaryOutput);
            Picker(draftOutput).SelectedItem = draftOutput.Choices.First(c => c.Key == device.Id);
            Check(model.FocusedRoot?.IsDraft == false && model.Graph.Nodes.Count(n => n.Kind == NodeKind.Application && n.Key == draftPath) == 1 &&
                model.Nodes.Select(n => n.Id).Distinct().Count() == model.Nodes.Count &&
                model.Connections.Single(c => c.IsPrimary && c.Source.Owner.Model.Key == draftPath).Target.Owner.Model.Key == device.Id,
                "editing the preview output commits exactly one software and one default route");
            model.FocusApplication(Path.Combine(directory, "ToggleOnlyApplication.exe"));
            model.CanvasNodes.Single(n => n.HasDefaultOutput).DefaultOutputEnabled = false;
            Check(model.FocusedRoot?.IsDraft == false && model.CanvasConnections.Count == 1 &&
                !model.CanvasConnections.Single().Model.Enabled && !model.CanvasNodes.Single(n => n.HasDefaultOutput).DefaultOutputEnabled,
                "turning off an unconfigured preview commits one disabled default pair");
        }
        finally
        {
            model.ImportConfiguration(original);
            model.Workspace.FocusedApplication = focus;
            await model.SwitchViewAsync(global);
            if (!global && focus != null) model.FocusApplication(focus);
        }
    }
}
