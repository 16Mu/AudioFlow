using AudioFlow.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioBuffer = AudioFlow.Core.AudioBuffer;

namespace AudioFlow.Audio;

public sealed class AudioEngine : IAsyncDisposable
{
    private readonly AudioCatalog catalog = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, SourceRuntime> sources = new();
    private readonly Dictionary<string, OutputRuntime> outputs = [];
    private readonly ConcurrentDictionary<string, AudioRouteStatus> statuses = new();
    private readonly RouteJournal journal;
    private readonly ApplicationInputs inputs;
    private readonly ConcurrentDictionary<string, string> inputStatuses = new();
    public string InputStatus(string nodeId) => inputStatuses.GetValueOrDefault(nodeId) ?? "";
    private readonly Action<string>? log;
    private readonly Stopwatch duration = new();
    private readonly CancellationTokenSource lifetime = new();
    private RouteGraph? desired;
    private string lastInventory = "";
    private bool disposed;
    private bool deviceRecoveryRequested;
    public bool Running => desired != null;
    public int PendingRecoveryCount => journal.PendingCount + inputs.PendingCount;

    public AudioEngine(string dataDirectory, Action<string>? log = null, bool recoveryOnly = false)
    {
        Directory.CreateDirectory(dataDirectory);
        journal = new(dataDirectory, recoveryOnly);
        inputs = new(dataDirectory);
        this.log = log;
    }

