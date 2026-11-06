using UnityEngine;
using UnityEngine.UIElements;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tools.RoadTool
{
    /// <summary>
    /// Icono de un tipo de carretera para el selector (M3-11): su sección transversal dibujada a escala (acera o arcén, carriles, mediana y
    /// marcas) con Painter2D, para distinguir de un vistazo calle, sentido único, avenidas, rural y autopista sin depender de un atlas.
    /// </summary>
    public sealed class RoadIcon : VisualElement
    {
        private readonly RoadType _type;

        public RoadIcon(RoadType type)
        {
            _type = type;
            pickingMode = PickingMode.Ignore;
            AddToClassList("road-icon");
            generateVisualContent += Draw;
        }

        private static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        private void Draw(MeshGenerationContext mgc)
        {
            Rect r = contentRect;
            if (r.width < 2f || r.height < 2f) return;
            var p = mgc.painter2D;
            float k = r.width / _type.TotalWidth;
            bool shoulder = _type.Id == RoadTypeIds.Highway4 || _type.Id == RoadTypeIds.Rural2;
            Color margin = shoulder ? C(0.46f, 0.47f, 0.50f) : C(0.80f, 0.79f, 0.76f);
            Color asphalt = _type.Id == RoadTypeIds.Rural2 ? C(0.36f, 0.34f, 0.31f) : C(0.22f, 0.23f, 0.26f);
            Color paint = C(0.95f, 0.95f, 0.92f), yellow = C(0.98f, 0.82f, 0.25f);
            Color median = _type.Id == RoadTypeIds.Highway4 ? C(0.70f, 0.70f, 0.72f) : C(0.38f, 0.62f, 0.34f);

            void Box(float x0, float w, Color c)
            {
                p.fillColor = c;
                p.BeginPath();
                p.MoveTo(new Vector2(x0, r.y));
                p.LineTo(new Vector2(x0 + w, r.y));
                p.LineTo(new Vector2(x0 + w, r.yMax));
                p.LineTo(new Vector2(x0, r.yMax));
                p.ClosePath();
                p.Fill();
            }

            float x = r.x;
            float m = _type.MarginWidth * k;
            float lane = _type.LaneWidth * k;
            bool oneWay = _type.LanesBackward == 0;
            int left = oneWay ? _type.LanesForward : _type.LanesBackward;
            float med = _type.MedianWidth > 0.01f && !oneWay ? Mathf.Max(_type.MedianWidth * k, 1.5f) : 0f;

            Box(x, m, margin);
            x += m;
            float roadStart = x;
            Box(x, left * lane, asphalt);
            x += left * lane;
            float rightStart = x;
            if (!oneWay)
            {
                if (med > 0f) Box(x, med, median);
                x += med;
                rightStart = x;
                Box(x, _type.LanesForward * lane, asphalt);
                x += _type.LanesForward * lane;
                if (med == 0f) Box(roadStart + left * lane - 0.7f, 1.4f, yellow);
            }
            float roadEnd = x;
            Box(x, m, margin);

            for (int i = 1; i < left; i++) Box(roadStart + i * lane - 0.4f, 0.8f, paint);
            if (!oneWay) for (int i = 1; i < _type.LanesForward; i++) Box(rightStart + i * lane - 0.4f, 0.8f, paint);
            if (shoulder)
            {
                Box(roadStart, 0.9f, paint);
                Box(roadEnd - 0.9f, 0.9f, paint);
            }
            else if (oneWay)
            {
                float cx = roadStart + left * lane * 0.5f, cy = r.center.y;
                p.strokeColor = paint;
                p.lineWidth = 1.6f;
                p.BeginPath();
                p.MoveTo(new Vector2(cx - 5f, cy));
                p.LineTo(new Vector2(cx + 5f, cy));
                p.MoveTo(new Vector2(cx + 1.5f, cy - 3.5f));
                p.LineTo(new Vector2(cx + 5f, cy));
                p.LineTo(new Vector2(cx + 1.5f, cy + 3.5f));
                p.Stroke();
            }
        }
    }
}
