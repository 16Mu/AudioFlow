using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioFlow.Core;

public static class ChannelPool
{
    public static List<VirtualChannel> Detect(IReadOnlyList<AudioEndpoint> endpoints)
    {
        string Normalize(string name) => Regex.Replace(Regex.Replace(Regex.Replace(name, @"\bOut\s+B[123]\b", "Output", RegexOptions.IgnoreCase),
            @"\b(input|output)\b", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim();
        var result = new List<VirtualChannel>();
        foreach (var group in endpoints.Where(e => e.Virtual).GroupBy(e => Normalize(e.Name), StringComparer.OrdinalIgnoreCase))
        {
            var render = group.Where(e => !e.Capture && !e.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase)).ToArray();
            var capture = group.Where(e => e.Capture).ToArray();
            if (render.Length != 1 || capture.Length != 1) continue;
            string id = "channel-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture[0].Id)))[..12].ToLowerInvariant();
            result.Add(new() { Id = id, Name = "传声音的麦克风 " + (result.Count + 1), RenderEndpointId = render[0].Id,
                CaptureEndpointId = capture[0].Id, Provider = render[0].Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ? "Voicemeeter" : "Cable",
                RequiresValidation = !render[0].Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) && !render[0].Name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase) });
        }
        return result;
    }
    public static IReadOnlyList<VirtualChannel> Connected(RouteGraph graph, IReadOnlyList<AudioEndpoint> endpoints) => graph.Channels
        .Where(c => endpoints.Any(e => !e.Capture && e.Id == c.RenderEndpointId) && endpoints.Any(e => e.Capture && e.Id == c.CaptureEndpointId))
        .DistinctBy(c => c.CaptureEndpointId).DistinctBy(c => c.RenderEndpointId).ToArray();
    public static HashSet<string> Used(RouteGraph graph, IReadOnlyList<AudioEndpoint>? endpoints = null)
    {
        var used = graph.Nodes.Where(n => n.Kind == NodeKind.Application && !string.IsNullOrWhiteSpace(n.ChannelId) &&
            graph.Connections.Any(c => c.Enabled && c.TargetId == n.Id)).Select(n => n.ChannelId!).ToHashSet();
        foreach (var node in graph.Nodes.Where(n => n.Kind == NodeKind.VirtualMicrophone && graph.Connections.Any(c => c.Enabled && c.TargetId == n.Id)))
            if (node.ChannelId != null) used.Add(node.ChannelId);
        foreach (var node in graph.Nodes.Where(n => n.Kind == NodeKind.Application && n.InputDeviceKey != null && n.InputDeviceKey != AudioOutput.None))
        {
            string? capture = node.InputDeviceKey == AudioOutput.SystemDefault ? endpoints?.FirstOrDefault(e => e.Capture && e.Default)?.Id : node.InputDeviceKey;
            foreach (var channel in graph.Channels.Where(c => c.CaptureEndpointId == capture)) used.Add(channel.Id);
        }
        foreach (var node in graph.Nodes.Where(n => n.Kind == NodeKind.Playback && graph.Connections.Any(c => c.Enabled && c.TargetId == n.Id)))
        {
            string? endpoint = endpoints == null ? node.Key : AudioOutput.Resolve(node.Key, endpoints)?.Id;
            foreach (var channel in graph.Channels.Where(c => c.RenderEndpointId == endpoint)) used.Add(channel.Id);
        }
        if (graph.SilentInputChannelId != null) used.Add(graph.SilentInputChannelId);
        return used;
    }
    public static IReadOnlyList<VirtualChannel> Free(RouteGraph graph, IReadOnlyList<AudioEndpoint> endpoints)
    {
        var used = Used(graph, endpoints);
        return Connected(graph, endpoints).Where(c => !c.RequiresValidation && !used.Contains(c.Id)).ToArray();
    }
    public static VirtualChannel ReserveSilence(RouteGraph graph, IReadOnlyList<AudioEndpoint> endpoints)
    {
        var channel = Connected(graph, endpoints).FirstOrDefault(c => c.Id == graph.SilentInputChannelId && !c.RequiresValidation)
            ?? Free(graph, endpoints).FirstOrDefault();
        if (channel == null) throw new GraphValidationException("暂时关不了这个软件的录音。请先腾出一个传声音的麦克风，或在设置里安装更多驱动。");
        graph.SilentInputChannelId = channel.Id;
        return channel;
    }
    public static void NormalizeOutputs(RouteGraph graph)
    {
        foreach (var node in graph.Nodes.Where(n => n.PrimaryOutput))
        {
            var route = graph.Connections.FirstOrDefault(c => c.TargetId == node.Id);
            if (route == null) continue;
            if (!route.Enabled || node.Key == AudioOutput.None)
            {
                if (node.Key != AudioOutput.None) node.LastOutputKey = node.Key;
                node.Key = AudioOutput.None; node.Name = "不播放声音"; route.Enabled = false;
            }
        }
    }
}
