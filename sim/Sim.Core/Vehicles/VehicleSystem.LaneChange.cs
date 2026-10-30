using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    public enum SpawnFailure : byte
    {
        None,
        /// <summary>La ruta no es valida: segmentos no conectados, carril inexistente o posiciones fuera del carril.</summary>
        InvalidRoute,
        /// <summary>No hay hueco en el carril de salida o el sistema esta lleno (se puede reintentar).</summary>
        NoRoom,
    }

    public enum LaneChangeDenial : byte { Ghost, Rear, LeaderGap, LeaderBrake, FollowerGap, FollowerBrake, Reserved, Leaving }

    /// <summary>
    /// Cambio de carril (M1-08, GDD 8.6, ADR 002 enmendado). Decision de modelo: el cambio es <b>instantaneo en la simulacion</b> (el
    /// vehiculo pasa a la lista del carril nuevo en el tick de la decision, con la aceptacion de hueco hecha contra los vecinos
    /// reales) y dura <see cref="VehicleConfig.LaneChangeTicks"/> como <b>maniobra</b> con dos efectos: (1) el render interpola el
    /// desplazamiento lateral (<see cref="VehicleSnapshot.Lateral"/>) y (2) el carril de origen conserva un <i>fantasma</i> (solo uno por
    /// carril): el seguidor del carril viejo sigue viendo al vehiculo como lider en su posicion real mientras se desplaza, asi nadie le
    /// pasa por encima. Mientras dura la maniobra el vehiculo no pide permiso de entrada ni inicia otro cambio. Las listas
    /// intrusivas quedan siempre sin solapes porque el vehiculo esta en una sola.
    /// </summary>
    public sealed partial class VehicleSystem
    {
        /// <summary>Motivos por los que se denego un cambio de carril (diagnostico; indices de <see cref="LaneChangeDenial"/>).</summary>
        public readonly int[] LaneChangeDenials = new int[8];

        /// <summary>Cambios de carril realizados.</summary>
        public int LaneChangeCount { get; private set; }
        /// <summary>Planes de carril relajados por no poder cambiar (se sale desde el carril actual).</summary>
        public int LaneChangeRelaxed { get; private set; }
        /// <summary>Vehiculos a los que se pidio re-ruta por no poder llegar al carril correcto.</summary>
        public int LaneChangeRerouted { get; private set; }

        /// <summary>
        /// Primer vehiculo que se desplaza lateralmente fuera de este carril (cambio en curso), o -1; el resto, por
        /// <c>GetVehicle(i).GhostNext</c>. Diagnostico y tests.
        /// </summary>
        public int LaneChangeGhost(LaneId lane) => lane.Value >= 0 && lane.Value < _laneGhost.Length ? _laneGhost[lane.Value] : -1;

        // ------------------------------------------------------------------ fase de cambio de carril

        /// <summary>
        /// Fase 0b (antes de las peticiones, orden ascendente de indice, efectos inmediatos: determinista). Avanza las maniobras y,
        /// para los vehiculos cuyo carril actual no conecta con el carril de llegada del paso siguiente, intenta pasar al carril
        /// contiguo en la direccion del carril de salida correcto.
        /// </summary>
        private void LaneChangePhase()
        {
            if (!_cfg.LaneChangeEnabled) return;
            RouteStep[] steps = _routes.Steps;
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if ((v.Flags & VehicleFlags.Alive) == 0) continue;
                if (v.LcTicks > 0)
                {
                    if (--v.LcTicks == 0) EndManeuver(i);
                    continue;
                }
                if (v.LcCooldown > 0)
                {
                    v.LcCooldown--;
                    continue;
                }
                if ((v.Flags & (VehicleFlags.HasEntryPermit | VehicleFlags.Frozen | VehicleFlags.Arrived)) != 0) continue;
                if (v.Kind != TrackKind.Lane || v.PrevConnection >= 0 || v.RouteCursor >= v.RouteLast) continue;
                int next = steps[v.RouteOffset + v.RouteCursor + 1].Lane.Value;
                if (FindConn(v.Track, next) >= 0)
                {
                    v.LcWait = 0;
                    continue; // ya esta en un carril que conecta
                }
                WrongLane(i, ref v, next);
            }
        }

        private void WrongLane(int i, ref Vehicle v, int next)
        {
            int dep = NearestDeparture(v.Track, next);
            if (dep < 0)
            {
                // El segmento actual ya no conecta con el siguiente (no deberia pasar sin un cambio de epoca): re-ruta.
                RequestReroute(i);
                return;
            }
            int slot = v.Track & 7, depSlot = dep & 7;
            int changes = depSlot > slot ? depSlot - slot : slot - depSlot;
            int target = v.Track + (depSlot > slot ? 1 : -1);
            float dEnd = _net.GetLane(new LaneId(v.Track)).Length - v.S;
            // Urgencia 0..1: crece al acercarse el final del carril respecto a la distancia que pide cada cambio.
            float u = 1f - (dEnd - _cfg.StopMargin) / (changes * _cfg.LaneChangeNominalDistance);
            u = u < 0f ? 0f : (u > 1f ? 1f : u);
            if (TryChangeLane(i, target, u))
            {
                LaneChangeCount++;
                v.LcWait = 0;
                return;
            }
            // No hay hueco. Si se espera parado mucho en el carril equivocado: primero se relaja el plan, al final se pide re-ruta.
            if (v.V >= 0.1f) return;
            if (v.LcWait < ushort.MaxValue) v.LcWait++;
            if (v.LcWait >= _cfg.LaneChangeRerouteWaitTicks)
            {
                v.LcWait = 0;
                RequestReroute(i);
            }
            else if (v.LcWait >= _cfg.LaneChangeRelaxWaitTicks) RelaxPlan(i);
        }

        private bool Deny(LaneChangeDenial reason)
        {
            LaneChangeDenials[(int)reason]++;
            return false;
        }

        /// <summary>
        /// Aceptacion de hueco (MOBIL simplificado, solo cambios obligatorios): el hueco delante y detras debe ser al menos s0 + MinGap
        /// (reducido hasta la mitad con urgencia), el vehiculo no debe frenar mas de BSafe al seguir al nuevo lider y el nuevo seguidor
        /// tampoco (b_safe = 3 m/s2). No se puede entrar en un carril con un fantasma, ni sobre el hueco reservado al inicio del carril,
        /// ni delante de un vehiculo que sale del carril por el frente pero aun tiene la trasera en el.
        /// </summary>
        private bool TryChangeLane(int i, int newLane, float urgency)
        {
            ref Vehicle c = ref _veh[i];
            int oldLane = c.Track;
            VehicleType tc = _types[c.Type];
            ref readonly Lane lo = ref _net.GetLane(new LaneId(oldLane));
            ref readonly Lane ln = ref _net.GetLane(new LaneId(newLane));
            float lenN = ln.Length;
            float s = lo.Length > 1e-3f ? c.S * (lenN / lo.Length) : c.S;
            if (s > lenN) s = lenN;
            float rear = s - tc.Length;
            if (rear < 0f) return Deny(LaneChangeDenial.Rear);
            float margin = (tc.S0 + _cfg.MinGap) * (1f - 0.5f * urgency);
            // No se entra sobre un fantasma del carril nuevo (otro vehiculo que sale de el lateralmente en ese mismo tramo).
            for (int g = _laneGhost[newLane]; g >= 0; g = _veh[g].GhostNext)
            {
                ref Vehicle gv = ref _veh[g];
                float gs = gv.S * (lenN / _net.GetLane(new LaneId(gv.Track)).Length);
                if (gs + margin > rear && gs - _types[gv.Type].Length - margin < s) return Deny(LaneChangeDenial.Ghost);
            }
            float bSafe = _cfg.BSafe + _cfg.LaneChangeUrgentExtraBrake * urgency;

            int f = -1, l = _laneTail[newLane];
            while (l >= 0 && _veh[l].S < s)
            {
                f = l;
                l = _veh[l].Ahead;
            }
            if (l >= 0)
            {
                ref Vehicle lv = ref _veh[l];
                float gap = lv.S - _types[lv.Type].Length - s;
                if (gap < margin) return Deny(LaneChangeDenial.LeaderGap);
                if (IdmAccel(tc, c.V, FreeSpeed(in c, in ln), gap, c.V - lv.V) < -bSafe) return Deny(LaneChangeDenial.LeaderBrake);
            }
            if (f >= 0)
            {
                ref Vehicle fv = ref _veh[f];
                float gap = rear - fv.S;
                if (gap < margin) return Deny(LaneChangeDenial.FollowerGap);
                if (IdmAccel(_types[fv.Type], fv.V, FreeSpeed(in fv, in ln), gap, fv.V - c.V) < -bSafe) return Deny(LaneChangeDenial.FollowerBrake);
            }
            else
            {
                // Nadie detras en el carril: puede venir alguien entrando (con permiso, o ya en la conexion que desemboca aqui).
                float res = _laneReservedCm[newLane] * 0.01f;
                if (res > 0f && rear - res < margin + _cfg.LaneChangeEntryClearance * (1f - urgency)) return Deny(LaneChangeDenial.Reserved);
                if (!IncomingAllows(in c, tc, in ln, newLane, rear, margin, bSafe)) return Deny(LaneChangeDenial.Reserved);
            }
            int last = _laneLast[newLane];
            if (last >= 0)
            {
                ref Vehicle a = ref _veh[last];
                bool onConn = a.Kind == TrackKind.Connection;
                if (onConn || a.PrevConnection >= 0)
                {
                    float rearA = (onConn ? a.S : _net.GetConnection(a.PrevConnection).Length + a.S) - _types[a.Type].Length;
                    if (lenN - s + rearA < margin) return Deny(LaneChangeDenial.Leaving);
                }
            }

            // Compromiso.
            Unlink(i);
            c.GhostNext = _laneGhost[oldLane];
            _laneGhost[oldLane] = i;
            c.LcFrom = oldLane;
            c.LcTicks = (byte)_cfg.LaneChangeTicks;
            c.LcLateral0 = TravelRight(in lo) - TravelRight(in ln);
            c.Track = newLane;
            c.S = s;
            _routes.Steps[c.RouteOffset + c.RouteCursor] = new RouteStep(ln.Segment, new LaneId(newLane));
            InsertSorted(i);
            return true;
        }

        /// <summary>Los vehiculos que van por una conexion hacia <paramref name="newLane"/> (aun fuera de su lista) no deben frenar mas de bSafe por el cambio.</summary>
        private bool IncomingAllows(in Vehicle c, VehicleType tc, in Lane ln, int newLane, float rear, float margin, float bSafe)
        {
            ref readonly Segment sg = ref _net.GetSegment(ln.Segment);
            EntityId node = ln.Direction == LaneDirection.Forward ? sg.NodeA : sg.NodeB;
            ref readonly Node nd = ref _net.GetNode(node);
            for (int q = 0; q < nd.ConnCount; q++)
            {
                int ci = nd.ConnFirst + q;
                ref readonly LaneConnection lc = ref _net.GetConnection(ci);
                if (lc.To.Value != newLane) continue;
                int h = _connHead[ci];
                if (h < 0) continue;
                ref Vehicle iv = ref _veh[h];
                float gap = rear + (lc.Length - iv.S);
                if (gap < margin) return false;
                if (IdmAccel(_types[iv.Type], iv.V, FreeSpeed(in iv, in ln), gap, iv.V - c.V) < -bSafe) return false;
            }
            return true;
        }

        private static float TravelRight(in Lane l) => l.Direction == LaneDirection.Forward ? l.Offset : -l.Offset;

        private float FreeSpeed(in Vehicle v, in Lane lane)
        {
            VehicleType t = _types[v.Type];
            float v0 = lane.SpeedLimit * t.V0Factor * v.SpeedFactor;
            if (v0 > t.VMax) v0 = t.VMax;
            return v0 < 0.1f ? 0.1f : v0;
        }

        private static float IdmAccel(VehicleType t, float v, float v0, float gap, float dv)
        {
            float s = gap < 0.1f ? 0.1f : gap;
            float sStar = t.S0 + MathF.Max(0f, v * t.T + v * dv * t.InvTwoSqrtAB);
            float r = v / v0;
            float r2 = r * r;
            float q = sStar / s;
            return t.AMax * (1f - r2 * r2 - q * q);
        }

        private void EndManeuver(int i)
        {
            ref Vehicle v = ref _veh[i];
            if (v.LcFrom >= 0 && v.LcFrom < _laneGhost.Length)
            {
                // Saca el vehiculo de la lista de fantasmas de su carril de origen (listas de 1-2 elementos).
                int cur = _laneGhost[v.LcFrom], prev = -1;
                while (cur >= 0 && cur != i)
                {
                    prev = cur;
                    cur = _veh[cur].GhostNext;
                }
                if (cur == i)
                {
                    if (prev < 0) _laneGhost[v.LcFrom] = v.GhostNext;
                    else _veh[prev].GhostNext = v.GhostNext;
                }
            }
            v.GhostNext = -1;
            v.LcFrom = -1;
            v.LcTicks = 0;
            v.LcCooldown = (byte)_cfg.LaneChangeCooldownTicks;
        }

        // ------------------------------------------------------------------ lideres durante una maniobra

        /// <summary>
        /// Lideres extra que impone un cambio de carril en curso (fase 2): (1) los vehiculos que se desplazan FUERA del carril de
        /// <paramref name=i/> siguen contando como lideres suyos (fantasmas); (2) si <paramref name=i/> es quien se desplaza, el
        /// lider de su carril de origen sigue siendo lider suyo hasta acabar la maniobra (el cuerpo aun ocupa ese carril).
        /// </summary>
        private void LaneChangeLeaders(int i, ref bool has, ref float gap, ref float lv)
        {
            ref Vehicle v = ref _veh[i];
            float lenMine = _net.GetLane(new LaneId(v.Track)).Length;
            for (int g = _laneGhost[v.Track]; g >= 0; g = _veh[g].GhostNext)
            {
                if (g == i) continue;
                ref Vehicle gv = ref _veh[g];
                float lenGhost = _net.GetLane(new LaneId(gv.Track)).Length;
                float sg = lenGhost > 1e-3f ? gv.S * (lenMine / lenGhost) : gv.S;
                if (sg <= v.S) continue;
                float gg = sg - _types[gv.Type].Length - v.S;
                if (!has || gg < gap)
                {
                    gap = gg;
                    lv = gv.V;
                    has = true;
                }
            }
            if (v.LcTicks == 0 || v.LcFrom < 0) return;
            int ol = v.LcFrom;
            float lenOld = _net.GetLane(new LaneId(ol)).Length;
            float so = lenMine > 1e-3f ? v.S * (lenOld / lenMine) : v.S;
            for (int a = _laneTail[ol]; a >= 0; a = _veh[a].Ahead)
            {
                if (_veh[a].S <= so) continue;
                float ga = _veh[a].S - _types[_veh[a].Type].Length - so;
                if (!has || ga < gap)
                {
                    gap = ga;
                    lv = _veh[a].V;
                    has = true;
                }
                break;
            }
            for (int g = _laneGhost[ol]; g >= 0; g = _veh[g].GhostNext)
            {
                if (g == i) continue;
                ref Vehicle gv = ref _veh[g];
                float lenGhost = _net.GetLane(new LaneId(gv.Track)).Length;
                float sg = lenGhost > 1e-3f ? gv.S * (lenOld / lenGhost) : gv.S;
                if (sg <= so) continue;
                float gg = sg - _types[gv.Type].Length - so;
                if (!has || gg < gap)
                {
                    gap = gg;
                    lv = gv.V;
                    has = true;
                }
            }
        }

        // ------------------------------------------------------------------ carril de salida

        /// <summary>
        /// Carril del mismo segmento y sentido que <paramref name="cur"/> con una conexion al carril de llegada <paramref name="next"/>,
        /// el mas cercano a <paramref name="cur"/> (a igual distancia, el de menor ranura = mas a la derecha). -1 si ninguno.
        /// </summary>
        private int NearestDeparture(int cur, int next)
        {
            ref readonly Lane l = ref _net.GetLane(new LaneId(cur));
            ref readonly Segment sg = ref _net.GetSegment(l.Segment);
            bool fwd = l.Direction == LaneDirection.Forward;
            int lo = fwd ? 0 : sg.LanesForward;
            int hi = fwd ? sg.LanesForward : sg.LanesForward + sg.LanesBackward;
            int baseLane = cur & ~7;
            int slot = cur & 7;
            for (int dist = 0; dist < RoadNetwork.MaxLanesPerSegment; dist++)
            {
                int a = slot - dist;
                if (a >= lo && a < hi && FindConn(baseLane + a, next) >= 0) return baseLane + a;
                int b = slot + dist;
                if (dist > 0 && b >= lo && b < hi && FindConn(baseLane + b, next) >= 0) return baseLane + b;
            }
            return -1;
        }

        // ------------------------------------------------------------------ ultimo recurso

        /// <summary>
        /// Vuelve a planificar los carriles de llegada del resto de la ruta saliendo desde el carril actual sin cambiar (si este carril
        /// conecta con el siguiente segmento por algun lado). Solo con el vehiculo sin permiso (nada reservado en el paso siguiente).
        /// </summary>
        private bool RelaxPlan(int i)
        {
            ref Vehicle v = ref _veh[i];
            if ((v.Flags & VehicleFlags.HasEntryPermit) != 0 || v.Kind != TrackKind.Lane) return false;
            RouteStep[] steps = _routes.Steps;
            int off = v.RouteOffset + v.RouteCursor;
            int n = v.RouteLast - v.RouteCursor + 1;
            for (int k = 0; k < n; k++)
            {
                RouteStep st = steps[off + k];
                ref readonly Segment sg = ref _net.GetSegment(st.Segment);
                _segScratch[k] = new RouteSegment(st.Segment, st.Lane.Slot < sg.LanesForward);
            }
            uint seed = unchecked((uint)i * 2654435761u ^ (uint)_tick);
            if (!_planner.Plan(new ReadOnlySpan<RouteSegment>(_segScratch, 0, n), steps[off].Lane, v.S, true, LaneId.None, seed, _laneScratch))
                return false;
            for (int k = 1; k < n; k++) steps[off + k] = new RouteStep(steps[off + k].Segment, _laneScratch[k]);
            float lastLen = _net.GetLane(steps[off + n - 1].Lane).Length;
            if (v.TargetS > lastLen) v.TargetS = lastLen;
            LaneChangeRelaxed++;
            return true;
        }

        /// <summary>Trunca la ruta en el carril actual y la marca para re-ruta (como una ruta rota por una edicion): el vehiculo espera parado.</summary>
        private void RequestReroute(int i)
        {
            ref Vehicle v = ref _veh[i];
            if ((v.Flags & (VehicleFlags.NeedsReroute | VehicleFlags.HasEntryPermit)) != 0 || v.Kind != TrackKind.Lane) return;
            v.RouteLast = v.RouteCursor;
            v.Flags |= VehicleFlags.NeedsReroute;
            v.TargetS = v.S;
            LaneChangeRerouted++;
        }
    }
}
