using AudioFlow.Core;
using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioFlow.Audio;

public sealed class AudioCatalog
{
    public IReadOnlyList<(int ProcessId, string Name, float Peak)> ActiveSessions(string endpointId)
    {
        var result = new List<(int, string, float)>();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if ((int)session.State != 1) continue;
            int pid = (int)session.GetProcessID;
            string name = session.DisplayName;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }
            float peak = 0; try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }
            result.Add((pid, name, peak));
        }
        return result;
    }
    public AudioCatalogSnapshot Read()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultRender = null, defaultCapture = null;
        try { using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultRender = d.ID; } catch { }
        try { using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); defaultCapture = d.ID; } catch { }
        var endpoints = new List<AudioEndpoint>();
        var sessions = new Dictionary<int, (float peak, string name, bool active)>();
        var recording = new HashSet<int>();
        var recordingEndpoints = new Dictionary<int, string>();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            // VB-CABLE exposes a multichannel alias connected to the same
            // recording device. This stereo router exposes the canonical input
            // only, otherwise one source could be played twice into one cable.
            bool hiddenAlias = device.FriendlyName.StartsWith("CABLE In 16ch", StringComparison.OrdinalIgnoreCase);
            bool capture = device.DataFlow == DataFlow.Capture;
            int rate = 0, channels = 0;
            try { using var client = device.CreateAudioClient(); var format = client.MixFormat; rate = format.SampleRate; channels = format.Channels; } catch { }
            var virtualDevice = IsVirtual(device.FriendlyName);
            if (!hiddenAlias) endpoints.Add(new(device.ID, device.FriendlyName, capture,
                device.ID == (capture ? defaultCapture : defaultRender), virtualDevice, rate, channels));

            try
            {
                var collection = device.AudioSessionManager.Sessions;
                for (int j = 0; j < collection.Count; j++)
                {
                    using var session = collection[j];
                    int pid = (int)session.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    if (capture) { if ((int)session.State == 1) { recording.Add(pid); recordingEndpoints[pid] = device.ID; } continue; }
                    float peak = 0;
                    try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }
                    var previous = sessions.GetValueOrDefault(pid);
                    sessions[pid] = (Math.Max(peak, previous.peak), session.DisplayName, previous.active || (int)session.State == 1);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        }
        var parents = ProcessTree.ReadParents();
        var items = new List<(string key, string path, string name, int pid, int root, float peak, bool audio, bool session, bool recording)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    bool hasSession = sessions.TryGetValue(process.Id, out var session);
                    if (!hasSession && !recording.Contains(process.Id) && process.MainWindowHandle == IntPtr.Zero) continue;
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var root = ProcessTree.FindSameExecutableRoot(process.Id, path, parents);
                    var name = process.MainModule?.FileVersionInfo.FileDescription;
                    if (string.IsNullOrWhiteSpace(name)) name = process.ProcessName;
                    items.Add((AppIdentity.Normalize(path), path, name, process.Id, root, session.peak, hasSession && session.active || recording.Contains(process.Id), hasSession || recording.Contains(process.Id), recording.Contains(process.Id)));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        var apps = items.GroupBy(a => a.key).Select(g => new AudioApplication(
            g.Key, g.First().name, g.First().path, g.Select(a => a.pid).Distinct().ToList(),
            g.Select(a => a.root).Distinct().ToList(), g.Max(a => a.peak), g.Any(a => a.audio), g.Any(a => a.session), g.Any(a => a.recording),
            g.Select(a => recordingEndpoints.GetValueOrDefault(a.pid)).FirstOrDefault(id => id != null)))
            .OrderByDescending(a => a.Peak > .001f).ThenByDescending(a => a.HasAudio).ThenBy(a => a.Name).ToList();
        return new(endpoints.OrderBy(e => e.Capture).ThenByDescending(e => e.Default).ThenBy(e => e.Name).ToList(),
            apps, SuggestChannels(endpoints));
    }

    public IReadOnlyList<int> FindApplicationProcesses(string appKey)
    {
        var result = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
                try { if (AppIdentity.Normalize(p.MainModule?.FileName ?? "") == AppIdentity.Normalize(appKey)) result.Add(p.Id); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return result;
    }

    public static bool IsVirtual(string name) => new[] { "CABLE", "VOICEMEETER", "VIRTUAL", "STEAM STREAMING" }
        .Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    public static List<VirtualChannel> SuggestChannels(IReadOnlyList<AudioEndpoint> endpoints) => ChannelPool.Detect(endpoints);

    public IReadOnlyList<string> CurrentRenderDevices(IEnumerable<int> processIds)
    {
        var ids = processIds.ToHashSet();
        var result = new HashSet<string>();
        using var e = new MMDeviceEnumerator();
        using var devices = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int j = 0; j < sessions.Count; j++)
                {
                    using var s = sessions[j];
                    if (ids.Contains((int)s.GetProcessID) && (int)s.State == 1) result.Add(device.ID);
                }
            }
            catch { }
        }
        return result.ToList();
    }

    public void SetApplicationVolume(IEnumerable<int> processIds, float volume, bool mute)
    {
        var ids = processIds.ToHashSet();
        using var e = new MMDeviceEnumerator();
        using var devices = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int j = 0; j < sessions.Count; j++)
                {
                    using var s = sessions[j];
                    if (ids.Contains((int)s.GetProcessID))
                    {
                        s.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1);
                        s.SimpleAudioVolume.Mute = mute;
                    }
                }
            }
            catch { }
        }
    }
    public string RenderSessionSignature(IEnumerable<int> processIds)
    {
        var ids = processIds.ToHashSet(); var result = new List<string>();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            var sessions = device.AudioSessionManager.Sessions;
            for (int j = 0; j < sessions.Count; j++)
            {
                using var session = sessions[j];
                if (ids.Contains((int)session.GetProcessID) && (int)session.State == 1) result.Add(device.ID + "|" + session.GetSessionInstanceIdentifier);
            }
        }
        return string.Join(";", result.Order());
    }
    internal async Task SettleRenderSessionsAsync(IReadOnlyList<int> pids, Action apply)
    {
        var total = Stopwatch.StartNew(); var stable = Stopwatch.StartNew(); string previous = "";
        do
        {
            string current = RenderSessionSignature(pids);
            if (current != previous) { previous = current; stable.Restart(); }
            apply();
            await Task.Delay(50);
        } while (total.Elapsed < TimeSpan.FromSeconds(1.5) && stable.Elapsed < TimeSpan.FromMilliseconds(300));
    }

    public (float volume, bool mute)? GetApplicationVolume(IEnumerable<int> processIds)
    {
        var ids = processIds.ToHashSet();
        (float volume, bool mute)? fallback = null;
        using var e = new MMDeviceEnumerator();
        using var devices = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var d = devices[i];
            try
            {
                var sessions = d.AudioSessionManager.Sessions;
                for (int j = 0; j < sessions.Count; j++)
                {
                    using var s = sessions[j];
                    if (ids.Contains((int)s.GetProcessID))
                    {
                        var state = (s.SimpleAudioVolume.Volume, s.SimpleAudioVolume.Mute);
                        if ((int)s.State == 1) return state;
                        fallback ??= state;
                    }
                }
            }
            catch { }
        }
        return fallback;
    }
    internal List<ApplicationSessionBinding> BindApplicationSessions(IEnumerable<int> processIds)
    {
        var ids = processIds.ToHashSet();
        var result = new List<ApplicationSessionBinding>();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int j = 0; j < sessions.Count; j++)
                {
                    var session = sessions[j];
                    if (!ids.Contains((int)session.GetProcessID)) { session.Dispose(); continue; }
                    try { result.Add(new(device.ID, session)); }
                    catch { session.Dispose(); throw; }
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException) { }
        }
        return result;
    }
    public void RestoreSessionVolumes(IReadOnlyList<OriginalAudioSession> originals, float? appliedVolume, bool appliedMute)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int j = 0; j < sessions.Count; j++)
                {
                    using var session = sessions[j];
                    var candidates = originals.Where(s => s.EndpointId == device.ID && s.Identifier == session.GetSessionIdentifier);
                    var original = candidates.FirstOrDefault(s => s.Instance == session.GetSessionInstanceIdentifier) ?? candidates.FirstOrDefault();
                    if (original == null) continue;
                    var volume = session.SimpleAudioVolume;
                    bool restoreVolume = appliedVolume is { } applied && Math.Abs(volume.Volume - applied) < .001f;
                    bool restoreMute = volume.Mute == appliedMute;
                    if (restoreVolume) volume.Volume = original.Volume;
                    if (restoreMute) volume.Mute = original.Muted;
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException) { }
        }
    }
    public void RestoreApplicationSessions(OriginalApplicationState original, IReadOnlyList<int> pids, (float volume, bool mute)? before)
    {
        bool volumeOwned = before == null || original.AppliedVolume is { } applied && Math.Abs(before.Value.volume - applied) < .001f;
        bool muteOwned = before == null || before.Value.mute == original.AppliedMute;
        var ids = pids.ToHashSet();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int j = 0; j < sessions.Count; j++)
                {
                    using var session = sessions[j];
                    if (!ids.Contains((int)session.GetProcessID)) continue;
                    var candidates = original.Sessions.Where(s => s.EndpointId == device.ID && s.Identifier == session.GetSessionIdentifier);
                    var saved = candidates.FirstOrDefault(s => s.Instance == session.GetSessionInstanceIdentifier) ?? candidates.FirstOrDefault();
                    if (volumeOwned && (saved?.Volume ?? original.Volume) is { } volume) session.SimpleAudioVolume.Volume = volume;
                    else if ((int)session.State == 1 && before is { } externalVolume) session.SimpleAudioVolume.Volume = externalVolume.volume;
                    if (muteOwned && original.Volume != null) session.SimpleAudioVolume.Mute = saved?.Muted ?? original.Muted;
                    else if ((int)session.State == 1 && before is { } externalMute) session.SimpleAudioVolume.Mute = externalMute.mute;
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException) { }
        }
    }
}

internal static class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static Dictionary<int, int> ReadParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (Process32FirstW(snapshot, ref entry))
                do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }
    public static int FindSameExecutableRoot(int pid, string path, Dictionary<int, int> parents)
    {
        var seen = new HashSet<int>();
        int root = pid;
        while (parents.TryGetValue(root, out var parent) && parent > 0 && seen.Add(parent))
        {
            try
            {
                using var p = Process.GetProcessById(parent);
                if (!string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) break;
                root = parent;
            }
            catch { break; }
        }
        return root;
    }
}
