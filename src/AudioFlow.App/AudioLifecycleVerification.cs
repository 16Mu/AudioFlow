using AudioFlow.Core;
using AudioFlow.Audio;
using System.IO;
using System.Text.Json;

namespace AudioFlow.App;

internal static class AudioLifecycleVerification
{
    public static async Task RunAsync(string directory, string scenePath, string volumeDevice, string mode)
    {
        var window = new MainWindow(directory);
        var model = window.ViewModel;
        await model.InitializeAsync();
        var graph = model.Store.Load(scenePath);
        graph.CloseToTray = false;
        model.ImportConfiguration(graph);
        await model.StartAsync();
        using var device = new EndpointVolumeBinding(volumeDevice);
        model.Engine.SetDeviceVolume(device, .29f);
        var catalog = new AudioCatalog();
        var source = graph.Nodes.First(n => n.Kind == NodeKind.Application);
        var pids = catalog.FindApplicationProcesses(source.Key);
        var applicationVolume = catalog.GetApplicationVolume(pids);
        File.WriteAllText(Path.Combine(directory, "lifecycle-ready.json"), JsonSerializer.Serialize(new
        {
            Running = model.Running,
            Outputs = pids.Select(pid => AudioPolicy.Unpack(AudioPolicy.GetOutput(pid, 1))).ToArray(),
            Volume = applicationVolume?.volume, Muted = applicationVolume?.mute, DeviceVolume = device.State.Volume
        }, SceneStore.JsonOptions));
        if (mode == "hold") await Task.Delay(TimeSpan.FromSeconds(30));
        if (mode == "wait-close")
        {
            var deadline = Environment.TickCount64 + 15000;
            while (!File.Exists(Path.Combine(directory, "lifecycle-close")) && Environment.TickCount64 < deadline)
                await Task.Delay(50);
        }
        window.RequestExit();
    }
}
