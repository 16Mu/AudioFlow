using AudioFlow.Core;

internal static class FeatureGraphChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (int count in new[] { 0, 1, 2, 32, 256 })
        {
            var endpoints = Enumerable.Range(1, count).SelectMany(i => new[] {
                new AudioEndpoint("render-" + i, $"CABLE-{i} Input (Virtual Audio Cable)", false, false, true, 48000, 2),
                new AudioEndpoint("capture-" + i, $"CABLE-{i} Output (Virtual Audio Cable)", true, false, true, 48000, 2) }).ToList();
            var channels = ChannelPool.Detect(endpoints);
            check(channels.Count == count && channels.Select(c => c.Id).Distinct().Count() == count, $"dynamic independent device discovery supports {count} pairs without a numbered-channel limit");
            var graph = new RouteGraph { Channels = channels };
            check(ChannelPool.Free(graph, endpoints).Count == count, $"all {count} newly detected independent pairs are initially free");
            if (count == 0) { reject(() => ChannelPool.ReserveSilence(graph, endpoints), "recording shutdown reports no available independent silent input"); continue; }
            var silent = ChannelPool.ReserveSilence(graph, endpoints);
            check(ChannelPool.Free(graph, endpoints).Count == count - 1 && ChannelPool.ReserveSilence(graph, endpoints).Id == silent.Id,
                $"multiple recording shutdowns reuse one reserved silent pair among {count} devices");
            graph.Nodes.Add(new() { Id = "app", Kind = NodeKind.Application, Key = @"C:\Fixture\Record.exe", InputDeviceKey = AudioOutput.None, LastInputDeviceKey = AudioOutput.SystemDefault });
            var cloned = SceneStore.Clone(graph);
            check(cloned.Nodes[0].InputDeviceKey == AudioOutput.None && cloned.SilentInputChannelId == silent.Id,
                "recording shutdown and its silent-device binding survive configuration serialization");
            graph.Nodes.Add(new() { Id = "virtual", Kind = NodeKind.VirtualMicrophone, ChannelId = silent.Id });
            graph.Connections.Add(new() { SourceId = "app", TargetId = "virtual", Mode = RouteMode.MicrophoneFeed });
            reject(() => GraphCompiler.Compile(graph, endpoints), "audio cannot be forwarded into a reserved silent microphone");
        }
        var largeEndpoints = Enumerable.Range(1, 256).SelectMany(i => new[] {
            new AudioEndpoint("r" + i, "CABLE " + i + " Input", false, false, true, 48000, 2),
            new AudioEndpoint("c" + i, "CABLE " + i + " Output", true, false, true, 48000, 2) }).ToList();
        largeEndpoints.Add(new("speaker", "Speaker", false, true, false, 48000, 2));
        var large = new RouteGraph { Channels = ChannelPool.Detect(largeEndpoints) };
        for (int i = 0; i < large.Channels.Count; i++)
        {
            var channel = large.Channels[i];
            large.Nodes.Add(new() { Id = "app" + i, Kind = NodeKind.Application, Key = @"C:\Fixture\App" + i + ".exe" });
            large.Nodes.Add(new() { Id = "mic" + i, Kind = NodeKind.VirtualMicrophone, ChannelId = channel.Id });
            large.Connections.Add(new() { SourceId = "app" + i, TargetId = "mic" + i, Mode = RouteMode.MicrophoneFeed });
        }
        ApplicationOutputs.Ensure(large);
        check(GraphCompiler.Compile(large, largeEndpoints).Sources.Count == 256 && large.Nodes.Count == 768,
            "a 256-channel logical routing graph fits the application limits and keeps per-app default output cards");
    }
}
