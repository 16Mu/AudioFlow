namespace AudioFlow.Core;

public static class ApplicationOutputs
{
    public static bool IsPrimary(RouteNode source, RouteNode target) =>
        source.Kind == NodeKind.Application && target.PrimaryOutput &&
        AppIdentity.Normalize(source.Key) == AppIdentity.Normalize(target.OwnerAppKey ?? "");

    public static void Ensure(RouteGraph graph)
    {
        foreach (var source in graph.Nodes.Where(n => n.Kind == NodeKind.Application && !string.IsNullOrWhiteSpace(n.Key)).ToArray())
        {
            var output = graph.Nodes.FirstOrDefault(n => n.PrimaryOutput &&
                AppIdentity.Normalize(n.OwnerAppKey ?? "") == AppIdentity.Normalize(source.Key));
            var route = graph.Connections.FirstOrDefault(c => c.Enabled && c.SourceId == source.Id &&
                graph.Nodes.Any(n => n.Id == c.TargetId && n.Kind == NodeKind.Playback));
            if (output == null)
            {
                var previous = graph.Nodes.FirstOrDefault(n => n.Id == route?.TargetId);
                output = new()
                {
                    Kind = NodeKind.Playback, PrimaryOutput = true, OwnerAppKey = source.Key,
                    Name = previous?.Name ?? "Windows 默认输出", Key = previous?.Key ?? AudioOutput.SystemDefault,
                    X = source.X + 410, Y = source.Y
                };
                graph.Nodes.Add(output);
                if (route != null) route.TargetId = output.Id;
            }
            if (!graph.Connections.Any(c => c.SourceId == source.Id && c.TargetId == output.Id))
                graph.Connections.Add(new() { SourceId = source.Id, TargetId = output.Id, Mode = RouteMode.Playback, Automatic = true });
        }
        // The former shared default card is replaced by separate per-app cards.
        graph.Nodes.RemoveAll(n => !n.PrimaryOutput && n.Kind == NodeKind.Playback && n.Key == AudioOutput.SystemDefault &&
            !graph.Connections.Any(c => c.SourceId == n.Id || c.TargetId == n.Id));
        ChannelPool.NormalizeOutputs(graph);
    }
}