    public async Task RecoverAsync()
    {
        await gate.WaitAsync();
        try { deviceRecoveryRequested = true; await Task.Run(() => inputs.Restore(catalog)); await journal.RestoreAllAsync(catalog, true); }
        finally { gate.Release(); }
    }
    public void SetDeviceVolume(EndpointVolumeBinding binding, float value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        deviceRecoveryRequested = false; journal.SetDeviceVolume(binding, value);
    }
    public void SetDeviceMuted(EndpointVolumeBinding binding, bool value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        deviceRecoveryRequested = false; journal.SetDeviceMuted(binding, value);
    }
    public async Task ApplyAsync(RouteGraph graph)
    {
        var copy = SceneStore.Clone(graph);
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var snapshot = await Task.Run(catalog.Read);
            var compiled = GraphCompiler.Compile(copy, snapshot.Endpoints);
            if (desired == null) duration.Restart();
            desired = copy;
            lastInventory = InventorySignature(snapshot);
            await ApplyCompiledAsync(compiled, snapshot);
            foreach (var warning in compiled.Warnings) log?.Invoke(warning);
        }
        finally { gate.Release(); }
    }

    /// <summary>Call on a slow control timer, never on the realtime/audio/UI callback.</summary>
    public async Task RefreshAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (desired != null)
            {
                var snapshot = await Task.Run(catalog.Read);
                var signature = InventorySignature(snapshot);
                if (signature != lastInventory || statuses.Values.Any(s => s.State is "Waiting" or "Restart" or "Error"))
                {
                    lastInventory = signature;
                    await ApplyCompiledAsync(GraphCompiler.Compile(desired, snapshot.Endpoints), snapshot);
                }
            }
            var managed = desired?.Nodes.Where(n => n.Kind == NodeKind.Application).Select(n => AppIdentity.Normalize(n.Key)).ToHashSet() ?? [];
            foreach (var key in journal.States.Where(s => !managed.Contains(AppIdentity.Normalize(s.Key))).Select(s => s.Key).ToArray())
                await journal.RestoreAsync(key, catalog);
            if (deviceRecoveryRequested) await Task.Run(journal.RestoreDevices);
            await RefreshInputsAsync();
        }
        catch (Exception ex) { log?.Invoke("设备刷新：" + ex.Message); }
        finally { gate.Release(); }
    }

    private static string InventorySignature(AudioCatalogSnapshot snapshot) =>
        string.Join("|", snapshot.Endpoints.Select(e => e.Id + ":" + e.Default)) + "::" +
        string.Join("|", snapshot.Applications.Select(a => a.Key + ":" + string.Join(",", a.ProcessIds) + ":" + a.HasAudio));

    private async Task ApplyCompiledAsync(CompiledGraph compiled, AudioCatalogSnapshot snapshot)
    {
        await RefreshInputsAsync(snapshot);
        var desiredIds = compiled.Sources.Select(s => s.Node.Id).ToHashSet();
        foreach (var id in sources.Keys.Where(id => !desiredIds.Contains(id)).ToArray())
            await RemoveSourceAsync(id, true);
        foreach (var id in statuses.Keys.Where(id => !desiredIds.Contains(id)).ToArray()) statuses.TryRemove(id, out _);

        foreach (var route in compiled.Sources)
        {
            var node = route.Node;
            var app = snapshot.Applications.FirstOrDefault(a => a.Key == AppIdentity.Normalize(node.Key));
            var pids = node.Kind == NodeKind.Application
                ? app?.ProcessIds.ToList() ?? (await Task.Run(() => catalog.FindApplicationProcesses(node.Key))).ToList()
                : [];
            var roots = app?.RootProcessIds.ToList() ?? pids;
            var fingerprint = node.Kind + ":" + node.Key + ":" + route.Primary?.EndpointId + ":" + route.Primary?.SystemDefault + ":" +
                string.Join(";", route.Destinations.Select(d => d.EndpointId)) + ":" + string.Join(",", roots);
            if (sources.TryGetValue(node.Id, out var current) && current.Fingerprint == fingerprint && !current.Faulted)
            {
                if (current.NativeOnly) current.RefreshNativeMeters(pids, catalog);
                bool audioStarted = app?.HasAudio == true && !current.AudioObserved;
                current.AudioObserved = app?.HasAudio ?? false;
                if (route.Primary is { } retainedPrimary && (audioStarted || current.Node.Volume != node.Volume || current.Node.Muted != node.Muted || current.PrimaryVolume != retainedPrimary.Volume))
                    await Task.Run(() =>
                    {
                        var state = journal.Remember(node.Key, pids, catalog);
                        journal.Applied(state, node.Volume * retainedPrimary.Volume, node.Muted);
                        catalog.SetApplicationVolume(pids, state.AppliedVolume!.Value, node.Muted);
                    });
                current.UpdateControls(node, route.Primary?.Volume ?? 1, route.Mirrors);
                continue;
            }
            await RemoveSourceAsync(node.Id, false);
            if (node.Kind == NodeKind.Application && pids.Count == 0)
            {
                statuses[node.Id] = new(node.Id, "Waiting", "等待软件运行并播放声音");
                continue;
            }
            if (route.Destinations.Count == 0)
            {
                if (node.Kind == NodeKind.Application)
                {
                    await journal.RestoreAsync(node.Key, catalog);
                    if (node.VolumeManaged || node.Muted) await Task.Run(() =>
                    {
                        var state = journal.Remember(node.Key, pids, catalog);
                        journal.Applied(state, node.Volume, node.Muted);
                        catalog.SetApplicationVolume(pids, node.Volume, node.Muted);
                    });
                }
                statuses[node.Id] = new(node.Id, "Idle", node.Muted ? "此来源已静音" : "使用 Windows 原输出");
                continue;
            }
            if (node.Kind == NodeKind.Microphone && !snapshot.Endpoints.Any(e => e.Capture && e.Id == node.Key))
            {
                statuses[node.Id] = new(node.Id, "Waiting", "麦克风设备未连接");
                continue;
            }
            try
            {
                if (route.Primary is { } primary)
                {
                    await Task.Run(() =>
                    {
                        var state = journal.Remember(node.Key, pids, catalog);
                        var preferred = primary.SystemDefault ? null : primary.EndpointId;
                        journal.Applied(state, node.Volume * primary.Volume, node.Muted, true, preferred);
                        if (primary.Volume == 0) catalog.SetApplicationVolume(pids, 0, node.Muted);
                        foreach (var pid in pids)
                            for (int role = 0; role < 3; role++) AudioPolicy.SetOutput(pid, role, preferred);
                    });
                    // Apps explicitly binding a device may ignore per-app defaults.
                    // Do not start mirrors or falsely mark such a route connected.
                    await Task.Delay(160, lifetime.Token);
                    var active = await Task.Run(() => catalog.CurrentRenderDevices(pids));
                    if (primary.Volume > 0 && active.Count > 0 && active.Any(id => id != primary.EndpointId))
                    {
                        statuses[node.Id] = new(node.Id, "Restart", "设备已选好。请重新打开这个软件，让声音换到新设备。");
                        continue;
                    }
                    // Stream routing can create a new session with default gain.
                    // Apply the requested primary gain again after the device move.
                    await Task.Run(async () =>
                    {
                        await catalog.SettleRenderSessionsAsync(pids, () =>
                        {
                            journal.Remember(node.Key, pids, catalog);
                            catalog.SetApplicationVolume(pids, node.Volume * primary.Volume, node.Muted);
                        });
                    });
                }
                var runtime = new SourceRuntime(node, fingerprint, log, route.Primary?.Volume ?? 1) { AudioObserved = app?.HasAudio ?? false };
                foreach (var destination in route.Mirrors)
                {
                    if (!outputs.TryGetValue(destination.EndpointId, out var output) || output.Faulted)
                    {
                        if (output != null) { await output.DisposeAsync(); outputs.Remove(destination.EndpointId); }
                        output = await OutputRuntime.CreateAsync(destination.EndpointId, log);
                        outputs[destination.EndpointId] = output;
                    }
                    runtime.Outputs.Add(output);
                    runtime.DestinationVolumes[destination.EndpointId] = destination.Volume;
                }
                sources[node.Id] = runtime;
                if (node.Kind == NodeKind.Application)
                {
                    // A primary-only route is observed via the existing session meter.
                    // Mirrors require one process-tree recorder per independent root.
                    if (route.Mirrors.Count == 0) runtime.RefreshNativeMeters(pids, catalog);
                    else foreach (var root in roots.Distinct()) await runtime.AddRecorderAsync((uint)root, null);
                }
                else await runtime.AddRecorderAsync(null, node.Key);
                statuses[node.Id] = new(node.Id, "Active",
                    route.Destinations.Any(d => d.Microphone) ? "声音正在传过去 · 请在接收软件里选用相应麦克风" : "正在从选好的设备播放");
            }
            catch (Exception ex)
            {
                await RemoveSourceAsync(node.Id, false);
                statuses[node.Id] = new(node.Id, "Error", "声音暂时没能传过去：" + UserError.Describe(ex));
                log?.Invoke(node.Name + "：" + ex.Message);
            }
        }
        var used = sources.Values.SelectMany(s => s.Outputs).Select(o => o.EndpointId).ToHashSet();
        foreach (var id in outputs.Keys.Where(id => !used.Contains(id)).ToArray())
        { await outputs[id].DisposeAsync(); outputs.Remove(id); }
    }

    private async Task RefreshInputsAsync(AudioCatalogSnapshot? snapshot = null)
    {
        if (desired == null) return;
        snapshot ??= await Task.Run(catalog.Read);
        var graph = desired;
        var keep = graph.Nodes.Where(n => n.Kind == NodeKind.Application && (n.InputDeviceKey != null || n.ChannelId != null))
            .Select(n => AppIdentity.Normalize(n.Key)).ToHashSet();
        await Task.Run(() => inputs.Restore(catalog, keep));
        foreach (var node in graph.Nodes.Where(n => n.Kind == NodeKind.Application))
        {
            try
            {
                var app = snapshot.Applications.FirstOrDefault(a => a.Key == AppIdentity.Normalize(node.Key));
                var pids = app?.ProcessIds ?? catalog.FindApplicationProcesses(node.Key);
                inputStatuses[node.Id] = await Task.Run(() => inputs.Apply(node, graph, pids, snapshot.Endpoints));
            }
            catch (Exception error) { inputStatuses[node.Id] = "麦克风还没换好：" + UserError.Describe(error); log?.Invoke(error.ToString()); }
        }
    }

    private async Task RemoveSourceAsync(string id, bool restore)
    {
        if (!sources.TryRemove(id, out var source)) return;
        await source.DisposeAsync();
        if (restore && source.Node.Kind == NodeKind.Application)
            await journal.RestoreAsync(source.Node.Key, catalog);
    }

    public EngineSnapshot Snapshot()
    {
        var levels = sources.Values.Select(s => new SourceLevel(s.Node.Id, s.Peak)).ToList();
        long overruns = 0, underruns = 0;
        foreach (var source in sources.Values)
            foreach (var input in source.Inputs) { overruns += input.Buffer.Overruns; underruns += input.Buffer.Underruns; }
        foreach (var source in sources.Values.Where(s => s.Faulted))
            statuses[source.Node.Id] = new(source.Node.Id, "Error", "音频设备已断开，等待重新连接");
        return new(statuses.Values.ToList(), levels, overruns, underruns, duration.Elapsed.TotalSeconds);
    }

    public async Task StopAndRestoreAsync(bool restoreDevices = false)
    {
        await gate.WaitAsync();
        try
        {
            desired = null; duration.Stop();
            foreach (var id in sources.Keys.ToArray())
                try { await RemoveSourceAsync(id, false); } catch (Exception error) { log?.Invoke("停止音频来源：" + error.Message); }
            foreach (var output in outputs.Values)
                try { await output.DisposeAsync(); } catch (Exception error) { log?.Invoke("停止播放设备：" + error.Message); }
            outputs.Clear(); statuses.Clear();
            deviceRecoveryRequested |= restoreDevices;
            await journal.RestoreAllAsync(catalog, restoreDevices);
            await Task.Run(() => inputs.Restore(catalog)); inputStatuses.Clear();
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await StopAndRestoreAsync(true);
        lifetime.Cancel();
        disposed = true;
        journal.Dispose(); lifetime.Dispose(); gate.Dispose();
    }
}

