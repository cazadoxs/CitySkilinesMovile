using System;
using SkiaSharp;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Runner.Render
{
    public enum RenderLayer { Vehicles, Flow }

    /// <summary>Dibuja la red y los vehiculos de un escenario en un mapa de bits (vista cenital, +Z hacia arriba).</summary>
    public sealed class SceneRenderer : IDisposable
    {
        private readonly Scenario _sc;
        private readonly RenderLayer _layer;
        private readonly int _size;
        private readonly int _height;
        private readonly SKBitmap _bitmap;
        private readonly SKCanvas _canvas;
        private readonly SKTypeface _face = SKTypeface.Default;
        private readonly SKFont _font;
        private readonly SKPaint _fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly SKPaint _stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        private readonly SKPath _path = new SKPath();
        private readonly float[] _segSpeedSum;
        private readonly float[] _segRatioSum;
        private readonly int[] _segCount;

        private float _scale;
        private float _offX;
        private float _offY;

        public SceneRenderer(Scenario scenario, int size, RenderLayer layer, int height = 0)
        {
            _sc = scenario;
            _size = size;
            _height = height > 0 ? height : size;
            _layer = layer;
            _bitmap = new SKBitmap(new SKImageInfo(size, _height, SKColorType.Rgba8888, SKAlphaType.Premul));
            _canvas = new SKCanvas(_bitmap);
            _font = new SKFont(_face, Math.Max(11f, size / 62f));
            int bound = scenario.Network.SegmentIndexBound;
            _segSpeedSum = new float[bound];
            _segRatioSum = new float[bound];
            _segCount = new int[bound];
            FitCamera();
        }

        public int Size => _size;
        public int Height => _height;
        public SKBitmap Bitmap => _bitmap;
        /// <summary>Rotulo grande opcional (demo M1-14) y su color de acento.</summary>
        public string? Caption;
        public SKColor CaptionColor = new SKColor(235, 238, 242);

        /// <summary>Encuadre automatico a los limites de la red (nodos y semianchura de calle) con margen.</summary>
        private void FitCamera()
        {
            RoadNetwork net = _sc.Network;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < net.NodeIndexBound; i++)
            {
                EntityId id = net.NodeIdAt(i);
                if (!id.IsValid) continue;
                Vec2 p = net.GetNode(id).Position;
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            if (minX > maxX) { minX = minZ = 0f; maxX = maxZ = 100f; }
            float w = Math.Max(maxX - minX, 1f), h = Math.Max(maxZ - minZ, 1f);
            float margin = _size * 0.07f;
            _scale = Math.Min((_size - 2f * margin) / w, (_height - 2f * margin) / h);
            _offX = (_size - w * _scale) * 0.5f - minX * _scale;
            _offY = (_height + h * _scale) * 0.5f + minZ * _scale; // y = offY - z * scale
        }

        private float PX(float x) => x * _scale + _offX;
        private float PY(float z) => _offY - z * _scale;

        /// <summary>Dibuja un fotograma completo.</summary>
        public void Draw()
        {
            _canvas.Clear(new SKColor(24, 28, 34));
            AccumulateFlow();
            DrawRoads();
            int count;
            float meanSpeed;
            if (_layer == RenderLayer.Vehicles) DrawVehicles(out count, out meanSpeed);
            else Stats(out count, out meanSpeed);
            DrawHud(count, meanSpeed);
            DrawLegend();
            DrawScaleBar();
            DrawCaption();
        }

        // ------------------------------------------------------------------ carreteras

        private void DrawRoads()
        {
            RoadNetwork net = _sc.Network;
            bool flow = _layer == RenderLayer.Flow;
            _stroke.StrokeCap = SKStrokeCap.Butt;
            _stroke.StrokeJoin = SKStrokeJoin.Round;

            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId sid = net.SegmentIdAt(i);
                if (!sid.IsValid) continue;
                ref readonly Segment sg = ref net.GetSegment(sid);
                float width = net.Types.Get(sg.RoadTypeId).TotalWidth * _scale;
                BuildCurve(sg.Curve);
                _stroke.StrokeWidth = Math.Max(width, 1.5f);
                _stroke.Color = new SKColor(78, 82, 90);
                _canvas.DrawPath(_path, _stroke);
            }

            // Discos de interseccion para tapar las cuñas entre brazos.
            _fill.Color = new SKColor(78, 82, 90);
            for (int i = 0; i < net.NodeIndexBound; i++)
            {
                EntityId nid = net.NodeIdAt(i);
                if (!nid.IsValid) continue;
                float r = 0f;
                foreach (EntityId s in net.GetNodeSegments(nid))
                    r = Math.Max(r, net.Types.Get(net.GetSegment(s).RoadTypeId).TotalWidth * 0.5f);
                Vec2 p = net.GetNode(nid).Position;
                _canvas.DrawCircle(PX(p.X), PY(p.Z), Math.Max(r * _scale, 0.75f), _fill);
            }

            if (flow)
            {
                for (int i = 0; i < net.SegmentIndexBound; i++)
                {
                    EntityId sid = net.SegmentIdAt(i);
                    if (!sid.IsValid) continue;
                    ref readonly Segment sg = ref net.GetSegment(sid);
                    float width = net.Types.Get(sg.RoadTypeId).TotalWidth * _scale;
                    BuildCurve(sg.Curve);
                    _stroke.StrokeWidth = Math.Max(width * 0.8f, 2f);
                    _stroke.Color = _segCount[i] == 0 ? new SKColor(96, 100, 108) : SpeedColor(_segRatioSum[i] / _segCount[i]);
                    _canvas.DrawPath(_path, _stroke);
                }
            }

            // Lineas de carril tenues (centro de cada carril).
            _stroke.StrokeWidth = 1f;
            _stroke.Color = new SKColor(255, 255, 255, flow ? (byte)26 : (byte)46);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId sid = net.SegmentIdAt(i);
                if (!sid.IsValid) continue;
                int lanes = net.GetLaneCount(sid);
                for (int slot = 0; slot < lanes; slot++)
                {
                    var lane = new LaneId(i, slot);
                    float len = net.GetLane(lane).Length;
                    int steps = Math.Max(2, (int)(len / 8f));
                    _path.Rewind();
                    for (int k = 0; k <= steps; k++)
                    {
                        net.SampleLane(lane, len * k / steps, out Vec2 p, out _);
                        if (k == 0) _path.MoveTo(PX(p.X), PY(p.Z)); else _path.LineTo(PX(p.X), PY(p.Z));
                    }
                    _canvas.DrawPath(_path, _stroke);
                }
            }
        }

        private void BuildCurve(in Bezier3 curve)
        {
            _path.Rewind();
            const int n = 16;
            for (int k = 0; k <= n; k++)
            {
                Vec2 p = curve.Evaluate(k / (float)n);
                if (k == 0) _path.MoveTo(PX(p.X), PY(p.Z)); else _path.LineTo(PX(p.X), PY(p.Z));
            }
        }

        // ------------------------------------------------------------------ vehiculos y flujo

        /// <summary>Velocidad libre de referencia (limite del carril o conexion) del vehiculo vivo con indice <paramref name="idx"/>.</summary>
        private float FreeSpeed(int idx, out int segment)
        {
            VehicleSystem vs = _sc.Vehicles;
            ref readonly Vehicle v = ref vs.GetVehicle(idx);
            RoadNetwork net = _sc.Network;
            float limit;
            if (v.Kind == TrackKind.Lane)
            {
                var lane = new LaneId(v.Track);
                limit = net.GetLane(lane).SpeedLimit;
                segment = lane.SegmentIndex;
            }
            else
            {
                ref readonly LaneConnection c = ref net.GetConnection(v.Track);
                limit = c.SpeedLimit;
                segment = -1;
            }
            return limit > 0.1f ? limit : 13.9f;
        }

        private void AccumulateFlow()
        {
            Array.Clear(_segSpeedSum, 0, _segSpeedSum.Length);
            Array.Clear(_segRatioSum, 0, _segRatioSum.Length);
            Array.Clear(_segCount, 0, _segCount.Length);
            VehicleSystem vs = _sc.Vehicles;
            for (int i = 0; i < vs.HighWater; i++)
            {
                ref readonly Vehicle v = ref vs.GetVehicle(i);
                if (!v.IsAlive) continue;
                float free = FreeSpeed(i, out int seg);
                if (seg < 0 || seg >= _segCount.Length) continue;
                _segSpeedSum[seg] += v.V;
                _segRatioSum[seg] += Math.Min(1f, v.V / free);
                _segCount[seg]++;
            }
        }

        private void Stats(out int count, out float meanSpeed)
        {
            VehicleSystem vs = _sc.Vehicles;
            count = vs.AliveCount;
            float sum = 0f;
            for (int i = 0; i < vs.HighWater; i++)
            {
                ref readonly Vehicle v = ref vs.GetVehicle(i);
                if (v.IsAlive) sum += v.V;
            }
            meanSpeed = count > 0 ? sum / count : 0f;
        }

        private void DrawVehicles(out int count, out float meanSpeed)
        {
            count = 0;
            meanSpeed = 0f;
            SnapshotRing ring = _sc.Vehicles.Snapshots;
            if (!ring.Acquire(out _, out SnapshotFrame cur)) return;
            try
            {
                VehicleSystem vs = _sc.Vehicles;
                double sum = 0;
                for (int i = 0; i < cur.Count; i++)
                {
                    ref readonly VehicleSnapshot s = ref cur.Data![i];
                    VehicleType vt = vs.GetVehicleType(s.Type);
                    float free = FreeSpeed(s.Id, out _);
                    float ratio = Math.Min(1f, s.Speed / free);
                    sum += s.Speed;

                    float len = Math.Max(vt.Length, 5f / _scale);
                    float wid = Math.Max(s.Type == VehicleTypeId.Truck ? 2.5f : 1.9f, 2.4f / _scale);
                    Vec2 c = s.Position - s.Tangent * (len * 0.5f);
                    float cx = PX(c.X), cy = PY(c.Z);
                    float angle = (float)(Math.Atan2(-s.Tangent.Z, s.Tangent.X) * 180.0 / Math.PI);
                    _canvas.Save();
                    _canvas.Translate(cx, cy);
                    _canvas.RotateDegrees(angle);
                    _fill.Color = SpeedColor(ratio);
                    _canvas.DrawRect(-len * _scale * 0.5f, -wid * _scale * 0.5f, len * _scale, wid * _scale, _fill);
                    _canvas.Restore();
                }
                count = cur.Count;
                meanSpeed = count > 0 ? (float)(sum / count) : 0f;
            }
            finally
            {
                ring.Release();
            }
        }

        /// <summary>Rojo (parado) - amarillo - verde (velocidad libre).</summary>
        public static SKColor SpeedColor(float ratio)
        {
            if (ratio < 0f) ratio = 0f; else if (ratio > 1f) ratio = 1f;
            if (ratio < 0.5f)
            {
                float t = ratio / 0.5f;
                return new SKColor(230, (byte)(40 + 190 * t), 40);
            }
            else
            {
                float t = (ratio - 0.5f) / 0.5f;
                return new SKColor((byte)(230 - 190 * t), (byte)(230 - 20 * t), (byte)(40 + 20 * t));
            }
        }

        // ------------------------------------------------------------------ texto y leyenda

        private void DrawText(string text, float x, float y)
        {
            _fill.Color = new SKColor(235, 238, 242);
            _canvas.DrawText(text, x, y, SKTextAlign.Left, _font, _fill);
        }

        private void DrawHud(int count, float meanSpeed)
        {
            VehicleSystem vs = _sc.Vehicles;
            float seconds = vs.CurrentTick * vs.Config.Dt;
            string text = "tick " + vs.CurrentTick + "  t=" + seconds.ToString("0.0") + " s  vehiculos " + count
                + "  v media " + (meanSpeed * 3.6f).ToString("0.0") + " km/h  flujo " + _sc.Sim.Metrics.CityFlowPercent.ToString("0") + " %";
            _fill.Color = new SKColor(0, 0, 0, 150);
            _canvas.DrawRect(8, 8, _font.MeasureText(text) + 16, _font.Size + 14, _fill);
            DrawText(text, 16, 8 + _font.Size + 3);
        }

        private void DrawCaption()
        {
            if (string.IsNullOrEmpty(Caption)) return;
            using var big = new SKFont(_face, Math.Max(18f, _size / 30f));
            float w = big.MeasureText(Caption) + 32f;
            float x = (_size - w) * 0.5f, y = _font.Size * 3.4f;
            _fill.Color = new SKColor(0, 0, 0, 190);
            _canvas.DrawRect(x, y, w, big.Size * 1.9f, _fill);
            _fill.Color = CaptionColor;
            _canvas.DrawText(Caption, x + 16f, y + big.Size * 1.3f, SKTextAlign.Left, big, _fill);
        }

        private void DrawLegend()
        {
            float x = 12f, w = _size * 0.22f, h = 12f;
            float y = _height - 44f;
            _fill.Color = new SKColor(0, 0, 0, 150);
            _canvas.DrawRect(x - 4, y - _font.Size - 8, w + 8 + _font.MeasureText("libre") + 6, _font.Size + h + 24, _fill);
            string title = _layer == RenderLayer.Flow ? "Flujo (v media / v libre)" : "Velocidad del vehiculo";
            DrawText(title, x, y - 4);
            int steps = (int)w;
            for (int i = 0; i < steps; i++)
            {
                _fill.Color = SpeedColor(i / (float)(steps - 1));
                _canvas.DrawRect(x + i, y + 2, 1.2f, h, _fill);
            }
            DrawText("parado", x, y + h + _font.Size + 2);
            DrawText("libre", x + w - _font.MeasureText("libre") + _font.MeasureText("libre") * 0.0f, y + h + _font.Size + 2);
        }

        private void DrawScaleBar()
        {
            // Longitud "redonda" mas cercana a ~18 % del ancho.
            float target = _size * 0.18f / _scale;
            float mag = (float)Math.Pow(10, Math.Floor(Math.Log10(target)));
            float m = target / mag;
            float nice = (m >= 5f ? 5f : m >= 2f ? 2f : 1f) * mag;
            float px = nice * _scale;
            float x1 = _size - 16f, x0 = x1 - px, y = _height - 22f;
            string label = nice.ToString("0") + " m";
            _fill.Color = new SKColor(0, 0, 0, 150);
            _canvas.DrawRect(x0 - 10, y - _font.Size - 14, px + 20, _font.Size + 30, _fill);
            _stroke.StrokeWidth = 2f;
            _stroke.StrokeCap = SKStrokeCap.Butt;
            _stroke.Color = new SKColor(235, 238, 242);
            _canvas.DrawLine(x0, y, x1, y, _stroke);
            _canvas.DrawLine(x0, y - 5, x0, y + 5, _stroke);
            _canvas.DrawLine(x1, y - 5, x1, y + 5, _stroke);
            DrawText(label, x0 + (px - _font.MeasureText(label)) * 0.5f, y - 8);
        }

        // ------------------------------------------------------------------ salida

        public void SavePng(string path)
        {
            using SKImage img = SKImage.FromBitmap(_bitmap);
            using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = System.IO.File.Create(path);
            data.SaveTo(fs);
        }

        /// <summary>Cuantiza el fotograma actual a la paleta fija; <paramref name="indices"/> debe tener Size*Size bytes.</summary>
        public void ToIndexed(byte[] indices)
        {
            ReadOnlySpan<byte> px = _bitmap.GetPixelSpan();
            for (int i = 0, j = 0; i < indices.Length; i++, j += 4)
                indices[i] = FixedPalette.Index(px[j], px[j + 1], px[j + 2]);
        }

        public void Dispose()
        {
            _canvas.Dispose();
            _bitmap.Dispose();
            _font.Dispose();
            _fill.Dispose();
            _stroke.Dispose();
            _path.Dispose();
        }
    }
}
