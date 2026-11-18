using AudioFlow.Audio;
using AudioFlow.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudioFlow.App;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(property); return true;
    }
}
public sealed class Command(Action<object?> action, Func<object?, bool>? condition = null) : ICommand
{
    public bool CanExecute(object? value) => condition?.Invoke(value) ?? true;
    public void Execute(object? value) => action(value);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public sealed record CardChoice(string Name, string Key, NodeKind Kind, string Hint, string? ChannelId = null)
{
    public ImageSource? Icon => AppIcons.Get(Key);
}
public sealed record AvailableItem(string Name, string Key, NodeKind Kind, string Detail)
{
    public ImageSource? Icon => AppIcons.Get(Key);
    public bool Matches(string query) => ApplicationSelection.Matches(Name, Key, query);
}
public sealed class PortViewModel(NodeViewModel owner, bool output, string name, bool microphone) : Observable
{
    public NodeViewModel Owner { get; } = owner;
    public bool Output { get; } = output;
    public string Name { get; } = name;
    public Brush Color { get; } = Theme.Brush(microphone ? "Accent.Input" : "Accent.Output");
    private Point anchor;
    public Point Anchor { get => anchor; set => Set(ref anchor, value); }
}
public sealed partial class NodeViewModel : Observable
{
    public RouteNode Model { get; }
    public MainViewModel Owner { get; }
    public NodeViewModel(RouteNode model, MainViewModel owner, bool draft = false)
    {
        Model = model; Owner = owner; IsDraft = draft;
        RefreshPorts(); RefreshChoices();
    }
    public bool IsDraft { get; internal set; }
    public string Id => Model.Id;
    public string Name => PlaybackCard && Model.Key == AudioOutput.SystemDefault ? "Windows 默认输出" : Model.Name;
    public ImageSource? Icon => AppIcons.Get(Model.Key);
    public bool SoftwareCard => Model.Kind == NodeKind.Application;
    public bool AudioSource => Model.Kind is NodeKind.Application or NodeKind.Microphone;
    public bool MicrophoneCard => Model.Kind is NodeKind.Microphone or NodeKind.VirtualMicrophone;
    public bool PlaybackCard => Model.Kind == NodeKind.Playback;
    public bool FocusedApplicationCard => !Owner.GlobalView && ReferenceEquals(Owner.FocusedRoot, this);
    public bool CanChooseCard => !FocusedApplicationCard;
    public bool CanDelete => !FocusedApplicationCard && !Model.PrimaryOutput;
    public bool HasDefaultOutput => Model.PrimaryOutput;
    public bool DefaultOutputEnabled
    {
        get => Model.Key != AudioOutput.None && (!Model.PrimaryOutput || Owner.Graph.Connections.FirstOrDefault(c => c.TargetId == Id)?.Enabled != false);
        set => Owner.SetDefaultOutputEnabled(this, value);
    }
    internal void RefreshDefaultOutput() { Changed(nameof(DefaultOutputEnabled)); Changed(nameof(Category)); }
    public string CardAction => FocusedApplicationCard ? "当前软件" : Model.PrimaryOutput ? "默认输出" : "右键配置";
    internal void RefreshProtection() { Changed(nameof(CanChooseCard)); Changed(nameof(CanDelete)); Changed(nameof(CardAction)); }
    public bool IsVirtual => Model.Kind == NodeKind.VirtualMicrophone;
    public bool IsConfigured => Model.Kind == NodeKind.VirtualMicrophone ? !string.IsNullOrWhiteSpace(Model.ChannelId) :
        !string.IsNullOrWhiteSpace(Model.Key);
    public string Category => Model.Kind switch
    {
        NodeKind.Application => "软件",
        NodeKind.Microphone => "真实麦克风 · 声音来源",
        NodeKind.VirtualMicrophone => "虚拟麦克风 · 接收声音",
        _ => Model.PrimaryOutput ? DefaultOutputEnabled ? "默认输出 · 原声播放" : "默认输出 · 已关闭" : "播放设备 · 复制声音"
    };
    public string PickerHint => SoftwareCard ? "选择正在运行的软件" : MicrophoneCard ? "选择真实／虚拟麦克风" : "选择播放设备";
    public string Glyph => SoftwareCard ? "♫" : PlaybackCard ? "◖" : "◉";
    public Brush Accent => Theme.Brush(MicrophoneCard ? "Accent.Input" : "Accent.Output");
    public ObservableCollection<PortViewModel> Inputs { get; } = [];
    public ObservableCollection<PortViewModel> Outputs { get; } = [];
    public ObservableCollection<RouteLevelViewModel> InputLevels { get; } = [];
    public ObservableCollection<RouteLevelViewModel> OutputLevels { get; } = [];
    public bool HasInputLevels => InputLevels.Count > 0;
    public bool HasOutputLevels => OutputLevels.Count > 0;
    public string SourceVolumeTitle => SoftwareCard ? "软件总音量" : "麦克风总音量";
    private float deviceVolume = 1;
    private bool deviceMuted, deviceAvailable;
    public bool DeviceVolumeAvailable => deviceAvailable;
    public float DeviceVolume { get => deviceVolume; set { if (Math.Abs(value - deviceVolume) > .001f) Owner.SetDeviceVolume(this, value); } }
    public bool DeviceMuted { get => deviceMuted; set { if (value != deviceMuted) Owner.SetDeviceMuted(this, value); } }
    public string DeviceVolumeLabel => $"{deviceVolume:P0}";
    internal void UpdateDeviceVolume(EndpointVolumeState? state)
    {
        bool available = state != null;
        if (available != deviceAvailable) { deviceAvailable = available; Changed(nameof(DeviceVolumeAvailable)); }
        if (state == null) return;
        if (Math.Abs(deviceVolume - state.Volume) > .0001f)
        { deviceVolume = state.Volume; Changed(nameof(DeviceVolume)); Changed(nameof(DeviceVolumeLabel)); }
        if (deviceMuted != state.Muted) { deviceMuted = state.Muted; Changed(nameof(DeviceMuted)); }
    }
    internal void SetRouteLevels(IEnumerable<ConnectionViewModel> connections)
    {
        var attached = connections.Where(c => c.Target.Owner.Id == Id || c.Source.Owner.Id == Id).ToArray();
        var existing = InputLevels.Select(r => r.Connection).Concat(OutputLevels.Select(r => r.Connection)).ToArray();
        if (existing.Length == attached.Length && attached.All(existing.Contains)) return;
        foreach (var row in InputLevels.Concat(OutputLevels)) row.Dispose();
        InputLevels.Clear(); OutputLevels.Clear();
        foreach (var connection in attached)
        {
            if (connection.Target.Owner.Id == Id) InputLevels.Add(new(connection, true));
            if (connection.Source.Owner.Id == Id) OutputLevels.Add(new(connection, false));
        }
        Changed(nameof(HasInputLevels)); Changed(nameof(HasOutputLevels));
    }
    public ObservableCollection<CardChoice> Choices { get; } = [];
    public ObservableCollection<VirtualChannel> InputChannels { get; } = [];
    private CardChoice? choice;
    private bool refreshing;
    public CardChoice? Choice
    {
        get => choice;
        set { if (!refreshing && value != null && value != choice) Owner.ChooseCard(this, value); }
    }
    public VirtualChannel? InputChannel
    {
        get => InputChannels.FirstOrDefault(c => c.Id == Model.ChannelId);
        set { if (!refreshing && value != null && value.Id != Model.ChannelId) Owner.ChooseInputChannel(this, value); }
    }
    private int incoming, outgoing;
    public bool Receiving => SoftwareCard && incoming > 0;
    public string LinkSummary => $"输入 {incoming} · 输出 {outgoing}";
    public string InputDeviceName => Owner.Graph.Channels.FirstOrDefault(c => c.Id == Model.ChannelId) is { } channel
        ? Owner.Catalog.Endpoints.FirstOrDefault(e => e.Id == channel.CaptureEndpointId)?.Name ?? "设备待连接"
        : "连接时分配输入通道";
    public string InputHelp => "在这个软件中，将麦克风设置为：" + InputDeviceName;
    public bool OutputDisabled => SoftwareCard && outgoing == 0 && Owner.Graph.Connections.Any(c => c.SourceId == Id && !c.Enabled &&
        Owner.Graph.Nodes.Any(n => n.Id == c.TargetId && ApplicationOutputs.IsPrimary(Model, n)));
    public bool UseSystemOutput => SoftwareCard && outgoing == 0 && !OutputDisabled;
    public void SetCounts(int inputs, int outputs)
    {
        incoming = inputs; outgoing = outputs;
        Changed(nameof(Receiving)); Changed(nameof(LinkSummary)); Changed(nameof(UseSystemOutput));
        Changed(nameof(OutputDisabled));
        Changed(nameof(InputDeviceName)); Changed(nameof(InputHelp));
        Changed(nameof(InputChannel));
    }
    internal void RefreshPorts()
    {
        Inputs.Clear(); Outputs.Clear();
        if (SoftwareCard) Inputs.Add(new(this, false, "声音输入", true));
        else if (Model.Kind == NodeKind.VirtualMicrophone) Inputs.Add(new(this, false, "接收声音", true));
        else if (PlaybackCard) Inputs.Add(new(this, false, "播放输入", false));
        if (AudioSource) Outputs.Add(new(this, true, SoftwareCard ? "声音输出" : "麦克风声音", !SoftwareCard));
        else if (IsVirtual) Outputs.Add(new(this, true, "交给软件", true));
    }
    private string choiceSignature = "";
    internal void RefreshChoices()
    {
        var candidates = new List<CardChoice>();
        if (SoftwareCard)
        {
            foreach (var app in Owner.GetApplicationCandidates())
                candidates.Add(new(app.Name, app.Path, NodeKind.Application, app.Peak > .001f ? "正在播放" :
                    app.HasAudio ? "正在使用音频" : app.ProcessIds.Count > 0 ? "运行中 · 暂无音频" : "等待软件运行"));
        }
        else if (MicrophoneCard)
        {
            foreach (var mic in Owner.Catalog.Endpoints.Where(e => e.Capture && !e.Virtual))
                candidates.Add(new(mic.Name, mic.Id, NodeKind.Microphone, "真实麦克风 · 输出声音"));
            foreach (var channel in Owner.Graph.Channels)
                candidates.Add(new(channel.Name, "", NodeKind.VirtualMicrophone, "虚拟麦克风 · 可接收软件声音", channel.Id));
        }
        else
        {
            if (Model.PrimaryOutput) candidates.Add(new("不进行输出", AudioOutput.None, NodeKind.Playback, "关闭此软件的默认原声输出"));
            candidates.Add(new("Windows 默认输出", AudioOutput.SystemDefault, NodeKind.Playback, Owner.DefaultOutputName));
            foreach (var endpoint in Owner.Catalog.Endpoints.Where(e => !e.Capture && e.Id != Owner.Graph.Channels.FirstOrDefault(c => c.Id == Owner.Graph.SilentInputChannelId)?.RenderEndpointId))
                candidates.Add(new(endpoint.Name, endpoint.Id, NodeKind.Playback, endpoint.Default ? "Windows 当前默认设备" : "播放设备"));
        }
        var selected = candidates.FirstOrDefault(c => c.Kind == Model.Kind &&
            (Model.Kind == NodeKind.VirtualMicrophone ? c.ChannelId == Model.ChannelId : AppIdentity.Normalize(c.Key) == AppIdentity.Normalize(Model.Key)));
        if (selected == null && IsConfigured)
        {
            selected = new(Name, Model.Key, Model.Kind, "当前选择 · 等待运行／设备连接", Model.ChannelId);
            candidates.Insert(0, selected);
        }
        var signature = Model.Kind + ":" + Model.Key + ":" + Model.ChannelId + "|" +
            string.Join("|", candidates.Select(c => c.Kind + ":" + c.Key + ":" + c.ChannelId + ":" + c.Name + ":" + c.Hint)) + "::" +
            string.Join("|", Owner.Graph.Channels.Select(c => c.Id));
        if (signature == choiceSignature) return;
        choiceSignature = signature;
        refreshing = true;
        try
        {
            Choices.Clear(); foreach (var candidate in candidates) Choices.Add(candidate);
            choice = selected;
            InputChannels.Clear(); foreach (var channel in Owner.Graph.Channels) InputChannels.Add(channel);
        }
        finally { refreshing = false; }
        Changed(null);
    }
    public Point Location
    {
        get => new(Model.X, Model.Y);
        set { if (Location == value) return; Model.X = value.X; Model.Y = value.Y; Changed(); if (!IsDraft) Owner.Edited(false); }
    }
    internal void RefreshLocation() => Changed(nameof(Location));
    public float Volume
    {
        get => Model.Volume;
        set { if (Math.Abs(value - Model.Volume) < .001f) return; Model.Volume = Math.Clamp(value, 0, 1); Model.VolumeManaged = true; Owner.PromoteAudioEdit(this); Changed(); Changed(nameof(VolumeLabel)); Owner.Edited(true); }
    }
    public string VolumeLabel => $"{Model.Volume:P0}";
    public bool Muted { get => Model.Muted; set { if (value == Model.Muted) return; Model.Muted = value; Model.VolumeManaged = true; Owner.PromoteAudioEdit(this); Changed(); Owner.Edited(true); } }
    private float level, inputLevel;
    public float Level { get => level; set => Set(ref level, Math.Clamp(value, 0, 1)); }
    public float InputLevel { get => inputLevel; set => Set(ref inputLevel, Math.Clamp(value, 0, 1)); }
    private string status = "尚未开始", detail = "";
    public string Status { get => status; set => Set(ref status, value); }
    public string Detail { get => detail; set => Set(ref detail, value); }
}
public sealed class ConnectionViewModel(RouteConnection model, PortViewModel source, PortViewModel target) : Observable
{
    public RouteConnection Model { get; } = model;
    public PortViewModel Source { get; } = source;
    public PortViewModel Target { get; } = target;
    public float Volume { get => Source.Owner.Owner.ReadRouteVolume(this); set => Source.Owner.Owner.SetRouteVolume(this, value); }
    public string VolumeLabel => $"{Volume:P0}";
    public bool SharedChannel => Source.Owner.IsVirtual;
    public bool IsPrimary => ApplicationOutputs.IsPrimary(Source.Owner.Model, Target.Owner.Model);
    public bool CanRemove => !IsPrimary;
    public bool IsDisconnected => !Model.Enabled || Target.Owner.Model.Key == AudioOutput.None ||
        Target.Owner.SoftwareCard && Target.Owner.Model.InputDeviceKey == AudioOutput.None;
    public string Description => (Model.Mode == RouteMode.Playback ? $"{Source.Owner.Name} → {Target.Owner.Name}（{(IsPrimary ? "默认原声输出" : "复制声音")}）" : $"{Source.Owner.Name} → {Target.Owner.Name}（声音输入）") + " · " + (Model.Enabled ? VolumeLabel : "已关闭");
    public void RefreshVolume() { Changed(nameof(Volume)); Changed(nameof(VolumeLabel)); Changed(nameof(Description)); RefreshConnectionState(); }
    private bool? lastDisconnected;
    internal void RefreshConnectionState()
    {
        bool value = IsDisconnected;
        if (lastDisconnected == value) return;
        lastDisconnected = value; Changed(nameof(IsDisconnected));
    }
    private bool active;
    public bool Active { get => active; set => Set(ref active, value); }
    private bool flowing;
    public bool Flowing { get => flowing; set => Set(ref flowing, value); }
}
public sealed class RouteLevelViewModel : Observable, IDisposable
{
    public ConnectionViewModel Connection { get; }
    public string Name => Incoming ? Connection.Source.Owner.Name : Connection.Target.Owner.Name;
    public bool Incoming { get; }
    public bool CanRemove => Connection.CanRemove;
    public float Volume { get => Connection.Volume; set => Connection.Volume = value; }
    public string VolumeLabel => Connection.VolumeLabel;
    public string Help => Connection.SharedChannel ? "共享通道音量：使用同一虚拟麦克风的软件会一起变化。" : "仅调整这条连接；来源输出和目标输入显示同一个音量。";
    public RouteLevelViewModel(ConnectionViewModel connection, bool incoming)
    {
        Connection = connection; Incoming = incoming;
        connection.PropertyChanged += ConnectionChanged;
    }
    private void ConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.Volume) or nameof(ConnectionViewModel.VolumeLabel))
        { Changed(nameof(Volume)); Changed(nameof(VolumeLabel)); }
    }
    public void Dispose() => Connection.PropertyChanged -= ConnectionChanged;
}
public sealed class PendingConnectionViewModel : Observable
{
    private PortViewModel? source;
    public PortViewModel? Source { get => source; set { if (Set(ref source, value)) Changed(nameof(Visible)); } }
    public bool Visible => Source != null;
    public ICommand StartCommand { get; }
    public ICommand CompleteCommand { get; }
    public bool WasCanceled { get; set; }
    public PendingConnectionViewModel(MainViewModel owner)
    {
        StartCommand = new Command(value => { WasCanceled = false; Source = value is PortViewModel port ? owner.StartPendingConnection(port) : null; });
        CompleteCommand = new Command(value =>
        {
            if (owner.Reconnecting) owner.CompleteReconnect(value as PortViewModel, WasCanceled);
            else if (!WasCanceled && Source != null && value is PortViewModel target) owner.Connect(Source, target);
            Source = null;
            WasCanceled = false;
        });
    }
}

