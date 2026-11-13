using System.Collections.Generic;
using System.Linq;
using IndustryPlanner.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI.GraphEditor
{
    /// <summary>
    /// The production editor: wires the canvas, hotbar, picker and side panel to the graph model,
    /// re-solves the graph after every change and handles keyboard shortcuts.
    /// </summary>
    public class GraphEditorView
    {
        public readonly VisualElement Root;
        readonly AppController m_App;
        readonly GraphCanvas m_Canvas = new();
        readonly Hotbar m_Hotbar = new();
        readonly Picker m_Picker = new();
        readonly SidePanel m_Panel;
        readonly TextField m_Name;
        readonly Label m_GameLabel, m_SaveState;
        readonly Button m_SnapButton, m_TierButton;
        GameDatabase m_Db;
        Keymap m_Keys;
        Blueprint m_PlacingBlueprint; // Set while a blueprint follows the cursor waiting to be placed.
        static Blueprint s_Clipboard; // Ctrl+C / Ctrl+V; shared between productions of the same game.
        ProductionGraph m_Graph;
        bool m_Dirty, m_TabBlocked;

        public GraphEditorView(AppController app)
        {
            m_App = app;
            Root = UiKit.View("GraphEditorView");
            VisualElement host = Root.Q("canvasHost");
            host.Add(m_Canvas);
            host.Add(m_Hotbar);
            Root.Add(m_Picker);
            m_Panel = new SidePanel(Root.Q("sidePanel"));

            m_Name = Root.Q<TextField>("nameField");
            m_Name.isDelayed = true;
            m_Name.RegisterValueChangedCallback(e => { m_Graph.name = string.IsNullOrWhiteSpace(e.newValue) ? "Untitled production" : e.newValue; MarkDirty(); });
            m_GameLabel = Root.Q<Label>("gameLabel");
            m_SaveState = Root.Q<Label>("saveState");
            Root.Q<Button>("backButton").clicked += Back;
            Root.Q<Button>("saveButton").clicked += Save;
            Root.Q<Button>("panelButton").clicked += m_Panel.Toggle;
            m_SnapButton = Root.Q<Button>("snapButton");
            m_SnapButton.clicked += ToggleSnap;
            m_TierButton = Root.Q<Button>("tierButton");
            m_TierButton.clicked += TierMenu;
            Root.Q<Button>("settingsButton").clicked += () => { Save(); m_App.ShowSettings(); };
            AppSettings.Changed += ApplySettings;

            m_Hotbar.ToolChanged += tool =>
            {
                if (tool != null) m_PlacingBlueprint = null;
                m_Canvas.ToolActive = tool != null || m_PlacingBlueprint != null;
                m_Canvas.SetGhost(tool?.name ?? (m_PlacingBlueprint != null ? "Blueprint: " + m_PlacingBlueprint.name : null));
            };
            m_Hotbar.LockedPicked += m_App.ShowToast;
            m_Canvas.PlaceRequested += Place;
            m_Canvas.ToolCancelRequested += CancelTool;
            m_Canvas.AlignHeld = () => m_Keys != null && m_Keys.Held(Keyboard.current, "editor.align");
            m_Canvas.LinkContextRequested += LinkMenu;
            m_Canvas.LinkSelectionChanged += m_Panel.Refresh;
            m_Canvas.LinkRequested += (from, to) =>
            {
                // An "any item" pin (empty splitter/merger/bus) takes the item from the other side.
                string item = from.Item != GameDatabase.AnyItem ? from.Item : to.Item;
                if (item == GameDatabase.AnyItem) { m_App.ShowToast("Connect an item to one of these first."); return; }
                if (m_Graph.HasLink(from.NodeId, to.NodeId, item)) return;
                // Splitters and mergers have a fixed number of sides, like in game.
                if (LinkLimitReached(from.NodeId, true) || LinkLimitReached(to.NodeId, false)) return;
                AdoptItem(from.NodeId, item);
                AdoptItem(to.NodeId, item);
                m_Graph.links.Add(new GraphLink(from.NodeId, to.NodeId, item));
                Changed();
            };
            m_Canvas.LinkDroppedOnEmpty += AddConnectedNode;
            m_Canvas.PortCleared += p =>
            {
                m_Graph.links.RemoveAll(l => l.item == p.Item && (p.IsOutput ? l.from == p.NodeId : l.to == p.NodeId));
                Changed();
            };
            m_Canvas.NodeDoubleClicked += id =>
            {
                GraphNode n = m_Graph.Node(id);
                if (m_Db.Building(n?.building)?.Kind == BuildingKind.Production) OpenProduction(n.selection);
                else ChooseSelection(id);
            };
            m_Canvas.BackgroundDoubleClicked += pos =>
                Pick("Add a building", Picker.All(m_Db, m_Graph), e => AddNode(e, pos));
            m_Canvas.NodeContextRequested += NodeMenu;
            m_Canvas.SelectionChanged += m_Panel.Refresh;
            m_Canvas.NodesMoved += MarkDirty;

            m_Panel.Changed += () => Changed();
            m_Panel.ChooseSelectionRequested += ChooseSelection;
            m_Panel.FocusNodeRequested += id => { m_Canvas.SelectOnly(id); m_Canvas.Frame(new[] { id }); };
            m_Panel.DuplicateRequested += DuplicateSelection;
            m_Panel.DeleteRequested += DeleteSelection;
            m_Panel.SaveBlueprintRequested += SaveBlueprint;
            m_Panel.OpenProductionRequested += OpenProduction;
            m_Panel.DeleteLinkRequested += DeleteLink;
            m_Panel.PlaceBlueprintRequested += bp =>
            {
                m_Hotbar.Deselect();
                m_PlacingBlueprint = bp;
                m_Canvas.ToolActive = true;
                m_Canvas.SetGhost("Blueprint: " + bp.name);
                m_App.ShowToast($"Click the canvas to place \"{bp.name}\". Esc to stop.");
            };

            // Tab toggles the panel, so stop UI Toolkit from using it to move focus.
            Root.RegisterCallback<AttachToPanelEvent>(e =>
            {
                if (m_TabBlocked) return;
                m_TabBlocked = true;
                e.destinationPanel.visualTree.RegisterCallback<NavigationMoveEvent>(nav =>
                {
                    if (Root.panel == null || nav.direction is not (NavigationMoveEvent.Direction.Next or NavigationMoveEvent.Direction.Previous)) return;
                    Root.focusController.IgnoreEvent(nav);
                    nav.StopPropagation();
                }, TrickleDown.TrickleDown);
            });
        }

        public void Open(GameDatabase db, ProductionGraph graph)
        {
            m_Db = db;
            m_Graph = graph;
            m_Name.SetValueWithoutNotify(graph.name);
            m_GameLabel.text = db.Name;
            m_Picker.Close();
            m_Canvas.Bind(db, graph);
            ApplySettings();
            m_Panel.Bind(db, m_Canvas);
            Changed(markDirty: false);
            m_Dirty = false;
            m_SaveState.text = "Saved";

            // A graph that was never viewed gets framed once its nodes have a size.
            if (graph.viewZoom <= 0 || (graph.viewOffset == Vector2.zero && Mathf.Approximately(graph.viewZoom, 1f)))
                m_Canvas.schedule.Execute(() => m_Canvas.Frame()).StartingIn(50);
        }

        /// <summary>Called every frame by AppController while the editor is visible.</summary>
        public void Tick()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || m_Graph == null) return;

            if (m_Keys.Pressed(kb, "editor.cancel") || (m_Picker.IsOpen && kb.escapeKey.wasPressedThisFrame))
            {
                if (m_Picker.IsOpen) m_Picker.Close();
                else if (m_Canvas.ToolActive) CancelTool();
                else m_Canvas.SelectOnly(null);
                return;
            }
            if (m_Picker.IsOpen || UiKit.IsTyping(Root.panel)) return;

            // Bindings are per game and editable in Settings (see Keymap).
            if (m_Keys.Pressed(kb, "editor.save")) Save();
            else if (m_Keys.Pressed(kb, "editor.duplicate")) DuplicateSelection();
            else if (m_Keys.Pressed(kb, "editor.delete"))
            {
                if (m_Canvas.SelectedLink != null) DeleteLink(m_Canvas.SelectedLink); else DeleteSelection();
            }
            else if (m_Keys.Pressed(kb, "editor.saveBlueprint")) SaveBlueprint();
            else if (m_Keys.Pressed(kb, "editor.copy")) Copy();
            else if (m_Keys.Pressed(kb, "editor.paste")) Paste();
            else if (m_Keys.Pressed(kb, "editor.blueprints")) m_Panel.ShowTab(SidePanel.Tab.Blueprints);
            else if (m_Keys.Pressed(kb, "editor.frame")) m_Canvas.Frame(m_Canvas.Selection.Count > 0 ? m_Canvas.Selection : null);
            else if (m_Keys.Pressed(kb, "editor.panel")) m_Panel.Toggle();
            else if (m_Keys.Pressed(kb, "editor.analysis")) m_Panel.ShowTab(SidePanel.Tab.Analysis);
            else if (m_Keys.Pressed(kb, "editor.snap")) ToggleSnap();
            else
                for (int i = 0; i < m_Hotbar.SlotCount; i++)
                    if (m_Keys.Pressed(kb, $"hotbar.{i}")) { m_Hotbar.Activate(i); break; }
        }

        public void Dispose() => AppSettings.Changed -= ApplySettings;

        public void SaveIfDirty()
        {
            if (m_Dirty) Save();
        }

        /// <summary>Pulls snapping, link style, animation and key bindings from the saved settings.</summary>
        void ApplySettings()
        {
            if (m_Db == null) return;
            AppSettings s = AppSettings.Current;
            m_Canvas.SnapSize = s.snapToGrid ? s.gridSize : 0;
            m_Canvas.Links.GridSize = s.snapToGrid ? s.gridSize : 0;
            m_Canvas.Links.Style = s.linkStyle;
            m_Canvas.Links.Animate = s.animateLinks;
            m_SnapButton.EnableInClassList("btn--toggled", s.snapToGrid);
            m_Keys = s.KeymapFor(m_Db);
            m_Hotbar.Bind(m_Db, m_Keys, b => m_Graph == null || m_Db.IsUnlocked(b, m_Graph));
            m_TierButton.EnableInClassList("is-hidden", !m_Db.HasProgression);
            if (m_Graph != null && m_Db.HasProgression)
                m_TierButton.text = m_Db.LevelName(Mathf.Min(m_Graph.tier, m_Db.MaxLevel)) + (m_Graph.alternates ? ", alternates" : "");
            m_Canvas.Links.MarkDirtyRepaint();
        }

        /// <summary>Progression menu: pick the tier reached and whether alternate recipes are unlocked.</summary>
        void TierMenu()
        {
            var items = new List<(string, System.Action, bool)>();
            foreach (ProgressionLevel level in m_Db.Def.progression)
            {
                int value = level.value;
                string mark = value == m_Graph.tier ? "  (current)" : "";
                items.Add(($"{level.name}: {level.detail}{mark}", () => SetProgression(value, m_Graph.alternates), false));
            }
            items.Add((m_Graph.alternates ? "Hide alternate recipes" : "Unlock alternate recipes",
                () => SetProgression(m_Graph.tier, !m_Graph.alternates), false));
            Popup.Menu(m_TierButton, items.ToArray());
        }

        void SetProgression(int tier, bool alternates)
        {
            m_Graph.tier = tier;
            m_Graph.alternates = alternates;
            ApplySettings(); // Re-locks hotbar buildings and updates the button.
            Changed();
            int locked = m_Graph.nodes.Count(n => !m_Db.IsUnlocked(n, m_Graph));
            if (locked > 0) m_App.ShowToast($"{locked} node{(locked == 1 ? " uses" : "s use")} things not unlocked yet. They're dimmed.");
        }

        /// <summary>Copies the selection (and links inside it) as an unnamed blueprint.</summary>
        void Copy()
        {
            if (m_Canvas.Selection.Count == 0) return;
            s_Clipboard = Blueprint.FromSelection(m_Graph, m_Canvas.Selection, "Clipboard");
            m_App.ShowToast(m_Canvas.Selection.Count == 1 ? "Copied 1 node" : $"Copied {m_Canvas.Selection.Count} nodes");
        }

        /// <summary>Pastes the copied nodes with their corner at the cursor.</summary>
        void Paste()
        {
            if (s_Clipboard == null) return;
            if (s_Clipboard.gameId != m_Graph.gameId) { m_App.ShowToast("The copied nodes belong to another game."); return; }
            m_Canvas.Selection.Clear();
            m_Canvas.Selection.UnionWith(s_Clipboard.PlaceInto(m_Graph, m_Canvas.Snap(m_Canvas.PointerContent)));
            Changed();
        }

        /// <summary>Opens the production behind a production-line node, saving this one first.</summary>
        void OpenProduction(string productionId)
        {
            ProductionGraph target = ProductionStore.LoadGraph(productionId);
            if (target == null) { m_App.ShowToast("That production no longer exists."); return; }
            Save();
            m_App.OpenEditor(target);
        }

        void CancelTool()
        {
            m_PlacingBlueprint = null;
            m_Hotbar.Deselect(); // Also clears the canvas ghost.
        }

        bool LinkLimitReached(string nodeId, bool outputSide)
        {
            BuildingDef b = m_Db.Building(m_Graph.Node(nodeId)?.building);
            int max = outputSide ? b?.maxOut ?? 0 : b?.maxIn ?? 0;
            if (max <= 0 || m_Graph.links.Count(l => outputSide ? l.from == nodeId : l.to == nodeId) < max) return false;
            m_App.ShowToast($"A {b.name.ToLowerInvariant()} has {max} {(outputSide ? "output" : "input")}{(max == 1 ? "" : "s")}. Add another one to split further.");
            return true;
        }

        /// <summary>Right click on a link: pick its belt or pipe tier, or delete it.</summary>
        /// <summary>Gives an empty pass-through node the item of its first link.</summary>
        void AdoptItem(string nodeId, string item)
        {
            GraphNode n = m_Graph.Node(nodeId);
            if (n != null && string.IsNullOrEmpty(n.selection) && m_Db.Building(n.building) is { } b && GameDatabase.IsPassThrough(b.Kind))
                n.selection = item;
        }

        void LinkMenu(GraphLink link, Vector2 pos)
        {
            var items = new List<(string, System.Action, bool)>();
            TransportDef current = m_Db.Transport(link, m_Graph);
            foreach (TransportDef t in m_Db.TransportsFor(link, m_Graph))
            {
                TransportDef tier = t;
                items.Add(($"{t.name}, {UiKit.Rate(t.rate)}{(t == current ? "  (current)" : "")}", () => { link.transport = tier.id; Changed(); }, false));
            }
            items.Add(($"Delete {m_Db.ItemName(link.item)} link", () => DeleteLink(link), true));
            Popup.Menu(m_Canvas, items.ToArray(), at: pos);
        }

        void DeleteLink(GraphLink link)
        {
            m_Graph.links.Remove(link);
            m_Canvas.SelectLink(null);
            Changed();
        }

        void SaveBlueprint()
        {
            if (m_Canvas.Selection.Count == 0) { m_App.ShowToast("Select the nodes to save first."); return; }
            Dialog.Prompt(Root, "Save as blueprint", "Name", m_Db.NodeTitle(m_Graph.Node(m_Canvas.Selection.First())) + " module", "Save", name =>
            {
                Blueprint bp = Blueprint.FromSelection(m_Graph, m_Canvas.Selection, name);
                ProductionStore.SaveBlueprint(bp);
                m_Panel.ShowTab(SidePanel.Tab.Blueprints);
                m_App.ShowToast($"Saved blueprint \"{name}\": {bp.nodes.Count} nodes, {bp.links.Count} links.");
            });
        }

        void ToggleSnap()
        {
            AppSettings s = AppSettings.Current;
            s.snapToGrid = !s.snapToGrid;
            s.Apply();
            m_App.ShowToast(s.snapToGrid ? "Snap to grid on" : "Snap to grid off");
        }

        // ---------------------------------------------------------------- model changes

        /// <summary>Re-solves the graph and refreshes every view. Call after any model change.</summary>
        void Changed(bool markDirty = true)
        {
            m_Graph.PruneLinks(m_Db);
            // A splitter/merger/bus with nothing connected goes back to accepting any item.
            foreach (GraphNode n in m_Graph.nodes)
                if (m_Db.Building(n.building) is { } b && GameDatabase.IsPassThrough(b.Kind) && !string.IsNullOrEmpty(n.selection)
                    && !m_Graph.links.Exists(l => l.from == n.id || l.to == n.id))
                    n.selection = "";
            m_Canvas.Sync(GraphSolver.Solve(m_Db, m_Graph));
            m_Panel.Refresh();
            if (markDirty) MarkDirty();
        }

        void MarkDirty()
        {
            m_Dirty = true;
            m_SaveState.text = "Unsaved changes";
        }

        void Save()
        {
            m_Graph.viewOffset = m_Canvas.Offset;
            m_Graph.viewZoom = m_Canvas.Zoom;
            ProductionStore.Save(m_Graph);
            m_Dirty = false;
            m_SaveState.text = "Saved";
        }

        void Back()
        {
            Save(); // Also keeps the camera position for next time.
            m_Picker.Close();
            m_Hotbar.Deselect();
            m_App.ShowHome();
        }

        /// <summary>Hotbar placement: choose what the new building runs, then add it under the cursor.</summary>
        void Place(Vector2 contentPos)
        {
            if (m_PlacingBlueprint != null)
            {
                m_Canvas.Selection.Clear();
                int linksBefore = m_Graph.links.Count;
                m_Canvas.Selection.UnionWith(m_PlacingBlueprint.PlaceInto(m_Graph, m_Canvas.Snap(contentPos)));
                Changed();
                int linked = m_Graph.links.Count - linksBefore; // After Changed(), so it counts links that survived.
                m_App.ShowToast($"Placed \"{m_PlacingBlueprint.name}\": {m_PlacingBlueprint.nodes.Count} nodes, {linked} of {m_PlacingBlueprint.links.Count} links.");
                return;
            }
            BuildingDef tool = m_Hotbar.ActiveTool;
            if (tool == null) return;
            if (GameDatabase.IsPassThrough(tool.Kind))
            {
                // No picker: splitters, mergers and buses take whatever gets connected.
                AddNode(new PickEntry { Building = tool.id, Selection = "" }, contentPos);
                return;
            }
            Pick($"{tool.name}: choose {PickNoun(tool)}", Picker.ForBuilding(m_Db, tool, m_Graph), e => AddNode(e, contentPos));
        }

        /// <summary>A pin dropped on empty space: offer every building that can use (or supply) that item.</summary>
        void AddConnectedNode(PortRef port, Vector2 contentPos)
        {
            if (port.Item == GameDatabase.AnyItem) { m_App.ShowToast("Connect an item to this first."); return; }
            string verb = port.IsOutput ? "Use" : "Supply";
            Pick($"{verb} {m_Db.ItemName(port.Item)}", Picker.ForItem(m_Db, port.Item, needConsumer: port.IsOutput, m_Graph), e =>
            {
                // Inputs sit to the right of the dragged output, suppliers to the left of the dragged input.
                Vector2 pos = port.IsOutput ? contentPos + new Vector2(150, 0) : contentPos - new Vector2(150, 0);
                GraphNode node = AddNode(e, pos);
                m_Graph.links.Add(port.IsOutput
                    ? new GraphLink(port.NodeId, node.id, port.Item)
                    : new GraphLink(node.id, port.NodeId, port.Item));
                Changed();
            });
        }

        GraphNode AddNode(PickEntry e, Vector2 center)
        {
            GraphNode node = m_Graph.AddNode(e.Building, e.Selection, m_Canvas.Snap(center - new Vector2(145, 50)));
            m_Canvas.Selection.Clear();
            m_Canvas.Selection.Add(node.id);
            Changed();
            return node;
        }

        void ChooseSelection(string nodeId)
        {
            GraphNode node = m_Graph.Node(nodeId);
            BuildingDef b = m_Db.Building(node?.building);
            if (b == null) return;
            Pick($"{b.name}: choose {PickNoun(b)}", Picker.ForBuilding(m_Db, b, m_Graph), e => { node.selection = e.Selection; Changed(); });
        }

        void NodeMenu(string nodeId, Vector2 worldPos)
        {
            if (!m_Canvas.Selection.Contains(nodeId)) m_Canvas.SelectOnly(nodeId);
            GraphNode node = m_Graph.Node(nodeId);
            BuildingDef b = m_Db.Building(node?.building);
            var items = new List<(string, System.Action, bool)>
            {
                ($"Change {PickNoun(b)}", () => ChooseSelection(nodeId), false),
                ("Copy", Copy, false),
                ("Duplicate", DuplicateSelection, false),
                ("Save as blueprint", SaveBlueprint, false),
                ("Delete", DeleteSelection, true),
            };
            if (b?.Kind == BuildingKind.Production) items.Insert(0, ("Open production", () => OpenProduction(node.selection), false));
            Popup.Menu(m_Canvas, items.ToArray(), at: worldPos);
        }

        void DeleteSelection()
        {
            if (m_Canvas.Selection.Count == 0) return;
            foreach (string id in m_Canvas.Selection.ToList()) m_Graph.RemoveNode(id);
            m_Canvas.Selection.Clear();
            Changed();
        }

        /// <summary>Copies the selected nodes, and the links between them, 40px down and right.</summary>
        void DuplicateSelection()
        {
            if (m_Canvas.Selection.Count == 0) return;
            var map = new Dictionary<string, string>();
            foreach (string id in m_Canvas.Selection)
            {
                GraphNode copy = m_Graph.Node(id).Clone();
                copy.id = System.Guid.NewGuid().ToString("N").Substring(0, 8);
                copy.position += new Vector2(40, 40);
                m_Graph.nodes.Add(copy);
                map[id] = copy.id;
            }
            foreach (GraphLink l in m_Graph.links.ToList())
                if (map.TryGetValue(l.from, out string a) && map.TryGetValue(l.to, out string b))
                    m_Graph.links.Add(new GraphLink(a, b, l.item));

            m_Canvas.Selection.Clear();
            foreach (string id in map.Values) m_Canvas.Selection.Add(id);
            Changed();
        }

        /// <summary>Opens the picker, or picks straight away when there is only one choice (e.g. Water Extractor).</summary>
        void Pick(string title, IEnumerable<PickEntry> entries, System.Action<PickEntry> onPick)
        {
            List<PickEntry> list = entries.ToList();
            if (list.Count == 1) onPick(list[0]);
            else if (list.Count > 1) m_Picker.Open(title, m_Db.Name, list, onPick);
            else m_App.ShowToast(m_Db.HasProgression ? $"Nothing unlocked at {m_Db.LevelName(m_Graph.tier)} can do that." : "Nothing in this game can do that.");
        }

        static string PickNoun(BuildingDef b) => b?.Kind switch
        {
            BuildingKind.Producer => "recipe", BuildingKind.Extractor => "resource", BuildingKind.Generator => "fuel",
            BuildingKind.Production => "production", _ => "item"
        };
    }
}
