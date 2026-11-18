using AudioFlow.Core;
using System.Text.Json;

namespace AudioFlow.Audio;

public sealed class OriginalAudioSession
{
    public string EndpointId { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string Instance { get; set; } = "";
    public float Volume { get; set; }
    public bool Muted { get; set; }
}

public sealed class OriginalApplicationState
{
    public string Key { get; set; } = "";
    public string?[] Outputs { get; set; } = new string?[3];
    public List<OriginalAudioSession> Sessions { get; set; } = [];
    public List<int> ProcessIds { get; set; } = [];
    public bool OutputChanged { get; set; }
    public string? AppliedOutput { get; set; }
    public float? Volume { get; set; }
    public bool Muted { get; set; }
    public float? AppliedVolume { get; set; }
    public bool AppliedMute { get; set; }
}

public sealed class OriginalDeviceState
{
    public string EndpointId { get; set; } = "";
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public float AppliedVolume { get; set; }
    public bool AppliedMute { get; set; }
    public bool VolumeChanged { get; set; }
    public bool MuteChanged { get; set; }
}

public sealed class AudioRecoveryState
{
    public int Version { get; set; } = 1;
    public List<OriginalApplicationState> Applications { get; set; } = [];
    public List<OriginalDeviceState> Devices { get; set; } = [];
}

internal sealed class RouteJournal : IDisposable
{
    private readonly string path;
    private readonly object sync = new();
    private readonly AudioRecoveryState recovery;
    private readonly FileStream ownership;
    private readonly Dictionary<string, ApplicationSessionBinding> sessionBindings = [];
    public IReadOnlyList<OriginalApplicationState> States { get { lock (sync) return recovery.Applications.ToArray(); } }

    public int PendingCount { get { lock (sync) return recovery.Applications.Count + recovery.Devices.Count; } }

    public RouteJournal(string directory, bool recoveryOnly)
    {
        path = Path.Combine(directory, "audio-recovery.json");
        if (!recoveryOnly) AudioRecovery.StopWorker(directory);
        ownership = AudioRecovery.Acquire(directory);
        try
        {
            recovery = File.Exists(path)
                ? JsonSerializer.Deserialize<AudioRecoveryState>(File.ReadAllText(path), SceneStore.JsonOptions)
                    ?? throw new GraphValidationException("声音恢复记录为空。")
                : new();
            if (recovery.Version != 1) throw new GraphValidationException("声音恢复记录版本不受支持。");
        }
        catch { ownership.Dispose(); throw; }
    }

    public OriginalApplicationState Remember(string key, IReadOnlyList<int> pids, AudioCatalog catalog)
    {
        lock (sync)
        {
            var state = recovery.Applications.FirstOrDefault(s => AppIdentity.Normalize(s.Key) == AppIdentity.Normalize(key));
            if (state == null)
            {
                state = new() { Key = key };
                for (int role = 0; role < 3; role++) state.Outputs[role] = AudioPolicy.GetOutput(pids[0], role);
                recovery.Applications.Add(state);
            }
            if (state.Volume == null && catalog.GetApplicationVolume(pids) is { } volume)
            { state.Volume = volume.volume; state.Muted = volume.mute; }
            foreach (var pid in pids)
            {
                if (!state.ProcessIds.Contains(pid)) state.ProcessIds.Add(pid);
            }
            foreach (var binding in catalog.BindApplicationSessions(pids))
            {
                var session = binding.Original;
                var id = session.EndpointId + "|" + session.Instance;
                if (!sessionBindings.TryAdd(id, binding)) binding.Dispose();
                else if (state.Sessions.All(s => s.EndpointId != session.EndpointId || s.Instance != session.Instance)) state.Sessions.Add(session);
            }
            Save();
            return state;
        }
    }

    public void Applied(OriginalApplicationState state, float volume, bool mute, bool outputChanged = false, string? output = null)
    {
        lock (sync)
        {
            state.AppliedVolume = Math.Clamp(volume, 0, 1); state.AppliedMute = mute;
            if (outputChanged) { state.OutputChanged = true; state.AppliedOutput = output; }
            Save();
        }
    }

