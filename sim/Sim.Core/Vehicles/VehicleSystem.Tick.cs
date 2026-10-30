using System;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    public sealed partial class VehicleSystem
    {
        /// <summary>
        /// Avanza un tick (0,1 s). Fases: 0 epoca, puerta.BeginTick, 1a peticiones, 1b concesiones, 2 aceleraciones,
        /// 3 integracion, 4 transiciones, 5 bajas y esperas, 6 snapshot.
        /// </summary>
        public void Update()
        {
            _tick++;
            if (_net.Epoch != _epoch) HandleEpoch();
            _gate.BeginTick(_tick);
            RequestPhase();
            ApplyGrants();
            AccelPhase();
            IntegratePhase();
            TransitionPhase();
            CleanupPhase();
            SnapshotPhase();
        }

        // ------------------------------------------------------------------ fase 1

        private void RequestPhase()
        {
            RouteStep[] steps = _routes.Steps;
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if ((v.Flags & (VehicleFlags.Alive | VehicleFlags.HasEntryPermit | VehicleFlags.Frozen | VehicleFlags.Arrived)) != VehicleFlags.Alive) continue;
                if (v.Kind != TrackKind.Lane || v.Ahead >= 0 || v.RouteCursor >= v.RouteLast) continue;
                VehicleType t = _types[v.Type];
                float d = _net.GetLane(new LaneId(v.Track)).Length - v.S;
                if (d > RequestHorizon(in v)) continue;
                LaneId to = steps[v.RouteOffset + v.RouteCursor + 1].Lane;
                int conn = FindConn(v.Track, to.Value);
                if (conn < 0) continue;
                var req = new EntryRequest
                {
                    Vehicle = i,
                    From = new LaneId(v.Track),
                    Connection = conn,
                    To = to,
                    DistanceToStop = d,
                    Speed = v.V,
                    Space = t.Length + t.S0,
                    StoppedTicks = v.WaitTicks,
                    Accel = t.AMax,
                };
                _gate.RequestEntry(in req);
            }
        }

        private void ApplyGrants()
        {
            ReadOnlySpan<EntryGrant> grants = _gate.ResolveRequests();
            for (int g = 0; g < grants.Length; g++)
            {
                ref readonly EntryGrant gr = ref grants[g];
                ref Vehicle v = ref _veh[gr.Vehicle];
                if (!v.IsAlive || v.Kind != TrackKind.Lane || (v.Flags & VehicleFlags.HasEntryPermit) != 0) continue;
                int cm = Cm(gr.Space);
                v.Flags |= VehicleFlags.HasEntryPermit;
                v.ReservedCm = (ushort)cm;
                _laneReservedCm[_net.GetConnection(gr.Connection).To.Value] += cm;
            }
        }

        // ------------------------------------------------------------------ fase 2

        private void AccelPhase()
        {
            for (int i = 0; i < _high; i++)
            {
                if (!_veh[i].IsAlive) continue;
                ComputeAccel(i);
            }
        }

        private void ComputeAccel(int i)
        {
            ref Vehicle v = ref _veh[i];
            if ((v.Flags & VehicleFlags.Frozen) != 0)
            {
                _accel[i] = 0f;
                _maxAdv[i] = 0f;
                _vCap[i] = 0f;
                return;
            }

            VehicleType t = _types[v.Type];
            RouteStep[] steps = _routes.Steps;
            int stepIdx = v.RouteOffset + v.RouteCursor;
            bool lastStep = v.RouteCursor >= v.RouteLast;
            float stopMargin = _cfg.StopMargin;

            float len, limit;
            int conn = -1;
            int nextLane = -1;
            float nextLimit = -1f;
            if (v.Kind == TrackKind.Lane)
            {
                ref readonly Lane l = ref _net.GetLane(new LaneId(v.Track));
                len = l.Length;
                limit = l.SpeedLimit;
                if (!lastStep)
                {
                    nextLane = steps[stepIdx + 1].Lane.Value;
                    conn = FindConn(v.Track, nextLane);
                    if (conn >= 0) nextLimit = _net.GetConnection(conn).SpeedLimit;
                }
            }
            else
            {
                ref readonly LaneConnection c = ref _net.GetConnection(v.Track);
                len = c.Length;
                limit = c.SpeedLimit;
                nextLane = steps[stepIdx + 1].Lane.Value;
                nextLimit = _net.GetLane(new LaneId(nextLane)).SpeedLimit;
            }
            float dEnd = len - v.S;
            if (dEnd < 0f) dEnd = 0f;
            float horizon = v.V * v.V / (2f * t.B) + v.V * _cfg.Dt + stopMargin + _cfg.RequestExtra;

            // Velocidad deseada y anticipacion del limite de la pista siguiente.
            float f = t.V0Factor * v.SpeedFactor;
            float v0 = limit * f;
            if (v0 > t.VMax) v0 = t.VMax;
            float cap = Inf;
            if (nextLimit > 0f)
            {
                float v0n = nextLimit * f;
                if (v0n > t.VMax) v0n = t.VMax;
                if (v0n < v0)
                {
                    float v1 = MathF.Sqrt(v0n * v0n + 2f * t.B * dEnd);
                    if (v1 < v0) v0 = v1;
                    float dEff = dEnd - v.V * _cfg.Dt;
                    if (dEff < 0f) dEff = 0f;
                    cap = MathF.Sqrt(v0n * v0n + 3f * t.B * dEff);
                }
            }
            if (v0 < 0.1f) v0 = 0.1f;

            // Lider: el de delante en la lista o, si es la cabeza, el que revele el lookahead.
            bool has = false, stopLine = false;
            float gap = 0f, lv = 0f;
            if (v.Ahead >= 0)
            {
                ref Vehicle a = ref _veh[v.Ahead];
                gap = a.S - _types[a.Type].Length - v.S;
                lv = a.V;
                has = true;
            }
            else
            {
                if (v.Kind == TrackKind.Lane)
                {
                    if (lastStep)
                    {
                        if ((v.Flags & VehicleFlags.NeedsReroute) != 0) gap = v.TargetS - v.S;
                        else gap = v.TargetS + t.S0 - v.S;
                        has = true;
                    }
                    else if ((v.Flags & VehicleFlags.HasEntryPermit) == 0 || conn < 0)
                    {
                        // Linea de parada (ADR 002 regla 2). Desviacion: solo dentro del horizonte de peticion, el mismo que usa la
                        // fase 1a; fuera de el, el termino 1/s^2 del IDM frenaria antes de poder siquiera pedir permiso.
                        // M1-07b: el lider virtual va s0 mas alla para que el frente se detenga a StopMargin de la linea (antes
                        // paraba a StopMargin + s0 = 3 m y cada arranque desde parado recorria 2 m de mas antes de entrar).
                        if (dEnd <= horizon)
                        {
                            gap = dEnd - stopMargin + t.S0;
                            has = true;
                            stopLine = true;
                        }
                    }
                    else
                    {
                        int ct = _connTail[conn];
                        if (ct >= 0)
                        {
                            gap = dEnd + _veh[ct].S - _types[_veh[ct].Type].Length;
                            lv = _veh[ct].V;
                            has = true;
                        }
                        else
                        {
                            float off2 = dEnd + _net.GetConnection(conn).Length;
                            if (off2 <= _cfg.LookaheadMax) has = LeaderOnLane(nextLane, off2, steps, v.RouteCursor + 1 >= v.RouteLast, v.TargetS, t.S0, ref gap, ref lv);
                        }
                    }
                }
                else
                {
                    if (dEnd <= _cfg.LookaheadMax)
                    {
                        bool lastNext = v.RouteCursor + 1 >= v.RouteLast;
                        has = LeaderOnLane(nextLane, dEnd, steps, lastNext, v.TargetS, t.S0, ref gap, ref lv);
                        if (!has && !lastNext)
                        {
                            // Parada virtual al final del carril siguiente: solo cuando ya habria que estar frenando (no se puede pedir permiso desde aqui).
                            float stopGap = dEnd + _net.GetLane(new LaneId(nextLane)).Length - stopMargin;
                            if (stopGap <= horizon)
                            {
                                gap = stopGap;
                                lv = 0f;
                                has = true;
                            }
                        }
                    }
                }

                // Lookahead 3 bis (ADR 004): vehiculo que ya salio de este carril pero cuya trasera sigue en el.
                if (v.Kind == TrackKind.Lane)
                {
                    int last = _laneLast[v.Track];
                    if (last >= 0 && last != i)
                    {
                        ref Vehicle a = ref _veh[last];
                        float lenA = _types[a.Type].Length;
                        bool onConn = a.Kind == TrackKind.Connection;
                        if (onConn || a.PrevConnection >= 0)
                        {
                            float rear = (onConn ? a.S : _net.GetConnection(a.PrevConnection).Length + a.S) - lenA;
                            float g3 = dEnd + rear;
                            if (!has || g3 < gap)
                            {
                                gap = g3;
                                lv = a.V;
                                has = true;
                            }
                        }
                    }
                }
            }

            // IDM.
            float vv = v.V;
            float r = vv / v0;
            float r2 = r * r;
            float inter = 0f;
            float maxAdv = Inf;
            if (has)
            {
                float s = gap < 0.1f ? 0.1f : gap;
                float dv = vv - lv;
                float sStar = t.S0 + MathF.Max(0f, vv * t.T + vv * dv * t.InvTwoSqrtAB);
                float q = sStar / s;
                inter = q * q;
                maxAdv = gap - _cfg.MinGap;
                if (stopLine && maxAdv > dEnd - stopMargin) maxAdv = dEnd - stopMargin;
                if (maxAdv < 0f) maxAdv = 0f;
            }
            float acc = t.AMax * (1f - r2 * r2 - inter);
            if (acc > t.AMax) acc = t.AMax;
            else if (acc < -_cfg.BEmergency) acc = -_cfg.BEmergency;
            _accel[i] = acc;
            _maxAdv[i] = maxAdv;
            _vCap[i] = cap;
        }

        /// <summary>Lider (real o el destino) en el carril <paramref name="lane"/>, a <paramref name="offset"/> metros de mi frente.</summary>
        private bool LeaderOnLane(int lane, float offset, RouteStep[] steps, bool laneIsLast, float targetS, float s0, ref float gap, ref float lv)
        {
            int tail = _laneTail[lane];
            if (tail >= 0)
            {
                gap = offset + _veh[tail].S - _types[_veh[tail].Type].Length;
                lv = _veh[tail].V;
                return true;
            }
            if (laneIsLast)
            {
                gap = offset + targetS + s0;
                lv = 0f;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ fase 3

        private void IntegratePhase()
        {
            float dt = _cfg.Dt;
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if (!v.IsAlive) continue;
                if ((v.Flags & VehicleFlags.Frozen) != 0)
                {
                    v.V = 0f;
                    v.A = 0f;
                    continue;
                }
                float vn = v.V + _accel[i] * dt;
                if (vn < 0f) vn = 0f;
                if (vn > _vCap[i]) vn = _vCap[i];
                float ds = vn * dt;
                float maxAdv = _maxAdv[i];
                if (ds > maxAdv)
                {
                    ds = maxAdv;
                    float vlim = ds / dt;
                    if (vn > vlim) vn = vlim;
                }
                v.S += ds;
                v.A = (vn - v.V) / dt;
                v.V = vn;
            }
        }

        // ------------------------------------------------------------------ fase 4

        private void TransitionPhase()
        {
            RouteStep[] steps = _routes.Steps;
            for (int i = 0; i < _high; i++)
            {
                if (!_veh[i].IsAlive) continue;
                TransitionVehicle(i, steps);
            }
        }

        private void TransitionVehicle(int i, RouteStep[] steps)
        {
            ref Vehicle v = ref _veh[i];
            for (int guard = 0; guard < 8; guard++)
            {
                if (v.Kind == TrackKind.Lane)
                {
                    float len = _net.GetLane(new LaneId(v.Track)).Length;
                    if (v.S <= len) break;
                    int conn = -1;
                    if (v.RouteCursor < v.RouteLast && (v.Flags & VehicleFlags.HasEntryPermit) != 0)
                        conn = FindConn(v.Track, steps[v.RouteOffset + v.RouteCursor + 1].Lane.Value);
                    if (conn < 0)
                    {
                        v.S = len;
                        break;
                    }
                    float over = v.S - len;
                    if (v.PrevConnection >= 0) ExitRear(i);
                    Unlink(i);
                    int fromLane = v.Track;
                    v.Kind = TrackKind.Connection;
                    v.Track = conn;
                    v.S = over;
                    InsertSorted(i);
                    _laneLast[fromLane] = i;
                    _gate.OnEntered(conn, i);
                }
                else
                {
                    ref readonly LaneConnection c = ref _net.GetConnection(v.Track);
                    if (v.S < c.Length) break;
                    float over = v.S - c.Length;
                    int prev = v.Track;
                    LaneId to = c.To;
                    Unlink(i);
                    v.Kind = TrackKind.Lane;
                    v.Track = to.Value;
                    v.S = over;
                    v.RouteCursor++;
                    _laneReservedCm[to.Value] -= v.ReservedCm;
                    v.ReservedCm = 0;
                    v.Flags &= ~VehicleFlags.HasEntryPermit;
                    v.PrevConnection = prev;
                    InsertSorted(i);
                }
            }

            if (v.Kind == TrackKind.Lane)
            {
                if (v.PrevConnection >= 0 && v.S >= _types[v.Type].Length) ExitRear(i);
                if (v.RouteCursor >= v.RouteLast
                    && (v.Flags & (VehicleFlags.NeedsReroute | VehicleFlags.Frozen)) == 0
                    && v.S >= v.TargetS - _cfg.ArrivalTolerance)
                    v.Flags |= VehicleFlags.Arrived;
            }
        }

        private void ExitRear(int i)
        {
            ref Vehicle v = ref _veh[i];
            int conn = v.PrevConnection;
            v.PrevConnection = -1;
            ClearLast(_net.GetConnection(conn).From.Value, i);
            _gate.OnExited(conn, i);
        }

        // ------------------------------------------------------------------ fase 5

        private void CleanupPhase()
        {
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if (!v.IsAlive) continue;
                if ((v.Flags & VehicleFlags.Arrived) != 0)
                {
                    ArrivedCount++;
                    RemoveVehicle(i);
                    continue;
                }
                if (v.V < 0.1f)
                {
                    if (v.WaitTicks < ushort.MaxValue) v.WaitTicks++;
                }
                else
                {
                    v.WaitTicks = 0;
                }
            }
        }

        // ------------------------------------------------------------------ fase 6

        private void SnapshotPhase()
        {
            if (!_ring.TryBeginWrite(out VehicleSnapshot[] buf)) return;
            int n = 0;
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if (!v.IsAlive) continue;
                ref VehicleSnapshot snap = ref buf[n++];
                snap.Id = i;
                snap.Generation = (ushort)v.Generation;
                snap.Type = v.Type;
                snap.Speed = v.V;
                if (v.Kind == TrackKind.Lane)
                {
                    var lane = new LaneId(v.Track);
                    _net.SampleLane(lane, v.S, out snap.Position, out snap.Tangent);
                    ref readonly Lane l = ref _net.GetLane(lane);
                    ref readonly Segment sg = ref _net.GetSegment(l.Segment);
                    float u = l.Length > 1e-6f ? v.S / l.Length : 0f;
                    if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
                    if (l.Direction == LaneDirection.Backward) u = 1f - u;
                    float ea = _net.GetNode(sg.NodeA).Elevation, eb = _net.GetNode(sg.NodeB).Elevation;
                    snap.Elevation = ea + (eb - ea) * u;
                }
                else
                {
                    _net.SampleConnection(v.Track, v.S, out snap.Position, out snap.Tangent);
                    snap.Elevation = _net.GetNode(_net.GetConnection(v.Track).Node).Elevation;
                }
            }
            _ring.Publish(n, _tick);
        }
    }
}
