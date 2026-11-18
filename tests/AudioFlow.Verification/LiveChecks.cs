using AudioFlow.Audio;
using AudioFlow.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class LiveChecks
{
    private const double A = 701, B = 1301;
    public static async Task RunAsync(int durationSeconds)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".cache", "live-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        string sourceDirectory = AppContext.BaseDirectory;
        foreach (var file in Directory.GetFiles(sourceDirectory)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)), true);
        var originalExe = Path.Combine(root, "AudioFlow.Verification.exe");
        var aPath = Path.Combine(root, "ToneA.exe"); var bPath = Path.Combine(root, "ToneB.exe");
        File.Copy(originalExe, aPath, true); File.Copy(originalExe, bPath, true);
        Process Start(string path, double frequency)
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--tone"); start.ArgumentList.Add(frequency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add((durationSeconds + 120).ToString());
            return Process.Start(start)!;
        }
        using var toneA = Start(aPath, A); using var toneB = Start(bPath, B);
        Console.WriteLine(await toneA.StandardOutput.ReadLineAsync());
        Console.WriteLine(await toneB.StandardOutput.ReadLineAsync());
        await Task.Delay(400);
        var catalog = new AudioCatalog(); var snapshot = catalog.Read();
        var channels = snapshot.SuggestedChannels.Where(c => c.Provider == "Cable").Take(2).ToArray();
        if (channels.Length < 2) throw new Exception("Two independent render/capture pairs are required for the live checks.");
        var c1 = snapshot.Endpoints.FirstOrDefault(e => !e.Capture && e.Name.Contains("Senary", StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Endpoints.First(e => !e.Capture && !e.Virtual);
        var c2 = snapshot.Endpoints.First(e => !e.Capture && e.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase));
        var c3 = snapshot.Endpoints.First(e => e.Id == channels[0].RenderEndpointId);
        var c4 = snapshot.Endpoints.First(e => e.Id == channels[1].RenderEndpointId);
        var sinks = new[] { c1, c2, c3, c4 };
        var nodeA = new RouteNode { Id = "A", Kind = NodeKind.Application, Name = "Test A", Key = aPath };
        var nodeB = new RouteNode { Id = "B", Kind = NodeKind.Application, Name = "Test B", Key = bPath };
        var graph = new RouteGraph { Nodes = [nodeA, nodeB], Channels = channels.ToList() };
        for (int i = 0; i < 4; i++) graph.Nodes.Add(new() { Id = "C" + (i + 1), Kind = NodeKind.Playback, Name = sinks[i].Name, Key = sinks[i].Id });
        foreach (var id in new[] { "C1", "C2", "C3", "C4" }) graph.Connections.Add(new() { SourceId = "A", TargetId = id });
        foreach (var id in new[] { "C2", "C4" }) graph.Connections.Add(new() { SourceId = "B", TargetId = id });
        await using var engine = new AudioEngine(root, message => Console.WriteLine("ENGINE " + message));
        var probes = new List<SignalProbe>();
        try
        {
            foreach (var sink in sinks)
            {
                var channel = channels.FirstOrDefault(c => c.RenderEndpointId == sink.Id);
                Console.WriteLine("OPEN PROBE " + sink.Name + " capture=" + (channel?.CaptureEndpointId ?? "render loopback"));
                probes.Add(await SignalProbe.CreateAsync(sink, channel?.CaptureEndpointId));
            }
            await engine.ApplyAsync(graph);
            await Task.Delay(600);
            var foreignPrimary = catalog.ActiveSessions(c1.Id).Where(s => s.ProcessId != toneA.Id &&
                s.ProcessId != toneB.Id && s.ProcessId != Environment.ProcessId && s.Peak > .001).ToArray();
            foreach (var session in foreignPrimary) Console.WriteLine($"FOREIGN PRIMARY pid={session.ProcessId} name={session.Name} peak={session.Peak}");
            foreach (var probe in probes) probe.SharedPrimary = probe == probes[0] && foreignPrimary.Length > 0;
            Console.WriteLine("A devices: " + string.Join(",", catalog.CurrentRenderDevices([toneA.Id])));
            Console.WriteLine("B devices: " + string.Join(",", catalog.CurrentRenderDevices([toneB.Id])));
            Console.WriteLine("A preferred: " + AudioPolicy.GetOutput(toneA.Id, 1));
            Console.WriteLine("B preferred: " + AudioPolicy.GetOutput(toneB.Id, 1));
            Console.WriteLine(JsonSerializer.Serialize(engine.Snapshot(), SceneStore.JsonOptions));
            if (engine.Snapshot().Statuses.Any(s => s.State != "Active")) throw new Exception("Not every source reached Active.");
            await MeasureAsync(probes, "MATRIX", [true, true, true, true], [false, true, false, true], .015);
            var referenceA = probes.Select(p => p.Analyzer.Amplitude(A)).ToArray();
            var referenceB = probes.Select(p => p.Analyzer.Amplitude(B)).ToArray();
            var protectedGraph = SceneStore.Clone(graph);
            ApplicationOutputs.Ensure(protectedGraph);
            protectedGraph.Connections.Reverse();
            var protectedRoutes = GraphCompiler.Compile(protectedGraph, snapshot.Endpoints);
            if (protectedRoutes.Sources.First(s => s.Node.Id == "A").Primary is not { Native: true } primaryA || primaryA.EndpointId != c1.Id ||
                protectedRoutes.Sources.First(s => s.Node.Id == "B").Primary is not { Native: true } primaryB || primaryB.EndpointId != c2.Id)
                throw new Exception("Protected default outputs changed when copy connections were reordered.");
            await engine.ApplyAsync(protectedGraph);
            if (AudioPolicy.Unpack(AudioPolicy.GetOutput(toneA.Id, 1)) != c1.Id || AudioPolicy.Unpack(AudioPolicy.GetOutput(toneB.Id, 1)) != c2.Id)
                throw new Exception("The designated native output was not applied to each application.");
            await MeasureAsync(probes, "PROTECTED DEFAULT OUTPUTS AND COPIES", [true, true, true, true], [false, true, false, true], .015);
            var protectedDefaultA = protectedGraph.Connections.Single(c => c.SourceId == "A" &&
                protectedGraph.Nodes.Any(n => n.Id == c.TargetId && ApplicationOutputs.IsPrimary(nodeA, n)));
            protectedDefaultA.Enabled = false;
            await engine.ApplyAsync(protectedGraph);
            await MeasureAsync(probes, "DEFAULT OFF WITH COPIES", [false, true, true, true], [false, true, false, true], .015);
            var soleOutput = SceneStore.Clone(protectedGraph);
            soleOutput.Connections.RemoveAll(c => c.SourceId == "A" && c.Id != protectedDefaultA.Id);
            await engine.ApplyAsync(soleOutput);
            await MeasureAsync(probes, "SOLE DEFAULT OFF", [false, false, false, false], [false, true, false, true], .015);
            protectedDefaultA.Enabled = true;
            await engine.ApplyAsync(protectedGraph);
            await MeasureAsync(probes, "DEFAULT REENABLED", [true, true, true, true], [false, true, false, true], .015);
            var individualA = new[] { .2f, .75f, .45f, .9f }; var individualB = new[] { 0f, .6f, 0f, .3f };
            for (int i = 0; i < 4; i++)
            {
                graph.Connections.First(c => c.SourceId == "A" && c.TargetId == "C" + (i + 1)).Volume = individualA[i];
                if (i is 1 or 3) graph.Connections.First(c => c.SourceId == "B" && c.TargetId == "C" + (i + 1)).Volume = individualB[i];
            }
            await engine.ApplyAsync(graph);
            await MeasureRouteGainsAsync(probes, "INDIVIDUAL OUTPUT GAINS", referenceA, referenceB, individualA, individualB);
            graph.Connections.First(c => c.SourceId == "A" && c.TargetId == "C1").Volume = 0; individualA[0] = 0;
            await engine.ApplyAsync(graph);
            await MeasureRouteGainsAsync(probes, "MUTE FIRST OUTPUT ONLY", referenceA, referenceB, individualA, individualB);
            foreach (var connection in graph.Connections) connection.Volume = 1;
            await engine.ApplyAsync(graph);
            nodeA.Volume = .4f; await engine.ApplyAsync(graph);
            await MeasureAsync(probes, "GAIN 40%", [true, true, true, true], [false, true, false, true], .006);
            nodeA.Volume = 1;
            graph.Nodes.Add(new() { Id = "M1", Kind = NodeKind.VirtualMicrophone, ChannelId = channels[0].Id });
            graph.Nodes.Add(new() { Id = "M2", Kind = NodeKind.VirtualMicrophone, ChannelId = channels[1].Id });
            graph.Connections.RemoveAll(c => c.TargetId is "C3" or "C4");
            graph.Connections.Add(new() { SourceId = "A", TargetId = "M1", Mode = RouteMode.MicrophoneFeed });
            graph.Connections.Add(new() { SourceId = "B", TargetId = "M2", Mode = RouteMode.MicrophoneFeed });
            graph.Connections.RemoveAll(c => c.SourceId == "A" && c.Mode == RouteMode.Playback); await engine.ApplyAsync(graph);
            await MeasureAsync(probes, "NO SPEAKER LINKS / INDEPENDENT MICS", [false, false, true, false], [false, true, false, true], .015);
            graph.Channels[0].Volume = .5f;
            graph.Connections.First(c => c.SourceId == "A" && c.TargetId == "M1").Volume = .4f;
            await engine.ApplyAsync(graph);
            await MeasureAsync(probes, "INPUT ROUTE AND CHANNEL GAIN", [false, false, true, false], [false, true, false, true], .003);
            graph.Channels[0].Volume = 1;
            graph.Connections.First(c => c.SourceId == "A" && c.TargetId == "M1").Volume = 1;
            // Unified software cards can receive audio directly on their input
            // port while their own output remains a separate process stream.
            nodeB.ChannelId = channels[0].Id;
            graph.Connections.RemoveAll(c => c.SourceId == "A" && c.TargetId == "M1");
            graph.Connections.Add(new() { SourceId = "A", TargetId = "B", Mode = RouteMode.MicrophoneFeed });
            await engine.ApplyAsync(graph);
            await MeasureAsync(probes, "DIRECT SOFTWARE INPUT", [false, false, true, false], [false, true, false, true], .015);
            nodeA.Muted = true; await engine.ApplyAsync(graph);
            await MeasureAsync(probes, "MUTE A", [false, false, false, false], [false, true, false, true], .015);
            nodeA.Muted = false;
            graph.Connections.RemoveAll(c => c.TargetId is "M1" or "M2" || c.SourceId == "A" && c.TargetId == "B");
            foreach (var id in new[] { "C1", "C2", "C3", "C4" }) graph.Connections.Add(new() { SourceId = "A", TargetId = id });
            graph.Connections.Add(new() { SourceId = "B", TargetId = "C4" });
            await engine.ApplyAsync(graph);
            var initialMemory = Process.GetCurrentProcess().PrivateMemorySize64;
            Console.WriteLine($"STRESS START durationSeconds={durationSeconds} memoryBytes={initialMemory}");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < durationSeconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, durationSeconds - clock.Elapsed.TotalSeconds))));
                var state = engine.Snapshot();
                if (state.Statuses.Any(s => s.State is "Error" or "Restart")) throw new Exception("Route failed during continuous run.");
                await engine.RefreshAsync();
                Console.WriteLine($"STRESS seconds={clock.Elapsed.TotalSeconds:F0} memoryBytes={Process.GetCurrentProcess().PrivateMemorySize64} overruns={state.BufferOverruns} underruns={state.BufferUnderruns}");
            }
            await MeasureAsync(probes, "AFTER STRESS", [true, true, true, true], [false, true, false, true], .015);
            await engine.StopAndRestoreAsync();
            for (int role = 0; role < 3; role++)
            {
                if (AudioPolicy.GetOutput(toneA.Id, role) != null || AudioPolicy.GetOutput(toneB.Id, role) != null)
                    throw new Exception("Original per-application output was not restored.");
            }
            var report = new { Passed = true, DurationSeconds = clock.Elapsed.TotalSeconds,
                MemoryStart = initialMemory, MemoryEnd = Process.GetCurrentProcess().PrivateMemorySize64,
                Outputs = sinks.Select(s => s.Name), Channels = channels.Select(c => c.Name),
                Checked = new[] { "A->C1,C2,C3,C4", "B->C2,C4", "protected native outputs and reordered copies", "default off with copies", "sole default off", "default reenabled", "40% volume", "local playback off", "independent microphones", "direct software input", "mute", "stop and restore" } };
            File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, SceneStore.JsonOptions));
            Console.WriteLine("LIVE CHECKS PASSED report=" + Path.Combine(root, "report.json"));
        }
        finally
        {
            await engine.StopAndRestoreAsync();
            foreach (var probe in probes) await probe.DisposeAsync();
            if (!toneA.HasExited) toneA.Kill();
            if (!toneB.HasExited) toneB.Kill();
        }
    }
    private static async Task MeasureAsync(List<SignalProbe> probes, string label, bool[] expectedA, bool[] expectedB, double aAmplitude)
    {
        await Task.Delay(600);
        foreach (var probe in probes) probe.Analyzer.Reset();
        await Task.Delay(1800);
        var failures = new List<string>();
        for (int i = 0; i < probes.Count; i++)
        {
            double a = probes[i].Analyzer.Amplitude(A), b = probes[i].Analyzer.Amplitude(B);
            Console.WriteLine($"{label} C{i + 1} A={a:F6} B={b:F6}");
            bool Good(double amplitude, bool expected, double target) => expected
                ? amplitude > target * .60 && amplitude < target * 1.65 : amplitude < .0015;
            if (probes[i].SharedPrimary)
            {
                // Physical outputs legitimately mix unrelated applications too.
                // The native destination is checked by owned PID above; verify
                // source presence here and strict isolation on all copied outputs.
                if (expectedA[i] && a < aAmplitude * .60) failures.Add(label + ": primary source missing");
                Console.WriteLine(label + " C1 is also playing another application's stream.");
                continue;
            }
            if (!Good(a, expectedA[i], aAmplitude) || !Good(b, expectedB[i], .015))
                failures.Add($"{label}: unexpected content or gain at C{i + 1}: A={a}, B={b}");
        }
        if (failures.Count > 0) throw new Exception(string.Join("; ", failures));
    }
    private static async Task MeasureRouteGainsAsync(List<SignalProbe> probes, string label, double[] referenceA, double[] referenceB, float[] expectedA, float[] expectedB)
    {
        await Task.Delay(600); foreach (var probe in probes) probe.Analyzer.Reset(); await Task.Delay(1800);
        for (int i = 0; i < probes.Count; i++)
        {
            var a = probes[i].Analyzer.Amplitude(A); var b = probes[i].Analyzer.Amplitude(B);
            Console.WriteLine($"{label} C{i + 1} A={a:F6} B={b:F6}");
            if (i == 0 && probes[i].SharedPrimary) continue;
            bool Good(double value, double baseline, float gain) => gain == 0 ? value < .0015 : Math.Abs(value - baseline * gain) < Math.Max(.001, baseline * gain * .18);
            if (!Good(a, referenceA[i], expectedA[i]) || !Good(b, referenceB[i], expectedB[i]))
                throw new Exception($"{label}: output C{i + 1} did not apply independent route volumes.");
        }
    }
    private sealed class SignalProbe : IAsyncDisposable
    {
        private readonly DeviceRecorder recorder;
        public ToneAnalyzer Analyzer { get; } = new([A, B]);
        public bool SharedPrimary { get; set; }
        private SignalProbe(DeviceRecorder recorder)
        {
            this.recorder = recorder;
            recorder.DataAvailable += samples => Analyzer.Add(samples);
            recorder.Start();
        }
        public static async Task<SignalProbe> CreateAsync(AudioEndpoint endpoint, string? captureId)
        {
            return await Task.Run(() => new SignalProbe(new DeviceRecorder(captureId ?? endpoint.Id, captureId == null)));
        }
        public async ValueTask DisposeAsync() { await Task.Run(recorder.Dispose); }
    }
}
