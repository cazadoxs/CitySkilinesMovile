using System;
using System.Collections.Generic;
using UnityEngine;
using Urbe.Game.Rendering.Roads;
using Urbe.Sim.Commands;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tools.RoadTool
{
    public enum RoadToolMode { Straight, Curve, Free, Demolish }

    /// <summary>Precios por metro de <c>Data/road-prices.json</c> (escenarios sin ciudad); en la ciudad mandan los de <c>CityBalance</c>, que el JSON copia.</summary>
    [Serializable]
    public sealed class RoadPrices
    {
        [Serializable] public sealed class Entry { public string key = ""; public float perMeter; }
        public string note = "";
        public Entry[] entries = Array.Empty<Entry>();

        public static RoadPrices Parse(string? json)
        {
            if (string.IsNullOrEmpty(json)) return Default();
            try { return JsonUtility.FromJson<RoadPrices>(json) ?? Default(); }
            catch (ArgumentException) { return Default(); }
        }

        public static RoadPrices Default() => new RoadPrices
        {
            entries = new[]
            {
                new Entry { key = "street_2", perMeter = 12f }, new Entry { key = "oneway_2", perMeter = 10f }, new Entry { key = "avenue_4", perMeter = 28f },
                new Entry { key = "avenue_6", perMeter = 45f }, new Entry { key = "rural_2", perMeter = 8f }, new Entry { key = "highway_4", perMeter = 60f },
            },
        };

        public float PerMeter(string key)
        {
            for (int i = 0; i < entries.Length; i++) if (entries[i].key == key) return entries[i].perMeter;
            return 10f;
        }
    }

    /// <summary>Resumen de lo que se va a construir (o del motivo por el que no).</summary>
    public struct RoadToolPlan
    {
        public bool HasGeometry;
        public bool Valid;
        /// <summary>Texto para el jugador: el motivo en rojo si no es válido, o el resumen si lo es.</summary>
        public string Message;
        public float Length;
        /// <summary>Ángulo del primer tramo respecto a +X, en grados [0, 360).</summary>
        public float AngleDeg;
        public float Cost;
        public int Segments;
    }

    /// <summary>
    /// Modelo de la herramienta de carreteras (M2-05, GDD 5.3), sin Unity ni entrada: puntos, ajuste a 15° y guías, plan de segmentos,
    /// validación con motivo y coste. El fantasma se pinta con <see cref="GhostPieces"/> y <see cref="RoadGeometry"/>, la misma geometría
    /// que el constructor real. Los comandos que genera son los de Sim.Core (<see cref="BuildRoadCommand"/>, <see cref="DemolishCommand"/>).
    /// </summary>
    public sealed class RoadToolModel
    {
        public const float AngleStepDegrees = 15f;
        public const float AngleSnapTolerance = 7.4f;
        public const float GuideSnapTolerance = 5f;
        public const float NodeSnapRadius = 8f;
        public const float DemolishPickRadius = 12f;
        public const float MaxSegmentLength = 190f;
        public const float MinCurveRadiusExtra = 2f;

        private readonly RoadNetwork _net;
        private readonly RoadPrices _prices;
        private readonly List<SegmentRequest> _requests = new List<SegmentRequest>();
        private readonly List<RoadPiece> _ghost = new List<RoadPiece>();
        private readonly List<Vec2> _free = new List<Vec2>();
        private readonly List<Vec2> _guides = new List<Vec2>();

        public RoadToolModel(RoadNetwork net, RoadPrices prices)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _prices = prices ?? RoadPrices.Default();
        }

        /// <summary>Dinero disponible (M3-07). Si el plan cuesta más no es válido y el motivo lo dice. Infinito en escenarios sin economía.</summary>
        public float Budget { get; set; } = float.PositiveInfinity;
        /// <summary>Tramos que no se pueden demoler (los extremos de la autovía de entrada); lo fija la ciudad.</summary>
        public Func<EntityId, bool>? IsProtected { get; set; }

        public RoadToolMode Mode { get; private set; } = RoadToolMode.Straight;
        public ushort RoadTypeId { get; private set; } = RoadTypeIds.Street2;
        public bool HasStart { get; private set; }
        public bool HasControl { get; private set; }
        public bool HasEnd { get; private set; }
        public Vec2 Start { get; private set; }
        public Vec2 Control { get; private set; }
        public Vec2 End { get; private set; }
        public EntityId DemolishTarget { get; private set; } = EntityId.None;
        public RoadToolPlan Plan { get; private set; }
        public IReadOnlyList<SegmentRequest> Requests => _requests;
        public IReadOnlyList<RoadPiece> GhostPieces => _ghost;
        /// <summary>Puntos de las guías activas (extensión de la vía en el inicio y rejilla de 15°) para dibujarlas.</summary>
        public IReadOnlyList<Vec2> GuideDirections => _guides;
        public bool CanConfirm => Plan.HasGeometry && Plan.Valid;

        public void SetMode(RoadToolMode mode)
        {
            if (mode == Mode) return;
            Mode = mode;
            Cancel();
        }

        public void SetRoadType(ushort id)
        {
            if (!_net.Types.IsValidId(id)) return;
            RoadTypeId = id;
            Rebuild();
        }

        /// <summary>Descarta lo pendiente (puntos, fantasma, selección de demolición).</summary>
        public void Cancel()
        {
            HasStart = HasControl = HasEnd = false;
            DemolishTarget = EntityId.None;
            _free.Clear();
            Rebuild();
        }

        // ------------------------------------------------------------------ entrada

        /// <summary>Toque o clic sobre el suelo: coloca el inicio, el control (curva) o mueve el final.</summary>
        public void Tap(Vec2 ground)
        {
            switch (Mode)
            {
                case RoadToolMode.Demolish: PickDemolish(ground); return;
                case RoadToolMode.Free:
                    if (!HasStart) { Start = SnapToNetwork(ground); HasStart = true; _free.Clear(); _free.Add(Start); Rebuild(); }
                    else MoveEnd(ground);
                    return;
            }
            if (!HasStart) { Start = SnapToNetwork(ground); HasStart = true; Rebuild(); return; }
            if (Mode == RoadToolMode.Curve && !HasControl) { Control = SnapDirection(Start, ground); HasControl = true; Rebuild(); return; }
            MoveEnd(ground);
        }

        /// <summary>Arrastre (cursor desplazado): mueve el punto pendiente. El primer arrastre sin inicio lo coloca.</summary>
        public void Drag(Vec2 ground, bool firstFrame)
        {
            if (Mode == RoadToolMode.Demolish) { PickDemolish(ground); return; }
            if (!HasStart) { Tap(ground); return; }
            if (Mode == RoadToolMode.Free)
            {
                Vec2 last = _free[_free.Count - 1];
                if (firstFrame) { _free.Clear(); _free.Add(Start); last = Start; }
                if ((ground - last).Length >= 4f) { _free.Add(ground); End = ground; HasEnd = true; Rebuild(); }
                return;
            }
            if (Mode == RoadToolMode.Curve && !HasControl) { Control = SnapDirection(Start, ground); Rebuild(); HasControl = true; return; }
            MoveEnd(ground);
        }

        /// <summary>Hover del ratón: previsualiza el final sin comprometerlo (solo si ya hay inicio).</summary>
        public void Hover(Vec2 ground)
        {
            if (!HasStart || Mode == RoadToolMode.Demolish || Mode == RoadToolMode.Free) return;
            if (Mode == RoadToolMode.Curve && !HasControl) { Control = SnapDirection(Start, ground); Rebuild(); return; }
            MoveEnd(ground);
        }

        private void MoveEnd(Vec2 ground)
        {
            Vec2 from = Mode == RoadToolMode.Curve ? Control : Start;
            Vec2 snapped = SnapToNetworkNode(ground, out bool onNode);
            End = onNode ? snapped : (Mode == RoadToolMode.Curve ? ground : SnapDirection(from, ground));
            HasEnd = true;
            Rebuild();
        }

        /// <summary>Tras confirmar, la calle sigue desde el final (dibujo encadenado).</summary>
        public void ContinueFromEnd()
        {
            if (!HasEnd) return;
            Start = End;
            HasEnd = HasControl = false;
            _free.Clear();
            _free.Add(Start);
            Rebuild();
        }

        // ------------------------------------------------------------------ ajustes

        private Vec2 SnapToNetwork(Vec2 p)
        {
            Vec2 n = SnapToNetworkNode(p, out bool onNode);
            if (onNode) return n;
            if (_net.NearestSegment(p, NodeSnapRadius * 0.5f, out EntityId seg, out float t, out _))
                return _net.GetSegment(seg).Curve.Evaluate(t);
            return p;
        }

        private Vec2 SnapToNetworkNode(Vec2 p, out bool onNode)
        {
            EntityId n = _net.NearestNode(p, NodeSnapRadius);
            onNode = n.IsValid;
            return onNode ? _net.GetNode(n).Position : p;
        }

        /// <summary>
        /// Ajusta la dirección de <paramref name="from"/> a <paramref name="p"/>: primero a la prolongación de la vía que acaba en
        /// <paramref name="from"/> (guía), si no a múltiplos de 15° (90° y 45° destacados). La longitud no cambia.
        /// </summary>
        public Vec2 SnapDirection(Vec2 from, Vec2 p)
        {
            _guides.Clear();
            Vec2 d = p - from;
            float len = d.Length;
            if (len < 1e-3f) return p;
            float ang = Mathf.Atan2(d.Z, d.X) * Mathf.Rad2Deg;

            EntityId node = _net.NearestNode(from, 1.5f);
            if (node.IsValid)
            {
                ReadOnlySpan<EntityId> segs = _net.GetNodeSegments(node);
                float bestDiff = GuideSnapTolerance;
                float bestAng = float.NaN;
                for (int i = 0; i < segs.Length; i++)
                {
                    ref readonly Segment s = ref _net.GetSegment(segs[i]);
                    Vec2 outT = s.NodeA == node ? s.Curve.Tangent(0f) : -s.Curve.Tangent(1f);
                    float ga = Mathf.Atan2(-outT.Z, -outT.X) * Mathf.Rad2Deg; // prolongación: opuesta a la salida
                    float diff = Mathf.Abs(Mathf.DeltaAngle(ang, ga));
                    if (diff < bestDiff) { bestDiff = diff; bestAng = ga; }
                    _guides.Add(new Vec2(Mathf.Cos(ga * Mathf.Deg2Rad), Mathf.Sin(ga * Mathf.Deg2Rad)));
                }
                if (!float.IsNaN(bestAng)) return from + new Vec2(Mathf.Cos(bestAng * Mathf.Deg2Rad), Mathf.Sin(bestAng * Mathf.Deg2Rad)) * len;
            }
            float snapped = Mathf.Round(ang / AngleStepDegrees) * AngleStepDegrees;
            if (Mathf.Abs(Mathf.DeltaAngle(ang, snapped)) > AngleSnapTolerance) return p;
            float r = snapped * Mathf.Deg2Rad;
            _guides.Add(new Vec2(Mathf.Cos(r), Mathf.Sin(r)));
            return from + new Vec2(Mathf.Cos(r), Mathf.Sin(r)) * len;
        }

        private void PickDemolish(Vec2 ground)
        {
            DemolishTarget = EntityId.None;
            if (_net.NearestSegment(ground, DemolishPickRadius, out EntityId seg, out _, out _)) DemolishTarget = seg;
            Rebuild();
        }

        // ------------------------------------------------------------------ plan, validación y coste

        private void Rebuild()
        {
            _requests.Clear();
            _ghost.Clear();
            var plan = new RoadToolPlan { Message = HintText() };

            if (Mode == RoadToolMode.Demolish)
            {
                if (DemolishTarget.IsValid && _net.IsSegmentAlive(DemolishTarget))
                {
                    ref readonly Segment s = ref _net.GetSegment(DemolishTarget);
                    RoadPiece piece = RoadPieces.FromSegment(_net, DemolishTarget);
                    piece.T0 = 0f; piece.T1 = 1f;
                    _ghost.Add(piece);
                    plan.HasGeometry = true;
                    plan.Segments = 1;
                    plan.Length = s.Length;
                    if (IsProtected != null && IsProtected(DemolishTarget))
                    {
                        plan.Valid = false;
                        plan.Message = "Los extremos de la autovía (la conexión con el exterior) no se pueden demoler.";
                    }
                    else
                    {
                        plan.Valid = true;
                        plan.Message = "Demoler tramo de " + Mathf.RoundToInt(s.Length) + " m. Confirma con el visto bueno.";
                    }
                }
                Plan = plan;
                return;
            }

            bool ready = HasStart && HasEnd && (Mode != RoadToolMode.Curve || HasControl);
            if (ready)
            {
                BuildRequests();
                plan.HasGeometry = _requests.Count > 0;
                if (plan.HasGeometry) Validate(ref plan);
            }
            else if (HasStart && Mode == RoadToolMode.Curve && HasControl)
            {
                plan.Message = "Toca el punto final de la curva.";
            }
            Plan = plan;
        }

        private string HintText()
        {
            switch (Mode)
            {
                case RoadToolMode.Demolish: return "Toca un tramo para demolerlo.";
                case RoadToolMode.Curve: return !HasStart ? "Toca el punto de inicio de la curva." : (!HasControl ? "Toca el punto de control." : "Toca el punto final.");
                case RoadToolMode.Free: return !HasStart ? "Toca el inicio y arrastra para dibujar libremente." : "Arrastra para dibujar; suelta y confirma.";
                default: return !HasStart ? "Toca el punto de inicio." : "Toca o arrastra para colocar el final.";
            }
        }

        private void BuildRequests()
        {
            var curves = new List<Bezier3>();
            switch (Mode)
            {
                case RoadToolMode.Straight:
                    curves.Add(Bezier3.FromLine(Start, End));
                    break;
                case RoadToolMode.Curve:
                    curves.Add(new Bezier3(Start, Start + (Control - Start) * (2f / 3f), End + (Control - End) * (2f / 3f), End));
                    break;
                case RoadToolMode.Free:
                    FreeCurves(curves);
                    break;
            }
            foreach (Bezier3 c in curves) AddSplit(c);
            foreach (SegmentRequest r in _requests)
            {
                var c = new Bezier3(r.Start, r.C1, r.C2, r.End);
                _ghost.Add(new RoadPiece(c, RoadTypeId, 0f, 1f, 0f, 0f, -1, RoadPieceFlags.MedianCapStart | RoadPieceFlags.MedianCapEnd));
            }
        }

        private void AddSplit(in Bezier3 curve)
        {
            float len = ApproxLength(curve);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / MaxSegmentLength));
            Bezier3 rest = curve;
            for (int i = 0; i < n; i++)
            {
                Bezier3 piece;
                if (i < n - 1)
                {
                    rest.Split(1f / (n - i), out piece, out Bezier3 next);
                    rest = next;
                }
                else piece = rest;
                _requests.Add(new SegmentRequest(piece.P0, piece.P1, piece.P2, piece.P3, RoadTypeId));
            }
        }

        /// <summary>Libre: simplifica el trazo (Douglas-Peucker, 3 m) y lo suaviza con tangentes Catmull-Rom convertidas a Bézier.</summary>
        private void FreeCurves(List<Bezier3> into)
        {
            var pts = new List<Vec2>(_free);
            if (pts.Count < 2 || (pts[pts.Count - 1] - pts[0]).Length < 1f) { if (HasEnd) pts = new List<Vec2> { Start, End }; }
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            Simplify(pts, 0, pts.Count - 1, 3f, keep);
            var s = new List<Vec2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) s.Add(pts[i]);
            if (HasEnd && (s[s.Count - 1] - End).Length > 0.5f) s.Add(End);
            for (int i = 0; i < s.Count - 1; i++)
            {
                Vec2 p0 = s[i], p1 = s[i + 1];
                Vec2 pPrev = i > 0 ? s[i - 1] : p0 - (p1 - p0);
                Vec2 pNext = i + 2 < s.Count ? s[i + 2] : p1 + (p1 - p0);
                Vec2 t0 = (p1 - pPrev) * (1f / 6f), t1 = (pNext - p0) * (1f / 6f);
                // Limita los controles al tercio del tramo para no hacer bucles.
                float chord = (p1 - p0).Length;
                Vec2 c1 = p0 + Clamp(t0, chord / 2f), c2 = p1 - Clamp(t1, chord / 2f);
                into.Add(new Bezier3(p0, c1, c2, p1));
            }
        }

        private static Vec2 Clamp(Vec2 v, float max) { float l = v.Length; return l > max && l > 1e-5f ? v * (max / l) : v; }

        private static void Simplify(List<Vec2> p, int a, int b, float tol, bool[] keep)
        {
            if (b <= a + 1) return;
            float best = 0f; int bi = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistToLine(p[i], p[a], p[b]);
                if (d > best) { best = d; bi = i; }
            }
            if (best > tol && bi >= 0) { keep[bi] = true; Simplify(p, a, bi, tol, keep); Simplify(p, bi, b, tol, keep); }
        }

        private static float DistToLine(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            float l2 = ab.LengthSquared;
            if (l2 < 1e-6f) return (p - a).Length;
            float t = Mathf.Clamp01(Vec2.Dot(p - a, ab) / l2);
            return (p - (a + ab * t)).Length;
        }

        private static float ApproxLength(in Bezier3 c)
        {
            float sum = 0f;
            Vec2 prev = c.P0;
            for (int i = 1; i <= 16; i++) { Vec2 q = c.Evaluate(i / 16f); sum += (q - prev).Length; prev = q; }
            return sum;
        }

        /// <summary>Radio de curvatura mínimo de la curva (m), muestreado.</summary>
        public static float MinRadius(in Bezier3 c)
        {
            float min = float.MaxValue;
            for (int i = 0; i <= 24; i++)
            {
                float t = i / 24f;
                Vec2 d1 = c.Derivative(t), d2 = c.SecondDerivative(t);
                float sp = d1.Length;
                if (sp < 1e-4f) continue;
                float k = Mathf.Abs(Vec2.Cross(d1, d2)) / (sp * sp * sp);
                if (k > 1e-6f) min = Mathf.Min(min, 1f / k);
            }
            return min;
        }

        /// <summary>Ángulo mínimo (grados) entre dos brazos de un cruce para que sus calzadas no se solapen más allá del retranqueo.</summary>
        public float MinJunctionAngle(ushort typeA, ushort typeB)
        {
            RoadType a = _net.Types.Get(typeA), b = _net.Types.Get(typeB);
            float ha = new RoadProfile(a).RoadwayHalfWidth, hb = new RoadProfile(b).RoadwayHalfWidth;
            float h = (ha + hb) * 0.5f;
            float setback = Mathf.Max(RoadNetwork.MinSetback, Mathf.Max(a.TotalWidth, b.TotalWidth) * 0.5f + 1f);
            float ang = 2f * Mathf.Atan(h / setback) * Mathf.Rad2Deg;
            return Mathf.Max(ang, 25f);
        }

        private void Validate(ref RoadToolPlan plan)
        {
            float length = 0f;
            foreach (SegmentRequest r in _requests) length += ApproxLength(new Bezier3(r.Start, r.C1, r.C2, r.End));
            plan.Length = length;
            plan.Segments = _requests.Count;
            Vec2 d0 = _requests[0].C1 - _requests[0].Start;
            if (d0.LengthSquared < 1e-6f) d0 = _requests[0].End - _requests[0].Start;
            float a = Mathf.Atan2(d0.Z, d0.X) * Mathf.Rad2Deg;
            plan.AngleDeg = a < 0f ? a + 360f : a;
            RoadType type = _net.Types.Get(RoadTypeId);
            plan.Cost = Mathf.Round(length * _prices.PerMeter(type.Key));
            string summary = Mathf.RoundToInt(length) + " m, " + Mathf.RoundToInt(plan.AngleDeg) + "°, coste " + plan.Cost.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

            for (int i = 0; i < _requests.Count; i++)
            {
                SegmentRequest req = _requests[i];
                ValidationResult v = _net.CanAddSegment(req);
                if (v != ValidationResult.Ok) { Fail(ref plan, Reason(v)); return; }
            }
            // Curvatura: el borde interior de la acera no puede plegarse sobre sí mismo.
            float minRadius = new RoadProfile(type).TotalWidth * 0.5f + MinCurveRadiusExtra;
            foreach (SegmentRequest r in _requests)
            {
                float rad = MinRadius(new Bezier3(r.Start, r.C1, r.C2, r.End));
                if (rad < minRadius) { Fail(ref plan, "Curva demasiado cerrada (radio mínimo " + Mathf.CeilToInt(minRadius) + " m)."); return; }
            }
            // Ángulo con las calles que ya acaban en el inicio o en el final, según el ancho.
            string? sharp = CheckJunctionAngles();
            if (sharp != null) { Fail(ref plan, sharp); return; }

            if (plan.Cost > Budget)
            {
                Fail(ref plan, "Dinero insuficiente: cuesta " + plan.Cost.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + " y tienes " + Budget.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + ".");
                return;
            }
            plan.Valid = true;
            plan.Message = summary;
        }

        private static void Fail(ref RoadToolPlan plan, string reason)
        {
            plan.Valid = false;
            plan.Message = reason;
        }

        private string? CheckJunctionAngles()
        {
            SegmentRequest first = _requests[0], last = _requests[_requests.Count - 1];
            string? m = CheckEnd(first.Start, new Bezier3(first.Start, first.C1, first.C2, first.End), false);
            if (m != null) return m;
            return CheckEnd(last.End, new Bezier3(last.Start, last.C1, last.C2, last.End), true);
        }

        private string? CheckEnd(Vec2 pos, in Bezier3 curve, bool atEnd)
        {
            EntityId node = _net.NearestNode(pos, _net.NodeSnapRadius());
            if (!node.IsValid) return null;
            Vec2 outNew = atEnd ? -curve.Tangent(1f) : curve.Tangent(0f);
            ReadOnlySpan<EntityId> segs = _net.GetNodeSegments(node);
            for (int i = 0; i < segs.Length; i++)
            {
                ref readonly Segment s = ref _net.GetSegment(segs[i]);
                Vec2 outOld = s.NodeA == node ? s.Curve.Tangent(0f) : -s.Curve.Tangent(1f);
                float ang = Mathf.Acos(Mathf.Clamp(Vec2.Dot(outNew, outOld), -1f, 1f)) * Mathf.Rad2Deg;
                float min = MinJunctionAngle(RoadTypeId, s.RoadTypeId);
                // Con solo un segmento en el nodo el empalme es una continuación (en inglete): solo estorba un giro casi en U.
                if (segs.Length == 1) min = 40f;
                if (ang < min - 0.01f)
                    return "Ángulo demasiado agudo con la calle existente (mínimo " + Mathf.CeilToInt(min) + "°).";
            }
            return null;
        }

        /// <summary>Texto en español del motivo de rechazo de la red.</summary>
        public static string Reason(ValidationResult v)
        {
            switch (v)
            {
                case ValidationResult.UnknownRoadType: return "Tipo de carretera desconocido.";
                case ValidationResult.OutOfBounds: return "Fuera del mapa.";
                case ValidationResult.SameNode: return "El inicio y el final están en el mismo punto.";
                case ValidationResult.TooShort: return "Demasiado corta (mínimo 8 m por tramo).";
                case ValidationResult.TooLong: return "Demasiado larga (máximo 200 m por tramo).";
                case ValidationResult.TooManyCrossings: return "Demasiados cruces en un solo trazo.";
                case ValidationResult.DuplicateSegment: return "Ya existe una calle entre esos puntos.";
                case ValidationResult.NodeFull: return "El cruce está lleno (máximo 8 calles).";
                case ValidationResult.AngleTooSharp: return "Ángulo demasiado agudo o calle encima de otra (mínimo 25°).";
                case ValidationResult.TooManyConnections: return "Demasiadas conexiones en el cruce.";
                case ValidationResult.InvalidTarget: return "No hay nada que demoler ahí.";
                default: return "No se puede construir ahí.";
            }
        }

        // ------------------------------------------------------------------ comandos

        /// <summary>Órdenes de ciudad (M3-07) para confirmar el plan válido: cobran dinero real. Vacío si no hay nada válido.</summary>
        public List<Urbe.Sim.City.CityCommand> BuildCityCommands()
        {
            var list = new List<Urbe.Sim.City.CityCommand>();
            if (!CanConfirm) return list;
            if (Mode == RoadToolMode.Demolish)
            {
                list.Add(new Urbe.Sim.City.DemolishRoadCityCommand(DemolishTarget));
                return list;
            }
            foreach (SegmentRequest r in _requests) list.Add(new Urbe.Sim.City.BuildRoadCityCommand(r));
            return list;
        }

        /// <summary>Comandos para confirmar el plan válido (en orden). Vacío si no hay nada válido.</summary>
        public List<ICommand> BuildCommands()
        {
            var list = new List<ICommand>();
            if (!CanConfirm) return list;
            if (Mode == RoadToolMode.Demolish)
            {
                list.Add(new DemolishCommand(DemolishTarget));
                return list;
            }
            foreach (SegmentRequest r in _requests) list.Add(new BuildRoadCommand(r));
            return list;
        }
    }

    internal static class RoadNetworkToolExtensions
    {
        /// <summary>Radio de ajuste a nodos que usa la red (no es público: valor por defecto de NetworkConfig).</summary>
        public static float NodeSnapRadius(this RoadNetwork _) => 4f;
    }
}
