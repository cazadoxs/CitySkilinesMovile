using System;
using System.Collections.Generic;
using System.Globalization;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Runner
{
    /// <summary>
    /// Instrumentacion de capacidad (M1-18). <c>--diagnose junction</c> satura una X aislada y mide el caudal por carril;
    /// <c>--diagnose network</c> clasifica, en un escenario, por que esta parado cada vehiculo.
    /// </summary>
    public static class Diagnose
    {
        private static string F(double v, string f = "0.000") => v.ToString(f, CultureInfo.InvariantCulture);

        public static int Run(string mode, string[] opts)
        {
            if (mode == "junction") return Junction(opts);
            if (mode == "network") return Network(opts);
            if (mode == "corridor") return Corridor(opts);
            Console.Error.WriteLine("--diagnose: junction | network | corridor");
            return 2;
        }

        private static string Opt(string[] o, string key, string def)
        {
            foreach (string s in o) if (s.StartsWith(key + "=", StringComparison.Ordinal)) return s.Substring(key.Length + 1);
            return def;
        }

        private static ushort TypeOf(string s)
        {
            switch (s)
            {
                case "street": return RoadTypeIds.Street2;
                case "avenue4": return RoadTypeIds.Avenue4;
                case "oneway": return RoadTypeIds.OneWay2;
                case "avenue6": return RoadTypeIds.Avenue6;
                default: throw new ArgumentException("tipo: street | avenue4 | avenue6");
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // X aislada saturada: cola en cada carril de entrada, se repone al instante.
        // ---------------------------------------------------------------------------------------------------------------------
        private static int Junction(string[] o)
        {
            ushort ew = TypeOf(Opt(o, "ew", "avenue4"));
            ushort ns = TypeOf(Opt(o, "ns", "avenue4"));
            string control = Opt(o, "control", "auto");
            bool straight = Opt(o, "turns", "straight") == "straight";
            float seconds = float.Parse(Opt(o, "seconds", "400"), CultureInfo.InvariantCulture);
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            EntityId center = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var arms = new EntityId[4];
            var dirs = new[] { (1f, 0f), (0f, 1f), (-1f, 0f), (0f, -1f) };
            for (int a = 0; a < 4; a++)
            {
                EntityId outer = net.AddNodeRaw(new Vec2(dirs[a].Item1 * 200f, dirs[a].Item2 * 200f), 0f);
                arms[a] = net.AddSegmentRaw(outer, center, Bezier3.FromLine(net.GetNode(outer).Position, new Vec2(0, 0)), a % 2 == 0 ? ew : ns);
            }
            if (control == "priority") net.SetNodeControl(center, JunctionControlIds.Priority);
            else if (control == "signal") net.SetNodeControl(center, JunctionControlIds.Signal);
            var ctrl = new JunctionController(net);
            var sys = new VehicleSystem(net, ctrl, 1024, 1, new VehicleConfig { DriverVariation = false });
            VehicleType car = sys.GetVehicleType(VehicleTypeId.Car);
            var rng = new Lcg(5);
            int ticks = (int)Math.Round(seconds / sys.Config.Dt);
            int warm = (int)Math.Round(60f / sys.Config.Dt);
            var prevKind = new TrackKind[1024];
            int entered = 0;
            long headTicksStopped = 0, headTicks = 0;
            for (int t = 0; t < ticks; t++)
            {
                for (int a = 0; a < 4; a++)
                {
                    int lanes = net.GetSegment(arms[a]).LanesForward;
                    for (int k = 0; k < lanes; k++)
                    {
                        LaneId lane = new LaneId(arms[a].Index, k);
                        int tail = sys.LaneTail(lane);
                        if (tail >= 0 && sys.GetVehicle(tail).S - car.Length < car.S0 + 0.5f) continue;
                        var conns = net.GetConnectionsFrom(lane);
                        if (conns.Length == 0) continue;
                        int pick = -1;
                        if (straight)
                        {
                            for (int i = 0; i < conns.Length; i++) if (conns[i].Turn == TurnType.Straight) { pick = i; break; }
                            if (pick < 0) for (int i = 0; i < conns.Length; i++) if (conns[i].Turn != TurnType.UTurn) { pick = i; break; }
                        }
                        else
                        {
                            int n = 0;
                            for (int i = 0; i < conns.Length; i++) if (conns[i].Turn != TurnType.UTurn) n++;
                            int r = (int)(rng.Next() % (uint)Math.Max(1, n));
                            for (int i = 0; i < conns.Length; i++) if (conns[i].Turn != TurnType.UTurn && r-- == 0) { pick = i; break; }
                        }
                        if (pick < 0) continue;
                        LaneId exit = conns[pick].To;
                        sys.TrySpawn(VehicleTypeId.Car, new[] { lane, exit }, 0f, net.GetLane(exit).Length - 5f, net.GetLane(lane).SpeedLimit, out _);
                    }
                }
                sys.Update();
                for (int i = 0; i < sys.HighWater; i++)
                {
                    ref readonly Vehicle v = ref sys.GetVehicle(i);
                    if (!v.IsAlive) continue;
                    if (v.Kind == TrackKind.Lane && v.RouteCursor >= 1 && v.S > 30f) { sys.Despawn(sys.IdOf(i)); prevKind[i] = TrackKind.Lane; continue; }
                    if (v.Kind == TrackKind.Connection && prevKind[i] == TrackKind.Lane && t >= warm) entered++;
                    prevKind[i] = v.Kind;
                    if (t >= warm && v.Kind == TrackKind.Lane && v.RouteCursor == 0 && v.Ahead < 0)
                    {
                        float d = net.GetLane(new LaneId(v.Track)).Length - v.S;
                        if (d < 12f) { headTicks++; if (v.V < 0.3f) headTicksStopped++; }
                    }
                }
            }
            double secs = (ticks - warm) * (double)sys.Config.Dt;
            int laneCount = 0;
            foreach (EntityId a in arms) laneCount += net.GetSegment(a).LanesForward;
            JunctionStats st = ctrl.Stats;
            Console.WriteLine("X " + Opt(o, "ew", "avenue4") + " x " + Opt(o, "ns", "avenue4") + ", control " + ctrl.GetControl(center) + ", " + (straight ? "solo rectos" : "todos los giros")
                + ": " + F(entered / secs) + " veh/s total, " + F(entered / secs / laneCount) + " veh/s por carril (" + laneCount + " carriles)");
            Console.WriteLine("  peticiones " + st.Requests + " concedidas " + st.Granted + " semaforo " + st.DeniedSignal + " stop " + st.DeniedStop + " reclamada " + st.DeniedClaimed
                + " conflicto " + st.DeniedConflict + " salida-llena " + st.DeniedExitFull + " hueco " + st.DeniedGap + "; cabeza parada " + F(100.0 * headTicksStopped / Math.Max(1, headTicks), "0.0") + " % del tiempo");
            return 0;
        }

        /// <summary>Corredor recto de segmentos consecutivos con la cabecera saturada; mide el caudal que cruza cada nodo intermedio.</summary>
        private static int Corridor(string[] o)
        {
            string[] names = Opt(o, "types", "avenue4,street,avenue4").Split(',');
            string[] lens = Opt(o, "lens", "200,190,200").Split(',');
            float seconds = float.Parse(Opt(o, "seconds", "400"), CultureInfo.InvariantCulture);
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            var nodes = new List<EntityId>();
            var segs = new List<EntityId>();
            float x = 0;
            nodes.Add(net.AddNodeRaw(new Vec2(0, 0), 0f));
            for (int i = 0; i < names.Length; i++)
            {
                x += float.Parse(lens[i], CultureInfo.InvariantCulture);
                nodes.Add(net.AddNodeRaw(new Vec2(x, 0), 0f));
                segs.Add(net.AddSegmentRaw(nodes[i], nodes[i + 1], Bezier3.FromLine(net.GetNode(nodes[i]).Position, net.GetNode(nodes[i + 1]).Position), TypeOf(names[i])));
            }
            var ctrl = new JunctionController(net);
            IJunctionGate gate = Opt(o, "gate", "ctrl") == "gap" ? (IJunctionGate)new GapOnlyGate(net) : ctrl;
            var sys = new VehicleSystem(net, gate, 1024, 1, new VehicleConfig { DriverVariation = Opt(o, "variation", "0") == "1" });
            var route = new RouteSegment[segs.Count];
            for (int i = 0; i < segs.Count; i++) route[i] = new RouteSegment(segs[i], true);
            int ticks = (int)Math.Round(seconds / sys.Config.Dt), warm = (int)Math.Round(60f / sys.Config.Dt);
            var last = new int[1024];
            for (int i = 0; i < last.Length; i++) last[i] = -1;
            var passes = new int[segs.Count];
            float lastLen = float.Parse(lens[segs.Count - 1], CultureInfo.InvariantCulture) - 20f;
            for (int t = 0; t < ticks; t++)
            {
                for (int k = 0; k < 4; k++) sys.TrySpawn(VehicleTypeId.Car, route, 0f, lastLen - 5f, net.GetLane(new LaneId(segs[0].Index, 0)).SpeedLimit, out _, (uint)(t * 4 + k + 1));
                sys.Update();
                if (Opt(o, "trace", "0") == "1" && t >= 1500 && t < 1800 && t % 5 == 0)
                {
                    string line = "t=" + F(t * 0.1, "0.0");
                    for (int ln = 0; ln < 3; ln++)
                    {
                        var lane = new LaneId(segs[0].Index, ln);
                        if (ln >= net.GetSegment(segs[0]).LanesForward + 1) break;
                        int head = sys.LaneHead(lane);
                        if (head >= 0)
                        {
                            ref readonly Vehicle h = ref sys.GetVehicle(head);
                            line += " | L" + ln + " d=" + F(net.GetLane(lane).Length - h.S, "0.0") + " v=" + F(h.V, "0.0") + " " + ctrl.GetLastDecision(head) + ((h.Flags & VehicleFlags.HasEntryPermit) != 0 ? "+P" : "");
                        }
                    }
                    var ex = new LaneId(segs[1].Index, 0);
                    int tl = sys.LaneTail(ex);
                    if (tl >= 0) { ref readonly Vehicle e = ref sys.GetVehicle(tl); line += " | exitTail rear=" + F(e.S - 4.5f, "0.0") + " v=" + F(e.V, "0.0"); }
                    Console.WriteLine(line);
                }
                for (int i = 0; i < sys.HighWater; i++)
                {
                    ref readonly Vehicle v = ref sys.GetVehicle(i);
                    if (!v.IsAlive) { last[i] = -1; continue; }
                    if (v.Kind != TrackKind.Lane) continue;
                    int seg = new LaneId(v.Track).SegmentIndex;
                    if (v.RouteCursor == segs.Count - 1 && v.S > 30f) { sys.Despawn(sys.IdOf(i)); last[i] = -1; continue; }
                    if (last[i] != seg)
                    {
                        if (t >= warm) for (int k = 1; k < segs.Count; k++) if (segs[k].Index == seg) passes[k]++;
                        last[i] = seg;
                    }
                }
            }
            double secs = (ticks - warm) * (double)sys.Config.Dt;
            string r = "corredor " + Opt(o, "types", "avenue4,street,avenue4") + ": entradas por nodo (veh/s):";
            for (int k = 1; k < segs.Count; k++) r += " " + F(passes[k] / secs) + " (" + names[k - 1] + "->" + names[k] + ")";
            Console.WriteLine(r);
            JunctionStats st = ctrl.Stats;
            Console.WriteLine("  peticiones " + st.Requests + " concedidas " + st.Granted + " salida-llena " + st.DeniedExitFull + " conflicto " + st.DeniedConflict + " reclamada " + st.DeniedClaimed);
            return 0;
        }

        private sealed class Lcg
        {
            private ulong _s;
            public Lcg(ulong seed) { _s = seed * 6364136223846793005UL + 1442695040888963407UL; }
            public uint Next() { _s = _s * 6364136223846793005UL + 1442695040888963407UL; return (uint)(_s >> 33); }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // Red completa: por que esta parado cada vehiculo
        // ---------------------------------------------------------------------------------------------------------------------
        private static int Network(string[] o)
        {
            ulong seed = ulong.Parse(Opt(o, "seed", "7"), CultureInfo.InvariantCulture);
            int vehicles = int.Parse(Opt(o, "vehicles", "2000"), CultureInfo.InvariantCulture);
            float tps = float.Parse(Opt(o, "tps", "4"), CultureInfo.InvariantCulture);
            float seconds = float.Parse(Opt(o, "seconds", "300"), CultureInfo.InvariantCulture);
            float every = float.Parse(Opt(o, "every", "60"), CultureInfo.InvariantCulture);
            string scn = Opt(o, "scenario", "bottleneck");
            Scenario sc = scn == "bottleneck" ? Scenarios.Bottleneck(seed, vehicles, tps) : Scenarios.Create(scn, seed, vehicles, tps);
            var ctrl = (JunctionController)sc.Sim.Gate;
            float dt = sc.Vehicles.Config.Dt;
            int total = (int)Math.Round(seconds / dt), step = (int)Math.Round(every / dt);
            JunctionStats st = ctrl.Stats;
            long g0 = 0, r0 = 0;
            float openAt = float.Parse(Opt(o, "open", "-1"), CultureInfo.InvariantCulture);
            int openTick = openAt >= 0 ? (int)Math.Round(openAt / dt) : -1;
            float tripsAfter = float.Parse(Opt(o, "tps-after", Opt(o, "tps", "4")), CultureInfo.InvariantCulture);
            var prevSeg = new int[vehicles * 2 + 8];
            var cross = new Dictionary<int, int>();
            for (int t = 1; t <= total; t++)
            {
                if (t == openTick && sc.Relief.HasValue) { sc.Sim.Commands.Enqueue(new Urbe.Sim.Commands.BuildRoadCommand(sc.Relief.Value)); sc.Sim.Trips.Config.TripsPerSecond = tripsAfter; }
                sc.Step();
                if (openTick >= 0 && t > openTick && t % 50 == 0 && Opt(o, "reroute", "1") == "1")
                {
                    var vr = sc.Vehicles;
                    for (int i = 0; i < vr.HighWater; i++)
                    {
                        ref readonly Vehicle v = ref vr.GetVehicle(i);
                        if (!v.IsAlive || v.V > 0.3f || v.WaitTicks < 30) continue;
                        vr.MarkForReroute(vr.IdOf(i));
                    }
                }
                if (scn == "bottleneck" && (t % 5 == 0 || t > 0))
                {
                    var vs0 = sc.Vehicles;
                    for (int i = 0; i < vs0.HighWater; i++)
                    {
                        ref readonly Vehicle v = ref vs0.GetVehicle(i);
                        if (!v.IsAlive) { if (i < prevSeg.Length) prevSeg[i] = -1; continue; }
                        if (v.Kind != TrackKind.Lane) continue;
                        int seg = new LaneId(v.Track).SegmentIndex;
                        if (i < prevSeg.Length && prevSeg[i] != seg)
                        {
                            Vec2 m = sc.Network.GetSegment(sc.Network.SegmentIdAt(seg)).Curve.Evaluate(0.5f);
                            if (Math.Abs(m.X - 1055f) < 8f) { int key = (int)Math.Round(m.Z); cross.TryGetValue(key, out int cc); cross[key] = cc + 1; }
                        }
                        if (i < prevSeg.Length) prevSeg[i] = seg;
                    }
                }
                if (t % step != 0) continue;
                var vs = sc.Vehicles;
                if (cross.Count > 0)
                {
                    string cs = "  cruces del rio (veh/s):";
                    foreach (var kv in cross) cs += " z=" + kv.Key + ": " + F(kv.Value / (double)every, "0.00");
                    Console.WriteLine(cs);
                    cross.Clear();
                }
                int alive = 0, moving = 0, inConn = 0, follower = 0, headFree = 0;
                var head = new int[8];
                for (int i = 0; i < vs.HighWater; i++)
                {
                    ref readonly Vehicle v = ref vs.GetVehicle(i);
                    if (!v.IsAlive) continue;
                    alive++;
                    if (v.V >= 0.5f) { moving++; continue; }
                    if (v.Kind == TrackKind.Connection) { inConn++; continue; }
                    if (v.Ahead >= 0) { follower++; continue; }
                    GrantDecision d = ctrl.GetLastDecision(i);
                    if ((v.Flags & VehicleFlags.HasEntryPermit) != 0) head[1]++;
                    else if (d == GrantDecision.None) headFree++;
                    else head[(int)d <= 7 ? (int)d : 0]++;
                }
                if (Opt(o, "hot", "0") == "1")
                {
                    var cnt = new Dictionary<int, int>();
                    var heads = new Dictionary<int, string>();
                    for (int i = 0; i < vs.HighWater; i++)
                    {
                        ref readonly Vehicle v = ref vs.GetVehicle(i);
                        if (!v.IsAlive || v.V >= 0.5f || v.Kind != TrackKind.Lane) continue;
                        int seg = new LaneId(v.Track).SegmentIndex;
                        cnt.TryGetValue(seg, out int c); cnt[seg] = c + 1;
                        if (v.Ahead < 0) heads[v.Track] = ctrl.GetLastDecision(i).ToString() + ((v.Flags & VehicleFlags.HasEntryPermit) != 0 ? "+permit" : "");
                    }
                    {
                        var perLane = new int[8];
                        for (int i = 0; i < vs.HighWater; i++)
                        {
                            ref readonly Vehicle v = ref vs.GetVehicle(i);
                            if (!v.IsAlive || v.Kind != TrackKind.Lane) continue;
                            var ln = new LaneId(v.Track);
                            Vec2 mm = sc.Network.GetSegment(sc.Network.SegmentIdAt(ln.SegmentIndex)).Curve.Evaluate(0.5f);
                            if (Math.Abs(mm.X - 920) < 1 && Math.Abs(mm.Z - 240) < 1) perLane[ln.Slot]++;
                        }
                        Console.WriteLine("    ultimo tramo antes del puente: vehiculos por carril " + string.Join(",", perLane));
                    }
                    var list = new List<KeyValuePair<int, int>>(cnt);
                    list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));
                    for (int k = 0; k < list.Count; k++)
                    {
                        { Vec2 mm = sc.Network.GetSegment(sc.Network.SegmentIdAt(list[k].Key)).Curve.Evaluate(0.5f); if (!(mm.X > 900 && mm.X < 1500 && Math.Abs(mm.Z - 400) < 45)) continue; }
                        EntityId sid = sc.Network.SegmentIdAt(list[k].Key);
                        var sg = sc.Network.GetSegment(sid);
                        Vec2 mid = sg.Curve.Evaluate(0.5f);
                        string hs = "";
                        for (int ln = 0; ln < sg.LanesForward + sg.LanesBackward; ln++) { if (heads.TryGetValue(new LaneId(list[k].Key, ln).Value, out string? h)) hs += " L" + ln + ":" + h; }
                        Console.WriteLine("    seg " + list[k].Key + " tipo " + sg.RoadTypeId + " en (" + F(mid.X, "0") + "," + F(mid.Z, "0") + ") parados " + list[k].Value + hs);
                    }
                }
                if (Opt(o, "dump", "0") == "1")
                {
                    for (int i = 0; i < vs.HighWater; i++)
                    {
                        ref readonly Vehicle v = ref vs.GetVehicle(i);
                        if (!v.IsAlive) continue;
                        Vec2 mm;
                        string where;
                        if (v.Kind == TrackKind.Lane) { var ln = new LaneId(v.Track); mm = sc.Network.GetSegment(sc.Network.SegmentIdAt(ln.SegmentIndex)).Curve.Evaluate(0.5f); where = "lane seg " + ln.SegmentIndex + " L" + ln.Slot + " S=" + F(v.S, "0.0") + "/" + F(sc.Network.GetLane(ln).Length, "0"); }
                        else { var cn = sc.Network.GetConnection(v.Track); mm = sc.Network.GetNode(cn.Node).Position; where = "conn node(" + F(mm.X, "0") + "," + F(mm.Z, "0") + ") S=" + F(v.S, "0.0") + "/" + F(cn.Length, "0.0"); }
                        if (mm.X > 780 && mm.X < 960 && Math.Abs(mm.Z - 400) < 90)
                            Console.WriteLine("      v" + i + " " + where + " V=" + F(v.V, "0.0") + " ahead=" + v.Ahead + " " + ctrl.GetLastDecision(i) + ((v.Flags & VehicleFlags.HasEntryPermit) != 0 ? "+P" : "") + " wait=" + v.WaitTicks);
                    }
                }
                if (Opt(o, "ticks", "") != "")
                {
                    var jn2 = ctrl.GetJunction(sc.Network.GetConnectionsFrom(new LaneId(vs.GetVehicle(9).Track))[0].Node)!;
                    for (int k = 0; k < 40; k++)
                    {
                        sc.Step();
                        string ln2 = "  tick+" + k;
                        for (int c = 0; c < jn2.Gates.Length; c++) if (jn2.Gates[c].Permits > 0 || jn2.Gates[c].Occupants > 0) ln2 += " c" + c + "(p" + jn2.Gates[c].Permits + ",o" + jn2.Gates[c].Occupants + ")";
                        foreach (int vi in new[] { 9, 52, 1122, 1103 })
                        {
                            ref readonly Vehicle xv = ref vs.GetVehicle(vi);
                            ln2 += " | v" + vi + " " + (xv.IsAlive ? ctrl.GetLastDecision(vi) + " S=" + F(xv.S, "0.0") + " V=" + F(xv.V, "0.0") + (xv.Kind == TrackKind.Connection ? " conn" : "") : "dead");
                        }
                        Console.WriteLine(ln2);
                    }
                }
                if (Opt(o, "node", "") != "")
                {
                    int vid = int.Parse(Opt(o, "node", "0"), CultureInfo.InvariantCulture);
                    ref readonly Vehicle hv = ref vs.GetVehicle(vid);
                    var hl = new LaneId(hv.Track);
                    var hc = sc.Network.GetConnectionsFrom(hl);
                    if (hc.Length > 0)
                    {
                        EntityId nid = hc[0].Node;
                        var jn = ctrl.GetJunction(nid)!;
                        Console.WriteLine("    nodo " + nid.Index + " control " + jn.Control + " conexiones " + jn.Gates.Length);
                        int baseC = sc.Network.GetNode(nid).ConnFirst;
                        for (int k = 0; k < sc.Network.GetNode(nid).ConnCount; k++)
                        {
                            ref readonly ConnGate g = ref jn.Gates[k];
                            ref readonly LaneConnection lc = ref sc.Network.GetConnection(baseC + k);
                            string line = "      c" + k + " " + lc.From.SegmentIndex + "L" + lc.From.Slot + "->" + lc.To.SegmentIndex + "L" + lc.To.Slot + " " + lc.Turn + " permits " + g.Permits + " occ " + g.Occupants + " rank " + g.Rank + " rule " + g.Rule;
                            if (g.Occupants > 0 || g.Permits > 0) line += " last=" + g.LastEntrant + " first=" + g.FirstOccupant;
                            Console.WriteLine(line);
                        }
                    }
                }
                long gr = st.Granted - g0, rq = st.Requests - r0;
                g0 = st.Granted; r0 = st.Requests;
                Console.WriteLine("t=" + F(t * dt, "0") + " s: vivos " + alive + " en movimiento " + moving + " | parados: seguidores " + follower + ", en cruce " + inConn
                    + ", cabeza con permiso " + head[1] + ", sin decision " + headFree + ", cabeza denegada: semaforo " + head[(int)GrantDecision.DeniedSignal] + " stop " + head[(int)GrantDecision.DeniedStop]
                    + " reclamada " + head[(int)GrantDecision.DeniedClaimed] + " conflicto " + head[(int)GrantDecision.DeniedConflict] + " salida-llena " + head[(int)GrantDecision.DeniedExitFull]
                    + " hueco " + head[(int)GrantDecision.DeniedGap] + " | concesiones/s " + F(gr / (double)every, "0.00") + " peticiones/s " + F(rq / (double)every, "0") + " flujo " + F(sc.Sim.Metrics.CityFlowPercent, "0.0") + " %");
            }
            return 0;
        }
    }
}
