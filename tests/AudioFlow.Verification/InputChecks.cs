using AudioFlow.Audio;
using AudioFlow.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class InputChecks
{
    public static async Task RecordAsync(string endpoint, string report)
    {
        using var bootstrap = endpoint == "default" ? await new WasapiPlayerBuilder().WithDefaultDeviceStreamRouting().WithLatency(40).BuildAsync() : null;
        if (bootstrap != null)
        {
            bootstrap.Init(new TestToneProvider(1709, 0)); bootstrap.Play();
            Console.WriteLine("RECORD BOOTSTRAP " + Environment.ProcessId);
            var deadline = Environment.TickCount64 + 30000;
            while (!File.Exists(report + ".start"))
            { if (Environment.TickCount64 > deadline) throw new TimeoutException("Recording bootstrap was not configured."); await Task.Delay(50); }
        }
        using var enumerator = new MMDeviceEnumerator();
        using var device = endpoint == "default" ? null : enumerator.GetDevice(endpoint);
        var builder = new WasapiRecorderBuilder().WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(40);
        if (device == null) builder.WithDefaultDeviceStreamRouting(); else builder.WithDevice(device);
        using var recorder = await builder.BuildAsync();
        float peak = 0;
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            foreach (float sample in MemoryMarshal.Cast<byte, float>(buffer)) peak = Math.Max(peak, Math.Abs(sample));
        };
        recorder.StartRecording(); Console.WriteLine("RECORD READY " + Environment.ProcessId);
        bootstrap?.Stop();
        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(100);
            File.WriteAllText(report + ".tmp", JsonSerializer.Serialize(new { Peak = peak }));
            File.Move(report + ".tmp", report, true); peak = 0;
        }
    }
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".cache", "recording-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        var channels = new AudioCatalog().Read().SuggestedChannels.Where(c => c.Provider == "Cable").Take(2).ToArray();
        if (channels.Length < 2) throw new Exception("Two independent channels are required to test isolated recording shutdown.");
        var channel = channels[0];
        var processes = new List<Process>();
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDevice(channel.RenderEndpointId);
        using var player = new WasapiPlayerBuilder().WithDevice(render).WithLatency(40).Build();
        float Peak(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name + ".json"))).RootElement.GetProperty("Peak").GetSingle();
        async Task Until(Func<bool> predicate, string message)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (!predicate()) { if (Environment.TickCount64 > deadline) throw new TimeoutException(message); await Task.Delay(50); }
        }
        try
        {
            foreach (var name in new[] { "RecordA", "RecordB" })
            {
                var exe = Path.Combine(directory, name + ".exe"); File.Copy(Path.Combine(directory, "AudioFlow.Verification.exe"), exe);
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--record-input"); start.ArgumentList.Add("default"); start.ArgumentList.Add(Path.Combine(directory, name + ".json"));
                var process = Process.Start(start)!; processes.Add(process);
                var ready = await process.StandardOutput.ReadLineAsync();
                if (ready == null) throw new Exception(await process.StandardError.ReadToEndAsync());
            }
            var catalog = new AudioCatalog();
            await Until(() => processes.All(p => catalog.GetApplicationVolume([p.Id]) != null), "Recording bootstrap audio sessions did not become available.");
            var oldInput = Enumerable.Range(0, 3).Select(role => AudioPolicy.GetInput(processes[0].Id, role)).ToArray();
            var oldInputB = Enumerable.Range(0, 3).Select(role => AudioPolicy.GetInput(processes[1].Id, role)).ToArray();
            try
            {
                foreach (var process in processes)
                    for (int role = 0; role < 3; role++) AudioPolicy.SetInput(process.Id, role, channel.CaptureEndpointId);
                await Until(() => processes.All(p => AudioPolicy.Unpack(AudioPolicy.GetInput(p.Id, 1)) == channel.CaptureEndpointId), "Recording preferences did not bind before capture initialization.");
                foreach (var name in new[] { "RecordA", "RecordB" }) File.WriteAllText(Path.Combine(directory, name + ".json.start"), "ready");
                foreach (var process in processes)
                {
                    var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    if (ready == null) throw new Exception(await process.StandardError.ReadToEndAsync());
                }
                player.Init(new TestToneProvider(1709, .03f)); player.Play();
                await Until(() => File.Exists(Path.Combine(directory, "RecordA.json")) && File.Exists(Path.Combine(directory, "RecordB.json")), "Recording reports were not created.");
                await Until(() => Peak("RecordA") > .01 && Peak("RecordB") > .01, "Recording test signals did not reach both applications.");
                if (Peak("RecordA") < .01 || Peak("RecordB") < .01) throw new Exception("Default recording streams did not follow the selected per-application virtual microphone.");
                if (AudioPolicy.Unpack(AudioPolicy.GetInput(processes[0].Id, 1)) != channel.CaptureEndpointId) throw new Exception("Per-application recording preference did not apply.");
                string engineDirectory = Path.Combine(directory, "engine");
                float mutedA, audibleB; string recoverySnapshot;
                await using (var engine = new AudioEngine(engineDirectory))
                {
                    var graph = new RouteGraph { Channels = channels.ToList(), SilentInputChannelId = channels[1].Id,
                        Nodes = [new() { Id = "record-A", Kind = NodeKind.Application, Name = "Record A", Key = processes[0].MainModule!.FileName,
                            InputDeviceKey = AudioOutput.None, RecordingConfigured = true }] };
                    ApplicationOutputs.Ensure(graph);
                    await engine.ApplyAsync(graph); await Task.Delay(1000); await engine.RefreshAsync();
                    mutedA = Peak("RecordA"); audibleB = Peak("RecordB");
                    if (!engine.InputStatus("record-A").StartsWith("录音已关闭")) throw new Exception(engine.InputStatus("record-A"));
                    recoverySnapshot = File.ReadAllText(Path.Combine(engineDirectory, "input-recovery.json"));
                    await engine.StopAndRestoreAsync();
                    if (engine.PendingRecoveryCount != 0) throw new Exception("Recording-only application did not restore its input and output preferences.");
                }
                Console.WriteLine($"INPUT MUTE A={mutedA} B={audibleB}");
                if (mutedA > .001 || audibleB < .01) throw new Exception("Per-application recording mute did not isolate the target application.");
                for (int role = 0; role < 3; role++) AudioPolicy.SetInput(processes[0].Id, role, channel.CaptureEndpointId);
                await Task.Delay(1000);
                if (Peak("RecordA") < .01) throw new Exception("Recording did not resume after restoring its original mute state.");
                File.WriteAllText(Path.Combine(engineDirectory, "input-recovery.json"), recoverySnapshot);
                for (int role = 0; role < 3; role++) AudioPolicy.SetInput(processes[0].Id, role, channels[1].CaptureEndpointId);
                await Task.Delay(500);
                await using (var recovery = new AudioEngine(engineDirectory, recoveryOnly: true))
                {
                    await recovery.RecoverAsync();
                    if (recovery.PendingRecoveryCount != 0 || AudioPolicy.Unpack(AudioPolicy.GetInput(processes[0].Id, 1)) != channel.CaptureEndpointId)
                        throw new Exception("Persisted recording preference recovery did not restore the recording-only application.");
                }
                await Task.Delay(500);
                if (Peak("RecordA") < .01 || Peak("RecordB") < .01) throw new Exception("Capture signal was not restored after recording recovery.");
                File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new { Passed = true, EngineVerified = true, RecoveryVerified = true, MutedA = mutedA, AudibleB = audibleB }, SceneStore.JsonOptions));
                Console.WriteLine("INPUT CHECKS PASSED " + directory);
            }
            finally
            {
                for (int role = 0; role < 3; role++) AudioPolicy.SetInput(processes[0].Id, role, oldInput[role]);
                for (int role = 0; role < 3; role++) AudioPolicy.SetInput(processes[1].Id, role, oldInputB[role]);
            }
        }
        finally
        {
            player.Stop();
            foreach (var process in processes) { if (!process.HasExited) process.Kill(); process.Dispose(); }
        }
    }
}
