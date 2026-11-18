using AudioFlow.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows.Input;

namespace AudioFlow.App;

public sealed partial class MainViewModel
{
    private string saveState = "尚未保存";
    private string? lastWrittenPath;
    private DateTime lastWrittenTime;
    private long lastWrittenLength = -1;
    private ICommand? saveConfigurationCommand, openConfigurationCommand, saveAsConfigurationCommand;
    public string SaveState { get => saveState; private set => Set(ref saveState, value); }
    public string ConfigurationName => workspace.ActiveConfigurationPath == null ? "找回的设置 · 请另存为" : Path.GetFileName(workspace.ActiveConfigurationPath);
    public string ConfigurationPath => workspace.ActiveConfigurationPath ?? workspace.UnavailableConfigurationPath ?? "原文件打不开，这里显示的是上次保留的设置";
    public ICommand SaveConfigurationCommand => saveConfigurationCommand ??= new Command(_ => { if (workspace.ActiveConfigurationPath == null) SaveAsDialog(); else SaveAll(); });
    public ICommand OpenConfigurationCommand => openConfigurationCommand ??= new Command(async _ =>
    {
        var picker = new OpenFileDialog { Title = "打开声音设置", Filter = "声流设置文件|*.json" };
        if (picker.ShowDialog() == true) await OpenConfigurationAsync(picker.FileName);
    }, _ => !Busy);
    public ICommand SaveAsConfigurationCommand => saveAsConfigurationCommand ??= new Command(_ => SaveAsDialog(), _ => !Busy);
    public Func<IReadOnlyList<VirtualChannel>, VirtualChannel?>? PickSharedChannel { get; set; }
    public string ChannelCapacity
    {
        get
        {
            var connected = ChannelPool.Connected(graph, Catalog.Endpoints);
            var used = ChannelPool.Used(graph, Catalog.Endpoints);
            int validated = connected.Count(c => !c.RequiresValidation);
            int occupied = connected.Count(c => !c.RequiresValidation && used.Contains(c.Id));
            int pending = connected.Count(c => c.RequiresValidation);
            return $"传声音的麦克风 {validated} 个 · 正在用 {occupied} · 可用 {validated - occupied}" +
                (pending > 0 ? $" · 还要测试 {pending}" : "") +
                (graph.SilentInputChannelId != null ? " · 其中 1 个用于关闭录音" : "") +
                (graph.Channels.Count > connected.Count ? $" · 未连接 {graph.Channels.Count - connected.Count}" : "");
        }
    }
    public string ChannelOwners(VirtualChannel channel)
    {
        var names = graph.Nodes.Where(n => n.Kind == NodeKind.Application &&
            (n.ChannelId == channel.Id || n.InputDeviceKey == channel.CaptureEndpointId)).Select(n => n.Name).Distinct().ToArray();
        var sources = graph.Connections.Where(c => graph.Nodes.Any(n => n.Id == c.TargetId && n.ChannelId == channel.Id))
            .Select(c => graph.Nodes.First(n => n.Id == c.SourceId).Name).Distinct().ToArray();
        return "接收：" + (names.Length == 0 ? "无" : string.Join("、", names)) + "；声音来自：" + (sources.Length == 0 ? "无" : string.Join("、", sources));
    }
    private void InitializeConfigurationFiles()
    {
        workspace.LastRoutingEnabled ??= graph.ResumeOnLaunch && !workspace.RoutingPaused;
        ChannelPool.NormalizeOutputs(graph);
        string? path = workspace.ActiveConfigurationPath;
        try
        {
            if (path == null)
            {
                if (workspace.UnavailableConfigurationPath != null) { SaveState = "上次的设置已找回 · 请另存为"; return; }
                path = Path.Combine(Store.DataDirectory, "Configurations", "默认配置.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Store.Save(graph, path); workspace.ActiveConfigurationPath = path;
            }
            else { if (!File.Exists(path)) throw new FileNotFoundException("上次配置文件不存在", path); graph = Store.Load(path); }
            SaveState = "已保存";
        }
        catch (Exception error)
        {
            workspace.ActiveConfigurationPath = null;
            workspace.UnavailableConfigurationPath = path;
            SaveState = "原文件打不开 · 上次的设置已保留";
            notice = "上次的设置已找回，请另存为新文件：" + UserError.Describe(error);
        }
    }
    private bool SaveAll()
    {
        if (disposed) return false;
        try
        {
            if (workspace.ActiveConfigurationPath is { } path)
            {
                // An externally removed file is not silently recreated.
                if (!File.Exists(path))
                {
                    workspace.UnavailableConfigurationPath = path;
                    workspace.ActiveConfigurationPath = null;
                    Changed(nameof(ConfigurationName)); Changed(nameof(ConfigurationPath));
                    Store.SaveWorkspace(workspace); SaveState = "原文件已移除 · 请另存为"; return false;
                }
                var existing = new FileInfo(path);
                if (path != lastWrittenPath || existing.LastWriteTimeUtc != lastWrittenTime || existing.Length != lastWrittenLength) Store.Load(path);
                Store.Save(graph, path);
                var written = new FileInfo(path); lastWrittenPath = path; lastWrittenTime = written.LastWriteTimeUtc; lastWrittenLength = written.Length;
            }
            Store.SaveWorkspace(workspace);
            SaveState = workspace.ActiveConfigurationPath == null ? "设置已保留 · 请另存为" : "已保存 " + DateTime.Now.ToString("HH:mm:ss");
            return true;
        }
        catch (Exception error)
        {
            SaveState = "保存失败：" + UserError.Describe(error); AddLog(SaveState);
            try { Store.SaveWorkspace(workspace); } catch (Exception snapshotError) { AddLog("没能保留这份设置：" + snapshotError.Message); }
            return false;
        }
    }
    private void SaveAsDialog()
    {
        var picker = new SaveFileDialog { Title = "声音设置另存为", Filter = "声流设置文件|*.json", FileName = "声音设置.json" };
        if (picker.ShowDialog() == true) _ = SaveConfigurationAsAsync(picker.FileName);
    }
    public async Task SaveConfigurationAsAsync(string path)
    {
        CancelScheduledEdit(); await configurationGate.WaitAsync();
        try
        {
            path = Path.GetFullPath(path); Store.Save(graph, path); workspace.ActiveConfigurationPath = path; workspace.UnavailableConfigurationPath = null;
            SaveAll(); Changed(nameof(ConfigurationName)); Changed(nameof(ConfigurationPath));
        }
        catch (Exception error) { SaveState = "保存失败：" + UserError.Describe(error); }
        finally { configurationGate.Release(); }
    }
    public async Task OpenConfigurationAsync(string path)
    {
        CancelScheduledEdit(); await configurationGate.WaitAsync(); Busy = true;
        try
        {
            if (!SaveAll() && workspace.ActiveConfigurationPath != null) return;
            if (!File.Exists(path)) throw new FileNotFoundException("配置文件不存在", path);
            var next = Store.Load(path); ApplicationOutputs.Ensure(next);
            GraphCompiler.Compile(next, Catalog.Endpoints);
            graph = next; draft = null; undo.Clear(); redo.Clear(); lastUndo = SceneStore.Clone(graph);
            workspace.ActiveConfigurationPath = Path.GetFullPath(path); Rebuild(); SaveAll();
            workspace.UnavailableConfigurationPath = null;
            Changed(nameof(ConfigurationName)); Changed(nameof(ConfigurationPath));
            if (Running) await Engine.ApplyAsync(graph);
            Notice = "已打开 " + ConfigurationName + "，两页都会更新。"; FitRequested?.Invoke();
        }
        catch (Exception error) { Notice = "文件没能打开，现在的设置仍然保留：" + UserError.Describe(error); }
        finally { Busy = false; configurationGate.Release(); }
    }
}
