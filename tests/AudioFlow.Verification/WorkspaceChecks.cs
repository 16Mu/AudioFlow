using AudioFlow.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class WorkspaceChecks
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AudioFlowWorkspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SceneStore(directory);
            var first = new RouteGraph { Nodes = [new() { Id = "A", Kind = NodeKind.Application, Key = @"C:\Test\A.exe" },
                new() { Id = "outA", Kind = NodeKind.Playback, Key = "speaker" }],
                Connections = [new() { SourceId = "A", TargetId = "outA", Volume = .43f }] };
            var second = new RouteGraph { Nodes = [new() { Id = "B", Kind = NodeKind.Application, Key = @"C:\Test\B.exe" },
                new() { Id = "outB", Kind = NodeKind.Playback, Key = "headphones" }],
                Connections = [new() { SourceId = "B", TargetId = "outB", Volume = .62f }] };
            var old = new JsonObject { ["Version"] = 1, ["Mode"] = "Application", ["LastEnabledMode"] = "Global", ["WasPaused"] = true,
                ["FocusedApplication"] = @"C:\Test\A.exe", ["ManualApplications"] = JsonSerializer.SerializeToNode(new[] { new SavedApplication("saved", @"C:\Test\A.exe") }),
                ["ApplicationConfiguration"] = JsonSerializer.SerializeToNode(first, SceneStore.JsonOptions),
                ["GlobalConfiguration"] = JsonSerializer.SerializeToNode(second, SceneStore.JsonOptions) };
            old["ApplicationConfiguration"]!["Nodes"]![0]!["ListenLocally"] = true;
            string path = Path.Combine(directory, "workspace.json"), json = old.ToJsonString();
            File.WriteAllText(path, json);
            var loaded = store.LoadWorkspace();
            check(loaded.View == CanvasView.PerApplication && loaded.RoutingPaused && loaded.ManualApplications.Count == 1,
                "legacy Mode, pause state, focus and manual applications migrate");
            check(loaded.Configuration.Nodes.Count == 4 && loaded.Configuration.Connections.Any(c => c.Volume == .43f) &&
                loaded.Configuration.Connections.Any(c => c.Volume == .62f), "legacy per-app and global routes both survive migration");
            check(File.ReadAllText(path + ".pre-migration") == json && !File.ReadAllText(path).Contains("\"Mode\":\"Application\""),
                "legacy workspace is backed up before conversion to the single shared format");
            check(store.LoadWorkspace().Configuration.Connections.Count == 2, "migrated workspace reopens without a second migration");
            ApplicationOutputs.Ensure(loaded.Configuration);
            ApplicationOutputs.Ensure(loaded.Configuration);
            check(loaded.Configuration.Nodes.Count(n => n.PrimaryOutput) == 2 && loaded.Configuration.Connections.Count == 2,
                "normalizing outputs keeps separate native devices and gains without duplicating routes");
            var a = loaded.Configuration.Nodes.First(n => n.Id == "A");
            var native = loaded.Configuration.Nodes.First(n => ApplicationOutputs.IsPrimary(a, n));
            loaded.Configuration.Nodes.Add(new() { Id = "copy", Kind = NodeKind.Playback, Key = "headphones" });
            loaded.Configuration.Connections.Insert(0, new() { SourceId = "A", TargetId = "copy" });
            var devices = new List<AudioEndpoint> { new("speaker", "speaker", false, true, false, 48000, 2),
                new("headphones", "headphones", false, false, false, 48000, 2) };
            var compiled = GraphCompiler.Compile(loaded.Configuration, devices).Sources.First(s => s.Node.Id == "A");
            check(compiled.Primary is { EndpointId: "speaker", Native: true, Volume: .43f } && compiled.Mirrors.Single().EndpointId == "headphones",
                "designated native output beats an earlier mirror connection");
            native.Key = AudioOutput.SystemDefault;
            check(GraphCompiler.Compile(loaded.Configuration, devices.Select(d => d with { Default = d.Id == "headphones" }).ToList()).Sources.First(s => s.Node.Id == "A").Primary?.SystemDefault == true,
                "protected native output follows Windows default changes and deduplicates a matching copy");
            string invalid = "{\"Version\":1,\"Unexpected\":true}";
            File.WriteAllText(path, invalid);
            bool rejected = false;
            try { store.LoadWorkspace(); } catch (JsonException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == invalid, "unrecognized workspace properties remain rejected without overwriting the file");
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("AudioFlowWorkspace-"))
                Directory.Delete(full, true);
        }
    }
}
