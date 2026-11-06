using System;
using Urbe.Sim.Commands;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Orden del jugador sobre la ciudad (ADR 005). Como <see cref="ICommand"/> pero con acceso a toda la ciudad (dinero, zonas, edificios).
    /// Validate no toca nada; Apply la ejecuta (false si falla sin cambios); Undo solo vale justo despues de un Apply y en orden LIFO.
    /// </summary>
    public abstract class CityCommand
    {
        /// <summary>Motivo del ultimo rechazo (Ok si se aplico).</summary>
        public CityResult Result { get; protected set; }
        /// <summary>Si Result es RoadRejected, el motivo que dio la red viaria.</summary>
        public ValidationResult RoadResult { get; protected set; }

        public abstract CityResult Validate(CitySimulation city);
        public abstract bool Apply(CitySimulation city);
        public abstract void Undo(CitySimulation city);
    }

    /// <summary>Historial de deshacer/rehacer de ordenes de ciudad, de pila acotada.</summary>
    public sealed class CityHistory
    {
        private readonly CityCommand?[] _undo;
        private int _undoCount;
        private readonly CityCommand?[] _redo;
        private int _redoCount;

        public CityHistory(int capacity = 100)
        {
            _undo = new CityCommand?[capacity];
            _redo = new CityCommand?[capacity];
        }

        public int UndoCount => _undoCount;
        public int RedoCount => _redoCount;

        public CityResult Execute(CitySimulation city, CityCommand cmd)
        {
            CityResult r = cmd.Validate(city);
            if (r != CityResult.Ok) return r;
            if (!cmd.Apply(city)) return cmd.Result == CityResult.Ok ? CityResult.InvalidTarget : cmd.Result;
            while (_redoCount > 0) _redo[--_redoCount] = null;
            if (_undoCount == _undo.Length)
            {
                Array.Copy(_undo, 1, _undo, 0, _undoCount - 1);
                _undoCount--;
            }
            _undo[_undoCount++] = cmd;
            return CityResult.Ok;
        }

        public bool Undo(CitySimulation city)
        {
            if (_undoCount == 0) return false;
            CityCommand cmd = _undo[--_undoCount]!;
            _undo[_undoCount] = null;
            cmd.Undo(city);
            if (_redoCount == _redo.Length)
            {
                Array.Copy(_redo, 1, _redo, 0, _redoCount - 1);
                _redoCount--;
            }
            _redo[_redoCount++] = cmd;
            return true;
        }

        public bool Redo(CitySimulation city)
        {
            if (_redoCount == 0) return false;
            CityCommand cmd = _redo[_redoCount - 1]!;
            if (cmd.Validate(city) != CityResult.Ok || !cmd.Apply(city)) return false;
            _redo[--_redoCount] = null;
            if (_undoCount == _undo.Length)
            {
                Array.Copy(_undo, 1, _undo, 0, _undoCount - 1);
                _undoCount--;
            }
            _undo[_undoCount++] = cmd;
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------- carreteras

    /// <summary>Construye una carretera cobrando su coste (longitud por precio del tipo). Deshacer devuelve el dinero.</summary>
    public sealed class BuildRoadCityCommand : CityCommand
    {
        private readonly BuildRoadCommand _inner;
        private long _paid;

        public SegmentRequest Request { get; }

        public BuildRoadCityCommand(in SegmentRequest request)
        {
            Request = request;
            _inner = new BuildRoadCommand(request);
        }

        public static long CostOf(CitySimulation city, in SegmentRequest r)
        {
            var curve = new Bezier3(r.Start, r.C1, r.C2, r.End);
            return city.Balance.RoadCost(r.RoadTypeId, ArcLengthTable.SegmentLength(curve, 0f, 1f));
        }

        public override CityResult Validate(CitySimulation city)
        {
            ValidationResult v = _inner.Validate(city.Network);
            if (v != ValidationResult.Ok)
            {
                RoadResult = v;
                return Result = v == ValidationResult.OutOfBounds ? CityResult.OutOfBounds : CityResult.RoadRejected;
            }
            if (!city.IsRoadUnlocked(Request.RoadTypeId)) return Result = CityResult.NotUnlocked;
            if (city.Money < CostOf(city, Request)) return Result = CityResult.InsufficientFunds;
            return Result = CityResult.Ok;
        }

        public override bool Apply(CitySimulation city)
        {
            long cost = CostOf(city, Request);
            if (!_inner.Apply(city.Network)) { Result = CityResult.RoadRejected; return false; }
            city.Spend(cost, CityExpense.Construction);
            _paid = cost;
            Result = CityResult.Ok;
            return true;
        }

        public override void Undo(CitySimulation city)
        {
            _inner.Undo(city.Network);
            city.Refund(_paid, CityExpense.Construction);
            _paid = 0;
        }
    }

    /// <summary>Demuele segmentos de carretera (los edificios que quedan sin celdas se demuelen solos). Sin coste ni reembolso.</summary>
    public sealed class DemolishRoadCityCommand : CityCommand
    {
        private readonly DemolishCommand _inner;
        private readonly EntityId[] _targets;

        public DemolishRoadCityCommand(EntityId segment) { _inner = new DemolishCommand(segment); _targets = new[] { segment }; }
        public DemolishRoadCityCommand(EntityId[] segments) { _inner = new DemolishCommand(segments); _targets = (EntityId[])segments.Clone(); }

        public override CityResult Validate(CitySimulation city)
        {
            ValidationResult v = _inner.Validate(city.Network);
            RoadResult = v;
            if (v != ValidationResult.Ok) return Result = CityResult.InvalidTarget;
            for (int i = 0; i < _targets.Length; i++)
                if (city.IsOutsideConnection(_targets[i])) return Result = CityResult.Protected;
            return Result = CityResult.Ok;
        }

        public override bool Apply(CitySimulation city)
        {
            if (!_inner.Apply(city.Network)) { Result = CityResult.InvalidTarget; return false; }
            Result = CityResult.Ok;
            return true;
        }

        public override void Undo(CitySimulation city) => _inner.Undo(city.Network);
    }

    // ---------------------------------------------------------------------------------------------- zonas

    /// <summary>
    /// Pinta o borra zona (GDD 5.4): con un pincel circular, un lazo (poligono) o los lados de un segmento hasta cierto fondo.
    /// Borrar es pintar <see cref="ZoneType.None"/>. Deshacer restaura la zona anterior de cada celda por posicion.
    /// </summary>
    public sealed class PaintZoneCommand : CityCommand
    {
        private enum Shape : byte { Circle, Polygon, Segment }

        private readonly Shape _shape;
        private readonly Vec2 _center;
        private readonly float _radius;
        private readonly Vec2[]? _polygon;
        private readonly EntityId _segment;
        private readonly int _depth;
        private readonly ZoneType _zone;
        private readonly int _density;

        private Vec2[] _pos = Array.Empty<Vec2>();
        private ZoneType[] _prevZone = Array.Empty<ZoneType>();
        private byte[] _prevDensity = Array.Empty<byte>();

        public int Changed => _pos.Length;

        private PaintZoneCommand(Shape shape, Vec2 center, float radius, Vec2[]? polygon, EntityId segment, int depth, ZoneType zone, int density)
        {
            _shape = shape;
            _center = center;
            _radius = radius;
            _polygon = polygon;
            _segment = segment;
            _depth = depth;
            _zone = zone;
            _density = density;
        }

        public static PaintZoneCommand Brush(Vec2 center, float radius, ZoneType zone, int density = 1)
            => new PaintZoneCommand(Shape.Circle, center, radius, null, EntityId.None, 0, zone, density);

        public static PaintZoneCommand Lasso(Vec2[] polygon, ZoneType zone, int density = 1)
            => new PaintZoneCommand(Shape.Polygon, default, 0f, (Vec2[])polygon.Clone(), EntityId.None, 0, zone, density);

        public static PaintZoneCommand FillSegment(EntityId segment, int depth, ZoneType zone, int density = 1)
            => new PaintZoneCommand(Shape.Segment, default, 0f, null, segment, depth, zone, density);

        public override CityResult Validate(CitySimulation city)
        {
            if (_shape == Shape.Circle && !(_radius > 0f && _radius <= 200f)) return Result = CityResult.InvalidArgument;
            if (_shape == Shape.Polygon && (_polygon == null || _polygon.Length < 3)) return Result = CityResult.InvalidArgument;
            if (_shape == Shape.Segment && (!city.Network.IsSegmentAlive(_segment) || _depth < 1)) return Result = CityResult.InvalidTarget;
            if (_zone != ZoneType.None && !city.IsZoneUnlocked(_zone, _density)) return Result = CityResult.NotUnlocked;
            return Result = CityResult.Ok;
        }

        public override bool Apply(CitySimulation city)
        {
            ZoneGrid zones = city.Zones;
            int[] buf = city.QueryBuffer;
            int n;
            if (_shape == Shape.Circle) n = zones.QueryCircle(_center, _radius, buf);
            else if (_shape == Shape.Polygon) n = zones.QueryPolygon(_polygon!, buf);
            else n = zones.QuerySegment(_segment, _depth, buf);
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                ref readonly ZoneCell c = ref zones.GetCell(buf[i]);
                if (c.Zone == _zone && c.Density == ZoneGrid.NormalizeDensity(_zone, _density)) continue;
                buf[changed++] = buf[i];
            }
            _pos = new Vec2[changed];
            _prevZone = new ZoneType[changed];
            _prevDensity = new byte[changed];
            for (int i = 0; i < changed; i++)
            {
                ref readonly ZoneCell c = ref zones.GetCell(buf[i]);
                _pos[i] = c.Pos;
                _prevZone[i] = c.Zone;
                _prevDensity[i] = c.Density;
            }
            for (int i = 0; i < changed; i++) zones.SetZone(buf[i], _zone, _density);
            Result = CityResult.Ok;
            return true;
        }

        public override void Undo(CitySimulation city)
        {
            for (int i = 0; i < _pos.Length; i++)
            {
                int c = city.Zones.FindCell(_pos[i], 0.6f);
                if (c < 0) c = city.Zones.FindCell(_pos[i], city.Balance.ZoneInheritRadius);
                if (c >= 0) city.Zones.SetZone(c, _prevZone[i], _prevDensity[i]);
            }
        }
    }
}
