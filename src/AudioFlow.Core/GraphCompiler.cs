namespace AudioFlow.Core;

public sealed class GraphValidationException(string message) : Exception(message);

public static class GraphCompiler
{
    public static CompiledGraph Compile(RouteGraph graph, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (graph.Version != 1) throw new GraphValidationException("场景版本不受支持。");
        if (graph.Nodes.Count > 2048 || graph.Connections.Count > 16384)
            throw new GraphValidationException("场景中的节点或连接过多。");
        var nodes = new Dictionary<string, RouteNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrEmpty(node.Id) || !nodes.TryAdd(node.Id, node))
                throw new GraphValidationException("节点标识重复或为空。");
            if (!float.IsFinite(node.Volume) || node.Volume is < 0 or > 2 ||
                !double.IsFinite(node.X) || !double.IsFinite(node.Y))
                throw new GraphValidationException("节点音量或位置不合法。");
            if (node.PrimaryOutput && (node.Kind != NodeKind.Playback || string.IsNullOrWhiteSpace(node.Key) ||
                !graph.Nodes.Any(n => n.Kind == NodeKind.Application && AppIdentity.Normalize(n.Key) == AppIdentity.Normalize(node.OwnerAppKey ?? ""))))
                throw new GraphValidationException("默认输出卡片必须属于一个已选择的软件。");
        }
        var channels = new Dictionary<string, VirtualChannel>(StringComparer.Ordinal);
        foreach (var channel in graph.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Id) || !channels.TryAdd(channel.Id, channel))
                throw new GraphValidationException("虚拟麦克风标识重复或为空。");
            if (channel.RenderEndpointId == channel.CaptureEndpointId)
                throw new GraphValidationException("虚拟麦克风必须对应不同的播放和录音端点。");
            if (!float.IsFinite(channel.Volume) || channel.Volume is < 0 or > 1)
                throw new GraphValidationException("虚拟通道音量不合法。");
            if (graph.Channels.Any(c => c.Id != channel.Id && (c.RenderEndpointId == channel.RenderEndpointId || c.CaptureEndpointId == channel.CaptureEndpointId)))
                throw new GraphValidationException("同一个虚拟设备对不能重复计作独立通道。");
        }
        var enabled = graph.Connections.Where(c => c.Enabled).ToList();
        foreach (var edge in graph.Connections)
        {
            if (!float.IsFinite(edge.Volume) || edge.Volume is < 0 or > 1)
                throw new GraphValidationException("连线音量不合法。");
            if (!nodes.TryGetValue(edge.SourceId, out var source) ||
                !nodes.TryGetValue(edge.TargetId, out var target))
                throw new GraphValidationException("连接引用了不存在的节点。");
            ValidateConnection(source, target, edge.Mode);
            if (target.PrimaryOutput && !ApplicationOutputs.IsPrimary(source, target))
                throw new GraphValidationException("默认输出连线仅属于对应软件，不能接入其他来源。");
        }
        ValidateFeedback(nodes, enabled, channels, endpoints);
        var warnings = new List<string>();
        var outputs = endpoints.Where(e => !e.Capture).Select(e => e.Id).ToHashSet();
        var captures = endpoints.Where(e => e.Capture).Select(e => e.Id).ToHashSet();
        var defaultOutput = AudioOutput.Resolve(AudioOutput.SystemDefault, endpoints);
        var results = new List<CompiledSource>();
        foreach (var source in graph.Nodes.Where(n => n.Kind is NodeKind.Application or NodeKind.Microphone))
        {
            var destinations = new List<Destination>();
            foreach (var edge in enabled.Where(e => e.SourceId == source.Id))
            {
                var target = nodes[edge.TargetId];
                if (target.Kind == NodeKind.Playback)
                {
                    if (AudioOutput.Resolve(target.Key, endpoints) is { } playback)
                    {
                        if (graph.Channels.Any(c => c.Id == graph.SilentInputChannelId && c.RenderEndpointId == playback.Id))
                            throw new GraphValidationException("此播放设备已预留用于静音录音输入，不能向它转发声音。");
                        destinations.Add(new(playback.Id, false, null, edge.Volume, target.Key == AudioOutput.SystemDefault, ApplicationOutputs.IsPrimary(source, target)));
                    }
                    else warnings.Add($"{target.Name}：设备未连接，保留此连线等待重连。");
                }
                else
                {
                    if (target.ChannelId is not { } id || !channels.TryGetValue(id, out var channel) ||
                        !outputs.Contains(channel.RenderEndpointId) || !captures.Contains(channel.CaptureEndpointId))
                    {
                        warnings.Add($"{target.Name}：请先设置虚拟麦克风的播放／录音设备对。");
                        continue;
                    }
                    if (id == graph.SilentInputChannelId)
                        throw new GraphValidationException("静音输入通道已预留给关闭录音的软件，不能转发声音到此通道。");
                    if (source.Kind == NodeKind.Microphone && source.Key == channel.CaptureEndpointId)
                        throw new GraphValidationException("不能把虚拟麦克风的录音端送回它自身的播放端。");
                    destinations.Add(new(channel.RenderEndpointId, true, id, edge.Volume * channel.Volume));
                }
            }
            // A playback node and a microphone node can refer to the same endpoint.
            // Sending the same source twice would amplify it and create echoes.
            if (source.Kind == NodeKind.Application && !string.IsNullOrWhiteSpace(source.Key) && destinations.Count == 0)
            {
                var disabledOutput = graph.Connections.FirstOrDefault(e => !e.Enabled && e.SourceId == source.Id &&
                    ApplicationOutputs.IsPrimary(source, nodes[e.TargetId]));
                if (disabledOutput != null)
                {
                    var target = nodes[disabledOutput.TargetId];
                    var playback = AudioOutput.Resolve(target.Key, endpoints) ?? defaultOutput;
                    // Keep the app managed at zero gain rather than restoring an audible Windows output.
                    if (playback != null)
                        destinations.Add(new(playback.Id, false, null, 0,
                            target.Key == AudioOutput.SystemDefault || playback.Id != target.Key, true));
                }
                else if (defaultOutput != null)
                    destinations.Add(new(defaultOutput.Id, false, null, SystemDefault: true));
            }
            var deduplicated = destinations.GroupBy(d => d.EndpointId).Select(g =>
                (g.FirstOrDefault(d => d.Native) ?? g.FirstOrDefault(d => !d.Microphone && !d.SystemDefault) ?? g.FirstOrDefault(d => !d.Microphone) ?? g.First())
                with { Volume = g.Max(d => d.Volume) }).ToList();
            Destination? primary = source.Kind == NodeKind.Application
                ? deduplicated.FirstOrDefault(d => d.Native && d.Volume > 0) ?? deduplicated.FirstOrDefault(d => !d.Microphone && d.Volume > 0) ?? deduplicated.FirstOrDefault(d => d.Volume > 0) ?? deduplicated.FirstOrDefault()
                : null;
            var mirrors = deduplicated.Where(d => d != primary).ToList();
            results.Add(new(source, primary, mirrors, deduplicated));
        }
        return new(results, warnings.Distinct().ToList());
    }

    public static void ValidateConnection(RouteNode source, RouteNode target, RouteMode mode)
    {
        if (source.Id == target.Id) throw new GraphValidationException("不能连接节点自身。");
        var audioSource = source.Kind is NodeKind.Application or NodeKind.Microphone;
        var valid = mode switch
        {
            RouteMode.Playback => audioSource && target.Kind == NodeKind.Playback,
            RouteMode.MicrophoneFeed => (audioSource &&
                target.Kind is NodeKind.VirtualMicrophone or NodeKind.Application) ||
                (source.Kind == NodeKind.VirtualMicrophone &&
                 target.Kind == NodeKind.Application &&
                 source.ChannelId == target.ChannelId),
            _ => false
        };
        if (!valid) throw new GraphValidationException("端口类型不匹配：软件声音连接播放设备或麦克风输入。");
    }

    private static void ValidateFeedback(Dictionary<string, RouteNode> nodes,
        List<RouteConnection> edges, Dictionary<string, VirtualChannel> channels, IReadOnlyList<AudioEndpoint> endpoints)
    {
        string? InputChannel(RouteNode node)
        {
            if (node.InputDeviceKey == AudioOutput.None) return null;
            if (node.InputDeviceKey != null && node.InputDeviceKey != AudioOutput.SystemDefault)
                return channels.Values.FirstOrDefault(c => c.CaptureEndpointId == node.InputDeviceKey)?.Id;
            if (node.InputDeviceKey == AudioOutput.SystemDefault && endpoints.FirstOrDefault(e => e.Capture && e.Default) is { } capture)
                return channels.Values.FirstOrDefault(c => c.CaptureEndpointId == capture.Id)?.Id;
            return node.ChannelId;
        }
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            var source = nodes[edge.SourceId];
            if (source.Kind != NodeKind.Application) continue;
            var target = nodes[edge.TargetId];
            string? channelId = target.Kind == NodeKind.Playback ? channels.Values.FirstOrDefault(c =>
                c.RenderEndpointId == AudioOutput.Resolve(target.Key, endpoints)?.Id)?.Id : target.ChannelId;
            if (channelId == null) continue;
            // Every application reading a channel receives the entire bus, including
            // connections to another target card assigned to that same channel.
            var recipients = nodes.Values.Where(n => n.Kind == NodeKind.Application && InputChannel(n) == channelId).ToArray();
            foreach (var recipient in recipients)
            {
                var from = AppIdentity.Normalize(source.Key);
                var to = AppIdentity.Normalize(recipient.Key);
                if (from.Length == 0 || to.Length == 0) continue;
                if (from == to) throw new GraphValidationException("这会把软件自己的声音送回它的麦克风。");
                if (!dependencies.TryGetValue(from, out var list)) dependencies[from] = list = [];
                list.Add(to);
            }
        }
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Cycle(string key)
        {
            if (visiting.Contains(key)) return true;
            if (!visited.Add(key)) return false;
            visiting.Add(key);
            if (dependencies.TryGetValue(key, out var next) && next.Any(Cycle)) return true;
            visiting.Remove(key);
            return false;
        }
        if (dependencies.Keys.Any(Cycle))
            throw new GraphValidationException("这些麦克风连接会形成应用间的声音反馈闭环。");
    }
}
