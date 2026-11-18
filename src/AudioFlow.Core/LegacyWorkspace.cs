using System.Text.Json;
using System.Text.Json.Nodes;

namespace AudioFlow.Core;

internal static class LegacyWorkspace
{
    public static WorkspaceState Read(string json, out bool migrated)
    {
        var root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new GraphValidationException("工作区必须是 JSON 对象。");
        migrated = root.ContainsKey("Mode");
        if (!migrated) return root.Deserialize<WorkspaceState>(SceneStore.JsonOptions)!;
        string mode = root["Mode"]?.GetValue<string>() ?? "PerApplication";
        string View(string value) => value switch
        {
            "Global" => "Global", "Application" or "PerApplication" => "PerApplication",
            _ => throw new GraphValidationException("旧工作区的配置视图不受支持。")
        };
        root["View"] ??= JsonValue.Create(View(mode));
        root["RoutingPaused"] ??= root["WasPaused"]?.DeepClone() ?? JsonValue.Create(false);
        if (!root.ContainsKey("Configuration"))
        {
            RouteGraph ReadGraph(string name)
            {
                var value = root[name]?.DeepClone() as JsonObject ?? new JsonObject();
                if (value["Nodes"] is JsonArray nodes)
                    foreach (var node in nodes.OfType<JsonObject>()) node.Remove("ListenLocally");
                var graph = value.Deserialize<RouteGraph>(SceneStore.JsonOptions)!;
                GraphCompiler.Compile(graph, []);
                return graph;
            }
            // The last running graph wins shared settings; unique routes from both views survive.
            bool globalFirst = View(root["LastEnabledMode"]?.GetValue<string>() ?? mode) == "Global";
            var first = ReadGraph(globalFirst ? "GlobalConfiguration" : "ApplicationConfiguration");
            var second = ReadGraph(globalFirst ? "ApplicationConfiguration" : "GlobalConfiguration");
            Merge(first, second);
            root["Configuration"] = JsonSerializer.SerializeToNode(first, SceneStore.JsonOptions);
        }
        foreach (string name in new[] { "Mode", "LastEnabledMode", "WasPaused", "ApplicationConfiguration", "GlobalConfiguration" })
            root.Remove(name);
        return root.Deserialize<WorkspaceState>(SceneStore.JsonOptions)!;
    }

    private static void Merge(RouteGraph target, RouteGraph other)
    {
        foreach (var channel in other.Channels)
        {
            var existing = target.Channels.FirstOrDefault(c => c.Id == channel.Id);
            if (existing == null) target.Channels.Add(channel);
            else if (existing.RenderEndpointId != channel.RenderEndpointId || existing.CaptureEndpointId != channel.CaptureEndpointId)
                throw new GraphValidationException("旧工作区的同名虚拟通道设备冲突；原文件已保留，请分别导出配置后重新导入。");
        }
        var ids = new Dictionary<string, string>();
        foreach (var node in other.Nodes)
        {
            string oldId = node.Id;
            var existing = target.Nodes.FirstOrDefault(n => n.Kind == node.Kind &&
                (node.Kind == NodeKind.VirtualMicrophone ? n.ChannelId == node.ChannelId && node.ChannelId != null :
                    !string.IsNullOrWhiteSpace(node.Key) && AppIdentity.Normalize(n.Key) == AppIdentity.Normalize(node.Key)));
            if (existing == null)
            {
                if (target.Nodes.Any(n => n.Id == node.Id)) node.Id = Guid.NewGuid().ToString("N");
                target.Nodes.Add(node); existing = node;
            }
            ids[oldId] = existing.Id;
        }
        foreach (var edge in other.Connections)
        {
            edge.SourceId = ids[edge.SourceId]; edge.TargetId = ids[edge.TargetId];
            if (target.Connections.Any(c => c.SourceId == edge.SourceId && c.TargetId == edge.TargetId && c.Mode == edge.Mode)) continue;
            if (target.Connections.Any(c => c.Id == edge.Id)) edge.Id = Guid.NewGuid().ToString("N");
            target.Connections.Add(edge);
        }
    }
}