    public void SetDeviceVolume(EndpointVolumeBinding binding, float value)
    {
        lock (sync)
        {
            var original = RememberDevice(binding);
            original.AppliedVolume = Math.Clamp(value, 0, 1); original.VolumeChanged = true;
            Save();
            binding.SetVolume(original.AppliedVolume);
        }
    }
    public void SetDeviceMuted(EndpointVolumeBinding binding, bool value)
    {
        lock (sync)
        {
            var original = RememberDevice(binding);
            original.AppliedMute = value; original.MuteChanged = true;
            Save();
            binding.SetMuted(value);
        }
    }
    private OriginalDeviceState RememberDevice(EndpointVolumeBinding binding)
    {
        var existing = recovery.Devices.FirstOrDefault(s => s.EndpointId == binding.EndpointId);
        if (existing != null) return existing;
        var current = binding.State;
        var state = new OriginalDeviceState
        {
            EndpointId = binding.EndpointId, Volume = current.Volume, Muted = current.Muted,
            AppliedVolume = current.Volume, AppliedMute = current.Muted
        };
        recovery.Devices.Add(state);
        return state;
    }

    private void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(recovery, SceneStore.JsonOptions));
        File.Move(temporary, path, true);
    }

    public async Task RestoreAsync(string key, AudioCatalog catalog)
    {
        OriginalApplicationState? state;
        lock (sync) state = recovery.Applications.FirstOrDefault(s => AppIdentity.Normalize(s.Key) == AppIdentity.Normalize(key));
        if (state == null) return;
        bool restored = await Task.Run(async () =>
        {
            // AudioPolicy identifies applications through real audio sessions.
            // A closed/idle process cannot be restored by PID. Keep its journal
            // until a new session exists, including after the desktop exits.
            var pids = catalog.FindApplicationProcesses(state.Key)
                .Where(pid => catalog.GetApplicationVolume([pid]) != null || RecordingSessions.Read([pid]).Count > 0).ToArray();
            if (pids.Length == 0) return false;
            var available = catalog.Read().Endpoints.Where(e => !e.Capture).Select(e => e.Id).ToHashSet();
            var before = catalog.GetApplicationVolume(pids);
            if (state.OutputChanged)
            {
                foreach (var pid in pids)
                    for (int role = 0; role < 3; role++)
                    {
                        var current = AudioPolicy.Unpack(AudioPolicy.GetOutput(pid, role));
                        if (!string.Equals(current, state.AppliedOutput, StringComparison.OrdinalIgnoreCase)) continue;
                        var original = state.Outputs[role];
                        AudioPolicy.SetOutput(pid, role, original == null || available.Contains(AudioPolicy.Unpack(original)!) ? original : null);
                    }
                await Task.Delay(160);
            }
            await catalog.SettleRenderSessionsAsync(pids, () =>
            {
                catalog.RestoreSessionVolumes(state.Sessions, state.AppliedVolume, state.AppliedMute);
                catalog.RestoreApplicationSessions(state, pids, before);
            });
            lock (sync)
                foreach (var session in state.Sessions)
                    if (sessionBindings.TryGetValue(session.EndpointId + "|" + session.Instance, out var binding))
                        try { binding.Restore(state.AppliedVolume, state.AppliedMute); }
                        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            return true;
        });
        if (!restored) return;
        lock (sync)
        {
            recovery.Applications.Remove(state);
            foreach (var session in state.Sessions)
                if (sessionBindings.Remove(session.EndpointId + "|" + session.Instance, out var binding)) binding.Dispose();
            Save();
        }
    }

    public async Task RestoreAllAsync(AudioCatalog catalog, bool restoreDevices)
    {
        var failures = new List<Exception>();
        foreach (var key in States.Select(s => s.Key))
            try { await RestoreAsync(key, catalog); }
            catch (Exception error) { failures.Add(error); }
        if (restoreDevices) await Task.Run(RestoreDevices);
        if (failures.Count > 0) throw new AggregateException("部分应用的声音设置未恢复，恢复记录已保留。", failures);
    }

    public void RestoreDevices()
    {
        lock (sync)
        {
            foreach (var state in recovery.Devices.ToArray())
                try
                {
                    using var device = new EndpointVolumeBinding(state.EndpointId);
                    var current = device.State;
                    if (state.VolumeChanged && Math.Abs(current.Volume - state.AppliedVolume) < .001f) device.SetVolume(state.Volume);
                    if (state.MuteChanged && current.Muted == state.AppliedMute) device.SetMuted(state.Muted);
                    recovery.Devices.Remove(state);
                }
                catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            Save();
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            foreach (var binding in sessionBindings.Values) binding.Dispose(); sessionBindings.Clear();
            ownership.Dispose();
        }
    }
}
