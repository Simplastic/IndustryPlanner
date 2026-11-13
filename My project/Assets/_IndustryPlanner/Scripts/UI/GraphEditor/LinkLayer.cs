using System.Collections.Generic;
using IndustryPlanner.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI.GraphEditor
{
    /// <summary>
    /// Draws every link from output pin to input pin in canvas content space, with moving arrows that show
    /// the direction of flow. Curved style uses a bezier when the target is to the right and a rounded detour
    /// when it is to the left; right-angle style always routes on horizontal and vertical segments.
    /// </summary>
    public class LinkLayer : VisualElement
    {
        const float Stub = 24f;          // Straight run out of / into a pin before turning.
        const float ArrowSpacing = 44f;
        const float ArrowSpeed = 36f;    // Content pixels per second at full flow.

        static readonly CustomStyleProperty<Color> s_Color = new("--link-color");
        static readonly CustomStyleProperty<Color> s_Starved = new("--link-starved-color");
        static readonly CustomStyleProperty<Color> s_Fluid = new("--link-fluid-color");
        static readonly CustomStyleProperty<Color> s_Selected = new("--link-selected-color");
        static readonly CustomStyleProperty<Color> s_Overloaded = new("--link-overloaded-color");

        readonly GraphCanvas m_Canvas;
        readonly IVisualElementScheduledItem m_Ticker;
        readonly List<Vector2> m_Points = new();
        readonly List<(GraphLink link, Vector2[] points)> m_HitPaths = new(); // Rebuilt every draw, for clicks.
        GraphLink m_Selected, m_Hovered;

        /// <summary>Alignment guides shown while dragging with the align key, in content space.</summary>
        public readonly List<(Vector2 a, Vector2 b)> Guides = new();

        public GraphLink Selected { get => m_Selected; set { m_Selected = value; MarkDirtyRepaint(); } }

        public GraphLink Hovered
        {
            get => m_Hovered;
            set { if (m_Hovered == value) return; m_Hovered = value; MarkDirtyRepaint(); }
        }
        Color m_Color = Color.gray, m_StarvedColor = Color.red, m_FluidColor = Color.cyan, m_SelectedColor = Color.white, m_OverloadedColor = Color.red;
        bool m_HasPreview, m_Animate;
        PortRef m_PreviewPort;
        Vector2 m_PreviewEnd;

        public LinkStyle Style { get; set; }
        public float GridSize { get; set; }

        public bool Animate
        {
            get => m_Animate;
            set
            {
                m_Animate = value;
                if (value) m_Ticker.Resume(); else m_Ticker.Pause();
                MarkDirtyRepaint();
            }
        }

        public LinkLayer(GraphCanvas canvas)
        {
            m_Canvas = canvas;
            AddToClassList("link-layer");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            m_Ticker = schedule.Execute(MarkDirtyRepaint).Every(33); // ~30 fps is plenty for the arrows.
            m_Ticker.Pause();
            RegisterCallback<CustomStyleResolvedEvent>(e =>
            {
                e.customStyle.TryGetValue(s_Color, out m_Color);
                e.customStyle.TryGetValue(s_Starved, out m_StarvedColor);
                e.customStyle.TryGetValue(s_Fluid, out m_FluidColor);
                e.customStyle.TryGetValue(s_Selected, out m_SelectedColor);
                e.customStyle.TryGetValue(s_Overloaded, out m_OverloadedColor);
            });
        }

        public void SetPreview(PortRef port, Vector2 end)
        {
            m_HasPreview = true;
            m_PreviewPort = port;
            m_PreviewEnd = end;
            MarkDirtyRepaint();
        }

        public void ClearPreview()
        {
            m_HasPreview = false;
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext mgc)
        {
            ProductionGraph graph = m_Canvas.Graph;
            if (graph == null) return;
            Painter2D p = mgc.painter2D;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            var views = m_Canvas.Views;
            GraphAnalysis analysis = m_Canvas.Analysis;
            float time = Time.realtimeSinceStartup;
            m_HitPaths.Clear();

            foreach (GraphLink l in graph.links)
            {
                if (!views.TryGetValue(l.from, out NodeView a) || !views.TryGetValue(l.to, out NodeView b)) continue;
                if (a.PortAnchor(l.item, true, this) is not Vector2 from || b.PortAnchor(l.item, false, this) is not Vector2 to) continue;

                bool selected = l == m_Selected || m_Canvas.Selection.Contains(l.from) || m_Canvas.Selection.Contains(l.to);
                bool starved = analysis != null && analysis.Nodes.TryGetValue(l.to, out NodeResult r) && r.Underfed;
                bool overloaded = analysis != null && analysis.Overloaded.Contains(l);
                Color color = selected ? m_SelectedColor : overloaded ? m_OverloadedColor : starved ? m_StarvedColor
                    : m_Canvas.Db.Item(l.item) is { fluid: true } ? m_FluidColor : m_Color;

                bool bezier = Route(from, to);
                m_HitPaths.Add((l, m_Points.ToArray()));
                p.strokeColor = l == m_Hovered && !selected ? Color.Lerp(color, Color.white, 0.3f) : color;
                p.lineWidth = l == m_Selected ? 5f : selected || l == m_Hovered || overloaded ? 4f : 3f;
                StrokeRoute(p, from, to, bezier);

                // Arrows only on links that carry something; starved supply moves them slower.
                float flow = analysis != null && analysis.LinkFlow.TryGetValue(l, out float f) ? f : 0f;
                if (m_Animate && flow > 0.001f)
                {
                    float efficiency = analysis.Nodes.TryGetValue(l.from, out NodeResult src) ? Mathf.Max(0.25f, src.Efficiency) : 1f;
                    float phase = Mathf.Repeat(time * ArrowSpeed * efficiency, ArrowSpacing);
                    DrawArrows(p, phase, Color.Lerp(color, Color.white, 0.45f));
                }
            }

            if (Guides.Count > 0)
            {
                p.strokeColor = m_SelectedColor;
                p.lineWidth = 1.5f;
                foreach (var (ga, gb) in Guides)
                {
                    p.BeginPath();
                    p.MoveTo(ga);
                    p.LineTo(gb);
                    p.Stroke();
                }
            }

            if (m_HasPreview && views.TryGetValue(m_PreviewPort.NodeId, out NodeView srcView)
                && srcView.PortAnchor(m_PreviewPort.Item, m_PreviewPort.IsOutput, this) is Vector2 pin)
            {
                Vector2 from = m_PreviewPort.IsOutput ? pin : m_PreviewEnd, to = m_PreviewPort.IsOutput ? m_PreviewEnd : pin;
                p.strokeColor = m_SelectedColor;
                p.lineWidth = 3f;
                StrokeRoute(p, from, to, Route(from, to));
            }
        }

        /// <summary>The link closest to a content-space point, if within tolerance.</summary>
        public GraphLink HitTest(Vector2 point, float tolerance)
        {
            GraphLink best = null;
            float bestDist = tolerance;
            foreach (var (link, pts) in m_HitPaths)
                for (int i = 1; i < pts.Length; i++)
                {
                    float d = DistanceToSegment(point, pts[i - 1], pts[i]);
                    if (d < bestDist) { bestDist = d; best = link; }
                }
            return best;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude < 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        // ---------------------------------------------------------------- routing

        /// <summary>Fills m_Points with the path (sampled when curved). Returns true for a bezier.</summary>
        bool Route(Vector2 a, Vector2 b)
        {
            m_Points.Clear();
            bool forward = b.x - a.x >= Stub * 2;
            if (Style == LinkStyle.Curved && forward)
            {
                Bezier(a, b, out Vector2 c1, out Vector2 c2);
                for (int i = 0; i <= 24; i++) m_Points.Add(BezierPoint(a, c1, c2, b, i / 24f));
                return true;
            }

            m_Points.Add(a);
            if (forward)
            {
                float midX = Snap((a.x + b.x) * 0.5f);
                midX = Mathf.Clamp(midX, a.x + Stub, b.x - Stub);
                m_Points.Add(new Vector2(midX, a.y));
                m_Points.Add(new Vector2(midX, b.y));
            }
            else
            {
                // Target is behind the source: leave right, cross over between (or below) the nodes, enter from the left.
                float x1 = a.x + Stub, x2 = b.x - Stub;
                float midY = Mathf.Abs(a.y - b.y) < 120f ? Mathf.Max(a.y, b.y) + 120f : (a.y + b.y) * 0.5f;
                midY = Snap(midY);
                m_Points.Add(new Vector2(x1, a.y));
                m_Points.Add(new Vector2(x1, midY));
                m_Points.Add(new Vector2(x2, midY));
                m_Points.Add(new Vector2(x2, b.y));
            }
            m_Points.Add(b);
            return false;
        }

        void StrokeRoute(Painter2D p, Vector2 a, Vector2 b, bool bezier)
        {
            p.BeginPath();
            p.MoveTo(a);
            if (bezier)
            {
                Bezier(a, b, out Vector2 c1, out Vector2 c2);
                p.BezierCurveTo(c1, c2, b);
            }
            else
            {
                float radius = Style == LinkStyle.Curved ? 40f : 8f;
                for (int i = 1; i < m_Points.Count - 1; i++)
                {
                    Vector2 prev = m_Points[i - 1], corner = m_Points[i], next = m_Points[i + 1];
                    float r = Mathf.Min(radius, Vector2.Distance(prev, corner) * 0.5f, Vector2.Distance(corner, next) * 0.5f);
                    if (r < 0.5f) p.LineTo(corner); else p.ArcTo(corner, next, r);
                }
                p.LineTo(b);
            }
            p.Stroke();
        }

        /// <summary>Small filled chevrons spaced along m_Points, offset by phase so they travel.</summary>
        void DrawArrows(Painter2D p, float phase, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            float walked = 0f, next = phase + 10f;
            for (int i = 1; i < m_Points.Count; i++)
            {
                Vector2 a = m_Points[i - 1], b = m_Points[i];
                float len = Vector2.Distance(a, b);
                if (len < 0.01f) continue;
                Vector2 dir = (b - a) / len, normal = new Vector2(-dir.y, dir.x);
                while (next <= walked + len)
                {
                    Vector2 pos = a + dir * (next - walked);
                    p.MoveTo(pos + dir * 5f);
                    p.LineTo(pos - dir * 4f + normal * 4.5f);
                    p.LineTo(pos - dir * 4f - normal * 4.5f);
                    p.ClosePath();
                    next += ArrowSpacing;
                }
                walked += len;
            }
            if (next > phase + 10f) p.Fill();
        }

        float Snap(float v) => GridSize > 0 ? Mathf.Round(v / GridSize) * GridSize : v;

        static void Bezier(Vector2 a, Vector2 b, out Vector2 c1, out Vector2 c2)
        {
            float dx = Mathf.Max(40f, (b.x - a.x) * 0.5f);
            c1 = a + new Vector2(dx, 0);
            c2 = b - new Vector2(dx, 0);
        }

        static Vector2 BezierPoint(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, float t)
        {
            float u = 1 - t;
            return u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
        }
    }
}
