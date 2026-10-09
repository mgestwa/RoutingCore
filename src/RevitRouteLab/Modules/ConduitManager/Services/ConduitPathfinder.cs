using System;
using System.Collections.Generic;
using RevitRouteLab.ConduitManager.Models;

namespace RevitRouteLab.ConduitManager.Services
{
    public class ConduitPathfinder
    {
        public bool TryFindShortestPath(TrayGraph graph, string startKey, string endKey, out List<TrayGraphEdge> routeEdges)
        {
            routeEdges = null;
            var distances = Explore(graph, startKey, out var previous);
            if (!distances.ContainsKey(endKey) || !previous.ContainsKey(endKey))
            {
                return false;
            }

            var path = new List<TrayGraphEdge>();
            var nodeKey = endKey;
            while (nodeKey != startKey)
            {
                if (!previous.TryGetValue(nodeKey, out var edge))
                {
                    return false;
                }

                path.Add(edge);
                nodeKey = edge.From;
            }

            path.Reverse();
            routeEdges = path;
            return true;
        }

        /// <summary>
        /// Wyznacza najkrótszą trasę, która rzeczywiście przechodzi po wskazanym
        /// prostym korytku. Stan Dijkstry pamięta, czy wymagana krawędź została
        /// już użyta, dzięki czemu samo dotknięcie konektora korytka nie spełnia
        /// ograniczenia.
        /// </summary>
        public bool TryFindShortestPathViaElement(
            TrayGraph graph,
            string startKey,
            string endKey,
            int requiredTrayId,
            out List<TrayGraphEdge> routeEdges)
        {
            routeEdges = new List<TrayGraphEdge>();
            if (graph == null || startKey == null || endKey == null ||
                !graph.ContainsNode(startKey) || !graph.ContainsNode(endKey))
            {
                return false;
            }

            var startState = new RequiredElementRouteState(startKey, false);
            var endState = new RequiredElementRouteState(endKey, true);
            var distances = new Dictionary<RequiredElementRouteState, double>();
            var previous = new Dictionary<RequiredElementRouteState, RequiredElementTransition>();
            var queue = new SortedSet<RequiredElementQueueEntry>(RequiredElementQueueEntry.Comparer);

            distances[startState] = 0;
            queue.Add(new RequiredElementQueueEntry(0, startState));

            while (queue.Count > 0)
            {
                var current = queue.Min;
                queue.Remove(current);

                if (!distances.TryGetValue(current.State, out var currentDistance) ||
                    current.Distance > currentDistance)
                {
                    continue;
                }

                foreach (var edge in graph.GetEdges(current.State.NodeKey))
                {
                    var hasPassedRequiredTray =
                        current.State.HasPassedRequiredTray ||
                        IsRequiredTrayEdge(edge, requiredTrayId);
                    var nextState = new RequiredElementRouteState(
                        edge.To,
                        hasPassedRequiredTray);
                    var candidate = currentDistance + edge.Cost;

                    if (distances.TryGetValue(nextState, out var known))
                    {
                        if (candidate >= known)
                        {
                            continue;
                        }

                        queue.Remove(new RequiredElementQueueEntry(known, nextState));
                    }

                    distances[nextState] = candidate;
                    previous[nextState] = new RequiredElementTransition(current.State, edge);
                    queue.Add(new RequiredElementQueueEntry(candidate, nextState));
                }
            }

            if (!distances.ContainsKey(endState))
            {
                return false;
            }

            var path = new List<TrayGraphEdge>();
            var state = endState;
            while (!state.Equals(startState))
            {
                if (!previous.TryGetValue(state, out var transition))
                {
                    return false;
                }

                path.Add(transition.Edge);
                state = transition.PreviousState;
            }

            path.Reverse();
            routeEdges = path;
            return true;
        }

        private static bool IsRequiredTrayEdge(TrayGraphEdge edge, int requiredTrayId)
        {
            return edge != null &&
                   edge.CreatesSegment &&
                   edge.Kind == TrayGraphEdgeKind.TrayRun &&
                   edge.Element != null &&
                   edge.Element.Id.IntegerValue == requiredTrayId;
        }

        /// <summary>
        /// Zwraca koszt dojścia do każdego osiągalnego węzła. Pozwala ocenić
        /// długość trasy do wielu kandydatów jednym przebiegiem, zamiast wybierać
        /// koniec trasy po odległości w linii prostej.
        /// </summary>
        public Dictionary<string, double> ComputeDistances(TrayGraph graph, string startKey)
        {
            return Explore(graph, startKey, out _);
        }

