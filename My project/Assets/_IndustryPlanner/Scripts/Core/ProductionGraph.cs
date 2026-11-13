using System;
using System.Collections.Generic;
using UnityEngine;

namespace IndustryPlanner.Core
{
    /// <summary>A group of identical machines. Saved as-is in the production file.</summary>
    [Serializable]
    public class GraphNode
    {
        public string id;
        public string building;
        [Tooltip("Recipe id for producers; item id for extractors, generators (fuel) and logistics nodes.")]
        public string selection;
        public float count = 1f;   // Number of machines (fractions allowed while planning).
        public float clock = 1f;   // 1 = 100%.
        public int purity = 1;     // Extractors: 0 impure, 1 normal, 2 pure.
        public int amplifiers;     // Filled amplifier slots (Somersloops).
        public float rate = 60f;   // Input nodes: items/min supplied.
        public Vector2 position;

        public GraphNode Clone() => (GraphNode)MemberwiseClone();
    }

    /// <summary>A belt or pipe: from an output pin to an input pin carrying the same item.</summary>
    [Serializable]
    public class GraphLink
    {
        public string from, to, item;
        public string transport;   // Belt/pipe tier id; empty = fastest unlocked.
        public int lanes = 1;      // Parallel belts or pipes.
        public GraphLink() { } // For JsonUtility.
        public GraphLink(string from, string to, string item) { this.from = from; this.to = to; this.item = item; }
    }

    [Serializable]
    public class ProductionGraph
    {
        public string id = Guid.NewGuid().ToString("N");
        public string gameId;
        public string name = "Untitled production";
        public long lastEditedTicks;
        public List<GraphNode> nodes = new();
        public List<GraphLink> links = new();
        public Vector2 viewOffset;
        public float viewZoom = 1f;
        public int tier = 99;           // Progression level; old saves default to everything unlocked.
        public bool alternates = true;  // Alternate recipes available.

        public DateTime LastEdited => new(lastEditedTicks == 0 ? DateTime.Now.Ticks : lastEditedTicks);

        public GraphNode Node(string id) => nodes.Find(n => n.id == id);

        public GraphNode AddNode(string building, string selection, Vector2 position)
        {
            var node = new GraphNode { id = Guid.NewGuid().ToString("N").Substring(0, 8), building = building, selection = selection, position = position };
            nodes.Add(node);
            return node;
        }

        public void RemoveNode(string nodeId)
        {
            nodes.RemoveAll(n => n.id == nodeId);
            links.RemoveAll(l => l.from == nodeId || l.to == nodeId);
        }

        public bool HasLink(string from, string to, string item) => links.Exists(l => l.from == from && l.to == to && l.item == item);

        /// <summary>Drops links whose pins no longer exist (after a recipe change).</summary>
        public void PruneLinks(GameDatabase db)
        {
            links.RemoveAll(l =>
            {
                GraphNode a = Node(l.from), b = Node(l.to);
                return a == null || b == null || !HasPort(db, a, l.item, true) || !HasPort(db, b, l.item, false);
            });
        }

        static bool HasPort(GameDatabase db, GraphNode n, string item, bool output) =>
            db.Ports(n).Exists(p => p.Item == item && p.IsOutput == output);
    }
}

namespace IndustryPlanner.Core
{
    /// <summary>A reusable group of nodes and the links between them. Positions are relative to the group's corner.</summary>
    [Serializable]
    public class Blueprint
    {
        public string id = Guid.NewGuid().ToString("N");
        public string gameId, name;
        public long createdTicks;
        public List<GraphNode> nodes = new();
        public List<GraphLink> links = new();

        /// <summary>Copies the given nodes, and links whose both ends are in the set, out of a graph.</summary>
        public static Blueprint FromSelection(ProductionGraph graph, ICollection<string> selection, string name)
        {
            var ids = new HashSet<string>(selection); // Snapshot: the live selection may change while a dialog is open.
            var bp = new Blueprint { gameId = graph.gameId, name = name, createdTicks = DateTime.Now.Ticks };
            Vector2 min = new(float.MaxValue, float.MaxValue);
            foreach (GraphNode n in graph.nodes) if (ids.Contains(n.id)) min = Vector2.Min(min, n.position);
            foreach (GraphNode n in graph.nodes)
            {
                if (!ids.Contains(n.id)) continue;
                GraphNode copy = n.Clone();
                copy.position -= min;
                bp.nodes.Add(copy);
            }
            bp.links.AddRange(graph.links.FindAll(l => ids.Contains(l.from) && ids.Contains(l.to))
                .ConvertAll(l => new GraphLink(l.from, l.to, l.item)));
            return bp;
        }

        /// <summary>Adds fresh copies to the graph at the given corner; returns the new node ids.</summary>
        public List<string> PlaceInto(ProductionGraph graph, Vector2 corner)
        {
            var map = new Dictionary<string, string>();
            foreach (GraphNode n in nodes)
            {
                GraphNode copy = n.Clone();
                copy.id = Guid.NewGuid().ToString("N").Substring(0, 8);
                copy.position = corner + n.position;
                graph.nodes.Add(copy);
                map[n.id] = copy.id;
            }
            foreach (GraphLink l in links)
                if (map.TryGetValue(l.from, out string a) && map.TryGetValue(l.to, out string b))
                    graph.links.Add(new GraphLink(a, b, l.item));
            return new List<string>(map.Values);
        }
    }
}
