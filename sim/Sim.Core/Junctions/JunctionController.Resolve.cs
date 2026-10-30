using System;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Junctions
{
    public sealed partial class JunctionController
    {
        /// <summary>
        /// Fase 1b (ADR 004): ordena la cola por (nodo, 15 - prioridad efectiva, espera desc, vehiculo) y evalua cada peticion:
        /// semaforo, stop, reclamada, conflicto (con liberacion parcial), hueco a la salida y concesion. Resultado independiente del
        /// orden de llegada. El span vale hasta el siguiente BeginTick.
        /// </summary>
        public ReadOnlySpan<EntryGrant> ResolveRequests()
        {
            _gCount = 0;
            int n = _qCount;
            if (n == 0) return ReadOnlySpan<EntryGrant>.Empty;
            IVehicleView view = _view ?? throw new InvalidOperationException("JunctionController sin vista: crea el VehicleSystem con esta puerta");
            int agingTicks = _cfg.AgingTicks > 0 ? _cfg.AgingTicks : 1;
            int agingMax = _cfg.AgingMax < 0 ? 0 : _cfg.AgingMax;

            for (int q = 0; q < n; q++)
            {
                ref EntryRequest req = ref _queue[q];
                ref readonly LaneConnection c = ref _net.GetConnection(req.Connection);
                int ni = c.Node.Index;
                JunctionNode jn = NodeFor(ni, c.Node);
                int local = c.LocalIndex;
                int v = req.Vehicle;
                int wait = GetWaitTicks(v);
                int eff, w;
                if (_forced[v])
                {
                    eff = 15;
                    w = 0xFFFF;
                }
                else
                {
                    int age = wait / agingTicks;
                    if (age > agingMax) age = agingMax;
                    eff = jn.Gates[local].Rank + age;
                    if (eff > 15) eff = 15;
                    w = wait;
                }
                _qNode[q] = ni;
                _qLocal[q] = local;
                _order[q] = q;
                _qEff[q] = (byte)eff;
                _keys[q] = ((ulong)(uint)ni << 40) | ((ulong)(15 - eff) << 36) | ((ulong)(0xFFFF - w) << 20) | (uint)v;
            }
            SortUtil.Sort(_keys, _order, n, _tmpKeys, _tmpIdx, _counts);

            int curNode = -1;
            ulong claim = 0;
            JunctionNode? node = null;
            for (int k = 0; k < n; k++)
            {
                int q = _order[k];
                int ni = _qNode[q];
                if (ni != curNode)
                {
                    curNode = ni;
                    claim = 0;
                    _scrCount = 0;
                    node = _nodes[ni]!;
                    node.State.WaitTick = _tick;
                    node.State.OldestWaitTicks = 0;
                }
                ref EntryRequest req = ref _queue[q];
                ushort wt = GetWaitTicks(req.Vehicle);
                if (wt > node!.State.OldestWaitTicks) node.State.OldestWaitTicks = wt;
                GrantDecision d = Evaluate(node, ref req, _qLocal[q], _qEff[q], ref claim, view);
                _decision[req.Vehicle] = d;
                Count(d);
            }
            return new ReadOnlySpan<EntryGrant>(_grants, 0, _gCount);
        }

        private void Count(GrantDecision d)
        {
            switch (d)
            {
                case GrantDecision.Granted: Stats.Granted++; break;
                case GrantDecision.DeniedSignal: Stats.DeniedSignal++; break;
                case GrantDecision.DeniedStop: Stats.DeniedStop++; break;
                case GrantDecision.DeniedClaimed: Stats.DeniedClaimed++; break;
                case GrantDecision.DeniedConflict: Stats.DeniedConflict++; break;
                case GrantDecision.DeniedExitFull: Stats.DeniedExitFull++; break;
                case GrantDecision.DeniedGap: Stats.DeniedGap++; break;
            }
        }

        private GrantDecision Evaluate(JunctionNode jn, ref EntryRequest req, int local, int eff, ref ulong claim, IVehicleView view)
        {
            ref ConnGate g = ref jn.Gates[local];
            ulong bit = 1UL << local;
            bool forced = _forced[req.Vehicle];

            if (!forced)
            {
                // 1. Semaforo.
                if (g.Rule == ApproachRule.Signal && jn.IsSignal && !SignalAllows(jn, in g, in req)) return GrantDecision.DeniedSignal;
                // 2. Stop: parado, cerca de la linea y el tiempo minimo.
                if (g.Rule == ApproachRule.Stop
                    && !(req.Speed <= _cfg.StopSpeed && req.DistanceToStop <= _cfg.StopZone && req.StoppedTicks >= _cfg.StopDwellTicks))
                    return GrantDecision.DeniedStop;
                // 3. Reclamada por una peticion anterior (mas prioritaria) de este tick.
                if ((g.ConflictMask & claim) != 0) return GrantDecision.DeniedClaimed;
            }

            // 4. Conflicto con permisos u ocupantes (liberacion parcial). Nunca se salta.
            ulong busy = g.ConflictMask & (jn.PermitMask | jn.OccupiedMask);
            if (busy != 0)
            {
                int baseConn = req.Connection - local;
                while (busy != 0)
                {
                    int j = Bits.TrailingZeroCount(busy);
                    busy &= busy - 1;
                    ref ConnGate gj = ref jn.Gates[j];
                    bool block = gj.Permits > 0;
                    if (!block && gj.Occupants > 0)
                    {
                        int e = g.ConflictFirst + Bits.PopCount(g.ConflictMask & ((1UL << j) - 1));
                        float otherEnd = jn.Entries[e].OtherEndDm * 0.1f;
                        block = view.RearOnConnection(gj.LastEntrant, baseConn + j) < otherEnd;
                    }
                    if (block)
                    {
                        claim |= bit;
                        return GrantDecision.DeniedConflict;
                    }
                }
            }

            // 4 bis (M1-07b). Aceptacion de huecos: el que cede no se cruza delante de un vehiculo con preferencia que aun no pidio.
            if (!forced && g.Rule != ApproachRule.Signal && _cfg.GapLookDistance > 0f && GapTooShort(jn, in g, ref req, local, eff, view))
                return GrantDecision.DeniedGap;

            // 5. Anti-bloqueo: hueco a la salida para todo el vehiculo (o el carril entero si es mas corto).
            int to = req.To.Value;
            float space = req.Space;
            if (!forced)
            {
                float laneLen = _net.GetLane(req.To).Length;
                float need = space < laneLen ? space : laneLen;
                // M1-07b: el de delante tambien avanza. Credito = lo que recorre la cola al frenar comodamente desde su velocidad.
                float vt = view.TailSpeed(req.To);
                float credit = _cfg.ExitCreditDecel > 0f ? vt * vt / (2f * _cfg.ExitCreditDecel) : 0f;
                float free = view.FreeSpace(req.To) + credit - ScratchOf(to);
                if (free + 1e-4f < need) return GrantDecision.DeniedExitFull;
            }

            // 6. Conceder.
            if (g.Permits < ushort.MaxValue) g.Permits++;
            jn.PermitMask |= bit;
            claim |= bit;
            AddScratch(to, space);
            _grants[_gCount++] = new EntryGrant { Vehicle = req.Vehicle, Connection = req.Connection, Space = space };
            _permitRef[req.Vehicle] = (jn.Node.Index << LocalBits) | local;
            _forced[req.Vehicle] = false;
            return GrantDecision.Granted;
        }

        /// <summary>
        /// True si algun movimiento en conflicto con mas rango que la prioridad efectiva <paramref name="eff"/> tiene un vehiculo sin
        /// permiso que llegaria a la zona comun antes de que esta peticion la despeje (mas el margen). Sin asignaciones.
        /// </summary>
        private bool GapTooShort(JunctionNode jn, in ConnGate g, ref EntryRequest req, int local, int eff, IVehicleView view)
        {
            ulong m = g.ConflictMask;
            if (m == 0) return false;
            int baseConn = req.Connection - local;
            float a = (req.Accel > 0f ? req.Accel : _cfg.GapDefaultAccel) * _cfg.GapAccelFactor;
            float v0 = req.Speed;
            while (m != 0)
            {
                int j = Bits.TrailingZeroCount(m);
                m &= m - 1;
                ref ConnGate gj = ref jn.Gates[j];
                if (gj.Rank <= eff) continue;
                LaneId fromJ = _net.GetConnection(baseConn + j).From;
                if (!view.NextApproach(fromJ, _cfg.GapLookDistance, out int cj, out float dj, out float vj)) continue;
                if (cj != baseConn + j || vj < 0.5f) continue; // parado: pide el mismo y el orden canonico decide
                int e = g.ConflictFirst + Bits.PopCount(g.ConflictMask & ((1UL << j) - 1));
                int ej = gj.ConflictFirst + Bits.PopCount(gj.ConflictMask & ((1UL << local) - 1));
                float clearD = req.DistanceToStop + jn.Entries[e].EndDm * 0.1f + req.Space;
                float tClear = (MathF.Sqrt(v0 * v0 + 2f * a * clearD) - v0) / a;
                float eta = (dj + jn.Entries[ej].StartDm * 0.1f) / vj;
                if (eta < tClear + _cfg.GapMargin) return true;
            }
            return false;
        }

        private bool SignalAllows(JunctionNode jn, in ConnGate g, in EntryRequest req)
        {
            if (((g.PhaseBits >> jn.State.Phase) & 1) == 0) return false;
            float dt = _cfg.Dt;
            switch (jn.State.Stage)
            {
                case SignalStage.Green:
                {
                    float v = req.Speed > 2f ? req.Speed : 2f;
                    return req.DistanceToStop <= v * (jn.State.StageTicksLeft * dt + _cfg.Amber * dt);
                }
                case SignalStage.Amber:
                {
                    if (req.DistanceToStop <= req.Speed * req.Speed / (2f * _cfg.AmberBrake) + 1f) return true;
                    return g.Turn == TurnType.Left && req.StoppedTicks >= _cfg.AmberClearStoppedTicks && req.DistanceToStop <= _cfg.StopZone;
                }
                default:
                    return false;
            }
        }

        private float ScratchOf(int lane)
        {
            for (int i = 0; i < _scrCount; i++)
                if (_scrLane[i] == lane) return _scrSpace[i];
            return 0f;
        }

        private void AddScratch(int lane, float space)
        {
            for (int i = 0; i < _scrCount; i++)
            {
                if (_scrLane[i] != lane) continue;
                _scrSpace[i] += space;
                return;
            }
            if (_scrCount == _scrLane.Length) return;
            _scrLane[_scrCount] = lane;
            _scrSpace[_scrCount] = space;
            _scrCount++;
        }
    }
}
