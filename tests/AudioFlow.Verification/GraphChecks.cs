using AudioFlow.Core;

internal static class GraphChecks
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
        void Reject(Action action, string name) { try { action(); } catch (GraphValidationException) { passed++; Console.WriteLine("PASS " + name); return; } throw new Exception("FAIL " + name); }
        var devices = Enumerable.Range(1, 4).Select(i => new AudioEndpoint("C" + i, "C" + i, false, false, false, 48000, 2)).ToList();
        devices.AddRange([new("V1", "V1", false, false, true, 48000, 2), new("M1", "M1", true, false, true, 48000, 2),
            new("V2", "V2", false, false, true, 48000, 2), new("M2", "M2", true, false, true, 48000, 2)]);
        var a = new RouteNode { Id = "A", Kind = NodeKind.Application, Key = @"C:\Example\A.exe", Name = "A" };
        var b = new RouteNode { Id = "B", Kind = NodeKind.Application, Key = @"C:\Example\B.exe", Name = "B" };
        var graph = new RouteGraph { Nodes = [a, b, .. devices.Where(d => d.Id.StartsWith("C")).Select(d => new RouteNode { Id = d.Id, Kind = NodeKind.Playback, Key = d.Id })] };
        foreach (var device in new[] { "C1", "C2", "C3", "C4" }) graph.Connections.Add(new() { SourceId = "A", TargetId = device });
        foreach (var device in new[] { "C2", "C4" }) graph.Connections.Add(new() { SourceId = "B", TargetId = device });
        var compiled = GraphCompiler.Compile(graph, devices);
        Check(compiled.Sources[0].Primary?.EndpointId == "C1" && compiled.Sources[0].Mirrors.Count == 3, "A chooses one native output and three copies");
        Check(compiled.Sources[1].Primary?.EndpointId == "C2" && compiled.Sources[1].Mirrors.Single().EndpointId == "C4", "B chooses C2 and C4 only");
        Check(compiled.Sources.Where(s => s.Destinations.Any(d => d.EndpointId == "C2")).Select(s => s.Node.Id).SequenceEqual(["A", "B"]), "C2 mixes A and B");
        Check(compiled.Sources.Count(s => s.Destinations.Any(d => d.EndpointId == "C3")) == 1, "C3 contains A only");
        graph.Connections.Add(new() { SourceId = "A", TargetId = "C1" });
        Check(GraphCompiler.Compile(graph, devices).Sources[0].Destinations.Count == 4, "duplicate destination does not double audio");
        graph.Connections.Last().Enabled = false;
        graph.Channels = [new() { Id = "one", RenderEndpointId = "V1", CaptureEndpointId = "M1" },
            new() { Id = "two", RenderEndpointId = "V2", CaptureEndpointId = "M2" }];
        graph.Nodes.Add(new() { Id = "microphone", Kind = NodeKind.VirtualMicrophone, ChannelId = "one" });
        graph.Connections.Add(new() { SourceId = "A", TargetId = "microphone", Mode = RouteMode.MicrophoneFeed });
        var microphoneOnlyGraph = SceneStore.Clone(graph);
        microphoneOnlyGraph.Connections.RemoveAll(c => c.SourceId == "A" && c.Mode == RouteMode.Playback);
        var micOnly = GraphCompiler.Compile(microphoneOnlyGraph, devices).Sources[0];
        Check(micOnly.Destinations.Count == 1 && micOnly.Primary?.EndpointId == "V1", "removing speaker links retains microphone feed");
        Check(GraphCompiler.Compile(microphoneOnlyGraph, devices).Sources[1].Destinations.Count == 2, "removing source A speaker links leaves source B untouched");
        var independentGain = SceneStore.Clone(graph);
        independentGain.Connections.First(c => c.SourceId == "A" && c.TargetId == "C1").Volume = .25f;
        independentGain.Connections.First(c => c.SourceId == "A" && c.TargetId == "C2").Volume = .7f;
        var gainedRoutes = GraphCompiler.Compile(independentGain, devices).Sources.First(s => s.Node.Id == "A");
        Check(gainedRoutes.Destinations.First(d => d.EndpointId == "C1").Volume == .25f && gainedRoutes.Destinations.First(d => d.EndpointId == "C2").Volume == .7f,
            "each output connection carries its own independent gain");
        independentGain.Connections.Where(c => c.SourceId == "A" && c.TargetId == "C1").ToList().ForEach(c => c.Volume = 0);
        Check(GraphCompiler.Compile(independentGain, devices).Sources.First(s => s.Node.Id == "A").Primary?.EndpointId == "C2",
            "muting the native output connection keeps another output audible");
        independentGain.Channels[0].Volume = .4f;
        independentGain.Connections.First(c => c.SourceId == "A" && c.TargetId == "microphone").Volume = .5f;
        Check(Math.Abs(GraphCompiler.Compile(independentGain, devices).Sources.First(s => s.Node.Id == "A").Destinations.First(d => d.EndpointId == "V1").Volume - .2f) < .0001,
            "individual input gain combines once with the shared virtual channel gain");
        var badGain = SceneStore.Clone(independentGain); badGain.Connections[0].Volume = float.NaN;
        Reject(() => GraphCompiler.Compile(badGain, devices), "nonfinite connection gain rejected");
        badGain = SceneStore.Clone(independentGain); badGain.Channels[0].Volume = -1;
        Reject(() => GraphCompiler.Compile(badGain, devices), "invalid shared input channel gain rejected");
        Check(GraphCompiler.Compile(graph, devices.Where(d => d.Id != "C3").ToList()).Warnings.Count > 0, "missing device keeps scene and reports pending connection");
        var bad = SceneStore.Clone(graph); bad.Nodes.Add(bad.Nodes[0]);
        Reject(() => GraphCompiler.Compile(bad, devices), "duplicate node ID rejected");
        bad = SceneStore.Clone(graph); bad.Nodes[0].Volume = float.NaN;
        Reject(() => GraphCompiler.Compile(bad, devices), "nonfinite volume rejected");
        bad = SceneStore.Clone(graph); bad.Connections.Add(new() { SourceId = "C1", TargetId = "A" });
        Reject(() => GraphCompiler.Compile(bad, devices), "invalid port direction rejected");
        bad = SceneStore.Clone(graph); bad.Nodes.First(n => n.Id == "A").ChannelId = "one";
        bad.Connections.Add(new() { SourceId = "microphone", TargetId = "A", Mode = RouteMode.MicrophoneFeed });
        Reject(() => GraphCompiler.Compile(bad, devices), "self microphone feedback rejected");
        bad = SceneStore.Clone(graph);
        bad.Nodes.First(n => n.Id == "B").ChannelId = "one";
        bad.Nodes.First(n => n.Id == "A").ChannelId = "two";
        bad.Connections.Add(new() { SourceId = "microphone", TargetId = "B", Mode = RouteMode.MicrophoneFeed });
        bad.Connections.Add(new() { SourceId = "B", TargetId = "A", Mode = RouteMode.MicrophoneFeed });
        Reject(() => GraphCompiler.Compile(bad, devices), "cross-application feedback cycle rejected");
        var direct = new RouteGraph
        {
            Nodes =
            [
                new() { Id = "A", Kind = NodeKind.Application, Key = a.Key },
                new() { Id = "B", Kind = NodeKind.Application, Key = b.Key, ChannelId = "two" },
                new() { Id = "real", Kind = NodeKind.Microphone, Key = "PhysicalMic" }
            ],
            Channels = SceneStore.Clone(graph).Channels,
            Connections =
            [
                new() { SourceId = "A", TargetId = "B", Mode = RouteMode.MicrophoneFeed },
                new() { SourceId = "real", TargetId = "B", Mode = RouteMode.MicrophoneFeed }
            ]
        };
        var directRoutes = GraphCompiler.Compile(direct, devices);
        Check(directRoutes.Sources.First(s => s.Node.Id == "A").Primary?.EndpointId == "V2", "software A directly feeds software B microphone");
        Check(directRoutes.Sources.First(s => s.Node.Id == "real").Mirrors.Single().EndpointId == "V2", "physical microphone mixes into software B input");
        Check(directRoutes.Sources.First(s => s.Node.Id == "B").Destinations.Count == 0, "incoming microphone does not become B output automatically");
        direct.Nodes[0].ChannelId = "one";
        direct.Connections.Add(new() { SourceId = "B", TargetId = "A", Mode = RouteMode.MicrophoneFeed });
        Reject(() => GraphCompiler.Compile(direct, devices), "direct software-to-software feedback rejected");
        var buffer = new AudioFlow.Core.AudioBuffer(16, 0);
        buffer.Gain = .5f;
        buffer.Write([.6f, .2f, .6f, .2f, .6f, .2f]);
        Span<float> output = stackalloc float[4]; buffer.MixInto(output, false);
        Check(Math.Abs(output[0] - .3f) < .0001 && Math.Abs(output[1] - .1f) < .0001, "stereo channels and source gain preserved");
        buffer.Write(new float[80]);
        Check(buffer.BufferedFrames <= 16 && buffer.Overruns > 0, "FIFO capacity stays bounded when writer outruns device");
        buffer.Clear(); output.Clear(); buffer.MixInto(output);
        Check(output.ToArray().All(f => f == 0), "idle input yields silence");
        var liveApplications = new AudioApplication[]
        {
            new(AppIdentity.Normalize(@"C:\Example\Music.exe"), "音乐播放器", @"C:\Example\Music.exe", [101], [101], 0, true, true),
            new(AppIdentity.Normalize(@"C:\Example\Weixin.exe"), "Weixin", @"C:\Example\Weixin.exe", [102], [102], 0, false, false)
        };
        Check(ApplicationSelection.Sidebar(liveApplications, [], [], []).Single().Name == "音乐播放器",
            "automatic application list excludes apps without active audio");
        Check(ApplicationSelection.Sidebar(liveApplications, [], [], []).Single().Peak == 0,
            "active audio app remains visible during a quiet audio frame");
        var manualApplication = new SavedApplication("Weixin", @"C:\Example\Weixin.exe");
        Check(ApplicationSelection.Sidebar(liveApplications, [manualApplication], [], []).Count == 2,
            "manual application remains available without audio output");
        Check(ApplicationSelection.Candidates(liveApplications, []).Count == 2,
            "application picker includes running apps without audio");
        Check(ApplicationSelection.Sidebar(liveApplications, [manualApplication], [], [], "微信").Single().Name == "Weixin",
            "application search recognizes a common display nickname");
        Check(ApplicationSelection.Candidates([], [manualApplication]).Single().Path == manualApplication.Path,
            "saved manual application remains selectable when closed");
        var configuredSidebar = ApplicationSelection.Sidebar(liveApplications, [], [manualApplication], []);
        Check(configuredSidebar.Count == 2 && configuredSidebar[0].HasAudio && configuredSidebar[1].Name == "Weixin",
            "configured quiet app remains visible below active audio apps");
        Check(ApplicationSelection.Sidebar([], [], [manualApplication], []).Single().ProcessIds.Count == 0,
            "configured app remains visible even after its process closes");
        Check(ApplicationSelection.Sidebar(liveApplications, [], [], [manualApplication]).Count == 2,
            "a running app with prior audio evidence is visible before playing again");
        var inactiveSession = liveApplications[1] with { HasAudioSession = true };
        Check(ApplicationSelection.Sidebar([inactiveSession], [], [], []).Single().Name == "Weixin",
            "an inactive audio session identifies a sound-capable running application");
        Check(ApplicationSelection.Sidebar([liveApplications[1]], [], [], []).Count == 0,
            "running alone does not qualify an app with no audio evidence");
        Check(ApplicationSelection.Sidebar([liveApplications[1]], [manualApplication], [], []).Count == 1,
            "manual selection overrides automatic audio capability filtering");
        Check(ApplicationSelection.Sidebar([liveApplications[1] with { ProcessIds = [], RootProcessIds = [] }], [], [], [manualApplication]).Count == 0,
            "closed known-audio apps stay hidden unless configured or manually added");
        var defaultDevices = devices.Select(e => e with { Default = e.Id == "C1" }).ToList();
        var defaultGraph = new RouteGraph { Nodes = [a, new() { Id = "real-default", Kind = NodeKind.Microphone, Key = "PhysicalMic" }] };
        var defaultRoutes = GraphCompiler.Compile(defaultGraph, defaultDevices);
        Check(defaultRoutes.Sources.First(s => s.Node.Id == "A").Primary is { EndpointId: "C1", SystemDefault: true },
            "an application without output links falls back to the current Windows default device");
        Check(defaultRoutes.Sources.First(s => s.Node.Id == "real-default").Destinations.Count == 0,
            "an unconnected physical microphone is not automatically played through speakers");
        defaultGraph.Nodes.Add(new() { Id = "system-output", Kind = NodeKind.Playback, Key = AudioOutput.SystemDefault });
        defaultGraph.Connections.Add(new() { SourceId = "A", TargetId = "system-output", Automatic = true, Volume = .8f });
        var changedDefaults = defaultDevices.Select(e => e with { Default = e.Id == "C2" }).ToList();
        Check(GraphCompiler.Compile(defaultGraph, changedDefaults).Sources[0].Primary is { EndpointId: "C2", SystemDefault: true, Volume: .8f },
            "a default output link follows Windows device changes while preserving its gain");
        Check(!new RouteGraph().CloseToTray, "closing the application exits by default so restoration runs");
        var toggleGraph = SceneStore.Clone(graph);
        ApplicationOutputs.Ensure(toggleGraph);
        var primaryOutput = toggleGraph.Nodes.Single(n => ApplicationOutputs.IsPrimary(toggleGraph.Nodes.First(n => n.Id == "A"), n));
        var primaryConnection = toggleGraph.Connections.Single(c => c.SourceId == "A" && c.TargetId == primaryOutput.Id);
        primaryConnection.Volume = .37f; primaryConnection.Enabled = false;
        var disabledRoutes = GraphCompiler.Compile(toggleGraph, defaultDevices).Sources.First(s => s.Node.Id == "A");
        Check(!disabledRoutes.Destinations.Any(d => d.EndpointId == primaryOutput.Key) && disabledRoutes.Destinations.Count == 4,
            "disabling the protected default output keeps other playback and microphone destinations audible");
        ApplicationOutputs.Ensure(toggleGraph); ApplicationOutputs.Ensure(toggleGraph);
        Check(!primaryConnection.Enabled && toggleGraph.Connections.Count(c => c.TargetId == primaryOutput.Id) == 1,
            "ensuring base cards preserves the disabled route without duplicating or reenabling it");
        var foreignRoute = SceneStore.Clone(toggleGraph);
        foreignRoute.Connections.Add(new() { SourceId = "B", TargetId = primaryOutput.Id, Enabled = false });
        Reject(() => GraphCompiler.Compile(foreignRoute, defaultDevices), "disabled default routes still reject another application's source");
        toggleGraph.Connections.RemoveAll(c => c.SourceId == "A" && c.TargetId != primaryOutput.Id);
        Check(GraphCompiler.Compile(toggleGraph, defaultDevices).Sources.First(s => s.Node.Id == "A").Primary is { Volume: 0 },
            "disabling the sole default output silences the app instead of restoring audible Windows playback");
        var disconnectedDevices = defaultDevices.Where(e => e.Id != primaryOutput.Key).Select(e => e with { Default = e.Id == "C2" }).ToList();
        Check(GraphCompiler.Compile(toggleGraph, disconnectedDevices).Sources.First(s => s.Node.Id == "A").Primary is { Volume: 0 },
            "a disconnected disabled device does not restore audible Windows playback");
        var reopenedToggle = SceneStore.Clone(toggleGraph);
        Check(!reopenedToggle.Connections.Single(c => c.Id == primaryConnection.Id).Enabled,
            "disabled default output state survives JSON serialization");
        primaryConnection.Enabled = true;
        primaryOutput.Key = primaryOutput.LastOutputKey!;
        Check(GraphCompiler.Compile(toggleGraph, defaultDevices).Sources.First(s => s.Node.Id == "A").Primary is { Native: true, Volume: .37f } restored && restored.EndpointId == primaryOutput.Key,
            "reenabling default output restores its selected device and original gain");
        var directory = Path.Combine(Path.GetTempPath(), "AudioFlowVerify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SceneStore(directory); store.Save(graph); var loaded = store.Load();
            Check(loaded.Connections.Count == graph.Connections.Count && loaded.Channels.Count == 2, "scene round trip preserves routes and independent channels");
            Check(!File.Exists(store.ScenePath + ".tmp"), "atomic scene save leaves no temporary file");
            store.Save(independentGain); var storedGain = store.Load();
            Check(storedGain.Connections.First(c => c.SourceId == "A" && c.TargetId == "C2").Volume == .7f && storedGain.Channels[0].Volume == .4f,
                "connection and input channel volumes persist in a scene");
            Check(store.LoadWorkspace().Configuration.Nodes.Count == 0,
                "new workspace starts empty without automatically importing an exported scene");
            var workspace = new WorkspaceState
            {
                View = CanvasView.Global, Configuration = SceneStore.Clone(independentGain),
                ManualApplications = [manualApplication], FocusedApplication = a.Key, RoutingPaused = true
            };
            store.SaveWorkspace(workspace);
            var savedWorkspace = store.LoadWorkspace();
            Check(savedWorkspace.ManualApplications.Single() == manualApplication && savedWorkspace.View == CanvasView.Global,
                "manual application and selected canvas view persist together");
            Check(savedWorkspace.Configuration.Connections.Count == independentGain.Connections.Count &&
                savedWorkspace.Configuration.Channels[0].Volume == .4f && savedWorkspace.RoutingPaused && savedWorkspace.FocusedApplication == a.Key,
                "one shared configuration preserves routes, volumes, pause state and focused app");
            var configuration = savedWorkspace.Configuration;
            savedWorkspace.View = CanvasView.PerApplication;
            Check(ReferenceEquals(configuration, savedWorkspace.Configuration), "changing view keeps the same routing graph");
            savedWorkspace.Configuration.Connections.First(c => c.SourceId == "A" && c.TargetId == "C2").Volume = .63f;
            store.SaveWorkspace(savedWorkspace);
            var reopened = store.LoadWorkspace();
            Check(reopened.View == CanvasView.PerApplication &&
                reopened.Configuration.Connections.First(c => c.SourceId == "A" && c.TargetId == "C2").Volume == .63f,
                "edits persist in the shared graph regardless of the selected view");
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "workspace.json")));
            Check(document.RootElement.EnumerateObject().Count(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Object) == 1 &&
                document.RootElement.GetProperty("Configuration").GetProperty("Nodes").GetArrayLength() == configuration.Nodes.Count,
                "workspace serializes exactly one routing configuration");
            Check(!File.Exists(Path.Combine(directory, "workspace.json.tmp")), "atomic workspace save leaves no temporary file");
            reopened.View = (CanvasView)99;
            Reject(() => store.SaveWorkspace(reopened), "invalid canvas view rejected");
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("AudioFlowVerify-")) throw new Exception("Unsafe test cleanup path");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        WorkspaceChecks.Run(Check);
        FeatureGraphChecks.Run(Check, Reject);
        LayoutChecks.Run(Check);
        Console.WriteLine($"UNIT CHECKS PASSED: {passed}");
    }
}