internal sealed class MixerInput(string id, AudioBuffer buffer)
{
    public string Id { get; } = id;
    public AudioBuffer Buffer { get; } = buffer;
}

internal sealed class OutputRuntime : IWaveProvider, IAsyncDisposable
{
    private readonly object inputGate = new();
    private MixerInput[] inputs = [];
    private WasapiPlayer? player;
    private MMDevice? device;
    public string EndpointId { get; private init; } = "";
    public bool Faulted { get; private set; }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public static Task<OutputRuntime> CreateAsync(string endpointId, Action<string>? log) => Task.Run(() =>
    {
        var output = new OutputRuntime { EndpointId = endpointId };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            output.device = enumerator.GetDevice(endpointId);
            output.player = new WasapiPlayerBuilder().WithDevice(output.device).WithLatency(40)
                .WithEventSync().WithMmcssThreadPriority("Pro Audio").Build();
            output.player.Init(output);
            output.player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) { output.Faulted = true; log?.Invoke("输出设备：" + e.Exception.Message); }
            };
            output.player.Play();
            return output;
        }
        catch { output.player?.Dispose(); output.device?.Dispose(); throw; }
    });
    public MixerInput AddInput(string id, float gain)
    {
        var input = new MixerInput(id, new AudioBuffer()) { };
        input.Buffer.Gain = gain;
        lock (inputGate) { inputs = [.. inputs, input]; }
        return input;
    }
    public void RemoveInput(MixerInput input)
    {
        lock (inputGate) { inputs = inputs.Where(i => i != input).ToArray(); }
    }
    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        samples.Clear();
        foreach (var input in Volatile.Read(ref inputs)) input.Buffer.MixInto(samples);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1, 1) : 0;
        return buffer.Length;
    }
    public async ValueTask DisposeAsync()
    {
        if (player != null) { await player.DisposeAsync(); player = null; }
        device?.Dispose(); device = null;
    }
}

