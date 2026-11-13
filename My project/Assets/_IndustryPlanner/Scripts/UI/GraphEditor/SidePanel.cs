using System;
using System.Collections.Generic;
using System.Linq;
using IndustryPlanner.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI.GraphEditor
{
    /// <summary>
    /// Right panel with two tabs. Selection edits the selected node (recipe, machines, clock, purity);
    /// Analysis shows power, starved machines, raw inputs, outputs and the balance of every item.
    /// </summary>
    public class SidePanel
    {
        public event Action Changed;                         // A field was edited: re-solve.
        public event Action<string> ChooseSelectionRequested; // Open the picker for this node.
        public event Action<string> FocusNodeRequested;
        public event Action DuplicateRequested, DeleteRequested, SaveBlueprintRequested;
        public event Action<Blueprint> PlaceBlueprintRequested;
        public event Action<string> OpenProductionRequested; // Production id of a production-line node.
        public event Action<GraphLink> DeleteLinkRequested;

        public enum Tab { Selection, Analysis, Blueprints }

        readonly VisualElement m_Root;
        readonly ScrollView m_Body;
        readonly Button m_TabSelection, m_TabAnalysis, m_TabBlueprints;
        GameDatabase m_Db;
        GraphCanvas m_Canvas;
        Tab m_Tab;

        public bool Visible => !m_Root.ClassListContains("side-panel--collapsed");

        public SidePanel(VisualElement root)
        {
            m_Root = root;
            m_Body = root.Q<ScrollView>("panelBody");
            m_TabSelection = root.Q<Button>("tabSelection");
            m_TabAnalysis = root.Q<Button>("tabAnalysis");
            m_TabBlueprints = root.Q<Button>("tabBlueprints");
            m_TabSelection.clicked += () => ShowTab(Tab.Selection);
            m_TabAnalysis.clicked += () => ShowTab(Tab.Analysis);
            m_TabBlueprints.clicked += () => ShowTab(Tab.Blueprints);
        }

        public void Bind(GameDatabase db, GraphCanvas canvas)
        {
            m_Db = db;
            m_Canvas = canvas;
            Refresh();
        }

        /// <summary>Slides the panel open or closed (width transition in GraphEditor.uss).</summary>
        public void Toggle() => m_Root.EnableInClassList("side-panel--collapsed", Visible);

        public void ShowTab(Tab tab)
        {
            m_Tab = tab;
            m_Root.RemoveFromClassList("side-panel--collapsed");
            Refresh();
        }

        public void Refresh()
        {
            m_TabSelection.EnableInClassList("tab--active", m_Tab == Tab.Selection);
            m_TabAnalysis.EnableInClassList("tab--active", m_Tab == Tab.Analysis);
            m_TabBlueprints.EnableInClassList("tab--active", m_Tab == Tab.Blueprints);
            m_Body.Clear();
            if (m_Db == null || m_Canvas?.Analysis == null) return;
            switch (m_Tab)
            {
                case Tab.Analysis: BuildAnalysis(); break;
                case Tab.Blueprints: BuildBlueprints(); break;
                default: BuildSelection(); break;
            }
        }

        // ---------------------------------------------------------------- Selection tab

        void BuildSelection()
        {
            if (m_Canvas.SelectedLink is { } link) { BuildLink(link); return; }
            var ids = m_Canvas.Selection.ToList();
            if (ids.Count == 0)
            {
                Section(null).Add(UiKit.Text(
                    "Select a node to edit it. Place buildings with the hotbar (keys 1–9), drag between pins to connect them, " +
                    "and use the panel button to show or hide this panel. Shortcuts can be changed in Settings.", "panel-hint"));
                return;
            }
            if (ids.Count > 1)
            {
                VisualElement s = Section(null);
                UiKit.Text($"{ids.Count} nodes selected", "panel-title t-bold", s);
                Actions();
                return;
            }

            GraphNode node = m_Canvas.Graph.Node(ids[0]);
            BuildingDef b = m_Db.Building(node.building);
            NodeResult result = m_Canvas.Analysis.Nodes.GetValueOrDefault(node.id);

            VisualElement head = Section(null);
            UiKit.Text(m_Db.NodeTitle(node), "panel-title t-bold", head);
            UiKit.Text(b?.name ?? node.building, "panel-subtitle", head);

            VisualElement setup = Section("Setup");
            string pickLabel = b?.Kind switch
            {
                BuildingKind.Producer => "Recipe", BuildingKind.Extractor => "Resource", BuildingKind.Generator => "Fuel",
                BuildingKind.Production => "Production", _ => "Item"
            };
            Row(setup, pickLabel).Add(UiKit.Btn(m_Db.NodeTitle(node), "btn btn--secondary field-row__button",
                () => ChooseSelectionRequested?.Invoke(node.id)));

            if (b?.Kind is BuildingKind.Producer or BuildingKind.Extractor or BuildingKind.Generator)
            {
                NumberField(setup, "Machines", node.count, v => node.count = Mathf.Max(0.01f, v));
                ClockFields(setup, node);
                if (b.amplifierSlots > 0 && !string.IsNullOrEmpty(m_Db.Def.amplifierName)) AmplifierFields(setup, node, b);
            }
            if (b?.Kind == BuildingKind.Extractor)
            {
                VisualElement seg = UiKit.El("segmented", Row(setup, "Node"));
                for (int i = 0; i < 3; i++)
                {
                    int purity = i;
                    UiKit.Btn(GameDatabase.PurityNames[i], "btn segment t-semibold", () => { node.purity = purity; Changed?.Invoke(); }, seg)
                        .EnableInClassList("segment--active", node.purity == i);
                }
            }
            if (b?.Kind == BuildingKind.Input) NumberField(setup, "Rate /min", node.rate, v => node.rate = Mathf.Max(0f, v));
            if (b?.Kind == BuildingKind.Production)
            {
                NumberField(setup, "Copies", node.count, v => node.count = Mathf.Max(0.01f, v));
                UiKit.Btn("Open this production", "btn btn--secondary panel-wide t-semibold",
                    () => OpenProductionRequested?.Invoke(node.selection), setup);
            }

            if (result != null && b?.Kind is BuildingKind.Producer or BuildingKind.Generator or BuildingKind.Production)
                SupplySection(node, result, b);

            if (result != null)
            {
                VisualElement flow = Section("Flow per minute");
                foreach (var pair in result.Nominal)
                {
                    float actual = result.Actual.GetValueOrDefault(pair.Key);
                    bool shortfall = actual < pair.Value - 0.001f;
                    DataRow(flow, pair.Key, shortfall ? $"{UiKit.Num(actual)} / {UiKit.Num(pair.Value)}" : UiKit.Num(actual),
                        shortfall ? "data-row__value--neg" : null);
                }
                string power = result.Power < 0 ? $"Produces {UiKit.Power(-result.Power * result.Efficiency)}" : $"Uses {UiKit.Power(result.Power)}";
                UiKit.Text(result.Underfed ? $"{power}. Running at {Mathf.RoundToInt(result.Efficiency * 100)}% because an input is short." : power + ".",
                    "panel-hint", flow);
            }
            Actions();
        }

        /// <summary>A selected link: what it carries, its belt or pipe tier, parallel lines and load.</summary>
        void BuildLink(GraphLink link)
        {
            GraphAnalysis a = m_Canvas.Analysis;
            ProductionGraph g = m_Canvas.Graph;
            VisualElement head = Section(null);
            UiKit.Text($"{m_Db.ItemName(link.item)} link", "panel-title t-bold", head);
            UiKit.Text($"{m_Db.NodeTitle(g.Node(link.from))} to {m_Db.NodeTitle(g.Node(link.to))}", "panel-subtitle", head);

            TransportDef current = m_Db.Transport(link, g);
            float flow = a.LinkFlow.GetValueOrDefault(link);
            float capacity = a.LinkCapacity.GetValueOrDefault(link, float.MaxValue);
            VisualElement setup = Section("Transport");
            if (current != null)
            {
                VisualElement tiers = UiKit.El("segmented segmented--wrap", Row(setup, m_Db.Item(link.item) is { fluid: true } ? "Pipe" : "Belt"));
                foreach (TransportDef t in m_Db.TransportsFor(link, g))
                {
                    TransportDef tier = t;
                    UiKit.Btn(t.name.Split(' ').Last(), "btn segment t-semibold", () => { link.transport = tier.id; Changed?.Invoke(); }, tiers)
                        .EnableInClassList("segment--active", t == current);
                }
                NumberField(setup, "Parallel lines", link.lanes, v => link.lanes = Mathf.Max(1, Mathf.RoundToInt(v)));
            }

            VisualElement load = Section("Load per minute");
            DataRow(load, link.item, capacity < float.MaxValue ? $"{UiKit.Num(flow)} / {UiKit.Num(capacity)}" : UiKit.Num(flow),
                a.Overloaded.Contains(link) ? "data-row__value--neg" : null);
            if (current != null)
            {
                // What the receiver would take if belts were no limit, to say how many lines that needs.
                float wanted = Mathf.Max(flow, a.Nodes.GetValueOrDefault(link.to)?.Nominal.GetValueOrDefault(link.item) ?? 0f);
                int lines = Mathf.Max(1, Mathf.CeilToInt(wanted / current.rate - 0.0001f));
                string text = a.Overloaded.Contains(link)
                    ? $"Overloaded: this needs {lines} × {current.name} (or a faster tier)."
                    : $"{Mathf.RoundToInt(flow / capacity * 100)}% of capacity.";
                UiKit.Text(text, "panel-hint", load);
                if (a.Overloaded.Contains(link) && lines != link.lanes)
                    UiKit.Btn($"Use {lines} parallel lines", "btn btn--secondary panel-wide t-semibold", () => { link.lanes = lines; Changed?.Invoke(); }, load);
            }
            VisualElement actions = UiKit.El("panel-actions", Section(null));
            UiKit.Btn("Delete link", "btn btn--danger t-semibold", () => DeleteLinkRequested?.Invoke(link), actions);
        }

        /// <summary>
        /// Machines needed for the input rate: for each linked input, how many machines (at the current clock)
        /// the incoming supply can keep busy. The smallest is what the supply actually sustains.
        /// </summary>
        void SupplySection(GraphNode node, NodeResult result, BuildingDef b)
        {
            if (result.Received.Count == 0) return;
            string unit = b.Kind == BuildingKind.Production ? "copies" : "machines";
            VisualElement s = Section($"Machines needed for the input rate");

            float sustained = float.MaxValue;
            foreach (var pair in result.Received)
            {
                float needed = result.Nominal.GetValueOrDefault(pair.Key);
                if (needed <= 0) continue;
                float machines = node.count * pair.Value / needed;
                sustained = Mathf.Min(sustained, machines);
                DataRow(s, pair.Key, $"{UiKit.Rate(pair.Value)} feeds {UiKit.Num(machines)}", null);
            }
            if (sustained == float.MaxValue) return;

            string clock = Mathf.Approximately(node.clock, 1f) ? "" : $" at {UiKit.Num(node.clock * 100)}%";
            UiKit.Text($"The supply runs {UiKit.Num(sustained)} {unit}{clock}; you have {UiKit.Num(node.count)}.", "panel-hint", s);
            if (Mathf.Abs(sustained - node.count) < 0.005f) return;

            VisualElement row = UiKit.El("panel-actions", s);
            float exact = Mathf.Round(sustained * 100f) / 100f, whole = Mathf.Max(1f, Mathf.Floor(sustained + 0.001f));
            UiKit.Btn($"Set to {UiKit.Num(exact)}", "btn btn--secondary t-semibold", () => { node.count = Mathf.Max(0.01f, exact); Changed?.Invoke(); }, row);
            if (!Mathf.Approximately(whole, exact))
                UiKit.Btn($"Set to {UiKit.Num(whole)}", "btn btn--secondary t-semibold", () => { node.count = whole; Changed?.Invoke(); }, row);
        }

        void Actions()
        {
            VisualElement row = UiKit.El("panel-actions", Section(null));
            UiKit.Btn("Duplicate", "btn btn--secondary t-semibold", () => DuplicateRequested?.Invoke(), row);
            UiKit.Btn("Save as blueprint", "btn btn--secondary t-semibold", () => SaveBlueprintRequested?.Invoke(), row);
            UiKit.Btn("Delete", "btn btn--danger t-semibold", () => DeleteRequested?.Invoke(), row);
        }

        /// <summary>Clock speed field with quick presets, and how many boost items (power shards) it needs.</summary>
        void ClockFields(VisualElement parent, GraphNode node)
        {
            float max = m_Db.Def.maxClock > 0 ? m_Db.Def.maxClock : 2.5f;
            NumberField(parent, "Clock %", node.clock * 100f, v => node.clock = Mathf.Clamp(v, 1f, max * 100f) / 100f);

            VisualElement presets = UiKit.El("segmented clock-presets", parent); // Full width so 250% fits.
            for (float c = 0.5f; c <= max + 0.001f; c += 0.5f)
            {
                float clock = c;
                UiKit.Btn($"{clock * 100:0}%", "btn segment t-semibold", () => { node.clock = clock; Changed?.Invoke(); }, presets)
                    .EnableInClassList("segment--active", Mathf.Approximately(node.clock, clock));
            }
            int boosts = m_Db.BoostsNeeded(node.clock);
            if (boosts > 0) UiKit.Text($"Needs {boosts} {m_Db.Def.boostName.ToLowerInvariant()} per machine.", "panel-hint field-row__hint", parent);
        }

        /// <summary>Production amplifier slots (Somersloops): each filled slot adds output and squares power with it.</summary>
        void AmplifierFields(VisualElement parent, GraphNode node, BuildingDef b)
        {
            VisualElement seg = UiKit.El("segmented", Row(parent, m_Db.Def.amplifierName));
            for (int i = 0; i <= b.amplifierSlots; i++)
            {
                int filled = i;
                UiKit.Btn(i.ToString(), "btn segment t-semibold", () => { node.amplifiers = filled; Changed?.Invoke(); }, seg)
                    .EnableInClassList("segment--active", node.amplifiers == i);
            }
            if (node.amplifiers <= 0) return;
            float amp = m_Db.Amplification(node);
            UiKit.Text($"Output ×{UiKit.Num(amp)}, power ×{UiKit.Num(amp * amp)}.", "panel-hint field-row__hint", parent);
        }

        // ---------------------------------------------------------------- Blueprints tab

        void BuildBlueprints()
        {
            VisualElement head = Section(null);
            UiKit.Text("Blueprints are saved groups of nodes you can place in any production of this game.", "panel-hint", head);
            Button save = UiKit.Btn($"Save selection as blueprint", "btn btn--primary panel-wide t-semibold",
                () => SaveBlueprintRequested?.Invoke(), head);
            save.SetEnabled(m_Canvas.Selection.Count > 0);

            var blueprints = ProductionStore.LoadBlueprints(m_Db.Id);
            VisualElement list = Section(blueprints.Count > 0 ? "Saved blueprints" : null);
            if (blueprints.Count == 0) UiKit.Text("None yet. Select some nodes, then save them here.", "panel-hint", list);

            foreach (Blueprint bp in blueprints)
            {
                VisualElement row = UiKit.El("blueprint-row", list);
                VisualElement texts = UiKit.El("blueprint-row__texts", row);
                UiKit.Text(bp.name, "blueprint-row__name t-semibold", texts);
                UiKit.Text(BlueprintSummary(bp), "blueprint-row__meta", texts);
                UiKit.Btn("Place", "btn btn--secondary t-semibold", () => PlaceBlueprintRequested?.Invoke(bp), row);
                Button more = UiKit.Btn("", "btn btn--icon blueprint-row__more", null, row);
                UiKit.El("icon icon--settings", more);
                more.clicked += () => Popup.Menu(more, new (string, Action, bool)[]
                {
                    ("Delete blueprint", () => Popup.Confirm(more, $"Delete \"{bp.name}\"?", "Delete",
                        () => { ProductionStore.DeleteBlueprint(bp.id); Refresh(); }), true),
                });
            }
        }

        /// <summary>"6 nodes, makes 5/min Reinforced Iron Plate".</summary>
        string BlueprintSummary(Blueprint bp)
        {
            var g = new ProductionGraph { gameId = bp.gameId, nodes = bp.nodes, links = bp.links };
            var outputs = GraphSolver.Solve(m_Db, g).FinalOutputs.OrderByDescending(p => p.Value).Take(2)
                .Select(p => $"{UiKit.Rate(p.Value)} {m_Db.ItemName(p.Key)}");
            string makes = string.Join(", ", outputs);
            return $"{bp.nodes.Count} nodes" + (makes.Length > 0 ? $", makes {makes}" : "");
        }

        void NumberField(VisualElement parent, string label, float value, Action<float> apply)
        {
            var field = new FloatField { isDelayed = true, value = (float)Math.Round(value, 4) };
            field.AddToClassList("field-row__field");
            field.RegisterValueChangedCallback(e => { apply(e.newValue); Changed?.Invoke(); });
            Row(parent, label).Add(field);
        }

        // ---------------------------------------------------------------- Analysis tab

        void BuildAnalysis()
        {
            GraphAnalysis a = m_Canvas.Analysis;
            ProductionGraph g = m_Canvas.Graph;

            VisualElement stats = UiKit.El("stat-grid", Section(null));
            Stat(stats, "Power used", UiKit.Power(a.PowerUsed), false);
            Stat(stats, "Power produced", UiKit.Power(a.PowerProduced), false);
            Stat(stats, "Machines", UiKit.Num(g.nodes.Where(n => m_Db.Building(n.building)?.Kind is BuildingKind.Producer
                or BuildingKind.Extractor or BuildingKind.Generator).Sum(n => n.count)), false);
            Stat(stats, "Underfed nodes", a.UnderfedCount.ToString(), a.UnderfedCount > 0);
            if (m_Db.Def.transport is { Length: > 0 }) Stat(stats, "Overloaded links", a.OverloadedCount.ToString(), a.OverloadedCount > 0);

            if (a.UnderfedCount > 0)
            {
                VisualElement s = Section("Underfed nodes");
                foreach (GraphNode n in g.nodes.Where(n => a.Nodes[n.id].Underfed))
                {
                    VisualElement row = UiKit.El("data-row data-row--clickable", s);
                    row.Add(UiKit.Icon(m_Db.Name, true, n.building, m_Db.Building(n.building)?.name));
                    UiKit.Text(m_Db.NodeTitle(n), "data-row__name", row);
                    UiKit.Text($"{Mathf.RoundToInt(a.Nodes[n.id].Efficiency * 100)}%", "data-row__value data-row__value--neg", row);
                    string id = n.id;
                    row.RegisterCallback<ClickEvent>(_ => FocusNodeRequested?.Invoke(id));
                }
            }

            RateList("Raw inputs", a.RawInputs);
            RateList("Outputs", a.FinalOutputs);

            VisualElement balance = Section("Item balance per minute");
            VisualElement header = UiKit.El("data-row data-row--header", balance);
            UiKit.Text("Item", "data-row__name", header);
            foreach (string col in new[] { "Made", "Needed", "Net" }) UiKit.Text(col, "data-row__value", header);
            foreach (ItemBalance b in a.Items.Values.OrderBy(b => b.Produced - b.Needed))
            {
                VisualElement row = UiKit.El("data-row", balance);
                row.Add(UiKit.Icon(m_Db.Name, false, b.Item, m_Db.ItemName(b.Item)));
                UiKit.Text(m_Db.ItemName(b.Item), "data-row__name", row);
                UiKit.Text(UiKit.Num(b.Produced), "data-row__value", row);
                UiKit.Text(UiKit.Num(b.Needed), "data-row__value", row);
                float net = b.Produced - b.Needed;
                UiKit.Text((net > 0.001f ? "+" : "") + UiKit.Num(net), "data-row__value " +
                    (net < -0.001f ? "data-row__value--neg" : net > 0.001f ? "data-row__value--pos" : ""), row);
            }
            if (a.Items.Count == 0) UiKit.Text("Add buildings to see their balance here.", "panel-hint", balance);
        }

        void RateList(string title, Dictionary<string, float> rates)
        {
            if (rates.Count == 0) return;
            VisualElement s = Section(title);
            foreach (var pair in rates.OrderByDescending(p => p.Value)) DataRow(s, pair.Key, UiKit.Rate(pair.Value), null);
        }

        // ---------------------------------------------------------------- small builders

        VisualElement Section(string heading)
        {
            VisualElement s = UiKit.El("panel-section", m_Body);
            if (heading != null) UiKit.Text(heading, "panel-heading t-semibold", s);
            return s;
        }

        static VisualElement Row(VisualElement parent, string label)
        {
            VisualElement row = UiKit.El("field-row", parent);
            UiKit.Text(label, "field-row__label", row);
            return row;
        }

        void DataRow(VisualElement parent, string item, string value, string valueClass)
        {
            VisualElement row = UiKit.El("data-row", parent);
            row.Add(UiKit.Icon(m_Db.Name, false, item, m_Db.ItemName(item)));
            UiKit.Text(m_Db.ItemName(item), "data-row__name", row);
            // Auto width: values here can be wider, like "12 / 30".
            UiKit.Text(value, "data-row__value data-row__value--auto " + valueClass, row);
        }

        static void Stat(VisualElement grid, string label, string value, bool warn)
        {
            VisualElement s = UiKit.El("stat", grid);
            UiKit.Text(label, "stat__label", s);
            UiKit.Text(value, warn ? "stat__value stat__value--warn t-bold" : "stat__value t-bold", s);
        }
    }
}