        /// <summary>
        /// Dijkstra na kopcu. Remisy rozstrzyga klucz węzła, więc ten sam model
        /// zawsze daje tę samą trasę — wcześniejsza wersja wybierała spośród
        /// remisów w kolejności zbioru mieszającego.
        /// </summary>
        private static Dictionary<string, double> Explore(
            TrayGraph graph,
            string startKey,
            out Dictionary<string, TrayGraphEdge> previous)
        {
            var distances = new Dictionary<string, double>(StringComparer.Ordinal);
            previous = new Dictionary<string, TrayGraphEdge>(StringComparer.Ordinal);
            if (graph == null || startKey == null || !graph.ContainsNode(startKey))
            {
                return distances;
            }

            var queue = new SortedSet<QueueEntry>(QueueEntry.Comparer);
            distances[startKey] = 0;
            queue.Add(new QueueEntry(0, startKey));

            while (queue.Count > 0)
            {
                var current = queue.Min;
                queue.Remove(current);

                if (!distances.TryGetValue(current.Key, out var currentDistance) ||
                    current.Distance > currentDistance)
                {
                    continue;
                }

                foreach (var edge in graph.GetEdges(current.Key))
                {
                    var candidate = currentDistance + edge.Cost;
                    if (distances.TryGetValue(edge.To, out var known))
                    {
                        if (candidate >= known)
                        {
                            continue;
                        }

                        queue.Remove(new QueueEntry(known, edge.To));
                    }

                    distances[edge.To] = candidate;
                    previous[edge.To] = edge;
                    queue.Add(new QueueEntry(candidate, edge.To));
                }
            }

            return distances;
        }

        private readonly struct RequiredElementRouteState : IEquatable<RequiredElementRouteState>
        {
            public RequiredElementRouteState(string nodeKey, bool hasPassedRequiredTray)
            {
                NodeKey = nodeKey;
                HasPassedRequiredTray = hasPassedRequiredTray;
            }

            public string NodeKey { get; }

            public bool HasPassedRequiredTray { get; }

            public bool Equals(RequiredElementRouteState other)
            {
                return HasPassedRequiredTray == other.HasPassedRequiredTray &&
                       string.Equals(NodeKey, other.NodeKey, StringComparison.Ordinal);
            }

            public override bool Equals(object? obj)
            {
                return obj is RequiredElementRouteState other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.Ordinal.GetHashCode(NodeKey ?? string.Empty) * 397) ^
                           HasPassedRequiredTray.GetHashCode();
                }
            }
        }

        private readonly struct RequiredElementTransition
        {
            public RequiredElementTransition(
                RequiredElementRouteState previousState,
                TrayGraphEdge edge)
            {
                PreviousState = previousState;
                Edge = edge;
            }

            public RequiredElementRouteState PreviousState { get; }

            public TrayGraphEdge Edge { get; }
        }

        private readonly struct RequiredElementQueueEntry
        {
            public RequiredElementQueueEntry(
                double distance,
                RequiredElementRouteState state)
            {
                Distance = distance;
                State = state;
            }

            public double Distance { get; }

            public RequiredElementRouteState State { get; }

            public static IComparer<RequiredElementQueueEntry> Comparer { get; } =
                new RequiredElementEntryComparer();

            private class RequiredElementEntryComparer : IComparer<RequiredElementQueueEntry>
            {
                public int Compare(RequiredElementQueueEntry x, RequiredElementQueueEntry y)
                {
                    var byDistance = x.Distance.CompareTo(y.Distance);
                    if (byDistance != 0)
                    {
                        return byDistance;
                    }

                    var byNode = string.CompareOrdinal(x.State.NodeKey, y.State.NodeKey);
                    return byNode != 0
                        ? byNode
                        : x.State.HasPassedRequiredTray.CompareTo(y.State.HasPassedRequiredTray);
                }
            }
        }

        private struct QueueEntry
        {
            public QueueEntry(double distance, string key)
            {
                Distance = distance;
                Key = key;
            }

            public double Distance { get; }

            public string Key { get; }

            public static IComparer<QueueEntry> Comparer { get; } = new EntryComparer();

            private class EntryComparer : IComparer<QueueEntry>
            {
                public int Compare(QueueEntry x, QueueEntry y)
                {
                    var byDistance = x.Distance.CompareTo(y.Distance);
                    return byDistance != 0
                        ? byDistance
                        : string.CompareOrdinal(x.Key, y.Key);
                }
            }
        }
    }
}
