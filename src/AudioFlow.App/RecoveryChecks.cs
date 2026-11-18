using AudioFlow.Audio;
using AudioFlow.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;

namespace AudioFlow.App;

internal static class RecoveryChecks
{
    public static async Task RunAsync(MainWindow window, MainViewModel model, string directory, List<string> checks)
    {
        var originalGraph = SceneStore.Clone(model.Graph);
        var originalView = model.Workspace.View;
        var originalFocus = model.Workspace.FocusedApplication;
        var paths = VerificationRuntime.CreateTones(Path.Combine(directory, "recovery-tones"), "RecoveryTone");
        using var tone = VerificationRuntime.StartTone(paths[0], 991);
        Process? restarted = null;
        Process? recoveryWorker = null;
        Process? idle = null;
        var catalog = new AudioCatalog();
        var channels = model.Catalog.SuggestedChannels.Take(2).ToArray();
        var speaker = model.Catalog.Endpoints.First(e => !e.Capture && e.Name.Contains("Senary", StringComparison.OrdinalIgnoreCase));
        var volumeDevice = model.Catalog.Endpoints.First(e => !e.Capture && e.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase));
        using var device = new EndpointVolumeBinding(volumeDevice.Id);
        var originalDevice = device.State;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Until(Func<bool> predicate, string failure)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
            if (!predicate()) throw new InvalidOperationException(failure);
        }
        string? Output(Process process) => AudioPolicy.Unpack(AudioPolicy.GetOutput(process.Id, 1));
        float Volume(Process process) => catalog.GetApplicationVolume([process.Id])?.volume ?? -1;
        RouteGraph Routed(float volume = .27f) => new()
        {
            Nodes = [new() { Id = "recover-source", Name = "恢复测试音", Key = paths[0], Kind = NodeKind.Application, Volume = volume },
                new() { Id = "recover-output", Name = channels[0].Name, Kind = NodeKind.Playback, Key = channels[0].RenderEndpointId,
                    PrimaryOutput = true, OwnerAppKey = paths[0] }],
            Channels = channels.ToList(), Connections = [new() { SourceId = "recover-source", TargetId = "recover-output", Mode = RouteMode.Playback, Automatic = true }]
        };
        var childDirectories = new List<string>();
        try
        {
            await tone.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await model.RefreshCatalogAsync();
            catalog.SetApplicationVolume([tone.Id], .64f, false);
            model.ImportConfiguration(new() { Channels = channels.ToList() });
            await model.SwitchViewAsync(true);
            var app = model.AddBlank(NodeKind.Application);
            model.ChooseCard(app, new("恢复测试音", paths[0], NodeKind.Application, "测试软件"));
            var defaultNode = model.Nodes.First(n => n.Model.Key == AudioOutput.SystemDefault);
            Check(model.Graph.Connections.Any(c => c.SourceId == app.Id && c.TargetId == defaultNode.Id && c.Automatic),
                "new application configuration has a visible Windows default output connection");
            var deviceNode = model.AddBlank(NodeKind.Playback);
            model.ChooseCard(deviceNode, new(volumeDevice.Name, volumeDevice.Id, NodeKind.Playback, "测试设备"));
            await model.StartAsync();
            await Until(() => Output(tone) == null && catalog.CurrentRenderDevices([tone.Id]).Contains(model.Catalog.Endpoints.First(e => !e.Capture && e.Default).Id),
                "Automatic output did not use the current Windows default playback device.");
            model.Nodes.First(n => n.Id == app.Id).Volume = .31f;
            model.Nodes.First(n => n.Id == deviceNode.Id).DeviceVolume = .23f;
            await Until(() => Math.Abs(Volume(tone) - .31f) < .01, "Default output source volume did not apply.");
            var restoreButton = (Button)window.FindName("RestoreOriginal");
            Check(restoreButton.Command == model.RestoreCommand && restoreButton.Command.CanExecute(null),
                "the visible restore button is bound to the shared restoration command");
            restoreButton.Command.Execute(null);
            await Until(() => !model.Running && !model.Busy && Math.Abs(Volume(tone) - .64f) < .01 &&
                Math.Abs(device.State.Volume - originalDevice.Volume) < .001, "Restore button did not return application and device volumes to their original values.");
            Check(Output(tone) == null && model.Paused && model.Graph.Connections.Count == 1,
                "restore button restores Windows state while preserving the shared configuration");
            await model.StartAsync();
            await Until(() => Math.Abs(Volume(tone) - .31f) < .01, "Restore test could not restart the source.");
            catalog.SetApplicationVolume([tone.Id], .48f, true);
            await model.RestoreOriginalStateAsync();
            Check(Math.Abs(Volume(tone) - .48f) < .01 && catalog.GetApplicationVolume([tone.Id])?.mute == true,
                "restoration preserves newer application volume and mute changes made outside AudioFlow");
            catalog.SetApplicationVolume([tone.Id], .64f, false);
            for (int role = 0; role < 3; role++) AudioPolicy.SetOutput(tone.Id, role, speaker.Id);
            await Until(() => catalog.CurrentRenderDevices([tone.Id]).Contains(speaker.Id), "Original application output fixture did not move to its device.");
            catalog.SetApplicationVolume([tone.Id], .64f, false);

            async Task VerifyChild(bool hold)
            {
                var childDirectory = Path.Combine(directory, hold ? "crash-recovery" : "exit-recovery");
                childDirectories.Add(childDirectory); Directory.CreateDirectory(childDirectory);
                var graph = Routed(.22f); graph.Nodes[0].Muted = true;
                var scene = Path.Combine(childDirectory, "routing.json"); model.Store.Save(graph, scene);
                using var child = VerificationRuntime.Start(Environment.ProcessPath!, "--verify-audio-lifecycle", childDirectory, scene, volumeDevice.Id, hold ? "hold" : "exit");
                try
                {
                    await Until(() => File.Exists(Path.Combine(childDirectory, "lifecycle-ready.json")) || child.HasExited,
                        "Audio lifecycle helper did not initialize.");
                    if (!File.Exists(Path.Combine(childDirectory, "lifecycle-ready.json")))
                        throw new InvalidOperationException(File.Exists(Path.Combine(childDirectory, "lifecycle-error.log"))
                            ? File.ReadAllText(Path.Combine(childDirectory, "lifecycle-error.log")) : "Audio lifecycle helper exited before routing.");
                    using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(childDirectory, "lifecycle-ready.json")));
                    Check(ready.RootElement.GetProperty("Running").GetBoolean() &&
                        ready.RootElement.GetProperty("Outputs").EnumerateArray().All(e => e.GetString() == channels[0].RenderEndpointId),
                        hold ? "recovery helper routed real audio before forced termination" : "exit helper routed real audio before closing its window");
                    if (hold)
                    {
                        child.Kill(); await child.WaitForExitAsync();
                        await using var recovery = new AudioEngine(childDirectory);
                        await recovery.RecoverAsync();
                    }
                    else
                    {
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                        if (child.ExitCode != 0) throw new InvalidOperationException("Normal audio lifecycle exit failed.");
                    }
                    await Until(() => Output(tone) == speaker.Id && Math.Abs(Volume(tone) - .64f) < .01 &&
                        catalog.GetApplicationVolume([tone.Id])?.mute == false && Math.Abs(device.State.Volume - originalDevice.Volume) < .001,
                        "Lifecycle restoration did not restore the original output, application gain, mute and device volume.");
                    Check(Output(tone) == speaker.Id, hold ? "persisted recovery restores audio after an unexpected process exit" :
                        "normal window exit automatically restores output, volume, mute and device settings");
                }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
            await VerifyChild(false);
            await VerifyChild(true);

            model.ImportConfiguration(Routed());
            await model.StartAsync();
            await Until(() => Output(tone) == channels[0].RenderEndpointId && Math.Abs(Volume(tone) - .27f) < .01, "Closed app recovery fixture did not activate.");
            var recoveryFile = Path.Combine(model.Store.DataDirectory, "audio-recovery.json");
            var closedState = JsonSerializer.Deserialize<AudioRecoveryState>(File.ReadAllText(recoveryFile), SceneStore.JsonOptions)!.Applications
                .First(s => AppIdentity.Normalize(s.Key) == AppIdentity.Normalize(paths[0]));
            tone.Kill(); await tone.WaitForExitAsync();
            await model.RefreshCatalogAsync();
            Check(model.Applications.Any(a => AppIdentity.Normalize(a.Key) == AppIdentity.Normalize(paths[0]) && a.Detail.Contains("等待运行")),
                "configured application remains in the sidebar after its audio process exits");
            await model.RestoreOriginalStateAsync();
            Check(!model.Notice.StartsWith("部分设置未能恢复"), "closed application restoration completes without leaving a retry error");
            File.WriteAllText(Path.Combine(directory, "closed-restoration.json"), JsonSerializer.Serialize(new
            {
                Original = closedState, Notice = model.Notice,
                Recovery = JsonSerializer.Deserialize<AudioRecoveryState>(File.ReadAllText(recoveryFile), SceneStore.JsonOptions)
            }, SceneStore.JsonOptions));
            Check(model.Engine.PendingRecoveryCount == 1, "closed application retains a pending recovery record instead of reporting false success");
            restarted = VerificationRuntime.StartTone(paths[0], 991);
            await restarted.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using (var freshPolicy = VerificationRuntime.Start(Path.Combine(Path.GetDirectoryName(paths[0])!, "AudioFlow.Verification.exe"), "--policy-get", restarted.Id.ToString()))
            {
                var freshResult = await freshPolicy.StandardOutput.ReadToEndAsync();
                await freshPolicy.WaitForExitAsync();
                File.WriteAllText(Path.Combine(directory, "reopened-audio.json"), JsonSerializer.Serialize(new
                {
                    Output = Output(restarted), Devices = catalog.CurrentRenderDevices([restarted.Id]),
                    Volume = Volume(restarted), FreshPolicy = freshResult
                }, SceneStore.JsonOptions));
            }
            await model.Engine.RefreshAsync();
            await Until(() => Output(restarted) == speaker.Id && catalog.CurrentRenderDevices([restarted.Id]).Contains(speaker.Id) && model.Engine.PendingRecoveryCount == 0,
                "A reopened application's routing was not restored: actual=" + Output(restarted) + ", expected=" + speaker.Id);
            Check(Output(restarted) == speaker.Id && model.Engine.PendingRecoveryCount == 0,
                "pending restoration restores real output and clears its journal when a closed application resumes playing");
            await Until(() => Math.Abs(Volume(restarted) - .64f) < .01 && catalog.GetApplicationVolume([restarted.Id])?.mute == false,
                "Restored application volume did not return before relaunch: " + Volume(restarted));
            Check(Math.Abs(Volume(restarted) - .64f) < .01 && catalog.GetApplicationVolume([restarted.Id])?.mute == false,
                "relaunching a restored application retains its pre-routing volume and mute state: actual=" + Volume(restarted) + ", mute=" + catalog.GetApplicationVolume([restarted.Id])?.mute);

            var closedExitDirectory = Path.Combine(directory, "closed-exit-recovery");
            childDirectories.Add(closedExitDirectory); Directory.CreateDirectory(closedExitDirectory);
            var closedExitScene = Path.Combine(closedExitDirectory, "routing.json");
            var closedExitGraph = Routed(.22f); closedExitGraph.Nodes[0].Muted = true;
            model.Store.Save(closedExitGraph, closedExitScene);
            using (var child = VerificationRuntime.Start(Environment.ProcessPath!, "--verify-audio-lifecycle",
                closedExitDirectory, closedExitScene, volumeDevice.Id, "wait-close"))
            {
                try
                {
                    await Until(() => File.Exists(Path.Combine(closedExitDirectory, "lifecycle-ready.json")), "Closed exit helper did not start.");
                    await Until(() => Output(restarted) == channels[0].RenderEndpointId && catalog.GetApplicationVolume([restarted.Id])?.mute == true,
                        "Closed exit helper did not route and mute the target.");
                    restarted.Kill(); await restarted.WaitForExitAsync(); restarted.Dispose(); restarted = null;
                    File.WriteAllText(Path.Combine(closedExitDirectory, "lifecycle-close"), "close");
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                    Check(child.ExitCode == 0, "desktop window exits after handing a closed application's restoration to the recovery worker");
                }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
            var workerStatus = Path.Combine(closedExitDirectory, "recovery-worker.json");
            bool WorkerPending()
            {
                if (!File.Exists(workerStatus)) return false;
                using var status = JsonDocument.Parse(File.ReadAllText(workerStatus));
                return status.RootElement.GetProperty("Pending").GetInt32() == 1;
            }
            await Until(WorkerPending,
                "Pending recovery worker did not keep the closed application's journal.");
            using (var status = JsonDocument.Parse(File.ReadAllText(workerStatus)))
                recoveryWorker = Process.GetProcessById(status.RootElement.GetProperty("ProcessId").GetInt32());
            idle = VerificationRuntime.Start(paths[0], "--idle");
            await idle.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(600);
            Check(!recoveryWorker.HasExited && JsonSerializer.Deserialize<AudioRecoveryState>(
                File.ReadAllText(Path.Combine(closedExitDirectory, "audio-recovery.json")), SceneStore.JsonOptions)!.Applications.Count == 1,
                "a reopened idle process does not discard restoration before a real audio session exists");
            idle.Kill(); await idle.WaitForExitAsync(); idle.Dispose(); idle = null;
            restarted = VerificationRuntime.StartTone(paths[0], 991);
            await restarted.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => Output(restarted) == speaker.Id && catalog.CurrentRenderDevices([restarted.Id]).Contains(speaker.Id) &&
                Math.Abs(Volume(restarted) - .64f) < .01 && catalog.GetApplicationVolume([restarted.Id])?.mute == false,
                "Recovery worker did not restore real output, volume and mute after the desktop exited.");
            await recoveryWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            using (var status = JsonDocument.Parse(File.ReadAllText(workerStatus)))
                Check(recoveryWorker.HasExited && status.RootElement.GetProperty("Complete").GetBoolean() &&
                    status.RootElement.GetProperty("Pending").GetInt32() == 0,
                    "recovery worker restores a closed application's output, gain and mute and exits when its journal is empty");
        }
        finally
        {
            if (idle is { HasExited: false }) { idle.Kill(); await idle.WaitForExitAsync(); }
            idle?.Dispose();
            if (recoveryWorker is { HasExited: false }) { recoveryWorker.Kill(); await recoveryWorker.WaitForExitAsync(); }
            recoveryWorker?.Dispose();
            await model.RestoreOriginalStateAsync();
            foreach (var childDirectory in childDirectories)
            { await using var recovery = new AudioEngine(childDirectory); await recovery.RecoverAsync(); }
            foreach (var process in new[] { tone, restarted }.OfType<Process>())
                if (!process.HasExited)
                {
                    for (int role = 0; role < 3; role++) AudioPolicy.SetOutput(process.Id, role, null);
                    catalog.SetApplicationVolume([process.Id], 1, false);
                    process.Kill(); await process.WaitForExitAsync();
                }
            restarted?.Dispose();
            device.SetVolume(originalDevice.Volume); device.SetMuted(originalDevice.Muted);
            model.ImportConfiguration(originalGraph); model.Workspace.FocusedApplication = originalFocus;
            await model.SwitchViewAsync(originalView == CanvasView.Global); model.SaveNow();
        }
    }
}
