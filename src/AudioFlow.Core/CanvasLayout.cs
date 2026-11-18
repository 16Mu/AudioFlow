namespace AudioFlow.Core;

public sealed record LayoutCard(string Id, string Name, NodeKind Kind, bool PrimaryOutput, double Width, double Height, double InputY, double OutputY);
public sealed record LayoutPoint(double X, double Y);
public sealed record LayoutResult(IReadOnlyDictionary<string, LayoutPoint> Positions, int Crossings, int Obstructions, double Score);

public static class CanvasLayout
{
    public static LayoutResult Arrange(IReadOnlyList<LayoutCard> cards, IReadOnlyList<RouteConnection> connections)
    {
        var byId = cards.ToDictionary(c => c.Id);
        var edges = connections.Where(e => byId.ContainsKey(e.SourceId) && byId.ContainsKey(e.TargetId)).ToArray();
        var adjacent = cards.ToDictionary(c => c.Id, _ => new HashSet<string>());
        foreach (var edge in edges) { adjacent[edge.SourceId].Add(edge.TargetId); adjacent[edge.TargetId].Add(edge.SourceId); }
        var remaining = cards.OrderBy(c => TypeOrder(c)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.Id).ToHashSet();
        var positions = new Dictionary<string, LayoutPoint>(); double componentTop = 60; int crossings = 0, obstructions = 0; double score = 0;
        while (remaining.Count > 0)
        {
            var component = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(remaining.First());
            while (queue.TryDequeue(out var id))
            { if (!component.Add(id)) continue; remaining.Remove(id); foreach (var neighbor in adjacent[id]) queue.Enqueue(neighbor); }
            var group = cards.Where(c => component.Contains(c.Id)).ToArray();
            var groupEdges = edges.Where(e => component.Contains(e.SourceId)).ToArray();
            var ranks = Ranks(group, groupEdges);
            LayoutResult? best = null;
            foreach (var spacing in new[] { (Horizontal: 150d, Vertical: 48d), (Horizontal: 190d, Vertical: 68d) })
            {
                var columns = group.GroupBy(c => ranks[c.Id]).OrderBy(g => g.Key).ToDictionary(g => g.Key,
                    g => g.OrderBy(TypeOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());
                var candidate = new Dictionary<string, LayoutPoint>();
                var x = new Dictionary<int, double>(); double columnX = 60;
                foreach (var column in columns) { x[column.Key] = columnX; columnX += column.Value.Max(c => c.Width) + spacing.Horizontal; }
                void Pack(bool align)
                {
                    foreach (var column in columns)
                    {
                        double top = 60;
                        foreach (var card in column.Value)
                        {
                            var incoming = groupEdges.Where(e => e.TargetId == card.Id && candidate.ContainsKey(e.SourceId)).ToArray();
                            double desired = incoming.Length == 0 || !align ? top : incoming.Select(e =>
                                candidate[e.SourceId].Y + byId[e.SourceId].OutputY - card.InputY).Average();
                            if (card.PrimaryOutput && incoming.Length == 1) desired = candidate[incoming[0].SourceId].Y + byId[incoming[0].SourceId].OutputY - card.InputY;
                            double y = card.PrimaryOutput && incoming.Length == 1 && ReferenceEquals(card, column.Value[0]) ? desired : Math.Max(top, desired);
                            candidate[card.Id] = new(x[column.Key], y); top = y + card.Height + spacing.Vertical;
                        }
                    }
                }
                Pack(false);
                for (int sweep = 0; sweep < 6; sweep++)
                {
                    bool down = sweep % 2 == 0;
                    foreach (var key in (down ? columns.Keys.Order() : columns.Keys.OrderDescending()).ToArray())
                    {
                        double Center(LayoutCard card)
                        {
                            var neighbors = groupEdges.Where(e => down ? e.TargetId == card.Id : e.SourceId == card.Id)
                                .Select(e => down ? e.SourceId : e.TargetId).Where(candidate.ContainsKey).ToArray();
                            return neighbors.Length == 0 ? candidate[card.Id].Y + card.Height / 2 : neighbors.Average(id => candidate[id].Y + byId[id].Height / 2);
                        }
                        columns[key] = columns[key].OrderBy(Center).ThenBy(TypeOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    Pack(true);
                    var evaluated = Evaluate(group, groupEdges, candidate);
                    if (best == null || evaluated.Score < best.Score) best = evaluated;
                }
            }
            double minimumY = best!.Positions.Values.Min(p => p.Y);
            foreach (var pair in best.Positions) positions[pair.Key] = new(pair.Value.X, pair.Value.Y + componentTop - minimumY);
            componentTop += group.Max(c => best.Positions[c.Id].Y + c.Height) - minimumY + 100;
            crossings += best.Crossings; obstructions += best.Obstructions; score += best.Score;
        }
        return new(positions, crossings, obstructions, score);
    }
    private static int TypeOrder(LayoutCard c) => c.Kind switch { NodeKind.Microphone => 0, NodeKind.Application => 1, NodeKind.VirtualMicrophone => 2, _ => c.PrimaryOutput ? 3 : 4 };
    private static Dictionary<string, int> Ranks(LayoutCard[] cards, RouteConnection[] edges)
    {
        var adjacency = cards.ToDictionary(c => c.Id, c => edges.Where(e => e.SourceId == c.Id).Select(e => e.TargetId).Distinct().ToArray());
        var index = new Dictionary<string, int>(); var low = new Dictionary<string, int>(); var onStack = new HashSet<string>(); var stack = new Stack<string>();
        var group = new Dictionary<string, int>(); int next = 0, component = 0;
        void Visit(string id)
        {
            index[id] = low[id] = next++; stack.Push(id); onStack.Add(id);
            foreach (string target in adjacency[id])
            { if (!index.ContainsKey(target)) { Visit(target); low[id] = Math.Min(low[id], low[target]); } else if (onStack.Contains(target)) low[id] = Math.Min(low[id], index[target]); }
            if (low[id] != index[id]) return;
            string member; do { member = stack.Pop(); onStack.Remove(member); group[member] = component; } while (member != id); component++;
        }
        foreach (var card in cards) if (!index.ContainsKey(card.Id)) Visit(card.Id);
        bool microphone = cards.Any(c => c.Kind == NodeKind.Microphone);
        var ranks = Enumerable.Range(0, component).ToDictionary(i => i, i => cards.Where(c => group[c.Id] == i).All(c => c.Kind == NodeKind.Microphone) ? 0 : microphone ? 1 : 0);
        var pairs = edges.Where(e => group[e.SourceId] != group[e.TargetId]).Select(e => (From: group[e.SourceId], To: group[e.TargetId])).Distinct().ToArray();
        for (int pass = 0; pass < component; pass++)
        { bool changed = false; foreach (var pair in pairs) if (ranks[pair.To] < ranks[pair.From] + 1) { ranks[pair.To] = ranks[pair.From] + 1; changed = true; } if (!changed) break; }
        return cards.ToDictionary(c => c.Id, c => ranks[group[c.Id]]);
    }
    private static LayoutResult Evaluate(LayoutCard[] cards, RouteConnection[] edges, Dictionary<string, LayoutPoint> positions)
    {
        var byId = cards.ToDictionary(c => c.Id); int crossing = 0, obstruction = 0; double length = 0;
        var selected = edges.Length <= 160 ? edges : edges.Where((_, i) => i % Math.Max(1, edges.Length / 160) == 0).Take(160).ToArray();
        var paths = selected.Select(edge =>
        {
            var source = byId[edge.SourceId]; var target = byId[edge.TargetId];
            var a = new LayoutPoint(positions[source.Id].X + source.Width, positions[source.Id].Y + source.OutputY);
            var b = new LayoutPoint(positions[target.Id].X, positions[target.Id].Y + target.InputY);
            double bend = Math.Min(100 + Math.Sqrt(Math.Abs(b.X - a.X) * 25), Math.Max(Math.Min(100, Math.Abs(b.Y - a.Y)), Math.Abs(b.X - a.X) / 2));
            var p = new LayoutPoint[17];
            for (int i = 0; i < p.Length; i++)
            {
                double t = i / 16d, s = 1 - t;
                p[i] = new(s * s * s * a.X + 3 * s * s * t * (a.X + bend) + 3 * s * t * t * (b.X - bend) + t * t * t * b.X,
                    s * s * s * a.Y + 3 * s * s * t * a.Y + 3 * s * t * t * b.Y + t * t * t * b.Y);
            }
            length += Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y);
            foreach (var card in cards.Where(c => c.Id != source.Id && c.Id != target.Id))
                if (p.Any(point => point.X > positions[card.Id].X - 12 && point.X < positions[card.Id].X + card.Width + 12 &&
                    point.Y > positions[card.Id].Y - 12 && point.Y < positions[card.Id].Y + card.Height + 12)) obstruction++;
            return (Edge: edge, Points: p);
        }).ToArray();
        static double Side(LayoutPoint a, LayoutPoint b, LayoutPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        for (int i = 0; i < paths.Length; i++) for (int j = i + 1; j < paths.Length; j++)
        {
            var a = paths[i]; var b = paths[j];
            if (a.Edge.SourceId == b.Edge.SourceId || a.Edge.TargetId == b.Edge.TargetId) continue;
            bool intersects = false;
            for (int x = 0; x < 16 && !intersects; x++) for (int y = 0; y < 16; y++)
                if (Side(a.Points[x], a.Points[x + 1], b.Points[y]) * Side(a.Points[x], a.Points[x + 1], b.Points[y + 1]) < 0 &&
                    Side(b.Points[y], b.Points[y + 1], a.Points[x]) * Side(b.Points[y], b.Points[y + 1], a.Points[x + 1]) < 0) { intersects = true; break; }
            if (intersects) crossing++;
        }
        double area = cards.Length == 0 ? 0 : cards.Max(c => positions[c.Id].Y + c.Height);
        return new(new Dictionary<string, LayoutPoint>(positions), crossing, obstruction, obstruction * 10000 + crossing * 1500 + length / 80 + area / 100);
    }
}
