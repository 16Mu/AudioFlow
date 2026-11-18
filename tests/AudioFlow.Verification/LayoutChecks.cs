using AudioFlow.Core;

internal static class LayoutChecks
{
    public static void Run(Action<bool, string> check)
    {
        LayoutCard Card(string id, NodeKind kind, double height = 330, bool primary = false, double input = 140, double output = 140)
            => new(id, id, kind, primary, 326, height, input, output);
        RouteConnection Edge(string from, string to) => new() { SourceId = from, TargetId = to };
        bool NoOverlap(LayoutCard[] cards, LayoutResult layout)
        {
            for (int i = 0; i < cards.Length; i++) for (int j = i + 1; j < cards.Length; j++)
            {
                var a = cards[i]; var b = cards[j]; var p = layout.Positions[a.Id]; var q = layout.Positions[b.Id];
                if (p.X < q.X + b.Width && q.X < p.X + a.Width && p.Y < q.Y + b.Height && q.Y < p.Y + a.Height) return false;
            }
            return true;
        }
        LayoutCard[] pair = [Card("app", NodeKind.Application, 540, output: 208), Card("out", NodeKind.Playback, 430, true, input: 131)];
        var simple = CanvasLayout.Arrange(pair, [Edge("app", "out")]);
        check(Math.Abs(simple.Positions["app"].Y + 208 - simple.Positions["out"].Y - 131) < .01 && simple.Obstructions == 0,
            "layout aligns native output ports using actual card and connector dimensions");
        pair = [Card("app", NodeKind.Application, 310, output: 70), Card("out", NodeKind.Playback, 414, true, input: 132)];
        simple = CanvasLayout.Arrange(pair, [Edge("app", "out")]);
        check(Math.Abs(simple.Positions["app"].Y + 70 - simple.Positions["out"].Y - 132) < .01 && simple.Positions.Values.All(p => p.Y >= 60),
            "layout aligns a shorter focused application with a taller output card and retains the canvas margin");
        LayoutCard[] chain = [Card("mic", NodeKind.Microphone), Card("app", NodeKind.Application, 760),
            Card("bus", NodeKind.VirtualMicrophone, 430), Card("receiver", NodeKind.Application, 610), Card("out", NodeKind.Playback, primary: true)];
        RouteConnection[] edges = [Edge("mic", "app"), Edge("app", "bus"), Edge("bus", "receiver"), Edge("receiver", "out")];
        var flow = CanvasLayout.Arrange(chain, edges);
        check(edges.All(e => flow.Positions[e.TargetId].X > flow.Positions[e.SourceId].X) && NoOverlap(chain, flow),
            "layout places microphones, software, virtual inputs, receivers and outputs along sound direction without overlap");
        LayoutCard[] crossed = [Card("A", NodeKind.Application, 730), Card("B", NodeKind.Application, 440),
            Card("C", NodeKind.Playback, 560), Card("D", NodeKind.Playback, 390)];
        var reordered = CanvasLayout.Arrange(crossed, [Edge("A", "D"), Edge("B", "C")]);
        check(reordered.Crossings == 0 && reordered.Obstructions == 0 && NoOverlap(crossed, reordered),
            "layout keeps independent routes separate instead of crossing or overlapping unequal cards");
        LayoutCard[] merge = [Card("A", NodeKind.Application, 700), Card("B", NodeKind.Application, 420),
            Card("mix", NodeKind.VirtualMicrophone, 650), Card("out", NodeKind.Playback)];
        RouteConnection[] mergeEdges = [Edge("A", "mix"), Edge("B", "mix"), Edge("mix", "out")];
        var merged = CanvasLayout.Arrange(merge, mergeEdges);
        check(NoOverlap(merge, merged) && merged.Obstructions == 0, "layout keeps shared sound inputs and branching connections clear of card bodies");
        var repeated = CanvasLayout.Arrange(merge, mergeEdges);
        check(merged.Positions.All(p => repeated.Positions[p.Key] == p.Value), "arranging the same graph produces stable positions");
        var cycle = CanvasLayout.Arrange(chain[..2], [Edge("mic", "app"), Edge("app", "mic")]);
        check(cycle.Positions.Count == 2 && cycle.Positions.Values.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)),
            "layout terminates safely for a cyclic imported graph");
        check(CanvasLayout.Arrange([], []).Positions.Count == 0, "empty canvas can be arranged");
    }
}