public sealed partial class MainViewModel : Observable, IAsyncDisposable
{
    public SceneStore Store { get; }
    public AudioEngine Engine { get; }
    private readonly AudioCatalog catalog = new();
    private readonly Dictionary<string, EndpointVolumeBinding> deviceVolumes = [];
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer meterTimer, refreshTimer;
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private WorkspaceState workspace = new();
    private RouteGraph graph
    {
        get => workspace.Configuration;
        set => workspace.Configuration = value;
    }
    public RouteGraph Graph => graph;
    public WorkspaceState Workspace => workspace;
    public bool GlobalView => workspace.View == CanvasView.Global;
    public string ViewLabel => GlobalView ? "全局画布" : "单软件配置";
    public string VersionLabel => "v" + typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public string DefaultOutputName => AudioOutput.DefaultName(Catalog.Endpoints);
    public string ViewHint => GlobalView ? "右键添加软件、麦克风和播放设备 · 两种视图共享配置" : "在应用列表选择配置对象 · 仅显示相关卡片 · 两种视图共享配置";
    public string FocusTitle => GlobalView ? "全部声音连接" : FocusedRoot?.Name ?? "选择要配置的应用";
    public string Counts => $"{CanvasNodes.Count} 张卡片 · {CanvasConnections.Count} 条连线";
    public string LiveSummary => $"已配置 {graph.Connections.Where(c => c.Enabled).Select(c => c.SourceId).Distinct().Count()} · " + (Running ? "声音路由运行中" : Paused ? "已暂停" : "未启用");
    public bool Paused => workspace.RoutingPaused;
    public string RunActionLabel => Running ? "停止声音路由" : "开启声音路由";
    public bool CloseToTray { get => graph.CloseToTray; set { graph.CloseToTray = value; Changed(); Edited(false); } }
    public bool ResumeOnLaunch { get => workspace.RestoreRoutingStateOnLaunch; set { workspace.RestoreRoutingStateOnLaunch = value; Changed(); Edited(false); } }
    public ObservableCollection<NodeViewModel> Nodes { get; } = [];
    public ObservableCollection<NodeViewModel> CanvasNodes { get; } = [];
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<ConnectionViewModel> CanvasConnections { get; } = [];
    public ObservableCollection<NodeViewModel> SelectedNodes { get; } = [];
    public ObservableCollection<ConnectionViewModel> SelectedConnections { get; } = [];
    public ObservableCollection<AvailableItem> Applications { get; } = [];
    public ObservableCollection<VirtualChannel> Channels { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public AudioCatalogSnapshot Catalog { get; private set; } = new([], [], []);
    public PendingConnectionViewModel Pending { get; }
    public NodeViewModel? FocusedRoot { get; private set; }
    private NodeViewModel? draft;
    private NodeViewModel? draftOutput;
    private RouteGraph lastUndo = new();
    private readonly Stack<RouteGraph> undo = [], redo = [];
    private RouteGraph? reconnectBackup;
    private RouteConnection? reconnectOriginal;
    public bool Reconnecting => reconnectOriginal != null;
    private CancellationTokenSource? editDelay;
    private bool rebuilding, refreshing, busy, running, disposed;
    private string notice = "两种视图共享同一份配置；未配置的应用使用系统默认输出。", search = "";
    public bool Busy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool Running { get => running; private set { workspace.LastRoutingEnabled = value; Set(ref running, value); RefreshRunState(); } }
    public string Notice { get => notice; private set => Set(ref notice, value); }
    public string Search { get => search; set { if (Set(ref search, value)) RefreshApplications(); } }
    public bool NoApplications => Applications.Count == 0;
    public string ApplicationEmptyHint => string.IsNullOrWhiteSpace(Search) ?
        "暂无已识别或已配置的音频应用\n可以点击“添加应用”手动选择" : "没有匹配的应用\n可以通过“添加应用”搜索其他软件";
    public Action? OpenSettings { get; set; }
    public Func<IReadOnlyList<AvailableItem>, AvailableItem?>? PickApplication { get; set; }
    public Action? FitRequested { get; set; }
    public Func<NodeViewModel, LayoutCard>? MeasureLayoutCard { get; set; }
    public ICommand ConfigureAppCommand { get; }
    public ICommand AddSoftwareCardCommand { get; }
    public ICommand AddMicrophoneCardCommand { get; }
    public ICommand AddPlaybackCardCommand { get; }
    public ICommand DeleteCardCommand { get; }
    public ICommand CopyInputCommand { get; }
    public ICommand ApplicationViewCommand { get; }
    public ICommand GlobalViewCommand { get; }
    public ICommand RunActionCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand AddApplicationCommand { get; }
    public ICommand ArrangeCommand { get; }

    public MainViewModel(string? directory = null)
    {
        Store = new(directory);
        try { workspace = Store.LoadWorkspace(); } catch (Exception error) { notice = "工作区读取失败：" + error.Message; }
        InitializeConfigurationFiles();
        lastUndo = SceneStore.Clone(graph);
        Engine = new(Store.DataDirectory, AddLog);
        Pending = new(this);
        ConfigureAppCommand = new Command(async value =>
        {
            if (value is AvailableItem app)
            {
                if (GlobalView) await SwitchViewAsync(false);
                FocusApplication(app.Key);
            }
        });
        AddSoftwareCardCommand = new Command(value => AddBlank(NodeKind.Application, value as NodeViewModel));
        AddMicrophoneCardCommand = new Command(value => AddBlank(NodeKind.Microphone, value as NodeViewModel));
        AddPlaybackCardCommand = new Command(value => AddBlank(NodeKind.Playback, value as NodeViewModel));
        DeleteCardCommand = new Command(value => { if (value is NodeViewModel node) DeleteIds(new HashSet<string> { node.Id }, new HashSet<string>()); }, value => value is NodeViewModel node && node.CanDelete);
        CopyInputCommand = new Command(value =>
        {
            if (value is NodeViewModel node && !string.IsNullOrWhiteSpace(node.Model.ChannelId))
            { Clipboard.SetText(node.InputDeviceName); Notice = "已复制录音设备名称，请在目标软件中选择它。"; }
        });
        ApplicationViewCommand = new Command(async _ => await SwitchViewAsync(false), _ => !Busy);
        GlobalViewCommand = new Command(async _ => await SwitchViewAsync(true), _ => !Busy);
        RunActionCommand = new Command(async _ =>
        {
            if (Running) await PauseAsync();
            else await StartAsync();
        }, _ => !Busy);
        PauseCommand = new Command(async _ => await PauseAsync(), _ => Running && !Busy);
        RestoreCommand = new Command(async _ => await RestoreOriginalStateAsync(), _ => !Busy);
        RefreshCommand = new Command(async _ => await RefreshCatalogAsync());
        UndoCommand = new Command(_ => Undo(), _ => undo.Count > 0);
        RedoCommand = new Command(_ => Redo(), _ => redo.Count > 0);
        DeleteCommand = new Command(_ => DeleteIds(SelectedNodes.Select(n => n.Id).ToHashSet(), SelectedConnections.Select(e => e.Model.Id).ToHashSet()));
        ImportCommand = new Command(_ => Import());
        ExportCommand = new Command(_ => Export());
        AddApplicationCommand = new Command(async _ =>
        {
            if (PickApplication?.Invoke(GetApplicationChoices()) is { } selected)
                await AddManualApplicationAsync(selected);
        }, _ => !Busy);
        ArrangeCommand = new Command(_ => Arrange());
        meterTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
        meterTimer.Tick += (_, _) => UpdateMeters();
        refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        refreshTimer.Tick += async (_, _) =>
        {
            if (refreshing || disposed) return;
            refreshing = true;
            try { await RefreshCatalogAsync(); await Engine.RefreshAsync(); }
            catch (Exception error) { AddLog(error.Message); }
            finally { refreshing = false; }
        };
        Rebuild();
        lastUndo = SceneStore.Clone(graph);
    }
    public async Task InitializeAsync()
    {
        await Engine.RecoverAsync(); await RefreshCatalogAsync();
        meterTimer.Start(); refreshTimer.Start();
        if (workspace.RestoreRoutingStateOnLaunch && workspace.LastRoutingEnabled == true) await StartAsync();
    }
    public async Task RefreshCatalogAsync()
    {
        if (disposed) return;
        Catalog = await Task.Run(catalog.Read);
        bool newAudioEvidence = false;
        foreach (var app in Catalog.Applications.Where(a => a.HasAudioSession))
            if (!workspace.KnownAudioApplications.Any(a => AppIdentity.Normalize(a.Path) == app.Key))
            { workspace.KnownAudioApplications.Add(new(app.Name, app.Path)); newAudioEvidence = true; }
        foreach (var app in Catalog.Applications.Where(a => a.HasRecordingSession))
            if (!workspace.KnownRecordingApplications.Any(a => AppIdentity.Normalize(a.Path) == app.Key))
            { workspace.KnownRecordingApplications.Add(new(app.Name, app.Path)); newAudioEvidence = true; }
        foreach (var channel in Catalog.SuggestedChannels)
            if (graph.Channels.All(c => c.RenderEndpointId != channel.RenderEndpointId && c.CaptureEndpointId != channel.CaptureEndpointId)) graph.Channels.Add(channel);
        if (graph.Nodes.Any(n => n.InputDeviceKey == AudioOutput.None))
        {
            try { string? previous = graph.SilentInputChannelId; ReserveQuietInput(); if (previous != graph.SilentInputChannelId) Edited(false); }
            catch (Exception error) { Notice = error.Message; }
        }
        RefreshChannels();
        RefreshApplications();
        foreach (var node in Nodes) { node.RefreshChoices(); node.RefreshRecordingChoices(); }
        if (!GlobalView && workspace.FocusedApplication == null && Applications.FirstOrDefault() is { } first)
            workspace.FocusedApplication = first.Key;
        BuildCanvas();
        RefreshDeviceVolumes();
        Changed(nameof(DefaultOutputName));
        Changed(nameof(ChannelCapacity));
        if (newAudioEvidence) SaveNow();
    }
    private string? PlaybackEndpoint(NodeViewModel node) => AudioOutput.Resolve(node.Model.Key, Catalog.Endpoints)?.Id;
    private void RefreshDeviceVolumes()
    {
        var required = Nodes.Where(n => n.PlaybackCard).Select(PlaybackEndpoint).OfType<string>().ToHashSet();
        foreach (var id in deviceVolumes.Keys.Where(id => !required.Contains(id)).ToArray()) { deviceVolumes[id].Dispose(); deviceVolumes.Remove(id); }
        foreach (var id in required)
        {
            if (!deviceVolumes.ContainsKey(id))
                try
                {
                    var binding = new EndpointVolumeBinding(id); deviceVolumes[id] = binding;
                    binding.Changed += state =>
                    {
                        void Update() { if (!disposed) foreach (var node in Nodes.Where(n => n.PlaybackCard && PlaybackEndpoint(n) == id)) node.UpdateDeviceVolume(state); }
                        if (dispatcher.CheckAccess()) Update(); else dispatcher.BeginInvoke(Update);
                    };
                }
                catch (Exception error) { AddLog("设备音量未就绪：" + error.Message); }
        }
        foreach (var node in Nodes.Where(n => n.PlaybackCard))
        {
            EndpointVolumeState? state = null;
            try { if (PlaybackEndpoint(node) is { } id && deviceVolumes.TryGetValue(id, out var binding)) state = binding.State; } catch { }
            node.UpdateDeviceVolume(state);
        }
    }
    public void SetDeviceVolume(NodeViewModel node, float volume)
    {
        try { if (PlaybackEndpoint(node) is { } id && deviceVolumes.TryGetValue(id, out var binding)) { Engine.SetDeviceVolume(binding, volume); node.UpdateDeviceVolume(binding.State); } }
        catch (Exception error) { Notice = "设备音量修改失败：" + error.Message; }
    }
    public void SetDeviceMuted(NodeViewModel node, bool muted)
    {
        try { if (PlaybackEndpoint(node) is { } id && deviceVolumes.TryGetValue(id, out var binding)) { Engine.SetDeviceMuted(binding, muted); node.UpdateDeviceVolume(binding.State); } }
        catch (Exception error) { Notice = "设备静音修改失败：" + error.Message; }
    }
    public float ReadRouteVolume(ConnectionViewModel connection) => connection.Source.Owner.IsVirtual ?
        graph.Channels.FirstOrDefault(c => c.Id == connection.Source.Owner.Model.ChannelId)?.Volume ?? 1 : connection.Model.Volume;
    private string? DestinationKey(RouteNode target) => target.Kind == NodeKind.Playback ? AudioOutput.Resolve(target.Key, Catalog.Endpoints)?.Id : graph.Channels.FirstOrDefault(c => c.Id == target.ChannelId)?.RenderEndpointId;
    public void SetRouteVolume(ConnectionViewModel connection, float value)
    {
        if (connection.Source.Owner.IsDraft)
        {
            var source = connection.Source.Owner;
            PromoteAudioEdit(source);
            connection = Connections.First(c => c.Source.Owner == source && c.IsPrimary);
        }
        value = Math.Clamp(value, 0, 1);
        if (Math.Abs(ReadRouteVolume(connection) - value) < .001f) return;
        if (connection.Source.Owner.IsVirtual)
        {
            if (graph.Channels.FirstOrDefault(c => c.Id == connection.Source.Owner.Model.ChannelId) is not { } channel) return;
            channel.Volume = value;
        }
        else
        {
            var endpoint = DestinationKey(connection.Target.Owner.Model);
            foreach (var item in Connections.Where(c => c.Source.Owner.Id == connection.Source.Owner.Id &&
                (c.Model.Id == connection.Model.Id || !string.IsNullOrWhiteSpace(endpoint) && DestinationKey(c.Target.Owner.Model) == endpoint))) item.Model.Volume = value;
        }
        foreach (var item in Connections) item.RefreshVolume();
        Edited(true);
    }
    private void RefreshChannels()
    {
        Channels.Clear(); foreach (var channel in graph.Channels) Channels.Add(channel);
    }
    public void SetDefaultOutputEnabled(NodeViewModel node, bool enabled)
    {
        if (!node.Model.PrimaryOutput || node.DefaultOutputEnabled == enabled) return;
        string key = enabled ? node.Model.LastOutputKey ?? AudioOutput.SystemDefault : AudioOutput.None;
        string name = key == AudioOutput.None ? "不进行输出" : key == AudioOutput.SystemDefault ? "Windows 默认输出" : Catalog.Endpoints.FirstOrDefault(e => e.Id == key)?.Name ?? "上次输出设备 · 等待连接";
        ChooseCard(node, new(name, key, NodeKind.Playback, "默认输出"));
    }
    private bool Configured(string key)
    {
        return graph.Nodes.Any(n => n.Kind == NodeKind.Application && AppIdentity.Normalize(n.Key) == AppIdentity.Normalize(key));
    }
    private IEnumerable<SavedApplication> ConfiguredApplications => graph.Nodes.Where(n => n.Kind == NodeKind.Application && !string.IsNullOrWhiteSpace(n.Key))
        .Select(n => new SavedApplication(n.Name, n.Key));
    private void RefreshApplications()
    {
        Applications.Clear();
        foreach (var app in ApplicationSelection.Sidebar(Catalog.Applications, workspace.ManualApplications, ConfiguredApplications, workspace.KnownAudioApplications, Search))
            Applications.Add(new(app.Name, app.Path, NodeKind.Application,
                Configured(app.Path) ? (app.HasAudio ? "已配置 · 使用音频中" : app.ProcessIds.Count > 0 ? "已配置 · 暂无音频" : "已配置 · 等待运行") :
                app.HasAudio ? "使用系统默认输出" : app.HasAudioSession || workspace.KnownAudioApplications.Any(a => AppIdentity.Normalize(a.Path) == app.Key) ?
                "暂无音频 · 使用系统默认输出" : app.ProcessIds.Count > 0 ? "手动添加 · 使用系统默认输出" : "手动添加 · 等待运行"));
        Changed(nameof(NoApplications)); Changed(nameof(ApplicationEmptyHint));
    }
    public IReadOnlyList<AudioApplication> GetApplicationCandidates() => ApplicationSelection.Candidates(Catalog.Applications,
        workspace.ManualApplications.Concat(ConfiguredApplications).Concat(workspace.KnownAudioApplications));
    public IReadOnlyList<AvailableItem> GetApplicationChoices() => GetApplicationCandidates().Select(app =>
        new AvailableItem(app.Name, app.Path, NodeKind.Application, app.Peak > .001f ? "正在播放声音" :
            app.HasAudio ? "正在使用音频" : app.ProcessIds.Count > 0 ? "正在运行 · 暂无音频" : "已保存 · 等待运行")).ToList();
    public async Task AddManualApplicationAsync(AvailableItem app)
    {
        if (app.Kind != NodeKind.Application || string.IsNullOrWhiteSpace(app.Key)) return;
        var key = AppIdentity.Normalize(app.Key);
        if (!workspace.ManualApplications.Any(a => AppIdentity.Normalize(a.Path) == key))
            workspace.ManualApplications.Add(new(app.Name, app.Key));
        if (GlobalView) await SwitchViewAsync(false);
        RefreshApplications(); FocusApplication(app.Key);
    }
    public void FocusApplication(string key)
    {
        CancelPendingConnection();
        workspace.FocusedApplication = key; draft = null; BuildCanvas(); SaveNow(); FitRequested?.Invoke();
        Notice = "正在配置 " + FocusTitle + "；默认输出卡片选择原声设备，新增输出连线复制声音。";
    }
    public async Task SwitchViewAsync(bool global)
    {
        await configurationGate.WaitAsync();
        try
        {
            if (GlobalView == global) return;
            Busy = true; CancelPendingConnection();
            workspace.View = global ? CanvasView.Global : CanvasView.PerApplication;
            draft = null; BuildCanvas(); SaveNow();
            Notice = "已打开" + ViewLabel + "，两种视图共享同一份配置。";
            Changed(nameof(GlobalView)); Changed(nameof(ViewLabel)); Changed(nameof(ViewHint));
            RefreshRunState();
            FitRequested?.Invoke();
        }
        catch (Exception error) { Notice = "无法打开配置：" + error.Message; AddLog(Notice); }
        finally { Busy = false; configurationGate.Release(); }
    }
    private void BuildCanvas()
    {
        if (rebuilding) return;
        rebuilding = true;
        try
        {
            var visibleNodes = new List<NodeViewModel>();
            var visibleEdges = new List<ConnectionViewModel>();
            if (GlobalView)
            {
                FocusedRoot = null; visibleNodes.AddRange(Nodes); visibleEdges.AddRange(Connections);
            }
            else
            {
                var focus = AppIdentity.Normalize(workspace.FocusedApplication ?? "");
                FocusedRoot = Nodes.FirstOrDefault(n => n.SoftwareCard && AppIdentity.Normalize(n.Model.Key) == focus);
                if (FocusedRoot == null && focus.Length > 0)
                {
                    var app = Catalog.Applications.FirstOrDefault(a => a.Key == focus);
                    draft ??= new NodeViewModel(new()
                    {
                        Id = "draft-" + Guid.NewGuid().ToString("N"), Kind = NodeKind.Application,
                        Key = workspace.FocusedApplication!, Name = app?.Name ?? workspace.ManualApplications.FirstOrDefault(a => AppIdentity.Normalize(a.Path) == focus)?.Name ?? Path.GetFileNameWithoutExtension(workspace.FocusedApplication) ?? "未运行的应用",
                        X = 280, Y = 230
                    }, this, true);
                    FocusedRoot = draft;
                }
                if (FocusedRoot != null)
                {
                    var ids = new HashSet<string> { FocusedRoot.Id };
                    foreach (var node in Nodes.Where(n => AppIdentity.Normalize(n.Model.OwnerAppKey ?? "") == focus)) ids.Add(node.Id);
                    foreach (var edge in Connections.Where(e => e.Model.SourceId == FocusedRoot.Id || e.Model.TargetId == FocusedRoot.Id))
                    { ids.Add(edge.Model.SourceId); ids.Add(edge.Model.TargetId); }
                    foreach (var virtualNode in Nodes.Where(n => n.IsVirtual && ids.Contains(n.Id)).ToArray())
                        foreach (var edge in Connections.Where(e => e.Model.SourceId == virtualNode.Id || e.Model.TargetId == virtualNode.Id))
                        { ids.Add(edge.Model.SourceId); ids.Add(edge.Model.TargetId); }
                    visibleNodes.AddRange(Nodes.Where(n => ids.Contains(n.Id)));
                    if (FocusedRoot.IsDraft)
                    {
                        visibleNodes.Add(FocusedRoot);
                        if (draftOutput?.Model.OwnerAppKey != FocusedRoot.Model.Key)
                            draftOutput = new(new() { Id = "output-" + FocusedRoot.Id, Kind = NodeKind.Playback,
                                PrimaryOutput = true, OwnerAppKey = FocusedRoot.Model.Key, Name = "Windows 默认输出",
                                Key = AudioOutput.SystemDefault, X = FocusedRoot.Model.X + 410, Y = FocusedRoot.Model.Y }, this, true);
                        visibleNodes.Add(draftOutput);
                        var edge = new ConnectionViewModel(new() { Id = "route-" + FocusedRoot.Id, SourceId = FocusedRoot.Id,
                            TargetId = draftOutput.Id, Mode = RouteMode.Playback, Automatic = true }, FocusedRoot.Outputs[0], draftOutput.Inputs[0]);
                        // Reuse the preview line across inventory refreshes to preserve selection and anchors.
                        visibleEdges.Add(CanvasConnections.FirstOrDefault(c => c.Model.Id == edge.Model.Id) ?? edge);
                        draftOutput.RefreshChoices();
                        FocusedRoot.SetCounts(0, 1); draftOutput.SetCounts(1, 0);
                        FocusedRoot.SetRouteLevels(visibleEdges); draftOutput.SetRouteLevels(visibleEdges);
                    }
                    visibleEdges.AddRange(Connections.Where(e => ids.Contains(e.Model.SourceId) && ids.Contains(e.Model.TargetId)));
                }
            }
            // Refreshing the inventory must not interrupt dragging, dropdowns
            // or selection when the visible graph has not changed.
            if (!CanvasNodes.SequenceEqual(visibleNodes))
            {
                SelectedNodes.Clear(); CanvasNodes.Clear();
                foreach (var node in visibleNodes) CanvasNodes.Add(node);
            }
            if (!CanvasConnections.SequenceEqual(visibleEdges))
            {
                SelectedConnections.Clear(); CanvasConnections.Clear();
                foreach (var edge in visibleEdges) CanvasConnections.Add(edge);
            }
            Changed(nameof(FocusTitle)); Changed(nameof(Counts)); Changed(nameof(LiveSummary)); Changed(nameof(ViewHint));
            foreach (var node in Nodes.Concat(visibleNodes).Distinct()) node.RefreshProtection();
        }
        finally { rebuilding = false; }
    }
    private void CommitDraft()
    {
        if (FocusedRoot?.IsDraft != true) return;
        FocusedRoot.Model.Id = Guid.NewGuid().ToString("N");
        FocusedRoot.IsDraft = false; graph.Nodes.Add(FocusedRoot.Model); draft = null;
        if (!Nodes.Contains(FocusedRoot)) Nodes.Add(FocusedRoot);
        EnsureDefaultOutput(FocusedRoot.Model);
    }
    public void PromoteAudioEdit(NodeViewModel node)
    {
        if (!node.IsDraft) return;
        CommitDraft();
        if (!Nodes.Contains(node)) Nodes.Add(node);
        RebuildConnections(); BuildCanvas();
    }
    private void EnsureDefaultOutput(RouteNode source)
    {
        ApplicationOutputs.Ensure(graph);
        foreach (var model in graph.Nodes.Where(n => Nodes.All(v => v.Id != n.Id))) Nodes.Add(new(model, this));
        foreach (var node in Nodes.Where(n => graph.Nodes.All(m => m.Id != n.Id)).ToArray()) Nodes.Remove(node);
    }
    public NodeViewModel AddBlank(NodeKind kind, NodeViewModel? anchor = null)
    {
        if (!GlobalView) CommitDraft();
        var root = anchor ?? FocusedRoot;
        double x = root?.Model.X + 340 ?? 110 + graph.Nodes.Count % 3 * 350;
        double y = root?.Model.Y + 110 ?? 100 + graph.Nodes.Count / 3 * 230;
        while (graph.Nodes.Any(n => Math.Abs(n.X - x) < 40 && Math.Abs(n.Y - y) < 130)) y += 190;
        var node = new RouteNode
        {
            Kind = kind, Name = kind == NodeKind.Application ? "选择软件" : kind == NodeKind.Microphone ? "选择麦克风" : "选择播放设备",
            X = x, Y = y, OwnerAppKey = GlobalView ? null : workspace.FocusedApplication
        };
        graph.Nodes.Add(node); Rebuild(); Edited(true);
        Notice = "卡片已添加：在卡片内选择具体对象，再从输出端拖线到输入端。";
        return Nodes.First(n => n.Id == node.Id);
    }
    public NodeViewModel AddItem(AvailableItem item)
    {
        var existing = Nodes.FirstOrDefault(n => n.Model.Kind == item.Kind && AppIdentity.Normalize(n.Model.Key) == AppIdentity.Normalize(item.Key));
        if (existing != null) return existing;
        var added = AddBlank(item.Kind);
        ChooseCard(added, new(item.Name, item.Key, item.Kind, "已选择"));
        return Nodes.First(n => n.Id == added.Id);
    }
    public NodeViewModel AddChannel(VirtualChannel channel)
    {
        var existing = Nodes.FirstOrDefault(n => n.IsVirtual && n.Model.ChannelId == channel.Id);
        if (existing != null) return existing;
        var node = AddBlank(NodeKind.Microphone);
        ChooseCard(node, new(channel.Name, "", NodeKind.VirtualMicrophone, "虚拟麦克风", channel.Id));
        return Nodes.First(n => n.Id == node.Id);
    }
    public void ChooseCard(NodeViewModel node, CardChoice value)
    {
        if (!node.CanChooseCard) { Notice = "当前软件由左侧应用列表选择；添加其他软件请右键添加卡片。"; return; }
        if (node.IsDraft && node.Model.PrimaryOutput)
        {
            CommitDraft(); RebuildConnections(); BuildCanvas();
            node = Nodes.First(n => n.Model.PrimaryOutput && AppIdentity.Normalize(n.Model.OwnerAppKey ?? "") == AppIdentity.Normalize(workspace.FocusedApplication ?? ""));
        }
        var backup = SceneStore.Clone(graph);
        try
        {
            if (node.IsDraft) { FocusApplication(value.Key); return; }
            if (value.Kind == NodeKind.Application && graph.Nodes.Any(n => n.Id != node.Id &&
                n.Kind == NodeKind.Application && AppIdentity.Normalize(n.Key) == AppIdentity.Normalize(value.Key)))
                throw new GraphValidationException("已经有该软件卡片，请使用现有卡片。");
            if (node.Model.PrimaryOutput && (value.Kind != NodeKind.Playback || string.IsNullOrWhiteSpace(value.Key)))
                throw new GraphValidationException("默认输出卡片只能选择播放设备。");
            var oldKind = node.Model.Kind; var oldChannel = node.Model.ChannelId; var oldKey = node.Model.Key;
            node.Model.Kind = value.Kind; node.Model.Key = value.Key; node.Model.Name = value.Name;
            if (node.Model.PrimaryOutput)
            {
                if (value.Key == AudioOutput.None && oldKey != AudioOutput.None) node.Model.LastOutputKey = oldKey;
                if (value.Key != AudioOutput.None) node.Model.LastOutputKey = value.Key;
                foreach (var line in graph.Connections.Where(c => c.TargetId == node.Id)) line.Enabled = value.Key != AudioOutput.None;
            }
            if (oldKind == NodeKind.Application)
                foreach (var output in graph.Nodes.Where(n => n.PrimaryOutput && AppIdentity.Normalize(n.OwnerAppKey ?? "") == AppIdentity.Normalize(oldKey))) output.OwnerAppKey = value.Key;
            if (!GlobalView && value.Kind == NodeKind.Microphone && oldKind == NodeKind.Microphone &&
                string.IsNullOrWhiteSpace(backup.Nodes.First(n => n.Id == node.Id).Key) && FocusedRoot is { } focusRoot)
            {
                node.Model.X = focusRoot.Model.X - 390; node.Model.Y = focusRoot.Model.Y + 35;
            }
            if (value.Kind == NodeKind.VirtualMicrophone) node.Model.ChannelId = value.ChannelId;
            else if (oldKind != value.Kind) node.Model.ChannelId = null;
            int removed = 0;
            foreach (var edge in graph.Connections.Where(c => c.SourceId == node.Id || c.TargetId == node.Id).ToArray())
            {
                var source = graph.Nodes.First(n => n.Id == edge.SourceId); var target = graph.Nodes.First(n => n.Id == edge.TargetId);
                if (source.Kind == NodeKind.VirtualMicrophone && target.Kind == NodeKind.Application &&
                    target.ChannelId == oldChannel && !target.InputChannelPinned) target.ChannelId = source.ChannelId;
                try { GraphCompiler.ValidateConnection(source, target, edge.Mode); }
                catch (GraphValidationException) { graph.Connections.Remove(edge); removed++; }
            }
            EnsureDefaultOutput(node.Model);
            GraphCompiler.Compile(graph, Catalog.Endpoints);
            node.RefreshPorts(); node.RefreshChoices(); RebuildConnections(); BuildCanvas(); Edited(true);
            RefreshDeviceVolumes();
            Notice = removed > 0 ? "选择已更新，不兼容的旧连线已移除，请重新连接。" : "已选择 " + value.Name + "。";
        }
        catch (Exception error) { graph = backup; Rebuild(); Notice = error.Message; }
    }
    public void ChooseInputChannel(NodeViewModel node, VirtualChannel channel)
    {
        var backup = SceneStore.Clone(graph);
        try
        {
            node.Model.ChannelId = channel.Id; node.Model.InputChannelPinned = true;
            node.Model.InputDeviceKey = channel.CaptureEndpointId; node.Model.RecordingConfigured = true;
            graph.Connections.RemoveAll(c => c.TargetId == node.Id &&
                graph.Nodes.First(n => n.Id == c.SourceId).Kind == NodeKind.VirtualMicrophone &&
                graph.Nodes.First(n => n.Id == c.SourceId).ChannelId != channel.Id);
            GraphCompiler.Compile(graph, Catalog.Endpoints); node.RefreshChoices(); RebuildConnections(); BuildCanvas(); Edited(true);
            Notice = "已选择输入通道。使用同一通道的软件会接收同一份混音。";
        }
        catch (Exception error) { graph = backup; Rebuild(); Notice = error.Message; }
    }
    public void Connect(PortViewModel from, PortViewModel to)
        => TryConnect(from, to, 1, true);
    private bool TryConnect(PortViewModel from, PortViewModel to, float volume, bool history)
    {
        if (!from.Output && to.Output) (from, to) = (to, from);
        if (!from.Output || to.Output) { Notice = "从右侧声音输出端，连接到左侧声音输入端。"; return false; }
        if (!from.Owner.IsConfigured || !to.Owner.IsConfigured) { Notice = "请先在卡片中选择软件或麦克风／设备。"; return false; }
        var backup = SceneStore.Clone(graph);
        try
        {
            CommitDraft();
            if (FocusedRoot != null && !Nodes.Contains(FocusedRoot)) Nodes.Add(FocusedRoot);
            var source = from.Owner.Model; var target = to.Owner.Model;
            if (target.PrimaryOutput && !ApplicationOutputs.IsPrimary(source, target))
                throw new GraphValidationException("默认输出卡片只接收它所属软件的原声；复制声音请添加播放设备卡片。");
            var existing = graph.Connections.FirstOrDefault(c => c.SourceId == source.Id && c.TargetId == target.Id);
            if (existing != null)
            {
                if (target.PrimaryOutput || !existing.Automatic) return false;
                existing.Automatic = false; existing.Volume = volume;
                RebuildConnections(); BuildCanvas(); Edited(history); return true;
            }
            if (target.Kind == NodeKind.Application)
            {
                if (source.Kind == NodeKind.VirtualMicrophone)
                {
                    if (!string.IsNullOrWhiteSpace(target.ChannelId) && target.ChannelId != source.ChannelId &&
                        (target.InputChannelPinned || graph.Connections.Any(c => c.TargetId == target.Id)))
                        throw new GraphValidationException("该软件已使用另一输入通道，先在卡片中更改或断开原输入连线。");
                    target.ChannelId = source.ChannelId;
                }
                else if (string.IsNullOrWhiteSpace(target.ChannelId))
                {
                    var available = ChannelPool.Free(graph, Catalog.Endpoints).FirstOrDefault();
                    if (available == null) available = PickSharedChannel?.Invoke(ChannelPool.Connected(graph, Catalog.Endpoints).Where(c => !c.RequiresValidation && c.Id != graph.SilentInputChannelId).ToArray());
                    if (available == null) throw new GraphValidationException("独立虚拟麦克风已用完。" + ChannelCapacity + "。请选择共享混音或安装更多虚拟设备。");
                    target.ChannelId = available.Id;
                }
                target.InputDeviceKey = graph.Channels.FirstOrDefault(c => c.Id == target.ChannelId)?.CaptureEndpointId;
                target.RecordingConfigured = true;
            }
            var mode = target.Kind == NodeKind.Playback ? RouteMode.Playback : RouteMode.MicrophoneFeed;
            GraphCompiler.ValidateConnection(source, target, mode);
            graph.Connections.Add(new() { SourceId = source.Id, TargetId = target.Id, Mode = mode, Volume = volume });
            GraphCompiler.Compile(graph, Catalog.Endpoints);
            RebuildConnections(); BuildCanvas(); Edited(history);
            Notice = target.Kind == NodeKind.Playback ? source.Name + " → " + target.Name + "：播放输出" :
                source.Name + " → " + target.Name + "：声音输入。目标软件需选择卡片上显示的录音设备。";
            return true;
        }
        catch (Exception error) { graph = backup; draft = null; Rebuild(); Notice = error.Message; return false; }
    }
    public void RemoveConnection(ConnectionViewModel connection)
    {
        if (!connection.CanRemove) { Notice = "默认输出连线不能删除；在默认输出卡片中更换设备。"; return; }
        if (!graph.Connections.Remove(connection.Model)) return;
        RebuildConnections(); BuildCanvas(); Edited(true); Notice = "已断开连接。";
    }
    public PortViewModel StartPendingConnection(PortViewModel port)
    {
        if (Reconnecting) CompleteReconnect(null, true);
        if (port.Output) return port;
        var candidates = Connections.Where(c => c.Target == port).ToList();
        var chosen = candidates.Count == 1 ? candidates[0] : SelectedConnections.FirstOrDefault(c => candidates.Contains(c));
        if (chosen == null || chosen.IsPrimary) return port;
        reconnectBackup = SceneStore.Clone(graph); reconnectOriginal = chosen.Model;
        var source = chosen.Source;
        graph.Connections.Remove(chosen.Model); RebuildConnections(); BuildCanvas(); Edited(false);
        Notice = "已移走连接：拖到新的输入端，放在空白处断开，Esc 恢复。";
        return source;
    }
    public PortViewModel PendingSource(PortViewModel port)
    {
        if (port.Output) return port;
        var candidates = Connections.Where(c => c.Target == port).ToList();
        var chosen = candidates.Count == 1 ? candidates[0] : SelectedConnections.FirstOrDefault(c => candidates.Contains(c));
        return chosen?.IsPrimary == false ? chosen.Source : port;
    }
    public void CompleteReconnect(PortViewModel? target, bool canceled)
    {
        if (reconnectOriginal == null || reconnectBackup == null) return;
        var original = reconnectOriginal; var backup = reconnectBackup;
        reconnectOriginal = null; reconnectBackup = null;
        bool connected = false;
        if (!canceled && target != null && Nodes.FirstOrDefault(n => n.Id == original.SourceId)?.Outputs.FirstOrDefault() is { } source)
            connected = TryConnect(source, target, original.Volume, false);
        if (canceled || target != null && !connected)
        {
            graph.Connections.Add(original); RebuildConnections(); BuildCanvas(); Edited(false);
            Notice = "原连接已恢复。"; return;
        }
        undo.Push(backup); redo.Clear(); lastUndo = SceneStore.Clone(graph); CommandManager.InvalidateRequerySuggested();
        Notice = connected ? "连接已移动，原来的音量已保留。" : "连接已断开。";
    }
    private void CancelPendingConnection()
    {
        if (Reconnecting) CompleteReconnect(null, true);
        Pending.Source = null;
        Pending.WasCanceled = false;
    }
    private void Rebuild()
    {
        ApplicationOutputs.Ensure(graph);
        Nodes.Clear(); SelectedNodes.Clear(); SelectedConnections.Clear();
        foreach (var node in graph.Nodes) Nodes.Add(new(node, this));
        RefreshChannels(); RebuildConnections(); RefreshApplications(); BuildCanvas();
        RefreshDeviceVolumes();
    }
    private void RebuildConnections()
    {
        Connections.Clear();
        foreach (var edge in graph.Connections)
        {
            var source = Nodes.FirstOrDefault(n => n.Id == edge.SourceId)?.Outputs.FirstOrDefault();
            var target = Nodes.FirstOrDefault(n => n.Id == edge.TargetId)?.Inputs.FirstOrDefault();
            if (source != null && target != null) Connections.Add(new(edge, source, target));
        }
        foreach (var node in Nodes)
        {
            node.SetCounts(graph.Connections.Count(c => c.TargetId == node.Id && c.Enabled), graph.Connections.Count(c => c.SourceId == node.Id && c.Enabled));
            node.SetRouteLevels(Connections);
            node.RefreshDefaultOutput();
            node.RefreshRecordingChoices();
        }
    }
    public void Edited(bool history)
    {
        if (disposed || rebuilding) return;
        if (history)
        {
            undo.Push(lastUndo); if (undo.Count > 80) { var kept = undo.Take(80).Reverse().ToArray(); undo.Clear(); foreach (var entry in kept) undo.Push(entry); }
            redo.Clear(); lastUndo = SceneStore.Clone(graph);
        }
        RefreshApplications(); Changed(nameof(Counts)); Changed(nameof(LiveSummary)); CommandManager.InvalidateRequerySuggested();
        Changed(nameof(ChannelCapacity)); SaveState = "保存中…";
        CancelScheduledEdit();
        editDelay = new CancellationTokenSource();
        _ = SaveEditAsync(editDelay.Token);
    }
    private void CancelScheduledEdit()
    {
        editDelay?.Cancel(); editDelay?.Dispose(); editDelay = null;
    }
    private async Task SaveEditAsync(CancellationToken token)
    {
        bool entered = false;
        try
        {
            await Task.Delay(50, token); await configurationGate.WaitAsync(token); entered = true;
            token.ThrowIfCancellationRequested(); SaveAll();
            if (Running)
            {
                await Engine.ApplyAsync(SceneStore.Clone(graph));
                Notice = SaveState.StartsWith("保存失败", StringComparison.Ordinal) ? "修改已生效，但文件" + SaveState : "修改已实时生效。";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Notice = "配置更新失败：" + error.Message; }
        finally { if (entered) configurationGate.Release(); }
    }
    private void Undo()
    {
        if (undo.Count == 0) return;
        CancelPendingConnection();
        redo.Push(SceneStore.Clone(graph)); graph = SceneStore.Clone(undo.Pop()); lastUndo = SceneStore.Clone(graph); draft = null;
        Rebuild(); Edited(false);
    }
    private void Redo()
    {
        if (redo.Count == 0) return;
        CancelPendingConnection();
        undo.Push(SceneStore.Clone(graph)); graph = SceneStore.Clone(redo.Pop()); lastUndo = SceneStore.Clone(graph); draft = null;
        Rebuild(); Edited(false);
    }
    private void DeleteIds(ISet<string> ids, ISet<string> edges)
    {
        if (ids.Count == 0 && edges.Count == 0) return;
        var removable = Nodes.Where(n => ids.Contains(n.Id) && n.CanDelete).Select(n => n.Id).ToHashSet();
        foreach (var output in graph.Nodes.Where(n => n.PrimaryOutput && graph.Nodes.Any(a => removable.Contains(a.Id) &&
            a.Kind == NodeKind.Application && AppIdentity.Normalize(a.Key) == AppIdentity.Normalize(n.OwnerAppKey ?? "")))) removable.Add(output.Id);
        ids = removable;
        edges = Connections.Where(c => edges.Contains(c.Model.Id) && c.CanRemove).Select(c => c.Model.Id).ToHashSet();
        if (ids.Count == 0 && edges.Count == 0) { Notice = "当前软件、默认输出卡片和默认连线不能删除。"; return; }
        graph.Nodes.RemoveAll(n => ids.Contains(n.Id));
        graph.Connections.RemoveAll(c => ids.Contains(c.SourceId) || ids.Contains(c.TargetId) || edges.Contains(c.Id));
        draft = null; Rebuild(); Edited(true);
    }
    public void Arrange()
    {
        var cards = CanvasNodes.Select(node => MeasureLayoutCard?.Invoke(node) ?? new LayoutCard(node.Id, node.Name, node.Model.Kind, node.Model.PrimaryOutput,
            320, (node.SoftwareCard ? 345 + (node.Receiving ? 90 : 0) + (node.ShowRecordingControls ? 175 : 0) : 330) + (node.InputLevels.Count + node.OutputLevels.Count) * 76, 140, 140)).ToArray();
        var arranged = CanvasLayout.Arrange(cards, CanvasConnections.Select(c => c.Model).ToArray());
        foreach (var node in CanvasNodes)
            if (arranged.Positions.TryGetValue(node.Id, out var point)) { node.Model.X = point.X; node.Model.Y = point.Y; node.RefreshLocation(); }
        Edited(true); FitRequested?.Invoke();
        Notice = "已整理画布：按声音方向分组，并减少连线交叉和遮挡。";
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "导入声音配置", Filter = "声音配置 (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try { ImportConfiguration(Store.Load(dialog.FileName)); }
        catch (Exception error) { Notice = error.Message; }
    }
    public void ImportConfiguration(RouteGraph configuration)
    {
        var imported = SceneStore.Clone(configuration);
        ApplicationOutputs.Ensure(imported);
        GraphCompiler.Compile(imported, Catalog.Endpoints);
        CancelPendingConnection(); undo.Push(SceneStore.Clone(graph)); redo.Clear();
        graph = imported; lastUndo = SceneStore.Clone(graph); draft = null;
        Rebuild(); Edited(false);
        Changed(nameof(CloseToTray)); Changed(nameof(ResumeOnLaunch));
        Notice = "声音配置已导入，两个视图同步更新。";
    }
    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "导出声音配置", Filter = "声音配置 (*.json)|*.json", FileName = "声音配置.json" };
        if (dialog.ShowDialog() == true)
            try { Store.Save(graph, dialog.FileName); Notice = "配置已导出。"; } catch (Exception error) { Notice = error.Message; }
    }
    public async Task StartAsync()
    {
        await configurationGate.WaitAsync(); Busy = true;
        try
        {
            CancelScheduledEdit();
            var selected = SceneStore.Clone(graph); GraphCompiler.Compile(selected, Catalog.Endpoints);
            SaveNow();
            await Engine.ApplyAsync(selected);
            workspace.RoutingPaused = false;
            Running = Engine.Running; SaveNow();
            Notice = "声音路由已运行，两种视图的修改都会实时生效。";
        }
        catch (Exception error) { Running = Engine.Running; Notice = "无法启用：" + error.Message; AddLog(Notice); }
        finally { Busy = false; RefreshRunState(); configurationGate.Release(); }
    }
    public async Task PauseAsync()
    {
        await configurationGate.WaitAsync(); Busy = true;
        try
        {
            CancelScheduledEdit();
            await Engine.StopAndRestoreAsync(); workspace.RoutingPaused = true; Running = Engine.Running;
            SaveNow(); Notice = RecoveryNotice("已暂停路由并恢复应用原来的 Windows 输出，配置已保存。");
        }
        catch (Exception error) { Notice = "恢复失败：" + error.Message; }
        finally { Busy = false; RefreshRunState(); configurationGate.Release(); }
    }
    public async Task RestoreOriginalStateAsync()
    {
        await configurationGate.WaitAsync(); Busy = true;
        try
        {
            CancelScheduledEdit(); CancelPendingConnection();
            await Engine.StopAndRestoreAsync(restoreDevices: true);
            workspace.RoutingPaused = true; Running = Engine.Running;
            RefreshDeviceVolumes(); SaveNow();
            Notice = RecoveryNotice("已恢复声流介入前的应用输出、音量与静音，配置已保留。");
        }
        catch (Exception error) { Running = Engine.Running; Notice = "部分设置未能恢复：" + error.Message; AddLog(Notice); }
        finally { Busy = false; RefreshRunState(); configurationGate.Release(); }
    }
    private string RecoveryNotice(string complete) => Engine.PendingRecoveryCount == 0 ? complete :
        $"声音路由已停止，配置已保留。还有 {Engine.PendingRecoveryCount} 项设置等待软件再次播放或设备重连后自动恢复。";
    private void RefreshRunState()
    {
        Changed(nameof(Paused)); Changed(nameof(RunActionLabel)); Changed(nameof(LiveSummary));
        if (!disposed && Engine != null) UpdateMeters();
        CommandManager.InvalidateRequerySuggested();
    }
    private void UpdateMeters()
    {
        if (disposed) return;
        var snapshot = Engine.Snapshot();
        foreach (var node in Nodes)
        {
            node.InputLevel = 0;
            var level = snapshot.Levels.FirstOrDefault(l => l.NodeId == node.Id)?.Peak;
            node.Level = node.AudioSource ? level ?? (Catalog.Applications.FirstOrDefault(a => a.Key == AppIdentity.Normalize(node.Model.Key))?.Peak ?? 0) : 0;
            var status = snapshot.Statuses.FirstOrDefault(s => s.NodeId == node.Id);
            node.Status = node.OutputDisabled || node.HasDefaultOutput && !node.DefaultOutputEnabled ? "默认输出已关闭" : !node.IsConfigured ? node.PickerHint : !Running ? (Paused ? "路由已暂停" : node.UseSystemOutput ? "使用系统默认输出" : "配置已保存 · 尚未启用")
                : status?.State switch { "Active" => "声音路由中", "Waiting" => "等待软件运行", "Restart" => "请重启该软件", "Error" => "连接未就绪", _ => node.UseSystemOutput ? "使用系统默认输出" : "通道已配置" };
            if (status != null) node.Detail = status.Message;
            node.RecordingStatus = Engine.InputStatus(node.Id);
        }
        foreach (var edge in Connections)
        {
            edge.RefreshConnectionState();
            bool enabled = edge.Model.Enabled && !edge.Source.Owner.Muted;
            var channelVolume = edge.Model.Mode == RouteMode.MicrophoneFeed ? graph.Channels.FirstOrDefault(c => c.Id == edge.Target.Owner.Model.ChannelId)?.Volume ?? 1 : 1;
            float incoming = Running && enabled ? edge.Source.Owner.Level * edge.Model.Volume * channelVolume : 0;
            if (edge.Target.Owner.SoftwareCard) edge.Target.Owner.InputLevel += incoming;
            else { edge.Target.Owner.InputLevel += incoming; edge.Target.Owner.Level += incoming; }
            edge.Active = Running && incoming > .001f;
            edge.Flowing = Running && edge.Model.Enabled && edge.Target.Owner.Model.InputDeviceKey != AudioOutput.None &&
                edge.Source.Owner.IsConfigured && edge.Target.Owner.IsConfigured && edge.Source.Owner.Status != "连接未就绪";
        }
        foreach (var edge in Connections.Where(e => e.Source.Owner.IsVirtual))
        {
            edge.Target.Owner.InputLevel = edge.Source.Owner.Level;
            edge.Active = Running && edge.Source.Owner.Level > .001f;
            edge.Flowing = Running && edge.Model.Enabled && edge.Target.Owner.Model.InputDeviceKey != AudioOutput.None;
        }
        if (FocusedRoot?.IsDraft == true) { FocusedRoot.Status = "默认输出 → " + DefaultOutputName; FocusedRoot.Level = Catalog.Applications.FirstOrDefault(a => a.Key == AppIdentity.Normalize(FocusedRoot.Model.Key))?.Peak ?? 0; }
    }
    public void AddLog(string message)
    {
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => AddLog(message)); return; }
        Log.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        while (Log.Count > 80) Log.RemoveAt(Log.Count - 1);
    }
    public void Notify(string message) => Notice = message;
    public void SaveNow() => SaveAll();
    public ValueTask DisposeAsync() => DisposeAsync(false);
    public async ValueTask DisposeAsync(bool recoverAfterExit)
    {
        if (disposed) return;
        meterTimer.Stop(); refreshTimer.Stop(); CancelScheduledEdit(); SaveNow();
        await configurationGate.WaitAsync();
        try
        {
            await Engine.StopAndRestoreAsync(true);
            if (recoverAfterExit && Engine.PendingRecoveryCount > 0)
                using (AudioRecovery.Launch(Environment.ProcessPath!, Store.DataDirectory, Environment.ProcessId)) { }
            await Engine.DisposeAsync(); disposed = true;
        }
        catch { meterTimer.Start(); refreshTimer.Start(); throw; }
        finally { configurationGate.Release(); }
        foreach (var volume in deviceVolumes.Values) volume.Dispose();
        deviceVolumes.Clear(); configurationGate.Dispose();
    }
}
