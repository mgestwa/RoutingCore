using System.Collections.Generic;
using System.Linq;

namespace INP_IE.ConduitManager.Models
{
    public class TrayGraph
    {
        private readonly Dictionary<string, List<TrayGraphEdge>> _adjacency = new Dictionary<string, List<TrayGraphEdge>>();

        public IEnumerable<string> NodeKeys
        {
            get
            {
                var nodes = new HashSet<string>(_adjacency.Keys);
                foreach (var edge in _adjacency.Values.SelectMany(edges => edges))
                {
                    nodes.Add(edge.To);
                }

                return nodes;
            }
        }

        public bool ContainsNode(string key)
        {
            return _adjacency.ContainsKey(key);
        }

        public IEnumerable<TrayGraphEdge> GetEdges(string key)
        {
            return _adjacency.TryGetValue(key, out var edges)
                ? edges
                : Enumerable.Empty<TrayGraphEdge>();
        }

        public void AddEdge(TrayGraphEdge edge)
        {
            if (!_adjacency.TryGetValue(edge.From, out var edges))
            {
                edges = new List<TrayGraphEdge>();
                _adjacency[edge.From] = edges;
            }

            if (!_adjacency.ContainsKey(edge.To))
            {
                _adjacency[edge.To] = new List<TrayGraphEdge>();
            }

            edges.Add(edge);
        }
    }
}