internal sealed class SourceRuntime(RouteNode node, string fingerprint, Action<string>? log, float primaryVolume = 1) : IAsyncDisposable
{
    private ApplicationSessionBinding[] nativeMeters = [];
    public bool NativeOnly => Node.Kind == NodeKind.Application && Outputs.Count == 0;
    public void RefreshNativeMeters(IReadOnlyList<int> pids, AudioCatalog catalog)
    {
        var next = catalog.BindApplicationSessions(pids).ToArray();
        var previous = Interlocked.Exchange(ref nativeMeters, next);
        foreach (var binding in previous) binding.Dispose();
    }
    private readonly List<WasapiRecorder> recorders = [];
    private readonly List<DeviceRecorder> deviceRecorders = [];
    private readonly List<MMDevice> devices = [];
    private long lastDataTicks;
    private float peak;
    public RouteNode Node { get; } = node;
    public string Fingerprint { get; } = fingerprint;
    public float PrimaryVolume => primaryVolume;
    public bool AudioObserved { get; set; }
    public List<OutputRuntime> Outputs { get; } = [];
    public Dictionary<string, float> DestinationVolumes { get; } = [];
    public ConcurrentBag<MixerInput> Inputs { get; } = [];
    private readonly List<(OutputRuntime Output, MixerInput Input)> bindings = [];
    public bool Faulted => Outputs.Any(o => o.Faulted) || recorderFault;
    private bool recorderFault;
    public void UpdateControls(RouteNode current, float primaryGain, IReadOnlyList<Destination> destinations)
    {
        Node.Volume = current.Volume; Node.Muted = current.Muted; Node.VolumeManaged = current.VolumeManaged;
        primaryVolume = primaryGain;
        foreach (var destination in destinations) DestinationVolumes[destination.EndpointId] = destination.Volume;
        foreach (var binding in bindings)
        {
            var volume = DestinationVolumes.GetValueOrDefault(binding.Output.EndpointId, 1);
            binding.Input.Buffer.Gain = Node.Muted ? 0 : Node.Kind == NodeKind.Microphone ? Node.Volume * volume : primaryVolume > 0 ? volume / primaryVolume : 0;
            if (Node.Muted) binding.Input.Buffer.Clear();
        }
    }
    public float Peak
    {
        get
        {
            if (NativeOnly)
            {
                float maximum = 0;
                foreach (var meter in Volatile.Read(ref nativeMeters)) maximum = Math.Max(maximum, meter.Peak);
                return primaryVolume > 0 ? maximum / primaryVolume : 0;
            }
            if (Stopwatch.GetTimestamp() - Volatile.Read(ref lastDataTicks) > Stopwatch.Frequency / 3) return 0;
            return Node.Kind == NodeKind.Application ? primaryVolume > 0 ? Volatile.Read(ref peak) / primaryVolume : 0 : Volatile.Read(ref peak) * Node.Volume;
        }
    }
    public async Task AddRecorderAsync(uint? pid, string? captureEndpoint)
    {
        if (pid == null)
        {
            var deviceRecorder = await Task.Run(() => new DeviceRecorder(captureEndpoint!));
            var microphoneInputs = AddInputs("mic");
            deviceRecorder.DataAvailable += floats => Push(floats, microphoneInputs);
            deviceRecorder.Failed += error => { recorderFault = true; log?.Invoke(Node.Name + " 捕获：" + error.Message); };
            deviceRecorders.Add(deviceRecorder);
            await Task.Run(deviceRecorder.Start);
            return;
        }
        var recorder = await Task.Run(async () =>
        {
            var builder = new WasapiRecorderBuilder().WithBufferLength(40).WithEventSync()
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithMmcssThreadPriority("Pro Audio");
            if (pid is { } process) builder.WithProcessLoopback(process, ProcessLoopbackMode.IncludeTargetProcessTree);
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(captureEndpoint!);
                devices.Add(device);
                using var audioClient = device.CreateAudioClient();
                builder.WithDevice(device).WithFormat(audioClient.MixFormat);
            }
            return await builder.BuildAsync();
        });
        var localBindings = AddInputs(pid.Value.ToString());
        var converter = new SampleConverter(recorder.WaveFormat);
        recorder.DataAvailable += (buffer, flags, _, _) => Push(converter.Convert(buffer), localBindings);
        recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) { recorderFault = true; log?.Invoke(Node.Name + " 捕获：" + e.Exception.Message); }
        };
        recorders.Add(recorder); recorder.StartRecording();
    }
    private MixerInput[] AddInputs(string suffix) => Outputs.Select(output =>
        {
            // Process loopback is captured after the application's session gain.
            // Physical microphone gain is owned by AudioFlow's DSP path.
            var routeVolume = DestinationVolumes.GetValueOrDefault(output.EndpointId, 1);
            var gain = Node.Muted ? 0 : Node.Kind == NodeKind.Microphone ? Node.Volume * routeVolume : primaryVolume > 0 ? routeVolume / primaryVolume : 0;
            var input = output.AddInput(Node.Id + ":" + suffix, gain);
            Inputs.Add(input); bindings.Add((output, input));
            return input;
        }).ToArray();
    private void Push(ReadOnlySpan<float> floats, MixerInput[] localBindings)
        {
            float max = 0;
            foreach (float value in floats) max = Math.Max(max, Math.Abs(value));
            Volatile.Write(ref peak, Node.Muted ? 0 : max);
            Volatile.Write(ref lastDataTicks, Stopwatch.GetTimestamp());
            if (Node.Muted) return;
            foreach (var input in localBindings) input.Buffer.Write(floats);
        }
    public async ValueTask DisposeAsync()
    {
        foreach (var binding in Interlocked.Exchange(ref nativeMeters, [])) binding.Dispose();
        foreach (var recorder in recorders) await recorder.DisposeAsync();
        recorders.Clear();
        foreach (var recorder in deviceRecorders) await Task.Run(recorder.Dispose);
        deviceRecorders.Clear();
        foreach (var binding in bindings) binding.Output.RemoveInput(binding.Input);
        bindings.Clear(); Inputs.Clear();
        foreach (var d in devices) d.Dispose();
        devices.Clear();
    }
}
