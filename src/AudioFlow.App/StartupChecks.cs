using AudioFlow.Audio;
using AudioFlow.Core;
using System.IO;
using System.Text.Json;

namespace AudioFlow.App;

internal static class StartupChecks
{
    public static async Task RunAsync(string directory, List<string> checks)
    {
        string root = Path.Combine(directory, "startup-state"); Directory.CreateDirectory(root);
        string tonePath = VerificationRuntime.CreateTones(Path.Combine(root, "tone"), "StartupTone")[0];
        using var tone = VerificationRuntime.StartTone(tonePath, 1907);
        await tone.StandardOutput.ReadLineAsync();
        string file = Path.Combine(root, "named-configuration.json");
        var named = new RouteGraph { Nodes = [new() { Id = "startup-source", Kind = NodeKind.Application, Key = tonePath, Name = "Startup tone", Volume = .27f }] };
        var snapshot = SceneStore.Clone(named); snapshot.Nodes[0].Volume = .71f;
        var store = new SceneStore(root); store.Save(named, file);
        var state = new WorkspaceState { Configuration = snapshot, ActiveConfigurationPath = file, LastRoutingEnabled = true, RestoreRoutingStateOnLaunch = true };
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        async Task<JsonDocument> Launch(string suffix)
        {
            store.SaveWorkspace(state);
            string report = Path.Combine(root, suffix + ".json");
            using var helper = VerificationRuntime.Start(Environment.ProcessPath!, "--verify-startup", root, report);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await helper.WaitForExitAsync(timeout.Token);
            if (helper.ExitCode != 0) throw new InvalidOperationException(File.ReadAllText(report + ".error"));
            return JsonDocument.Parse(File.ReadAllText(report));
        }
        try
        {
            using (var active = await Launch("running"))
                Check(active.RootElement.GetProperty("Running").GetBoolean() && Math.Abs(active.RootElement.GetProperty("SessionVolume").GetSingle() - .27f) < .01 &&
                    active.RootElement.GetProperty("Path").GetString() == file,
                    "actual desktop startup loads the named file instead of its stale snapshot and resumes real playback");
            state.LastRoutingEnabled = false;
            using (var stopped = await Launch("stopped"))
                Check(!stopped.RootElement.GetProperty("Running").GetBoolean() && Math.Abs(stopped.RootElement.GetProperty("SessionVolume").GetSingle() - 1) < .01,
                    "actual desktop startup preserves the last stopped state without changing real audio volume");
            File.WriteAllText(file, "broken-json");
            using (var corrupted = await Launch("corrupted"))
                Check(corrupted.RootElement.GetProperty("NodeCount").GetInt32() > 0 && Math.Abs(corrupted.RootElement.GetProperty("Volume").GetSingle() - .71f) < .01 &&
                    File.ReadAllText(file) == "broken-json" && corrupted.RootElement.GetProperty("Path").ValueKind == JsonValueKind.Null,
                    "actual desktop startup preserves a damaged file and recovers its last local snapshot");
            if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture path");
            File.Delete(file);
            using (var missing = await Launch("missing"))
                Check(missing.RootElement.GetProperty("NodeCount").GetInt32() > 0 && !File.Exists(file),
                    "actual desktop startup recovers a missing configuration without creating an empty replacement file");
        }
        finally { if (!tone.HasExited) tone.Kill(); }
    }
}
