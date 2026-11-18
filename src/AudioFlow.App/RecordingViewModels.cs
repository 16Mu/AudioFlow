using AudioFlow.Core;
using System.Collections.ObjectModel;

namespace AudioFlow.App;

public sealed partial class NodeViewModel
{
    public bool ShowRecordingControls => SoftwareCard && (Model.RecordingConfigured || Receiving ||
        Owner.Catalog.Applications.Any(a => a.Key == AppIdentity.Normalize(Model.Key) && a.HasRecordingSession) ||
        Owner.Workspace.KnownRecordingApplications.Any(a => AppIdentity.Normalize(a.Path) == AppIdentity.Normalize(Model.Key)));
    public ObservableCollection<CardChoice> RecordingChoices { get; } = [];
    public string RecordingKey => Model.InputDeviceKey ?? Owner.Graph.Channels.FirstOrDefault(c => c.Id == Model.ChannelId)?.CaptureEndpointId ?? observedRecordingKey;
    private string observedRecordingKey = AudioOutput.SystemDefault;
    public CardChoice? RecordingChoice
    {
        get => RecordingChoices.FirstOrDefault(c => c.Key == RecordingKey);
        set { if (value != null && value.Key != RecordingKey) Owner.ChooseRecordingDevice(this, value.Key); }
    }
    public bool RecordingEnabled
    {
        get => RecordingKey != AudioOutput.None;
        set { if (value != RecordingEnabled) Owner.ChooseRecordingDevice(this, value ? Model.LastInputDeviceKey ?? AudioOutput.SystemDefault : AudioOutput.None); }
    }
    private string recordingStatus = "";
    public string RecordingStatus { get => recordingStatus; internal set => Set(ref recordingStatus, value); }
    internal void RefreshRecordingChoices()
    {
        if (!SoftwareCard) return;
        var observed = Owner.Catalog.Applications.FirstOrDefault(a => a.Key == AppIdentity.Normalize(Model.Key))?.RecordingEndpointId;
        observedRecordingKey = observed != null && observed != Owner.Catalog.Endpoints.FirstOrDefault(e => e.Capture && e.Default)?.Id ? observed : AudioOutput.SystemDefault;
        var candidates = new List<CardChoice>
        {
            new("不接收声音", AudioOutput.None, NodeKind.Microphone, "让这个软件听不到声音；需要一个空着的传声音设备"),
            new("跟随 Windows 的麦克风选择", AudioOutput.SystemDefault, NodeKind.Microphone,
                Owner.Catalog.Endpoints.FirstOrDefault(e => e.Capture && e.Default)?.Name ?? "等待麦克风连接")
        };
        string? reserved = Owner.Graph.Channels.FirstOrDefault(c => c.Id == Owner.Graph.SilentInputChannelId)?.CaptureEndpointId;
        candidates.AddRange(Owner.Catalog.Endpoints.Where(e => e.Capture && e.Id != reserved).Select(e => new CardChoice(e.Name, e.Id, NodeKind.Microphone, e.Virtual ? "传声音的麦克风" : "真实麦克风")));
        if (candidates.All(c => c.Key != RecordingKey)) candidates.Add(new("上次选的麦克风 · 等待连接", RecordingKey, NodeKind.Microphone, "设备未连接"));
        if (!RecordingChoices.Select(c => c.Key + c.Name).SequenceEqual(candidates.Select(c => c.Key + c.Name)))
        { RecordingChoices.Clear(); foreach (var choice in candidates) RecordingChoices.Add(choice); }
        Changed(nameof(RecordingChoice)); Changed(nameof(RecordingEnabled)); Changed(nameof(ShowRecordingControls));
    }
}

public sealed partial class MainViewModel
{
    public void ConfigureRecording(NodeViewModel node)
    {
        node.Model.RecordingConfigured = true; node.Model.InputDeviceKey ??= AudioOutput.SystemDefault;
        PromoteAudioEdit(node); node.RefreshRecordingChoices(); Edited(true);
    }
    public void ChooseRecordingDevice(NodeViewModel node, string key)
    {
        if (!node.SoftwareCard) return;
        var backup = SceneStore.Clone(graph);
        try
        {
            PromoteAudioEdit(node);
            if (key == AudioOutput.None)
            {
                node.Model.InputDeviceKey ??= node.RecordingKey;
                ReserveQuietInput();
            }
            if (key != AudioOutput.None && node.RecordingKey != AudioOutput.None) node.Model.LastInputDeviceKey = node.RecordingKey;
            if (key == AudioOutput.None && node.RecordingKey != AudioOutput.None) node.Model.LastInputDeviceKey = node.RecordingKey;
            if (key != AudioOutput.None) node.Model.LastInputDeviceKey = key;
            node.Model.InputDeviceKey = key; node.Model.RecordingConfigured = true;
            var channel = graph.Channels.FirstOrDefault(c => c.CaptureEndpointId == key && c.Id != graph.SilentInputChannelId);
            if (channel != null) { node.Model.ChannelId = channel.Id; node.Model.InputChannelPinned = true; }
            if (!graph.Nodes.Any(n => n.InputDeviceKey == AudioOutput.None)) graph.SilentInputChannelId = null;
            GraphCompiler.Compile(graph, Catalog.Endpoints); RebuildConnections(); BuildCanvas(); Edited(true);
            node.RefreshRecordingChoices(); Notice = key == AudioOutput.None ? "已经选好不接收声音，会留出一个空着的设备供它使用。" : "麦克风已经选好。开启声音连接后，这个软件会使用它。";
        }
        catch (Exception error) { graph = backup; Rebuild(); Notice = UserError.Describe(error); node.RefreshRecordingChoices(); }
    }
    private void ReserveQuietInput()
    {
        string? previous = graph.SilentInputChannelId;
        var current = ChannelPool.Connected(graph, Catalog.Endpoints).FirstOrDefault(c => c.Id == previous && !c.RequiresValidation);
        bool Quiet(VirtualChannel channel) => !catalog.ActiveSessions(channel.RenderEndpointId).Any(s => s.Peak > .001);
        graph.SilentInputChannelId = null;
        if (current != null && ChannelPool.Free(graph, Catalog.Endpoints).Any(c => c.Id == current.Id) && Quiet(current))
        { graph.SilentInputChannelId = previous; return; }
        var available = ChannelPool.Free(graph, Catalog.Endpoints).FirstOrDefault(Quiet);
        if (available == null) { graph.SilentInputChannelId = previous; throw new GraphValidationException("暂时关不了录音。请先腾出一个传声音的麦克风，或在设置里安装更多驱动。"); }
        graph.SilentInputChannelId = available.Id;
    }
}
