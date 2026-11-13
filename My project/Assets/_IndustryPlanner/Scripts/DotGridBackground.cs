using UnityEngine;
using UnityEngine.UIElements;

namespace IndustryPlanner.UI
{
    /// <summary>
    /// Dotted canvas background, shared by the home screen and the node editor.
    /// Styled from USS with --dot-color, --dot-spacing and --dot-size; the editor pans/zooms it with SetView.
    /// </summary>
    [UxmlElement]
    public partial class DotGridBackground : VisualElement
    {
        static readonly CustomStyleProperty<Color> s_DotColor = new("--dot-color");
        static readonly CustomStyleProperty<float> s_DotSpacing = new("--dot-spacing");
        static readonly CustomStyleProperty<float> s_DotSize = new("--dot-size");

        Color m_DotColor = new(1f, 1f, 1f, 0.07f);
        float m_Spacing = 24f;
        float m_Size = 2f;
        Vector2 m_Offset;
        float m_Zoom = 1f;

        public DotGridBackground()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
        }

        /// <summary>Moves the grid with the graph so dots stay fixed to the canvas while panning.</summary>
        public void SetView(Vector2 offset, float zoom)
        {
            m_Offset = offset;
            m_Zoom = zoom;
            MarkDirtyRepaint();
        }

        void OnCustomStyleResolved(CustomStyleResolvedEvent evt)
        {
            if (evt.customStyle.TryGetValue(s_DotColor, out var color)) m_DotColor = color;
            if (evt.customStyle.TryGetValue(s_DotSpacing, out var spacing)) m_Spacing = Mathf.Max(4f, spacing);
            if (evt.customStyle.TryGetValue(s_DotSize, out var size)) m_Size = Mathf.Max(0.5f, size);
            MarkDirtyRepaint();
        }

        void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            Rect rect = contentRect;
            float spacing = m_Spacing * m_Zoom;
            while (spacing < 12f) spacing *= 2f; // Keep the dot count bounded when zoomed far out.
            if (rect.width <= 0f || rect.height <= 0f) return;

            // First dot position: the offset wrapped into one cell, so the grid scrolls with the content.
            float startX = Mathf.Repeat(m_Offset.x, spacing) - spacing;
            float startY = Mathf.Repeat(m_Offset.y, spacing) - spacing;
            int cols = Mathf.CeilToInt(rect.width / spacing) + 2;
            int rows = Mathf.CeilToInt(rect.height / spacing) + 2;
            float half = m_Size * 0.5f;

            // Only dots fully inside the element are drawn, so the grid never spills over neighbours.
            int firstCol = startX - half < 0 ? 1 : 0, firstRow = startY - half < 0 ? 1 : 0;
            while (startX + (cols - 1) * spacing + half > rect.width && cols > firstCol) cols--;
            while (startY + (rows - 1) * spacing + half > rect.height && rows > firstRow) rows--;
            int count = cols - firstCol;
            if (count <= 0) return;

            // One allocation per row keeps every mesh well under the 65k vertex limit.
            for (int y = firstRow; y < rows; y++)
            {
                MeshWriteData mesh = mgc.Allocate(count * 4, count * 6);
                float top = rect.y + startY + y * spacing - half;

                for (int x = firstCol; x < cols; x++)
                {
                    float left = rect.x + startX + x * spacing - half;
                    mesh.SetNextVertex(new Vertex { position = new Vector3(left, top, Vertex.nearZ), tint = m_DotColor });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(left + m_Size, top, Vertex.nearZ), tint = m_DotColor });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(left + m_Size, top + m_Size, Vertex.nearZ), tint = m_DotColor });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(left, top + m_Size, Vertex.nearZ), tint = m_DotColor });

                    ushort i = (ushort)((x - firstCol) * 4);
                    mesh.SetNextIndex(i);
                    mesh.SetNextIndex((ushort)(i + 1));
                    mesh.SetNextIndex((ushort)(i + 2));
                    mesh.SetNextIndex((ushort)(i + 2));
                    mesh.SetNextIndex((ushort)(i + 3));
                    mesh.SetNextIndex(i);
                }
            }
        }
    }
}
