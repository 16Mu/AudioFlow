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
    public string Name => PlaybackCard && Model.Key == AudioOutput.SystemDefault ? "跟随 Windows 的选择" : Model.Name;
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
    public string CardAction => FocusedApplicationCard ? "当前软件" : Model.PrimaryOutput ? "主要播放" : "右键可设置";
    internal void RefreshProtection() { Changed(nameof(CanChooseCard)); Changed(nameof(CanDelete)); Changed(nameof(CardAction)); }
    public bool IsVirtual => Model.Kind == NodeKind.VirtualMicrophone;
    public bool IsConfigured => Model.Kind == NodeKind.VirtualMicrophone ? !string.IsNullOrWhiteSpace(Model.ChannelId) :
        !string.IsNullOrWhiteSpace(Model.Key);
    public string Category => Model.Kind switch
    {
        NodeKind.Application => "软件",
        NodeKind.Microphone => "麦克风 · 收取你的声音",
        NodeKind.VirtualMicrophone => "传声音的麦克风",
        _ => Model.PrimaryOutput ? DefaultOutputEnabled ? "平时播放的设备" : "平时播放 · 已关闭" : "再送到这个设备播放"
    };
    public string PickerHint => SoftwareCard ? "选择正在运行的软件" : MicrophoneCard ? "选择麦克风或传声音的设备" : "选择耳机、扬声器等设备";
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
    public string LinkSummary => $"接收 {incoming} 处 · 发到 {outgoing} 处";
    public string InputDeviceName => Owner.Graph.Channels.FirstOrDefault(c => c.Id == Model.ChannelId) is { } channel
        ? Owner.Catalog.Endpoints.FirstOrDefault(e => e.Id == channel.CaptureEndpointId)?.Name ?? "等待设备连接"
        : "连好后，这里会显示软件要选的麦克风";
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
        if (SoftwareCard) Inputs.Add(new(this, false, "接收声音", true));
        else if (Model.Kind == NodeKind.VirtualMicrophone) Inputs.Add(new(this, false, "接收声音", true));
        else if (PlaybackCard) Inputs.Add(new(this, false, "接收声音", false));
        if (AudioSource) Outputs.Add(new(this, true, SoftwareCard ? "发出声音" : "麦克风声音", !SoftwareCard));
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
                    app.HasAudio ? "正在发声或使用麦克风" : app.ProcessIds.Count > 0 ? "已打开 · 暂无声音" : "等待软件打开"));
        }
        else if (MicrophoneCard)
        {
            foreach (var mic in Owner.Catalog.Endpoints.Where(e => e.Capture && !e.Virtual))
                candidates.Add(new(mic.Name, mic.Id, NodeKind.Microphone, "收取你的声音"));
            foreach (var channel in Owner.Graph.Channels)
                candidates.Add(new(channel.Name, "", NodeKind.VirtualMicrophone, "把声音传给其他软件", channel.Id));
        }
        else
        {
            if (Model.PrimaryOutput) candidates.Add(new("不播放声音", AudioOutput.None, NodeKind.Playback, "关闭这个软件平时播放的声音"));
            candidates.Add(new("跟随 Windows 的选择", AudioOutput.SystemDefault, NodeKind.Playback, Owner.DefaultOutputName));
            foreach (var endpoint in Owner.Catalog.Endpoints.Where(e => !e.Capture && e.Id != Owner.Graph.Channels.FirstOrDefault(c => c.Id == Owner.Graph.SilentInputChannelId)?.RenderEndpointId))
                candidates.Add(new(endpoint.Name, endpoint.Id, NodeKind.Playback, endpoint.Default ? "Windows 里当前选择的播放设备" : "播放设备"));
        }
        var selected = candidates.FirstOrDefault(c => c.Kind == Model.Kind &&
            (Model.Kind == NodeKind.VirtualMicrophone ? c.ChannelId == Model.ChannelId : AppIdentity.Normalize(c.Key) == AppIdentity.Normalize(Model.Key)));
        if (selected == null && IsConfigured)
        {
            selected = new(Name, Model.Key, Model.Kind, "已选择 · 等待软件打开或设备连接", Model.ChannelId);
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
    private string status = "还没开始", detail = "";
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
    public string ToggleLabel => Model.Enabled ? "暂停这条连接" : "重新开启";
    public string ToggleHelp => Source.Owner.IsVirtual ? "暂停后，这个软件暂时不接收声音；需要一个空着的传声音设备。重新开启会用回这个麦克风。" : "暂停后，这条线不再传声音，连线和音量会保留。";
    public string Description => $"{Source.Owner.Name} → {Target.Owner.Name} · " + (IsDisconnected ? "已暂停" : VolumeLabel) + "。点一下选中，右键可暂停或删除。";
    public void RefreshVolume() { Changed(nameof(Volume)); Changed(nameof(VolumeLabel)); Changed(nameof(Description)); RefreshConnectionState(); }
    private bool? lastDisconnected;
    internal void RefreshConnectionState()
    {
        bool value = IsDisconnected;
        if (lastDisconnected == value) return;
        lastDisconnected = value; Changed(nameof(IsDisconnected)); Changed(nameof(ToggleLabel)); Changed(nameof(Description));
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
    public string Help => Connection.SharedChannel ? "共用这个麦克风的软件，收到的声音会一起变大或变小。" : "只改变这条线上的声音大小，两端显示的是同一个音量。";
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
    public string ViewLabel => GlobalView ? "查看全部连接" : "设置一个软件";
    public string VersionLabel => "v" + typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public string DefaultOutputName => AudioOutput.DefaultName(Catalog.Endpoints);
    public string ViewHint => GlobalView ? "在空白处右键添加软件或设备，再拖动圆点连起来" : "先在左边选软件，再把它连到你想用的耳机或麦克风";
    public string FocusTitle => GlobalView ? "全部连接" : FocusedRoot?.Name ?? "先选一个软件";
    public string Counts => $"{CanvasNodes.Count} 张卡片 · {CanvasConnections.Count} 条连线";
    public string LiveSummary => $"已连接 {graph.Connections.Where(c => c.Enabled).Select(c => c.SourceId).Distinct().Count()} 个软件或麦克风 · " + (Running ? "正在按连线播放" : Paused ? "已暂停" : "还没开启");
    public bool Paused => workspace.RoutingPaused;
    public string RunActionLabel => Running ? "停止声音连接" : "开启声音连接";
    public bool CloseToTray { get => graph.CloseToTray; set { graph.CloseToTray = value; Changed(); Edited(false); } }
    public bool ResumeOnLaunch { get => workspace.RestoreRoutingStateOnLaunch; set { workspace.RestoreRoutingStateOnLaunch = value; Changed(); Edited(false); } }
    private WindowsAutoStart? windowsAutoStart;
    internal WindowsAutoStart AutoStartRegistration => windowsAutoStart ??= new(Store.DataDirectory);
    internal void UseAutoStartRegistration(WindowsAutoStart registration) => windowsAutoStart = registration;
    public bool StartWithWindows
    {
        get
        {
            try { return AutoStartRegistration.Enabled; }
            catch (Exception error) { AddLog(error.ToString()); return false; }
        }
        set
        {
            try
            {
                AutoStartRegistration.SetEnabled(value);
                Notice = value ? "已开启，登录 Windows 后会自动打开声流。" : "已关闭开机启动。";
            }
            catch (Exception error) { AddLog(error.ToString()); Notice = "开机启动没能设置好：" + UserError.Describe(error); }
            Changed();
        }
    }
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
    private string notice = "两页是同一份设置，切换时声音照常。没改过的软件按原设置播放。", search = "";
    public bool Busy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool Running { get => running; private set { workspace.LastRoutingEnabled = value; Set(ref running, value); RefreshRunState(); } }
    public string Notice { get => notice; private set => Set(ref notice, value); }
    public string Search { get => search; set { if (Set(ref search, value)) RefreshApplications(); } }
    public bool NoApplications => Applications.Count == 0;
    public string ApplicationEmptyHint => string.IsNullOrWhiteSpace(Search) ?
        "这里还没有软件\n先让软件播放声音，或点击“添加软件”" : "没找到这个软件\n试试别的名称，或点击“添加软件”";
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
        try { workspace = Store.LoadWorkspace(); } catch (Exception error) { notice = "上次的设置没能打开：" + UserError.Describe(error); }
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
            { Clipboard.SetText(node.InputDeviceName); Notice = "名称已复制。请在接收声音的软件里，把它选为麦克风。"; }
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
            catch (Exception error) { Notice = UserError.Describe(error); }
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
        catch (Exception error) { Notice = "设备音量修改失败：" + UserError.Describe(error); }
    }
    public void SetDeviceMuted(NodeViewModel node, bool muted)
    {
        try { if (PlaybackEndpoint(node) is { } id && deviceVolumes.TryGetValue(id, out var binding)) { Engine.SetDeviceMuted(binding, muted); node.UpdateDeviceVolume(binding.State); } }
        catch (Exception error) { Notice = "设备静音修改失败：" + UserError.Describe(error); }
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
        string name = key == AudioOutput.None ? "不播放声音" : key == AudioOutput.SystemDefault ? "跟随 Windows 的选择" : Catalog.Endpoints.FirstOrDefault(e => e.Id == key)?.Name ?? "上次选的播放设备 · 等待连接";
        ChooseCard(node, new(name, key, NodeKind.Playback, "主要播放"));
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
                Configured(app.Path) ? (app.HasAudio ? "已设置 · 正在发声" : app.ProcessIds.Count > 0 ? "已设置 · 暂无声音" : "已设置 · 等待打开") :
                app.HasAudio ? "按 Windows 的选择播放" : app.HasAudioSession || workspace.KnownAudioApplications.Any(a => AppIdentity.Normalize(a.Path) == app.Key) ?
                "暂无声音 · 按原设置播放" : app.ProcessIds.Count > 0 ? "自己添加 · 按原设置播放" : "已添加 · 等待打开"));
        Changed(nameof(NoApplications)); Changed(nameof(ApplicationEmptyHint));
    }
    public IReadOnlyList<AudioApplication> GetApplicationCandidates() => ApplicationSelection.Candidates(Catalog.Applications,
        workspace.ManualApplications.Concat(ConfiguredApplications).Concat(workspace.KnownAudioApplications));
    public IReadOnlyList<AvailableItem> GetApplicationChoices() => GetApplicationCandidates().Select(app =>
        new AvailableItem(app.Name, app.Path, NodeKind.Application, app.Peak > .001f ? "正在播放声音" :
            app.HasAudio ? "正在发声或使用麦克风" : app.ProcessIds.Count > 0 ? "已打开 · 暂无声音" : "已保存 · 等待打开")).ToList();
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
        Notice = "正在设置 " + FocusTitle + "：主要播放卡片决定平时在哪听；再连一个设备，就能同时在那里播放。";
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
            Notice = "已打开" + ViewLabel + "，两页会一起更新。";
            Changed(nameof(GlobalView)); Changed(nameof(ViewLabel)); Changed(nameof(ViewHint));
            RefreshRunState();
            FitRequested?.Invoke();
        }
        catch (Exception error) { Notice = "这个软件的设置没能打开：" + UserError.Describe(error); AddLog(error.ToString()); }
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
                        Key = workspace.FocusedApplication!, Name = app?.Name ?? workspace.ManualApplications.FirstOrDefault(a => AppIdentity.Normalize(a.Path) == focus)?.Name ?? Path.GetFileNameWithoutExtension(workspace.FocusedApplication) ?? "还没打开的软件",
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
                                PrimaryOutput = true, OwnerAppKey = FocusedRoot.Model.Key, Name = "跟随 Windows 的选择",
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
            Kind = kind, Name = kind == NodeKind.Application ? "选择软件" : kind == NodeKind.Microphone ? "选择麦克风" : "选择耳机、扬声器等设备",
            X = x, Y = y, OwnerAppKey = GlobalView ? null : workspace.FocusedApplication
        };
        graph.Nodes.Add(node); Rebuild(); Edited(true);
        Notice = "卡片已添加。先选好软件或设备，再从右边的圆点拖到左边的圆点。";
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
        ChooseCard(node, new(channel.Name, "", NodeKind.VirtualMicrophone, "传声音的麦克风", channel.Id));
        return Nodes.First(n => n.Id == node.Id);
    }
    public void ChooseCard(NodeViewModel node, CardChoice value)
    {
        if (!node.CanChooseCard) { Notice = "请在左边选择要设置的软件。想再加一个软件，可以在空白处右键添加卡片。"; return; }
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
                throw new GraphValidationException("这个软件已经有卡片了，请用原来的那张。");
            if (node.Model.PrimaryOutput && (value.Kind != NodeKind.Playback || string.IsNullOrWhiteSpace(value.Key)))
                throw new GraphValidationException("这里只能选择耳机、扬声器等播放设备。");
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
            Notice = removed > 0 ? "已经换好。旧的线不能再用，请重新连到你想要的位置。" : "已选择 " + value.Name + "。";
        }
        catch (Exception error) { graph = backup; Rebuild(); Notice = UserError.Describe(error); }
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
            Notice = "已经选好。共用这个麦克风的软件，会听到同样的声音。";
        }
        catch (Exception error) { graph = backup; Rebuild(); Notice = UserError.Describe(error); }
    }
    public void Connect(PortViewModel from, PortViewModel to)
        => TryConnect(from, to, 1, true);
    private bool TryConnect(PortViewModel from, PortViewModel to, float volume, bool history)
    {
        if (!from.Output && to.Output) (from, to) = (to, from);
        if (!from.Output || to.Output) { Notice = "从发声卡片右边的圆点，拖到接收卡片左边的圆点。"; return false; }
        if (!from.Owner.IsConfigured || !to.Owner.IsConfigured) { Notice = "先在这张卡片里选好软件、麦克风或耳机。"; return false; }
        var backup = SceneStore.Clone(graph);
        try
        {
            CommitDraft();
            if (FocusedRoot != null && !Nodes.Contains(FocusedRoot)) Nodes.Add(FocusedRoot);
            var source = from.Owner.Model; var target = to.Owner.Model;
            if (target.PrimaryOutput && !ApplicationOutputs.IsPrimary(source, target))
                throw new GraphValidationException("这张主要播放卡片只用于它上游的软件。要再送到别处，请另加一张播放设备卡片。");
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
                        throw new GraphValidationException("这个软件已经连到别的麦克风了。请先在卡片里更换，或断开原来的线。");
                    target.ChannelId = source.ChannelId;
                }
                else if (string.IsNullOrWhiteSpace(target.ChannelId))
                {
                    var available = ChannelPool.Free(graph, Catalog.Endpoints).FirstOrDefault();
                    if (available == null) available = PickSharedChannel?.Invoke(ChannelPool.Connected(graph, Catalog.Endpoints).Where(c => !c.RequiresValidation && c.Id != graph.SilentInputChannelId).ToArray());
                    if (available == null) throw new GraphValidationException("能传不同声音的麦克风已经用完了。" + ChannelCapacity + "。可以选择听同样的声音，或在设置里添加更多驱动。");
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
            Notice = target.Kind == NodeKind.Playback ? source.Name + " → " + target.Name + "：从这个设备播放" :
                source.Name + " → " + target.Name + "：把声音传给这个软件。请在软件里选用卡片显示的麦克风。";
            return true;
        }
        catch (Exception error) { graph = backup; draft = null; Rebuild(); Notice = UserError.Describe(error); return false; }
    }
    public void RemoveConnection(ConnectionViewModel connection)
    {
        if (!connection.CanRemove) { Notice = "这条主要播放的线需要保留。想换耳机，请在它连接的卡片里更换。"; return; }
        ChangeConnection(connection, null);
    }
    public void SelectConnection(ConnectionViewModel connection)
    {
        SelectedNodes.Clear(); SelectedConnections.Clear();
        if (CanvasConnections.Contains(connection)) SelectedConnections.Add(connection);
    }
    public void SetConnectionEnabled(ConnectionViewModel connection, bool enabled)
    {
        if (connection.Model.Enabled == enabled) return;
        if (connection.IsPrimary)
        {
            SetDefaultOutputEnabled(connection.Target.Owner, enabled);
            var current = CanvasConnections.FirstOrDefault(c => c.IsPrimary && c.Source.Owner == connection.Source.Owner);
            if (current != null) SelectConnection(current);
            return;
        }
        ChangeConnection(connection, enabled);
    }
    private void ChangeConnection(ConnectionViewModel connection, bool? enabled)
    {
        var edge = graph.Connections.FirstOrDefault(c => c.Id == connection.Model.Id);
        if (edge == null) return;
        var backup = SceneStore.Clone(graph);
        try
        {
            if (connection.Source.Owner.IsVirtual && connection.Target.Owner.SoftwareCard)
            {
                var target = connection.Target.Owner;
                string key = enabled == true ? graph.Channels.First(c => c.Id == connection.Source.Owner.Model.ChannelId).CaptureEndpointId : AudioOutput.None;
                if (key == AudioOutput.None)
                {
                    target.Model.InputDeviceKey ??= target.RecordingKey;
                    if (target.RecordingKey != AudioOutput.None) target.Model.LastInputDeviceKey = target.RecordingKey;
                    ReserveQuietInput();
                }
                else { target.Model.LastInputDeviceKey = key; target.Model.ChannelId = connection.Source.Owner.Model.ChannelId; target.Model.InputChannelPinned = true; }
                target.Model.InputDeviceKey = key; target.Model.RecordingConfigured = true;
                if (!graph.Nodes.Any(n => n.InputDeviceKey == AudioOutput.None)) graph.SilentInputChannelId = null;
            }
            if (enabled.HasValue) edge.Enabled = enabled.Value;
            else graph.Connections.Remove(edge);
            GraphCompiler.Compile(graph, Catalog.Endpoints);
            RebuildConnections(); BuildCanvas(); RefreshRunState(); Edited(true);
            if (enabled.HasValue && CanvasConnections.FirstOrDefault(c => c.Model.Id == edge.Id) is { } current) SelectConnection(current);
            Notice = !enabled.HasValue ? "这条线已删除。可以点“撤销”找回来。" : enabled.Value ? "这条连接已重新开启。" : "这条连接已暂停，连线和音量已保留。右键可重新开启。";
        }
        catch (Exception error) { graph = backup; Rebuild(); Notice = UserError.Describe(error); }
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
        Notice = "拖到另一个左侧圆点，就能换个去处。松在空白处会断开，按 Esc 可以放回去。";
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
        var updated = new List<ConnectionViewModel>();
        foreach (var edge in graph.Connections)
        {
            var source = Nodes.FirstOrDefault(n => n.Id == edge.SourceId)?.Outputs.FirstOrDefault();
            var target = Nodes.FirstOrDefault(n => n.Id == edge.TargetId)?.Inputs.FirstOrDefault();
            if (source != null && target != null)
            {
                var current = Connections.FirstOrDefault(c => c.Model == edge && c.Source == source && c.Target == target) ?? new(edge, source, target);
                current.RefreshVolume(); updated.Add(current);
            }
        }
        foreach (var old in Connections.Where(c => !updated.Contains(c)).ToArray()) Connections.Remove(old);
        for (int i = 0; i < updated.Count; i++)
        {
            int current = Connections.IndexOf(updated[i]);
            if (current < 0) Connections.Insert(i, updated[i]);
            else if (current != i) Connections.Move(current, i);
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
        catch (Exception error) { Notice = "这次的修改没能生效：" + UserError.Describe(error); }
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
        if (ids.Count == 0 && edges.Count == 0) { Notice = "当前的软件、主要播放卡片和它们之间的线需要保留。"; return; }
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
        Notice = "卡片已整理，声音从左往右走，连线更容易看清。";
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "打开声音设置", Filter = "声流设置文件|*.json" };
        if (dialog.ShowDialog() != true) return;
        try { ImportConfiguration(Store.Load(dialog.FileName)); }
        catch (Exception error) { Notice = UserError.Describe(error); }
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
        Notice = "声音设置已打开，两页都会更新。";
    }
    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "保存声音设置", Filter = "声流设置文件|*.json", FileName = "声音设置.json" };
        if (dialog.ShowDialog() == true)
            try { Store.Save(graph, dialog.FileName); Notice = "声音设置已保存到所选文件。"; } catch (Exception error) { Notice = UserError.Describe(error); }
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
            Notice = "已经开始按连线播放。现在改连线或音量，就会马上生效。";
        }
        catch (Exception error) { Running = Engine.Running; Notice = "无法启用：" + UserError.Describe(error); AddLog(error.ToString()); }
        finally { Busy = false; RefreshRunState(); configurationGate.Release(); }
    }
    public async Task PauseAsync()
    {
        await configurationGate.WaitAsync(); Busy = true;
        try
        {
            CancelScheduledEdit();
            await Engine.StopAndRestoreAsync(); workspace.RoutingPaused = true; Running = Engine.Running;
            SaveNow(); Notice = RecoveryNotice("已停止连接，软件的声音去向恢复到原来。你画的线和音量设置已保存。");
        }
        catch (Exception error) { Notice = "恢复失败：" + UserError.Describe(error); }
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
            Notice = RecoveryNotice("声音去向、音量和静音已恢复到使用声流之前。你画的连接仍然保留。");
        }
        catch (Exception error) { Running = Engine.Running; Notice = "部分设置未能恢复：" + UserError.Describe(error); AddLog(error.ToString()); }
        finally { Busy = false; RefreshRunState(); configurationGate.Release(); }
    }
    private string RecoveryNotice(string complete) => Engine.PendingRecoveryCount == 0 ? complete :
        $"连接已停止，设置已保存。还有 {Engine.PendingRecoveryCount} 项声音设置，会在软件再次发声或设备接回后自动恢复。";
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
            node.Status = node.OutputDisabled || node.HasDefaultOutput && !node.DefaultOutputEnabled ? "主要播放已关闭" : !node.IsConfigured ? node.PickerHint : !Running ? (Paused ? "声音连接已停止" : node.UseSystemOutput ? "按 Windows 的选择播放" : "设置已保存 · 还没开启连接")
                : status?.State switch { "Active" => "正在按连线播放", "Waiting" => "等待软件打开", "Restart" => "请重启该软件", "Error" => "这条连接还不能使用", _ => node.UseSystemOutput ? "按 Windows 的选择播放" : "传声音的麦克风已选好" };
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
                edge.Source.Owner.IsConfigured && edge.Target.Owner.IsConfigured && edge.Source.Owner.Status != "这条连接还不能使用";
        }
        foreach (var edge in Connections.Where(e => e.Source.Owner.IsVirtual))
        {
            edge.Target.Owner.InputLevel = edge.Source.Owner.Level;
            edge.Active = Running && edge.Source.Owner.Level > .001f;
            edge.Flowing = Running && edge.Model.Enabled && edge.Target.Owner.Model.InputDeviceKey != AudioOutput.None;
        }
        if (FocusedRoot?.IsDraft == true) { FocusedRoot.Status = "主要播放 → " + DefaultOutputName; FocusedRoot.Level = Catalog.Applications.FirstOrDefault(a => a.Key == AppIdentity.Normalize(FocusedRoot.Model.Key))?.Peak ?? 0; }
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
