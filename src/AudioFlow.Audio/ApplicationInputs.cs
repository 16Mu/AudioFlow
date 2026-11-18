using AudioFlow.Core;
using System.Text.Json;

namespace AudioFlow.Audio;

public sealed class OriginalInputState
{
    public string Key { get; set; } = "";
    public string?[] Inputs { get; set; } = new string?[3];
    public string? AppliedInput { get; set; }
}

internal sealed class ApplicationInputs
{
    private readonly string path;
    private readonly List<OriginalInputState> originals;
    private readonly Dictionary<string, string> applied = [];
    public int PendingCount => originals.Count;
    public ApplicationInputs(string directory)
    {
        path = Path.Combine(directory, "input-recovery.json");
        originals = File.Exists(path) ? JsonSerializer.Deserialize<List<OriginalInputState>>(File.ReadAllText(path), SceneStore.JsonOptions) ?? [] : [];
    }
    private void Save()
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(originals, SceneStore.JsonOptions));
        File.Move(path + ".tmp", path, true);
    }
    public string Apply(RouteNode node, RouteGraph graph, IReadOnlyList<int> pids, IReadOnlyList<AudioEndpoint> endpoints)
    {
        string? selection = node.InputDeviceKey;
        if (selection == null && node.ChannelId != null)
            selection = graph.Channels.FirstOrDefault(c => c.Id == node.ChannelId)?.CaptureEndpointId;
        if (selection == null) return "";
        if (pids.Count == 0) return "录音设备已保存 · 等待软件运行";
        var sessions = RecordingSessions.Read(pids);
        if (sessions.Count == 0 && !pids.Any(pid => new AudioCatalog().GetApplicationVolume([pid]) != null))
            return "录音设备已保存 · 等待软件建立音频会话";
        string? endpoint = selection == AudioOutput.SystemDefault ? null : selection;
        if (selection == AudioOutput.None)
        {
            var silent = graph.Channels.FirstOrDefault(c => c.Id == graph.SilentInputChannelId);
            if (silent == null || !endpoints.Any(e => e.Capture && e.Id == silent.CaptureEndpointId))
                return "录音关闭未生效：静音虚拟通道未连接";
            if (new AudioCatalog().ActiveSessions(silent.RenderEndpointId).Any(s => s.Peak > .001))
                return "录音关闭未生效：静音通道受到外部声音占用，请释放或更换通道";
            endpoint = silent.CaptureEndpointId;
        }
        if (endpoint != null && !endpoints.Any(e => e.Capture && e.Id == endpoint)) return "录音设备未连接 · 等待重连";
        var original = originals.FirstOrDefault(s => AppIdentity.Normalize(s.Key) == AppIdentity.Normalize(node.Key));
        if (original == null)
        {
            original = new() { Key = node.Key };
            int audioPid = pids.First(pid => RecordingSessions.Read([pid]).Count > 0 || new AudioCatalog().GetApplicationVolume([pid]) != null);
            for (int role = 0; role < 3; role++) original.Inputs[role] = AudioPolicy.GetInput(audioPid, role);
            originals.Add(original);
        }
        original.AppliedInput = endpoint; Save();
        foreach (var pid in pids)
        {
            string signature = (endpoint ?? "default") + "|" + pid;
            if (applied.GetValueOrDefault(node.Key + "|" + pid) == signature) continue;
            for (int role = 0; role < 3; role++) AudioPolicy.SetInput(pid, role, endpoint);
            applied[node.Key + "|" + pid] = signature;
        }
        string? expected = endpoint ?? endpoints.FirstOrDefault(e => e.Capture && e.Default)?.Id;
        if (sessions.Any(s => s.EndpointId != expected)) return "录音偏好已设置 · 等待切换或请重开此软件的录音";
        return selection == AudioOutput.None ? "录音已关闭 · 使用独立静音输入" : "录音设备已应用";
    }
    public void Restore(AudioCatalog catalog, HashSet<string>? keep = null)
    {
        foreach (var original in originals.ToArray())
        {
            if (keep?.Contains(AppIdentity.Normalize(original.Key)) == true) continue;
            var pids = catalog.FindApplicationProcesses(original.Key).Where(pid => RecordingSessions.Read([pid]).Count > 0 || catalog.GetApplicationVolume([pid]) != null).ToArray();
            if (pids.Length == 0) continue;
            var available = catalog.Read().Endpoints.Where(e => e.Capture).Select(e => e.Id).ToHashSet();
            foreach (var pid in pids)
                for (int role = 0; role < 3; role++)
                {
                    if (!string.Equals(AudioPolicy.Unpack(AudioPolicy.GetInput(pid, role)), original.AppliedInput, StringComparison.OrdinalIgnoreCase)) continue;
                    string? previous = original.Inputs[role];
                    AudioPolicy.SetInput(pid, role, previous == null || available.Contains(AudioPolicy.Unpack(previous)!) ? previous : null);
                }
            originals.Remove(original);
            foreach (var key in applied.Keys.Where(k => k.StartsWith(original.Key + "|", StringComparison.Ordinal)).ToArray()) applied.Remove(key);
            Save();
        }
        if (keep == null) applied.Clear();
    }
}
