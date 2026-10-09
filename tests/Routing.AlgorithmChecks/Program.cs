using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.ConduitManager.Models;
using INP_IE.ConduitManager.Services;

var finder = new ConduitPathfinder();
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
}
TrayGraphEdge Edge(string a, string b, double cost, Element? tray = null,
    TrayGraphEdgeKind kind = TrayGraphEdgeKind.TrayRun, bool createsSegment = true)
    => new(a, b, tray!, new XYZ(), new XYZ(), createsSegment, cost, kind, null!, null!);
TrayGraph Graph(params TrayGraphEdge[] edges)
{
    var graph = new TrayGraph();
    foreach (var edge in edges) graph.AddEdge(edge);
    return graph;
}
string PathKeys(List<TrayGraphEdge> path) => string.Join(",", path.Select(e => e.To));

var weighted = Graph(Edge("s", "t", 10), Edge("s", "a", 2), Edge("a", "t", 3));
Check(finder.TryFindShortestPath(weighted, "s", "t", out var path) && PathKeys(path) == "a,t" && path.Sum(e => e.Cost) == 5,
    "Find minimum weighted cost, not minimum hop count");
Check(!finder.TryFindShortestPath(weighted, "t", "s", out _), "Respect edge direction");
Check(!finder.TryFindShortestPath(weighted, "s", "missing", out _), "Unreachable target");
Check(finder.ComputeDistances(weighted, "s")["a"] == 2 && finder.ComputeDistances(weighted, "s")["t"] == 5,
    "Single-source distances used by automatic endpoint ranking");
Check(finder.ComputeDistances(weighted, "missing").Count == 0, "Missing start produces no distances");
Check(!finder.TryFindShortestPath(weighted, "s", "s", out _), "Preserved source contract: no empty start=end route");

var ties = new[] { Edge("s", "b", 1), Edge("b", "t", 1), Edge("s", "a", 1), Edge("a", "t", 1) };
Check(finder.TryFindShortestPath(Graph(ties), "s", "t", out path) && PathKeys(path) == "a,t", "Deterministic tie by node name");
Check(finder.TryFindShortestPath(Graph(ties.Reverse().ToArray()), "s", "t", out path) && PathKeys(path) == "a,t", "Tie stable after insertion reorder");
var cycle = Graph(Edge("s", "a", 0), Edge("a", "s", 0), Edge("a", "t", 2));
Check(finder.TryFindShortestPath(cycle, "s", "t", out path) && path.Sum(e => e.Cost) == 2, "Zero-cost connector cycles terminate");

var required = new CableTray { Id = new ElementId(42) };
var via = Graph(Edge("s", "t", 1), Edge("s", "a", 1), Edge("a", "b", 2, required), Edge("b", "t", 1));
Check(finder.TryFindShortestPathViaElement(via, "s", "t", 42, out path) && PathKeys(path) == "a,b,t",
    "Required tray overrides shorter bypass");
Check(!finder.TryFindShortestPathViaElement(via, "s", "t", 99, out _), "Missing required tray fails");
var touching = Graph(Edge("s", "a", 0, required, TrayGraphEdgeKind.Connection, false), Edge("a", "t", 1));
Check(!finder.TryFindShortestPathViaElement(touching, "s", "t", 42, out _), "Touching required connector is not traversing tray");
var bridge = Graph(Edge("s", "t", 12, null, TrayGraphEdgeKind.Bridge), Edge("s", "a", 4), Edge("a", "t", 4));
Check(finder.TryFindShortestPath(bridge, "s", "t", out path) && PathKeys(path) == "a,t", "Penalized bridge loses to cheaper connected route");

// Compare against an independent all-pairs oracle on directed graphs with
// nonnegative costs, ties, cycles and disconnected components.
var random = new Random(20261009);
for (var sample = 0; sample < 100; sample++)
{
    const int n = 8;
    var graph = new TrayGraph();
    var oracle = new double[n, n];
    for (var a = 0; a < n; a++)
    {
        for (var b = 0; b < n; b++) oracle[a, b] = a == b ? 0 : double.PositiveInfinity;
        graph.AddEdge(Edge(a.ToString(), a.ToString(), 0));
    }
    for (var a = 0; a < n; a++)
    for (var b = 0; b < n; b++)
    {
        if (a == b || random.NextDouble() > .22) continue;
        var cost = random.Next(0, 15);
        graph.AddEdge(Edge(a.ToString(), b.ToString(), cost));
        oracle[a, b] = cost;
    }
    for (var k = 0; k < n; k++)
    for (var a = 0; a < n; a++)
    for (var b = 0; b < n; b++)
        oracle[a, b] = Math.Min(oracle[a, b], oracle[a, k] + oracle[k, b]);
    for (var a = 0; a < n; a++)
    {
        var distances = finder.ComputeDistances(graph, a.ToString());
        for (var b = 0; b < n; b++)
        {
            var reachable = !double.IsPositiveInfinity(oracle[a, b]);
            Check(distances.TryGetValue(b.ToString(), out var actual) == reachable && (!reachable || actual == oracle[a, b]),
                $"Distance oracle {sample}/{a}/{b}");
            if (a == b) continue;
            var found = finder.TryFindShortestPath(graph, a.ToString(), b.ToString(), out path);
            Check(found == reachable && (!found || path.Sum(e => e.Cost) == oracle[a, b]), $"Route oracle {sample}/{a}/{b}");
            if (found)
                Check(path[0].From == a.ToString() && path[^1].To == b.ToString() &&
                    path.Zip(path.Skip(1), (left, right) => left.To == right.From).All(x => x), "Route continuity");
        }
    }
}
Console.WriteLine($"PASS: {checks} assertions; 100 random graphs; original pathfinder and graph sources compiled directly.");
Console.WriteLine("These checks do not exercise Revit geometry or runtime integration.");
