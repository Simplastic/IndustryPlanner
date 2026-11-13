using System.Collections.Generic;
using System.Linq;
using IndustryPlanner.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI.GraphEditor
{
    /// <summary>Identifies a pin: node, item and direction. Stored in each pin handle's userData.</summary>
    public readonly struct PortRef
    {
        public readonly string NodeId, Item;
        public readonly bool IsOutput;
        public PortRef(string nodeId, string item, bool isOutput) { NodeId = nodeId; Item = item; IsOutput = isOutput; }
    }

    /// <summary>
    /// One node on the canvas, drawn like the home cards: inputs on the left edge, outputs on the right.
    /// Rebuilt from the model and the latest analysis by <see cref="Refresh"/>.
    /// </summary>
    public class NodeView : VisualElement
    {
        public readonly GraphNode Node;
        readonly VisualElement m_IconHost, m_Inputs, m_Outputs;
        readonly Label m_Title, m_Subtitle, m_Badge, m_Power, m_Productivity;
        readonly VisualElement m_BarFill;
        readonly Dictionary<(string, bool), VisualElement> m_Dots = new();

        public NodeView(GraphNode node)
        {
            Node = node;
            AddToClassList("node");
            VisualElement header = UiKit.El("node__header", this);
            m_IconHost = UiKit.El("", header);
            VisualElement titles = UiKit.El("node__titles", header);
            m_Title = UiKit.Text("", "node__title t-bold", titles);
            m_Subtitle = UiKit.Text("", "node__subtitle", titles);
            m_Badge = UiKit.Text("", "node__badge t-bold", header);

            VisualElement ports = UiKit.El("node__ports", this);
            m_Inputs = UiKit.El("nport-column", ports);
            m_Outputs = UiKit.El("nport-column nport-column--out", ports);

            // Footer: power on the left; productivity and a thin bar show when the node is hovered,
            // selected, or running below 100%.
            VisualElement footer = UiKit.El("node__footer", this);
            m_Power = UiKit.Text("", "node__power", footer);
            m_Productivity = UiKit.Text("", "node__prod t-semibold", footer);
            m_BarFill = UiKit.El("node__bar-fill", UiKit.El("node__bar", footer));
            SyncPosition();
        }

        // Inline left/top: the node's position is data that changes every drag frame.
        public void SyncPosition()
        {
            style.left = Node.position.x;
            style.top = Node.position.y;
        }

        public void SetSelected(bool selected) => EnableInClassList("node--selected", selected);

        public void Refresh(GameDatabase db, ProductionGraph graph, NodeResult result)
        {
            BuildingDef b = db.Building(Node.building);
            string gameFolder = db.Name;

            m_IconHost.Clear();
            m_IconHost.Add(UiKit.Icon(gameFolder, true, Node.building, b?.name, "m"));
            m_Title.text = db.NodeTitle(Node);
            bool locked = !db.IsUnlocked(Node, graph);
            m_Subtitle.text = locked ? $"Not unlocked at {db.LevelName(graph.tier)}" : Subtitle(b, graph);
            EnableInClassList("node--locked", locked);
            m_Badge.text = b != null && b.Kind is BuildingKind.Producer or BuildingKind.Extractor or BuildingKind.Generator or BuildingKind.Production
                ? "×" + UiKit.Num(Node.count) : "";

            m_Inputs.Clear();
            m_Outputs.Clear();
            m_Dots.Clear();
            foreach (PortSpec p in db.Ports(Node))
            {
                bool linked = graph.links.Exists(l => l.item == p.Item && (p.IsOutput ? l.from == Node.id : l.to == Node.id));
                float actual = result != null && result.Actual.TryGetValue(p.Item, out float a) ? a : p.PerMin;
                AddPortRow(db, p, linked, actual, gameFolder);
            }

            float power = result?.Power ?? db.Power(Node);
            m_Power.text = power < 0 ? "+" + UiKit.Power(-power * (result?.Efficiency ?? 1f)) : UiKit.Power(power);
            bool machine = b != null && b.Kind is BuildingKind.Producer or BuildingKind.Extractor or BuildingKind.Generator or BuildingKind.Production;
            EnableInClassList("node--production", b?.Kind == BuildingKind.Production);
            float efficiency = result?.Efficiency ?? 1f;
            m_Productivity.text = machine ? $"Productivity {Mathf.RoundToInt(efficiency * 100)}%" : "";
            m_BarFill.style.width = Length.Percent(efficiency * 100f); // Data-driven width.
            EnableInClassList("node--logistics", !machine);
            EnableInClassList("node--underfed", result is { Underfed: true });
        }

        int Links(ProductionGraph g, bool outputs) => g.links.Count(l => outputs ? l.from == Node.id : l.to == Node.id);

        string Subtitle(BuildingDef b, ProductionGraph graph)
        {
            if (b == null) return "Unknown building";
            string clock = Mathf.Approximately(Node.clock, 1f) ? "" : $" at {UiKit.Num(Node.clock * 100)}%";
            return b.Kind switch
            {
                BuildingKind.Extractor => $"{b.name}, {GameDatabase.PurityNames[Mathf.Clamp(Node.purity, 0, 2)].ToLowerInvariant()} node{clock}",
                BuildingKind.Input => $"Input at {UiKit.Rate(Node.rate)}",
                BuildingKind.Output or BuildingKind.Bus => b.name,
                BuildingKind.Splitter => $"Splitter, {Links(graph, true)} of {b.maxOut} outputs, shared evenly",
                BuildingKind.Merger => $"Merger, {Links(graph, false)} of {b.maxIn} inputs",
                BuildingKind.Production => Node.count == 1 ? "Production line" : $"{UiKit.Num(Node.count)} copies of a production line",
                _ => b.name + clock + (Node.amplifiers > 0 ? $", {Node.amplifiers} amplified" : "")
            };
        }

        void AddPortRow(GameDatabase db, PortSpec p, bool linked, float actual, string gameFolder)
        {
            ItemDef item = db.Item(p.Item);
            VisualElement row = UiKit.El(p.IsOutput ? "nport nport--out" : "nport nport--in", p.IsOutput ? m_Outputs : m_Inputs);

            VisualElement dot = UiKit.El("nport__dot", row);
            dot.userData = new PortRef(Node.id, p.Item, p.IsOutput);
            dot.EnableInClassList("nport__dot--linked", linked);
            dot.EnableInClassList("nport__dot--fluid", item is { fluid: true });
            m_Dots[(p.Item, p.IsOutput)] = dot;

            row.Add(UiKit.Icon(gameFolder, false, p.Item, item?.name, "s"));
            UiKit.Text(db.ItemName(p.Item), p.Item == GameDatabase.AnyItem ? "nport__item nport__item--any" : "nport__item", row);

            // Starved inputs show "received / needed"; everything else shows the actual rate.
            bool shortInput = !p.IsOutput && !p.Unbounded && actual < p.PerMin - 0.001f;
            string text = shortInput ? $"{UiKit.Num(actual)} / {UiKit.Rate(p.PerMin)}" : UiKit.Rate(actual);
            UiKit.Text(text, shortInput ? "nport__rate nport__rate--short t-semibold" : "nport__rate t-semibold", row);
        }

        /// <summary>While a link is dragged, marks the pins it could connect to (null clears).</summary>
        public void MarkCandidates(PortRef? from)
        {
            foreach (VisualElement dot in m_Dots.Values)
            {
                var pin = (PortRef)dot.userData;
                bool candidate = from is PortRef f && pin.IsOutput != f.IsOutput && pin.NodeId != f.NodeId
                    && (pin.Item == f.Item || pin.Item == GameDatabase.AnyItem || f.Item == GameDatabase.AnyItem);
                bool source = from is PortRef s && pin.NodeId == s.NodeId && pin.Item == s.Item && pin.IsOutput == s.IsOutput;
                dot.EnableInClassList("nport__dot--candidate", candidate);
                dot.EnableInClassList("nport__dot--source", source);
            }
        }

        /// <summary>Centre of a pin handle in the given element's local space, or null if it doesn't exist.</summary>
        public Vector2? PortAnchor(string item, bool output, VisualElement space)
        {
            if (!m_Dots.TryGetValue((item, output), out VisualElement dot) || float.IsNaN(dot.worldBound.width)) return null;
            return space.WorldToLocal(dot.worldBound.center);
        }
    }
}
