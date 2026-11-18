namespace AudioFlow.Core;

public sealed class GraphValidationException(string message) : Exception(message);

public static class GraphCompiler
{
    public static CompiledGraph Compile(RouteGraph graph, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (graph.Version != 1) throw new GraphValidationException("这个声音设置文件暂时打不开，请换一个文件。");
        if (graph.Nodes.Count > 2048 || graph.Connections.Count > 16384)
            throw new GraphValidationException("这个文件里的卡片和连线太多了，请减少一些后再试。");
        var nodes = new Dictionary<string, RouteNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrEmpty(node.Id) || !nodes.TryAdd(node.Id, node))
                throw new GraphValidationException("设置文件里有重复或缺少信息的卡片，请换一个文件。");
            if (!float.IsFinite(node.Volume) || node.Volume is < 0 or > 2 ||
                !double.IsFinite(node.X) || !double.IsFinite(node.Y))
                throw new GraphValidationException("设置文件里的音量或卡片位置不正确，请换一个文件。");
            if (node.PrimaryOutput && (node.Kind != NodeKind.Playback || string.IsNullOrWhiteSpace(node.Key) ||
                !graph.Nodes.Any(n => n.Kind == NodeKind.Application && AppIdentity.Normalize(n.Key) == AppIdentity.Normalize(node.OwnerAppKey ?? ""))))
                throw new GraphValidationException("请先选择软件，再设置它的主要播放设备。");
        }
        var channels = new Dictionary<string, VirtualChannel>(StringComparer.Ordinal);
        foreach (var channel in graph.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Id) || !channels.TryAdd(channel.Id, channel))
                throw new GraphValidationException("设置文件里有重复或缺少信息的传声音设备。");
            if (channel.RenderEndpointId == channel.CaptureEndpointId)
                throw new GraphValidationException("传声音的设备要分别选择发送和接收声音的设备，不能两边都选同一个。");
            if (!float.IsFinite(channel.Volume) || channel.Volume is < 0 or > 1)
                throw new GraphValidationException("传声音设备的音量不正确，请重新设置。");
            if (graph.Channels.Any(c => c.Id != channel.Id && (c.RenderEndpointId == channel.RenderEndpointId || c.CaptureEndpointId == channel.CaptureEndpointId)))
                throw new GraphValidationException("同一个传声音的设备不能重复添加，请使用原来的那一个。");
        }
        var enabled = graph.Connections.Where(c => c.Enabled).ToList();
        foreach (var edge in graph.Connections)
        {
            if (!float.IsFinite(edge.Volume) || edge.Volume is < 0 or > 1)
                throw new GraphValidationException("这条线的音量不正确，请重新调整。");
            if (!nodes.TryGetValue(edge.SourceId, out var source) ||
                !nodes.TryGetValue(edge.TargetId, out var target))
                throw new GraphValidationException("有一条线连到了已经不存在的卡片，请重新连接。");
            ValidateConnection(source, target, edge.Mode);
            if (target.PrimaryOutput && !ApplicationOutputs.IsPrimary(source, target))
                throw new GraphValidationException("这条主要播放的线只用于它所属的软件，请给其他软件另外添加播放设备。");
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
                            throw new GraphValidationException("这个设备正在用来关闭录音，不能再往它送声音。");
                        destinations.Add(new(playback.Id, false, null, edge.Volume, target.Key == AudioOutput.SystemDefault, ApplicationOutputs.IsPrimary(source, target)));
                    }
                    else warnings.Add($"{target.Name}：设备未连接，保留此连线等待重连。");
                }
                else
                {
                    if (target.ChannelId is not { } id || !channels.TryGetValue(id, out var channel) ||
                        !outputs.Contains(channel.RenderEndpointId) || !captures.Contains(channel.CaptureEndpointId))
                    {
                        warnings.Add($"{target.Name}：请先在设置里选好发送和接收声音的设备。");
                        continue;
                    }
                    if (id == graph.SilentInputChannelId)
                        throw new GraphValidationException("这个设备已经留给关闭录音的软件，不能再往它送声音。");
                    if (source.Kind == NodeKind.Microphone && source.Key == channel.CaptureEndpointId)
                        throw new GraphValidationException("不能让这个麦克风把刚收到的声音，又送回自己。");
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
        if (source.Id == target.Id) throw new GraphValidationException("不能把一张卡片连回自己。");
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
        if (!valid) throw new GraphValidationException("这两个位置不能连在一起。请从软件右边的圆点，拖到播放设备或麦克风左边的圆点。");
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
            throw new GraphValidationException("这样连接会让声音在几个软件之间不停传回来。请断开一条返回的线。");
    }
}
