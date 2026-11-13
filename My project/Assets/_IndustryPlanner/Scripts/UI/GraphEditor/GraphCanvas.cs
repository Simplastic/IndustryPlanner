using System;
using System.Collections.Generic;
using IndustryPlanner.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI.GraphEditor
{
    /// <summary>
    /// Pannable, zoomable canvas holding node views and the link layer.
    /// Mouse: left drag a node to move it (hold the align key to line it up with others), left drag a pin to link,
    /// left or middle drag empty space to pan, right drag to box-select, wheel to zoom. Click a link to select it,
    /// right click a link or node for its menu, right click a pin to unlink it.
    /// The canvas only reports intentions through events; GraphEditorView changes the model.
    /// </summary>
    public class GraphCanvas : VisualElement
    {
        const float MinZoom = 0.25f, MaxZoom = 2f, DragThreshold = 4f;

        public event Action<Vector2> PlaceRequested;              // Click on empty canvas while a hotbar tool is active.
        public event Action ToolCancelRequested;                  // Right click on empty canvas.
        public event Action<PortRef, PortRef> LinkRequested;      // (output, input)
        public event Action<PortRef, Vector2> LinkDroppedOnEmpty; // Pin dragged to empty space: offer matching recipes.
        public event Action<PortRef> PortCleared;
        public event Action<string, Vector2> NodeContextRequested;
        public event Action<string> NodeDoubleClicked;
        public event Action<GraphLink, Vector2> LinkContextRequested;
        public event Action<Vector2> BackgroundDoubleClicked;       // Content position: add a node there.
        public event Action LinkSelectionChanged;
        public event Action SelectionChanged, NodesMoved, ViewChanged;

        /// <summary>Asked during node drags: is the "align with other nodes" key held?</summary>
        public Func<bool> AlignHeld { get; set; }

        public readonly HashSet<string> Selection = new();
        public Vector2 Offset { get; private set; }
        public float Zoom { get; private set; } = 1f;
        public Vector2 PointerContent { get; private set; }
        public bool ToolActive { get; set; }
        public IReadOnlyDictionary<string, NodeView> Views => m_Views;
        public ProductionGraph Graph { get; private set; }
        public GraphAnalysis Analysis { get; private set; }
        public GameDatabase Db => m_Db;
        public LinkLayer Links => m_Links;

        /// <summary>Grid step for node positions in content pixels; 0 turns snapping off.</summary>
        public int SnapSize { get; set; }

        readonly DotGridBackground m_Grid;
        readonly VisualElement m_Content, m_NodeLayer;
        readonly LinkLayer m_Links;
        readonly Label m_Ghost;
        readonly Dictionary<string, NodeView> m_Views = new();
        GameDatabase m_Db;

        enum DragMode { None, PanOrClick, Pan, BoxOrClick, Box, Nodes, Link }
        DragMode m_Drag;
        int m_Button;
        bool m_Moved;
        Vector2 m_PressPos, m_LastPos;
        string m_PressNode;
        PortRef m_DragPort;
        VisualElement m_LinkTarget;                                   // Candidate pin under the cursor while linking.
        readonly Dictionary<string, Vector2> m_DragStart = new();     // Node positions when a drag began.
        Vector2 m_DragTotal;
        string m_DragPrimary;
        float m_LastClickTime = -1f;   // For our own double-click detection (the OS setting is often too slow).
        Vector2 m_LastClickPos;
        string m_LastClickTarget;                                         // The node under the cursor when dragging began.
        readonly HashSet<string> m_BoxBase = new();                   // Selection kept when shift-box-selecting.
        readonly VisualElement m_Marquee;

        public GraphCanvas()
        {
            AddToClassList("graph-canvas");
            m_Grid = new DotGridBackground();
            m_Grid.AddToClassList("dot-grid");
            Add(m_Grid);
            m_Content = UiKit.El("graph-content", this);
            m_Links = new LinkLayer(this);
            m_Content.Add(m_Links);
            m_NodeLayer = UiKit.El("", m_Content);
            m_Ghost = UiKit.Text("", "placement-ghost is-hidden t-semibold", this);
            m_Ghost.pickingMode = PickingMode.Ignore;
            m_Marquee = UiKit.El("marquee is-hidden", this);
            m_Marquee.pickingMode = PickingMode.Ignore;

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => { m_Drag = DragMode.None; EndLinking(); });
            RegisterCallback<PointerLeaveEvent>(_ => m_Ghost.AddToClassList("is-hidden"));
            RegisterCallback<WheelEvent>(OnWheel);
        }

        // ---------------------------------------------------------------- model sync

        public void Bind(GameDatabase db, ProductionGraph graph)
        {
            m_Db = db;
            Graph = graph;
            Selection.Clear();
            m_NodeLayer.Clear();
            m_Views.Clear();
            SetView(graph.viewOffset, graph.viewZoom <= 0 ? 1f : graph.viewZoom);
        }

        /// <summary>Creates/removes views to match the model, then refreshes them with the analysis.</summary>
        public void Sync(GraphAnalysis analysis)
        {
            Analysis = analysis;
            var alive = new HashSet<string>();
            foreach (GraphNode n in Graph.nodes)
            {
                alive.Add(n.id);
                if (!m_Views.TryGetValue(n.id, out NodeView view))
                {
                    view = new NodeView(n);
                    view.RegisterCallback<GeometryChangedEvent>(_ => m_Links.MarkDirtyRepaint());
                    m_NodeLayer.Add(view);
                    m_Views[n.id] = view;
                    // Pop in; when a whole graph opens the nodes arrive slightly staggered.
                    NodeView entering = view;
                    entering.AddToClassList("node--enter");
                    long delay = 16 + Mathf.Min(m_Views.Count, 20) * 12;
                    entering.schedule.Execute(() => entering.RemoveFromClassList("node--enter")).StartingIn(delay);
                    // Pins move while the pop-in scales the node, so redraw links once it has settled.
                    entering.schedule.Execute(m_Links.MarkDirtyRepaint).StartingIn(delay + 200);
                }
                view.Refresh(m_Db, Graph, analysis.Nodes.GetValueOrDefault(n.id));
                view.SetSelected(Selection.Contains(n.id));
            }
            foreach (string id in new List<string>(m_Views.Keys))
            {
                if (alive.Contains(id)) continue;
                NodeView gone = m_Views[id];
                gone.pickingMode = PickingMode.Ignore;
                gone.AddToClassList("node--exit"); // Fade out, then leave the tree.
                gone.schedule.Execute(gone.RemoveFromHierarchy).StartingIn(160);
                m_Views.Remove(id);
                Selection.Remove(id);
            }
            m_Links.MarkDirtyRepaint();
        }

        public void SetGhost(string text)
        {
            m_Ghost.text = text ?? "";
            m_Ghost.EnableInClassList("is-hidden", string.IsNullOrEmpty(text));
        }

        // ---------------------------------------------------------------- view & selection

        public Vector2 ToContent(Vector2 local) => (local - Offset) / Zoom;

        public Vector2 Snap(Vector2 p) => SnapSize > 0
            ? new Vector2(Mathf.Round(p.x / SnapSize) * SnapSize, Mathf.Round(p.y / SnapSize) * SnapSize)
            : p;

        public void SetView(Vector2 offset, float zoom)
        {
            Offset = offset;
            Zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            // Inline transform: pan/zoom changes every frame while dragging.
            m_Content.style.translate = new Translate(Offset.x, Offset.y);
            m_Content.style.scale = new Scale(new Vector3(Zoom, Zoom, 1f));
            m_Grid.SetView(Offset, Zoom);
            ViewChanged?.Invoke();
        }

        /// <summary>Fits every node (or just the given ones) in view.</summary>
        public void Frame(IEnumerable<string> ids = null)
        {
            var filter = ids != null ? new HashSet<string>(ids) : null;
            var bounds = new Rect();
            bool any = false;
            foreach (NodeView v in m_Views.Values)
            {
                if (filter != null && !filter.Contains(v.Node.id)) continue;
                Vector2 size = float.IsNaN(v.layout.width) ? new Vector2(290, 180) : v.layout.size;
                var r = new Rect(v.Node.position, size);
                bounds = any ? Rect.MinMaxRect(Mathf.Min(bounds.xMin, r.xMin), Mathf.Min(bounds.yMin, r.yMin),
                    Mathf.Max(bounds.xMax, r.xMax), Mathf.Max(bounds.yMax, r.yMax)) : r;
                any = true;
            }
            if (!any || float.IsNaN(layout.width)) return;
            const float pad = 80f;
            float zoom = Mathf.Min(1f, Mathf.Min((layout.width - pad * 2) / bounds.width, (layout.height - pad * 2 - 140) / bounds.height));
            zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            SetView(new Vector2(layout.width, layout.height - 140) * 0.5f - bounds.center * zoom, zoom);
        }

        public void SelectOnly(string id)
        {
            m_Links.Selected = null;
            Selection.Clear();
            if (id != null) Selection.Add(id);
            ApplySelection();
        }

        void ApplySelection()
        {
            foreach (NodeView v in m_Views.Values) v.SetSelected(Selection.Contains(v.Node.id));
            SelectionChanged?.Invoke();
        }

        static NodeView FindNode(VisualElement e)
        {
            for (; e != null; e = e.parent) if (e is NodeView v) return v;
            return null;
        }

        // ---------------------------------------------------------------- pointer input

        /// <summary>
        /// True when this left click follows another on the same target within the double-click time
        /// (Settings > Double-click speed) and a few pixels. A detected double click resets the timer.
        /// </summary>
        bool IsDoubleClick(Vector2 local, string target)
        {
            float now = Time.realtimeSinceStartup;
            bool isDouble = target == m_LastClickTarget && now - m_LastClickTime <= AppSettings.Current.doubleClickMs / 1000f
                && (local - m_LastClickPos).sqrMagnitude < 36f;
            m_LastClickTime = isDouble ? -1f : now;
            m_LastClickPos = local;
            m_LastClickTarget = target;
            return isDouble;
        }

        void OnPointerDown(PointerDownEvent e)
        {
            if (m_Drag != DragMode.None) return;
            Vector2 local = this.WorldToLocal(e.position);
            m_PressPos = m_LastPos = local;
            m_Button = e.button;
            m_Moved = false;
            m_PressNode = null;
            var target = e.target as VisualElement;

            if (target?.userData is PortRef port)
            {
                if (e.button == 1) { PortCleared?.Invoke(port); return; }
                if (e.button != 0) return;
                m_Drag = DragMode.Link;
                m_DragPort = port;
                m_Links.SetPreview(port, ToContent(local));
                m_Content.AddToClassList("graph-content--linking");
                foreach (NodeView v in m_Views.Values) v.MarkCandidates(port);
            }
            else if (FindNode(target) is NodeView node)
            {
                SelectLink(null);
                if (e.button == 0)
                {
                    if (IsDoubleClick(local, node.Node.id)) { NodeDoubleClicked?.Invoke(node.Node.id); return; }
                    if (e.shiftKey || e.actionKey)
                    {
                        if (!Selection.Remove(node.Node.id)) Selection.Add(node.Node.id);
                        ApplySelection();
                    }
                    else if (!Selection.Contains(node.Node.id)) SelectOnly(node.Node.id);
                    m_Drag = DragMode.Nodes;
                    m_DragPrimary = node.Node.id;
                    m_DragTotal = Vector2.zero;
                    m_DragStart.Clear();
                    foreach (string id in Selection) if (m_Views.TryGetValue(id, out NodeView v)) m_DragStart[id] = v.Node.position;
                }
                else if (e.button == 1)
                {
                    m_Drag = DragMode.BoxOrClick; // Right click opens the node menu; right drag box-selects.
                    m_PressNode = node.Node.id;
                }
                else m_Drag = DragMode.Pan;
            }
            else
            {
                // Links are hit-tested by distance, since they are drawn rather than being elements.
                GraphLink hit = m_Links.HitTest(ToContent(local), 8f / Zoom);
                if (hit != null && e.button is 0 or 1)
                {
                    SelectLink(hit);
                    if (e.button == 1) LinkContextRequested?.Invoke(hit, e.position);
                    e.StopPropagation();
                    return;
                }
                SelectLink(null);
                if (e.button == 0 && IsDoubleClick(local, "") && !ToolActive)
                {
                    BackgroundDoubleClicked?.Invoke(ToContent(local));
                    e.StopPropagation();
                    return;
                }
                m_Drag = e.button switch { 1 => DragMode.BoxOrClick, 2 => DragMode.Pan, _ => DragMode.PanOrClick };
            }

            this.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent e)
        {
            Vector2 local = this.WorldToLocal(e.position);
            PointerContent = ToContent(local);
            if (ToolActive && !string.IsNullOrEmpty(m_Ghost.text))
            {
                m_Ghost.RemoveFromClassList("is-hidden");
                m_Ghost.style.left = local.x + 16;
                m_Ghost.style.top = local.y + 16;
            }
            if (!this.HasPointerCapture(e.pointerId))
            {
                // Hover feedback for links (they can be clicked to select or right clicked to delete).
                if (e.target == this || e.target == m_Grid) m_Links.Hovered = m_Links.HitTest(PointerContent, 8f / Zoom);
                else m_Links.Hovered = null;
                return;
            }

            Vector2 delta = local - m_LastPos;
            m_LastPos = local;
            if (!m_Moved && (local - m_PressPos).sqrMagnitude > DragThreshold * DragThreshold) m_Moved = true;

            switch (m_Drag)
            {
                case DragMode.PanOrClick when m_Moved:
                    m_Drag = DragMode.Pan;
                    SetView(Offset + delta, Zoom);
                    break;
                case DragMode.Pan:
                    SetView(Offset + delta, Zoom);
                    break;
                case DragMode.BoxOrClick when m_Moved:
                    m_Drag = DragMode.Box;
                    m_BoxBase.Clear();
                    if (e.shiftKey) m_BoxBase.UnionWith(Selection); // Shift adds to the current selection.
                    m_Marquee.RemoveFromClassList("is-hidden");
                    UpdateBox(local);
                    break;
                case DragMode.Box:
                    UpdateBox(local);
                    break;
                case DragMode.Nodes when m_Moved:
                    DragNodes(delta);
                    break;
                case DragMode.Link:
                    UpdateLinkTarget(e.position);
                    break;
            }
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (!this.HasPointerCapture(e.pointerId)) return;
            this.ReleasePointer(e.pointerId);
            DragMode mode = m_Drag;
            m_Drag = DragMode.None;
            EndLinking();

            switch (mode)
            {
                case DragMode.PanOrClick when !m_Moved:
                    if (ToolActive) PlaceRequested?.Invoke(PointerContent);
                    else { Selection.Clear(); ApplySelection(); }
                    break;
                case DragMode.BoxOrClick when !m_Moved:
                    if (m_PressNode != null) NodeContextRequested?.Invoke(m_PressNode, e.position);
                    else ToolCancelRequested?.Invoke();
                    break;
                case DragMode.Box:
                    m_Marquee.AddToClassList("is-hidden");
                    ApplySelection();
                    break;
                case DragMode.Nodes:
                    m_Links.Guides.Clear();
                    m_Links.MarkDirtyRepaint();
                    if (m_Moved) NodesMoved?.Invoke();
                    break;
                case DragMode.Link:
                    var picked = panel.Pick(e.position);
                    if (picked?.userData is PortRef other)
                    {
                        bool itemsMatch = other.Item == m_DragPort.Item || other.Item == GameDatabase.AnyItem || m_DragPort.Item == GameDatabase.AnyItem;
                        if (other.IsOutput != m_DragPort.IsOutput && itemsMatch && other.NodeId != m_DragPort.NodeId)
                            LinkRequested?.Invoke(m_DragPort.IsOutput ? m_DragPort : other, m_DragPort.IsOutput ? other : m_DragPort);
                    }
                    else if (FindNode(picked) == null && m_Moved)
                        LinkDroppedOnEmpty?.Invoke(m_DragPort, PointerContent);
                    break;
            }
        }

        // ---------------------------------------------------------------- drag helpers

        /// <summary>Moves the dragged nodes. Holding the align key lines the pressed node up with its
        /// neighbours' edges and centres; otherwise positions snap to the grid.</summary>
        void DragNodes(Vector2 delta)
        {
            m_DragTotal += delta / Zoom;
            Vector2 move = m_DragTotal;
            bool align = AlignHeld?.Invoke() == true;
            m_Links.Guides.Clear();
            if (align) move = Align(move);

            foreach (var pair in m_DragStart)
            {
                if (!m_Views.TryGetValue(pair.Key, out NodeView v)) continue;
                v.Node.position = align ? pair.Value + move : Snap(pair.Value + move);
                v.SyncPosition();
            }
            m_Links.MarkDirtyRepaint();
        }

        Vector2 Align(Vector2 move)
        {
            if (!m_Views.TryGetValue(m_DragPrimary, out NodeView primary) || float.IsNaN(primary.layout.width)) return move;
            var r = new Rect(m_DragStart[m_DragPrimary] + move, primary.layout.size);
            float tolerance = 10f / Zoom;
            float bestX = tolerance, bestY = tolerance, dx = 0, dy = 0;
            Rect? withX = null, withY = null;
            float guideX = 0, guideY = 0;

            foreach (NodeView other in m_Views.Values)
            {
                if (m_DragStart.ContainsKey(other.Node.id) || float.IsNaN(other.layout.width)) continue;
                var o = new Rect(other.Node.position, other.layout.size);
                foreach (var (mine, theirs) in new[] { (r.xMin, o.xMin), (r.xMax, o.xMax), (r.center.x, o.center.x), (r.xMin, o.xMax + 48), (r.xMax, o.xMin - 48) })
                    if (Mathf.Abs(theirs - mine) < bestX) { bestX = Mathf.Abs(theirs - mine); dx = theirs - mine; withX = o; guideX = theirs; }
                foreach (var (mine, theirs) in new[] { (r.yMin, o.yMin), (r.yMax, o.yMax), (r.center.y, o.center.y) })
                    if (Mathf.Abs(theirs - mine) < bestY) { bestY = Mathf.Abs(theirs - mine); dy = theirs - mine; withY = o; guideY = theirs; }
            }

            r.position += new Vector2(dx, dy);
            if (withX is Rect ox) m_Links.Guides.Add((new Vector2(guideX, Mathf.Min(r.yMin, ox.yMin) - 24), new Vector2(guideX, Mathf.Max(r.yMax, ox.yMax) + 24)));
            if (withY is Rect oy) m_Links.Guides.Add((new Vector2(Mathf.Min(r.xMin, oy.xMin) - 24, guideY), new Vector2(Mathf.Max(r.xMax, oy.xMax) + 24, guideY)));
            return move + new Vector2(dx, dy);
        }

        /// <summary>Draws the marquee and selects every node it touches (visuals only until release).</summary>
        void UpdateBox(Vector2 local)
        {
            Rect box = Rect.MinMaxRect(Mathf.Min(m_PressPos.x, local.x), Mathf.Min(m_PressPos.y, local.y),
                Mathf.Max(m_PressPos.x, local.x), Mathf.Max(m_PressPos.y, local.y));
            m_Marquee.style.left = box.x;
            m_Marquee.style.top = box.y;
            m_Marquee.style.width = box.width;
            m_Marquee.style.height = box.height;

            Selection.Clear();
            Selection.UnionWith(m_BoxBase);
            foreach (NodeView v in m_Views.Values)
            {
                Vector2 size = float.IsNaN(v.layout.width) ? new Vector2(290, 180) : v.layout.size;
                var r = new Rect(v.Node.position * Zoom + Offset, size * Zoom);
                if (r.Overlaps(box)) Selection.Add(v.Node.id);
                v.SetSelected(Selection.Contains(v.Node.id));
            }
        }

        public GraphLink SelectedLink => m_Links.Selected;

        public void SelectLink(GraphLink link)
        {
            if (m_Links.Selected == link) return;
            m_Links.Selected = link;
            if (link != null && Selection.Count > 0) { Selection.Clear(); ApplySelection(); }
            LinkSelectionChanged?.Invoke();
        }

        /// <summary>Highlights the compatible pin under the cursor and snaps the preview to it.</summary>
        void UpdateLinkTarget(Vector2 worldPos)
        {
            VisualElement picked = panel.Pick(worldPos);
            VisualElement target = picked != null && picked.ClassListContains("nport__dot--candidate") ? picked : null;
            if (target != m_LinkTarget)
            {
                m_LinkTarget?.RemoveFromClassList("nport__dot--target");
                target?.AddToClassList("nport__dot--target");
                m_LinkTarget = target;
            }
            m_Links.SetPreview(m_DragPort, target != null ? m_Content.WorldToLocal(target.worldBound.center) : PointerContent);
        }

        void EndLinking()
        {
            m_Links.ClearPreview();
            m_LinkTarget?.RemoveFromClassList("nport__dot--target");
            m_LinkTarget = null;
            m_Content.RemoveFromClassList("graph-content--linking");
            foreach (NodeView v in m_Views.Values) v.MarkCandidates(null);
        }

        void OnWheel(WheelEvent e)
        {
            Vector2 local = this.WorldToLocal(e.mousePosition);
            float zoom = Mathf.Clamp(Zoom * (e.delta.y > 0 ? 0.9f : 1.1f), MinZoom, MaxZoom);
            // Keep the point under the cursor fixed while zooming.
            SetView(local - (local - Offset) * (zoom / Zoom), zoom);
            e.StopPropagation();
        }
    }
}
