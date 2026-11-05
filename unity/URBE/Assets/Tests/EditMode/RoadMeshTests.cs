using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Urbe.Game.Rendering.Roads;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Debug = UnityEngine.Debug;

namespace Urbe.Game.Tests
{
    /// <summary>M2-04b: mallas de carreteras. Cruces en T, X e Y, curvas, vértices compartidos exactos, sin huecos ni solapes, y medición de regeneración local.</summary>
    public class RoadMeshTests
    {
        private RoadTypeCatalog _catalog = null!;
        private RoadProfile[] _profiles = null!;

        [SetUp]
        public void SetUp()
        {
            _catalog = RoadTypeCatalog.CreateDefault();
            _profiles = RoadProfile.BuildAll(_catalog);
        }

        // ------------------------------------------------------------------ construcción de redes de prueba

        private RoadNetwork NewNet() => new RoadNetwork(_catalog, new NetworkConfig());

        /// <summary>Estrella: un nodo en el origen con un brazo por ángulo (grados, desde +X hacia +Z) y su tipo.</summary>
        private RoadNetwork Star(float[] anglesDeg, ushort[] types, float length, out EntityId center)
        {
            RoadNetwork net = NewNet();
            center = net.AddNodeRaw(new Vec2(0f, 0f), 0f);
            for (int i = 0; i < anglesDeg.Length; i++)
            {
                float a = anglesDeg[i] * Mathf.Deg2Rad;
                var end = net.AddNodeRaw(new Vec2(Mathf.Cos(a) * length, Mathf.Sin(a) * length), 0f);
                net.AddSegmentRaw(center, end, Bezier3.FromLine(new Vec2(0f, 0f), new Vec2(Mathf.Cos(a) * length, Mathf.Sin(a) * length)), types[i]);
            }
            return net;
        }

        private static ushort[] Same(int n, ushort t)
        {
            var a = new ushort[n];
            for (int i = 0; i < n; i++) a[i] = t;
            return a;
        }

        private MeshBuffers BuildAll(RoadNetwork net)
        {
            var all = new MeshBuffers();
            var tmp = new MeshBuffers();
            var inter = new IntersectionGeometry(_profiles);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId id = net.SegmentIdAt(i);
                if (!id.IsValid) continue;
                tmp.Clear();
                RoadPiece p = RoadPieces.FromSegment(net, id);
                RoadGeometry.BuildSegment(p, _profiles[p.RoadTypeId], tmp);
                Append(all, tmp);
            }
            for (int i = 0; i < net.NodeIndexBound; i++)
            {
                EntityId id = net.NodeIdAt(i);
                if (!id.IsValid) continue;
                tmp.Clear();
                inter.BuildNode(net, id, tmp);
                Append(all, tmp);
            }
            return all;
        }

        private static void Append(MeshBuffers dst, MeshBuffers src)
        {
            int baseIdx = dst.VertexCount;
            for (int i = 0; i < src.VertexCount; i++)
            {
                RoadVertex v = src.Vertices[i];
                dst.AddVertex(v.Position, new Vector3(v.Nx / 127f, v.Ny / 127f, v.Nz / 127f), v.Nw, v.Uv.x, v.Uv.y, v.Segment);
            }
            for (int i = 0; i < src.IndexCount; i += 3) dst.AddTriangleRaw(src.Indices[i] + baseIdx, src.Indices[i + 1] + baseIdx, src.Indices[i + 2] + baseIdx);
        }

        // ------------------------------------------------------------------ utilidades de comprobación

        private static bool IsUpFacing(MeshBuffers m, int a, int b, int c) => m.Vertices[a].Ny > 100 && m.Vertices[b].Ny > 100 && m.Vertices[c].Ny > 100;

        /// <summary>Todo triángulo con normales hacia arriba debe mirar hacia arriba de verdad (orientación visible en Unity).</summary>
        private static void AssertHorizontalTrianglesFaceUp(MeshBuffers m)
        {
            int checkedCount = 0;
            for (int i = 0; i < m.IndexCount; i += 3)
            {
                int a = m.Indices[i], b = m.Indices[i + 1], c = m.Indices[i + 2];
                if (!IsUpFacing(m, a, b, c)) continue;
                Vector3 n = Vector3.Cross(m.Vertices[b].Position - m.Vertices[a].Position, m.Vertices[c].Position - m.Vertices[a].Position);
                Assert.That(n.y, Is.GreaterThanOrEqualTo(-1e-4f), "triángulo horizontal invertido en índice " + i);
                checkedCount++;
            }
            Assert.That(checkedCount, Is.GreaterThan(0));
        }

        private static List<(Vector2 a, Vector2 b, Vector2 c)> RoadwayTriangles(MeshBuffers m)
        {
            var list = new List<(Vector2, Vector2, Vector2)>();
            for (int i = 0; i < m.IndexCount; i += 3)
            {
                int a = m.Indices[i], b = m.Indices[i + 1], c = m.Indices[i + 2];
                if (m.Vertices[a].Nw != MeshBuffers.RoadMask || m.Vertices[b].Nw != MeshBuffers.RoadMask || m.Vertices[c].Nw != MeshBuffers.RoadMask) continue;
                if (!IsUpFacing(m, a, b, c)) continue;
                list.Add((XZ(m.Vertices[a].Position), XZ(m.Vertices[b].Position), XZ(m.Vertices[c].Position)));
            }
            return list;
        }

        private static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);

        private static int Coverage(List<(Vector2 a, Vector2 b, Vector2 c)> tris, Vector2 p)
        {
            int n = 0;
            const float eps = 1e-4f;
            foreach (var t in tris)
            {
                float d1 = Sign(p, t.a, t.b), d2 = Sign(p, t.b, t.c), d3 = Sign(p, t.c, t.a);
                bool allPos = d1 > eps && d2 > eps && d3 > eps;
                bool allNeg = d1 < -eps && d2 < -eps && d3 < -eps;
                if (allPos || allNeg) n++;
            }
            return n;
        }

        private static float Sign(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);

        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>Sin solapes: ningún punto de la calzada queda cubierto por dos triángulos. Sin huecos: los puntos que están sobre algún brazo, a más de un ancho del borde, quedan cubiertos.</summary>
        private void AssertNoGapsNoOverlap(RoadNetwork net, EntityId center, float arm, float samplesRadius)
        {
            MeshBuffers m = BuildAll(net);
            var tris = RoadwayTriangles(m);
            Assert.That(tris.Count, Is.GreaterThan(4));
            var arms = new List<(Vector2 a, Vector2 b, float half, float median)>();
            ReadOnlySpan<EntityId> segs = net.GetNodeSegments(center);
            for (int k = 0; k < segs.Length; k++)
            {
                ref readonly Segment s = ref net.GetSegment(segs[k]);
                Vec2 pa = net.GetNode(s.NodeA).Position, pb = net.GetNode(s.NodeB).Position;
                arms.Add((new Vector2(pa.X, pa.Z), new Vector2(pb.X, pb.Z), _profiles[s.RoadTypeId].RoadwayHalfWidth, _profiles[s.RoadTypeId].HasMedian ? _catalog.Get(s.RoadTypeId).MedianWidth * 0.5f : -1f));
            }

            var rng = new System.Random(7);
            int holes = 0, overlaps = 0, inRoad = 0;
            for (int i = 0; i < 6000; i++)
            {
                var p = new Vector2((float)(rng.NextDouble() * 2 - 1) * samplesRadius, (float)(rng.NextDouble() * 2 - 1) * samplesRadius);
                int cov = Coverage(tris, p);
                if (cov > 1) overlaps++;
                bool onArm = false;
                bool inMedian = false;
                foreach (var a in arms)
                {
                    float d = DistToSegment(p, a.a, a.b);
                    if (d < a.half - 0.15f) onArm = true;
                    if (d < a.median + 0.2f) inMedian = true;
                }
                if (inMedian) continue; // la mediana es una isleta elevada: no lleva calzada
                if (onArm)
                {
                    inRoad++;
                    if (cov < 1) holes++;
                }
            }
            Assert.That(inRoad, Is.GreaterThan(200), "la muestra debe caer sobre la calzada");
            Assert.That(overlaps, Is.EqualTo(0), "puntos de calzada cubiertos por 2 triángulos (solape)");
            Assert.That(holes, Is.EqualTo(0), "puntos de calzada sin cubrir (hueco)");
            _ = arm;
        }

        private static bool SameBits(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);

        private static bool ContainsExact(MeshBuffers m, Vector3 p)
        {
            for (int i = 0; i < m.VertexCount; i++) if (SameBits(m.Vertices[i].Position, p)) return true;
            return false;
        }

        /// <summary>Los 6 puntos de borde de cada brazo existen, idénticos bit a bit, en la malla del segmento y en la del nodo.</summary>
        private void AssertSharedVerticesExact(RoadNetwork net, EntityId node)
        {
            var inter = new IntersectionGeometry(_profiles);
            var nodeMesh = new MeshBuffers();
            inter.BuildNode(net, node, nodeMesh);
            Assert.That(nodeMesh.VertexCount, Is.GreaterThan(0));
            ReadOnlySpan<EntityId> segs = net.GetNodeSegments(node);
            Span<Vector3> pts = stackalloc Vector3[RoadGeometry.PointCount];
            for (int k = 0; k < segs.Length; k++)
            {
                ref readonly Segment s = ref net.GetSegment(segs[k]);
                RoadPiece piece = RoadPieces.FromSegment(net, segs[k]);
                var segMesh = new MeshBuffers();
                RoadGeometry.BuildSegment(piece, _profiles[piece.RoadTypeId], segMesh);
                float t = s.NodeA == node ? s.TLaneA : s.TLaneB;
                RoadGeometry.CrossSectionAt(piece, t, _profiles[piece.RoadTypeId], pts, out _);
                foreach (int idx in new[] { RoadGeometry.PLeftOuter, RoadGeometry.PLeftCurbTop, RoadGeometry.PLeftCurbBottom,
                                            RoadGeometry.PRightCurbBottom, RoadGeometry.PRightCurbTop, RoadGeometry.PRightOuter })
                {
                    Assert.That(ContainsExact(segMesh, pts[idx]), Is.True, $"segmento {k}: falta el punto {idx} de la sección");
                    Assert.That(ContainsExact(nodeMesh, pts[idx]), Is.True, $"nodo: falta el punto {idx} del segmento {k} (no coincide exactamente)");
                }
            }
        }

        // ------------------------------------------------------------------ segmentos

        [Test]
        public void SampleRows_Recta_DosFilasHastaQuinceMetros_YFilasCadaCuarentaMetros()
        {
            var straight = new RoadPiece(Bezier3.FromLine(new Vec2(0, 0), new Vec2(30, 0)), RoadTypeIds.Street2);
            Span<float> t = stackalloc float[RoadGeometry.MaxRows];
            Span<float> s = stackalloc float[RoadGeometry.MaxRows];
            Assert.That(RoadGeometry.SampleRows(straight, t, s), Is.EqualTo(2));
            Assert.That(t[0], Is.EqualTo(0f));
            Assert.That(t[1], Is.EqualTo(1f));
            var longer = new RoadPiece(Bezier3.FromLine(new Vec2(0, 0), new Vec2(200, 0)), RoadTypeIds.Street2);
            int n = RoadGeometry.SampleRows(longer, t, s);
            Assert.That(n, Is.GreaterThanOrEqualTo(5), "200 m con filas cada 40 m como máximo");
            for (int i = 1; i < n; i++) Assert.That(s[i] - s[i - 1], Is.LessThanOrEqualTo(RoadGeometry.MaxRowSpacing + 3f));
        }

        [Test]
        public void SampleRows_Curva90Grados_UnaFilaCada8GradosComoMucho()
        {
            // Cuarto de círculo de radio 60 con la aproximación cúbica estándar.
            const float k = 0.5523f * 60f;
            var curve = new Bezier3(new Vec2(0, 0), new Vec2(0, k), new Vec2(60 - k, 60), new Vec2(60, 60));
            var piece = new RoadPiece(curve, RoadTypeIds.Street2);
            Span<float> t = stackalloc float[RoadGeometry.MaxRows];
            Span<float> s = stackalloc float[RoadGeometry.MaxRows];
            int n = RoadGeometry.SampleRows(piece, t, s);
            Assert.That(n, Is.GreaterThanOrEqualTo(10), "90° / 8° = 11 filas aproximadamente");
            Assert.That(t[0], Is.EqualTo(0f));
            Assert.That(t[n - 1], Is.EqualTo(1f));
            for (int i = 1; i < n; i++)
            {
                float cos = Vec2.Dot(curve.Tangent(t[i - 1]), curve.Tangent(t[i]));
                Assert.That(Mathf.Acos(Mathf.Clamp(cos, -1f, 1f)) * Mathf.Rad2Deg, Is.LessThan(10f), "salto angular entre filas");
            }
        }

        [Test]
        public void Segmento_TodosLosTiposDeCarretera_CaraArribaYMallaNoVacia()
        {
            for (ushort type = 0; type < _catalog.Count; type++)
            {
                var piece = new RoadPiece(Bezier3.FromLine(new Vec2(0, 0), new Vec2(80, 0)), type, flags: RoadPieceFlags.MedianCapStart | RoadPieceFlags.MedianCapEnd
                    | RoadPieceFlags.StopLineStart | RoadPieceFlags.StopLineEnd);
                var buf = new MeshBuffers();
                RoadGeometry.BuildSegment(piece, _profiles[type], buf);
                Assert.That(buf.TriangleCount, Is.GreaterThan(10), _catalog.Get(type).Key);
                AssertHorizontalTrianglesFaceUp(buf);
            }
        }

        [Test]
        public void Segmento_ArcoCerrado_SinTriangulosInvertidosNiSolapes()
        {
            const float k = 0.5523f * 40f;
            var curve = new Bezier3(new Vec2(0, 0), new Vec2(0, k), new Vec2(40 - k, 40), new Vec2(40, 40));
            var piece = new RoadPiece(curve, RoadTypeIds.Avenue4);
            var buf = new MeshBuffers();
            RoadGeometry.BuildSegment(piece, _profiles[RoadTypeIds.Avenue4], buf);
            AssertHorizontalTrianglesFaceUp(buf);
            var tris = RoadwayTriangles(buf);
            var rng = new System.Random(3);
            for (int i = 0; i < 5000; i++)
            {
                var p = new Vector2((float)rng.NextDouble() * 60f - 10f, (float)rng.NextDouble() * 60f - 10f);
                Assert.That(Coverage(tris, p), Is.LessThanOrEqualTo(1), "solape en la curva en " + p);
            }
        }

        [Test]
        public void Segmento_PosicionLateralCoincideConLosCarriles()
        {
            // Un carril de la red y la calzada deben estar centrados: el centro de un carril cae dentro de la calzada, entre los bordillos.
            RoadNetwork net = NewNet();
            var a = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = net.AddNodeRaw(new Vec2(100, 0), 0f);
            EntityId seg = net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0)), RoadTypeIds.Avenue4);
            RoadProfile pr = _profiles[RoadTypeIds.Avenue4];
            for (int slot = 0; slot < net.GetLaneCount(seg); slot++)
            {
                float off = net.GetLane(net.GetLane(seg, slot)).Offset;
                Assert.That(off, Is.GreaterThan(pr.LeftCurb).And.LessThan(pr.RightCurb));
                bool inMedian = off > pr.MedianL && off < pr.MedianR;
                Assert.That(inMedian, Is.False, "el carril no puede caer en la mediana");
            }
            // Forward (+) en la derecha de A hacia B (en +X, la derecha es −Z).
            RoadPiece piece = RoadPieces.FromSegment(net, seg);
            Span<Vector3> pts = stackalloc Vector3[RoadGeometry.PointCount];
            RoadGeometry.CrossSectionAt(piece, 0.5f, pr, pts, out _);
            Assert.That(pts[RoadGeometry.PRightCurbBottom].z, Is.LessThan(pts[RoadGeometry.PLeftCurbBottom].z), "lateral positivo = derecha de A a B");
        }

        // ------------------------------------------------------------------ cruces

        [Test]
        public void CruceEnX_SinHuecosNiSolapes_YVerticesCompartidosExactos()
        {
            RoadNetwork net = Star(new float[] { 0, 90, 180, 270 }, Same(4, RoadTypeIds.Street2), 60f, out EntityId c);
            AssertNoGapsNoOverlap(net, c, 60f, 40f);
            AssertSharedVerticesExact(net, c);
            AssertHorizontalTrianglesFaceUp(BuildAll(net));
        }

        [Test]
        public void CruceEnT_SinHuecosNiSolapes_YVerticesCompartidosExactos()
        {
            RoadNetwork net = Star(new float[] { 0, 90, 180 }, Same(3, RoadTypeIds.Street2), 60f, out EntityId c);
            AssertNoGapsNoOverlap(net, c, 60f, 40f);
            AssertSharedVerticesExact(net, c);
            AssertHorizontalTrianglesFaceUp(BuildAll(net));
        }

        [Test]
        public void CruceEnY_SinHuecosNiSolapes_YVerticesCompartidosExactos()
        {
            RoadNetwork net = Star(new float[] { 90, 210, 330 }, Same(3, RoadTypeIds.Street2), 60f, out EntityId c);
            AssertNoGapsNoOverlap(net, c, 60f, 40f);
            AssertSharedVerticesExact(net, c);
            AssertHorizontalTrianglesFaceUp(BuildAll(net));
        }

        [Test]
        public void CruceMixto_AvenidaConCalles_AnchosDistintos()
        {
            RoadNetwork net = Star(new float[] { 0, 90, 180, 270 },
                new ushort[] { RoadTypeIds.Avenue6, RoadTypeIds.Street2, RoadTypeIds.Avenue6, RoadTypeIds.OneWay2 }, 90f, out EntityId c);
            AssertNoGapsNoOverlap(net, c, 90f, 50f);
            AssertSharedVerticesExact(net, c);
            AssertHorizontalTrianglesFaceUp(BuildAll(net));
        }

        [Test]
        public void CruceAgudo_60Grados_SinSolapesNiHuecos()
        {
            // Dos brazos a 60° y el tercero opuesto: el contorno tiene una esquina estrecha y un lado recto largo.
            RoadNetwork net = Star(new float[] { 0, 60, 200 }, Same(3, RoadTypeIds.Street2), 80f, out EntityId c);
            AssertNoGapsNoOverlap(net, c, 80f, 45f);
            AssertSharedVerticesExact(net, c);
            AssertHorizontalTrianglesFaceUp(BuildAll(net));
        }

        private static bool OnArmBeyondSetback(Vector2 p, float angleDeg)
        {
            float a = angleDeg * Mathf.Deg2Rad;
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float along = Vector2.Dot(p, d), lat = p.x * d.y - p.y * d.x;
            return along > 7f && along < 79f && Mathf.Abs(lat) < 2.8f;
        }

        [Test]
        public void CruceCoNcavo_RecorteDeOrejas_DaElMismoResultadoQueElAbanico()
        {
            // Tres brazos dentro de un arco de 130°: hueco reflejo de 230° (contorno cóncavo). Se prueba con abanico y con recorte forzado.
            RoadNetwork net = Star(new float[] { 0, 65, 130 }, Same(3, RoadTypeIds.Street2), 80f, out EntityId c);
            foreach (bool force in new[] { false, true })
            {
                var inter = new IntersectionGeometry(_profiles) { ForceEarClipping = force };
                var node = new MeshBuffers();
                inter.BuildNode(net, c, node);
                if (force) Assert.That(inter.EarClipCount, Is.EqualTo(1), "el recorte de orejas se usó");
                MeshBuffers all = new MeshBuffers();
                var tmp = new MeshBuffers();
                for (int i = 0; i < net.SegmentIndexBound; i++)
                {
                    EntityId id = net.SegmentIdAt(i);
                    if (!id.IsValid) continue;
                    tmp.Clear();
                    RoadPiece p = RoadPieces.FromSegment(net, id);
                    RoadGeometry.BuildSegment(p, _profiles[p.RoadTypeId], tmp);
                    Append(all, tmp);
                }
                Append(all, node);
                AssertHorizontalTrianglesFaceUp(all);
                var tris = RoadwayTriangles(all);
                var rng = new System.Random(5);
                int overlaps = 0, holes = 0, inRoad = 0;
                for (int i = 0; i < 5000; i++)
                {
                    var pt = new Vector2((float)(rng.NextDouble() * 2 - 1) * 30f, (float)(rng.NextDouble() * 2 - 1) * 30f + 20f);
                    int cov = Coverage(tris, pt);
                    if (cov > 1) overlaps++;
                    if (OnArmBeyondSetback(pt, 0f) || OnArmBeyondSetback(pt, 65f) || OnArmBeyondSetback(pt, 130f))
                    {
                        inRoad++;
                        if (cov < 1) holes++;
                    }
                }
                Assert.That(inRoad, Is.GreaterThan(100), "force=" + force);
                Assert.That(overlaps, Is.EqualTo(0), "solapes, force=" + force);
                Assert.That(holes, Is.EqualTo(0), "huecos, force=" + force);
            }
        }

        [Test]
        public void CruceDeCinco_Brazos_CubreElCentro()
        {
            RoadNetwork net = Star(new float[] { 0, 72, 144, 216, 288 }, Same(5, RoadTypeIds.Street2), 60f, out EntityId c);
            AssertNoGapsNoOverlap(net, c, 60f, 40f);
            AssertSharedVerticesExact(net, c);
        }

        [Test]
        public void FinalDeCalle_TapaSemicircular_CoincideConElSegmento()
        {
            RoadNetwork net = NewNet();
            var a = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = net.AddNodeRaw(new Vec2(80, 0), 0f);
            net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(80, 0)), RoadTypeIds.Street2);
            var inter = new IntersectionGeometry(_profiles);
            var m = new MeshBuffers();
            inter.BuildNode(net, b, m);
            Assert.That(m.TriangleCount, Is.GreaterThanOrEqualTo(IntersectionGeometry.DeadEndSectors * 3), "6 sectores de calzada + bordillo + acera");
            AssertHorizontalTrianglesFaceUp(m);
            AssertSharedVerticesExact(net, b);
            // La tapa sobresale por detrás del extremo (hacia +X).
            Assert.That(m.BoundsMax.x, Is.GreaterThan(80f + 2.9f));
        }

        [Test]
        public void Continuacion_MismoTipoYTangenteContinua_NoGeneraMallaDeNodo()
        {
            RoadNetwork net = NewNet();
            var a = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = net.AddNodeRaw(new Vec2(50, 0), 0f);
            var c = net.AddNodeRaw(new Vec2(100, 0), 0f);
            net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(50, 0)), RoadTypeIds.Street2);
            net.AddSegmentRaw(b, c, Bezier3.FromLine(new Vec2(50, 0), new Vec2(100, 0)), RoadTypeIds.Street2);
            Assert.That(IntersectionGeometry.NeedsNodeMesh(net, b), Is.False);
            var m = new MeshBuffers();
            new IntersectionGeometry(_profiles).BuildNode(net, b, m);
            Assert.That(m.VertexCount, Is.EqualTo(0));
        }

        [Test]
        public void Continuacion_CambioDeAncho_GeneraTransicionSinHuecos()
        {
            RoadNetwork net = NewNet();
            var a = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = net.AddNodeRaw(new Vec2(50, 0), 0f);
            var c = net.AddNodeRaw(new Vec2(100, 0), 0f);
            net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(50, 0)), RoadTypeIds.Street2);
            net.AddSegmentRaw(b, c, Bezier3.FromLine(new Vec2(50, 0), new Vec2(100, 0)), RoadTypeIds.Avenue6);
            Assert.That(IntersectionGeometry.NeedsNodeMesh(net, b), Is.True);
            var m = new MeshBuffers();
            new IntersectionGeometry(_profiles).BuildNode(net, b, m);
            Assert.That(m.TriangleCount, Is.GreaterThan(4));
            AssertSharedVerticesExact(net, b);
            AssertHorizontalTrianglesFaceUp(m);
        }

        [Test]
        public void Continuacion_ConQuiebro45_EnInglete_SinNodoNiHuecosNiSolapes()
        {
            RoadNetwork net = NewNet();
            var a = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = net.AddNodeRaw(new Vec2(60, 0), 0f);
            var c = net.AddNodeRaw(new Vec2(60 + 42, 42), 0f);
            net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(60, 0)), RoadTypeIds.Street2);
            net.AddSegmentRaw(b, c, Bezier3.FromLine(new Vec2(60, 0), new Vec2(102, 42)), RoadTypeIds.Street2);
            Assert.That(IntersectionGeometry.NeedsNodeMesh(net, b), Is.False, "el inglete no necesita malla de nodo");
            MeshBuffers all = BuildAll(net);
            AssertHorizontalTrianglesFaceUp(all);
            var tris = RoadwayTriangles(all);
            Assert.That(Coverage(tris, new Vector2(61.2f, -1.6f)), Is.EqualTo(1), "cara exterior del codo");
            var rng = new System.Random(2);
            int overlaps = 0, holes = 0, inRoad = 0;
            for (int i = 0; i < 8000; i++)
            {
                var p = new Vector2((float)rng.NextDouble() * 60f + 35f, (float)rng.NextDouble() * 50f - 8f);
                int cov = Coverage(tris, p);
                if (cov > 1) overlaps++;
                // Rectángulos de las dos calles (con 0,3 m de margen para no tocar el borde ni el inglete exacto).
                bool in1 = p.x < 60f - 0.3f && Mathf.Abs(p.y) < 2.7f;
                Vector2 d = new Vector2(0.7071f, 0.7071f);
                Vector2 rel = p - new Vector2(60f, 0f);
                float along = Vector2.Dot(rel, d), lat = rel.x * d.y - rel.y * d.x;
                bool in2 = along > 0.3f && Mathf.Abs(lat) < 2.7f && along < 55f;
                if (in1 || in2) { inRoad++; if (cov < 1) holes++; }
            }
            Assert.That(inRoad, Is.GreaterThan(300));
            Assert.That(overlaps, Is.EqualTo(0), "solapes en el codo");
            Assert.That(holes, Is.EqualTo(0), "huecos en el codo");
        }

        [Test]
        public void Continuacion_Quiebro90Y_Avenida_EnInglete_YQuiebroMuyCerrado_UsaNodoGenerico()
        {
            RoadNetwork net = NewNet();
            var a = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = net.AddNodeRaw(new Vec2(100, 0), 0f);
            var c = net.AddNodeRaw(new Vec2(100, 100), 0f);
            net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0)), RoadTypeIds.Avenue4);
            net.AddSegmentRaw(b, c, Bezier3.FromLine(new Vec2(100, 0), new Vec2(100, 100)), RoadTypeIds.Avenue4);
            Assert.That(IntersectionGeometry.NeedsNodeMesh(net, b), Is.False);
            MeshBuffers all = BuildAll(net);
            AssertHorizontalTrianglesFaceUp(all);
            var tris = RoadwayTriangles(all);
            var rng = new System.Random(9);
            for (int i = 0; i < 6000; i++)
                Assert.That(Coverage(tris, new Vector2((float)rng.NextDouble() * 120f - 10f, (float)rng.NextDouble() * 120f - 10f)), Is.LessThanOrEqualTo(1));

            // Giro de 140° (ángulo de 40° entre salidas): supera el inglete y cae al nodo genérico.
            RoadNetwork net2 = NewNet();
            var a2 = net2.AddNodeRaw(new Vec2(0, 0), 0f);
            var b2 = net2.AddNodeRaw(new Vec2(100, 0), 0f);
            var c2 = net2.AddNodeRaw(new Vec2(100 - 77f, 64f), 0f);
            net2.AddSegmentRaw(a2, b2, Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0)), RoadTypeIds.Street2);
            net2.AddSegmentRaw(b2, c2, Bezier3.FromLine(new Vec2(100, 0), new Vec2(23, 64)), RoadTypeIds.Street2);
            Assert.That(IntersectionGeometry.NeedsNodeMesh(net2, b2), Is.True);
            var m = new MeshBuffers();
            new IntersectionGeometry(_profiles).BuildNode(net2, b2, m);
            Assert.That(m.TriangleCount, Is.GreaterThan(4));
        }

        // ------------------------------------------------------------------ elevación y estructura

        [Test]
        public void Elevacion_InterpoladaLinealmenteEntreNodos()
        {
            var piece = new RoadPiece(Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0)), RoadTypeIds.Street2, elevA: 0f, elevB: 6f);
            Span<Vector3> pts = stackalloc Vector3[RoadGeometry.PointCount];
            RoadGeometry.CrossSectionAt(piece, 0.5f, _profiles[RoadTypeIds.Street2], pts, out SectionFrame f);
            Assert.That(pts[RoadGeometry.PLeftCurbBottom].y, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(pts[RoadGeometry.PLeftOuter].y, Is.EqualTo(3f + RoadProfile.CurbHeight).Within(1e-5f));
            Assert.That(f.Slope, Is.EqualTo(0.06f).Within(1e-4f));
        }

        [Test]
        public void LineaDeDeteccion_SoloEnCarrilesQueEntranEnElCruce()
        {
            // Calle de doble sentido: el extremo B recibe los carriles Forward (derecha de A a B, es decir z negativa).
            var piece = new RoadPiece(Bezier3.FromLine(new Vec2(0, 0), new Vec2(60, 0)), RoadTypeIds.Street2, flags: RoadPieceFlags.StopLineEnd);
            var buf = new MeshBuffers();
            RoadGeometry.BuildSegment(piece, _profiles[RoadTypeIds.Street2], buf);
            var paint = new List<Vector3>();
            for (int i = 0; i < buf.VertexCount; i++)
                if (buf.Vertices[i].Position.y > 0.01f && buf.Vertices[i].Position.y < 0.05f) paint.Add(buf.Vertices[i].Position);
            Assert.That(paint.Count, Is.EqualTo(4));
            foreach (Vector3 p in paint)
            {
                Assert.That(p.z, Is.LessThan(0f), "solo la mitad derecha");
                Assert.That(p.x, Is.GreaterThan(58f).And.LessThan(59.5f), "justo antes del cruce");
            }
        }

        [Test]
        public void Cierre_DeMediana_SoloConMedianaYConBandera()
        {
            var withMed = new RoadPiece(Bezier3.FromLine(new Vec2(0, 0), new Vec2(60, 0)), RoadTypeIds.Avenue4);
            var capped = withMed;
            capped.Flags = RoadPieceFlags.MedianCapStart | RoadPieceFlags.MedianCapEnd;
            var a = new MeshBuffers();
            var b = new MeshBuffers();
            RoadGeometry.BuildSegment(withMed, _profiles[RoadTypeIds.Avenue4], a);
            RoadGeometry.BuildSegment(capped, _profiles[RoadTypeIds.Avenue4], b);
            Assert.That(b.TriangleCount, Is.EqualTo(a.TriangleCount + 4));
            var street = new RoadPiece(Bezier3.FromLine(new Vec2(0, 0), new Vec2(60, 0)), RoadTypeIds.Street2);
            var s1 = new MeshBuffers();
            var s2 = new MeshBuffers();
            RoadGeometry.BuildSegment(street, _profiles[RoadTypeIds.Street2], s1);
            street.Flags = RoadPieceFlags.MedianCapStart | RoadPieceFlags.MedianCapEnd;
            RoadGeometry.BuildSegment(street, _profiles[RoadTypeIds.Street2], s2);
            Assert.That(s2.TriangleCount, Is.EqualTo(s1.TriangleCount));
        }

        // ------------------------------------------------------------------ rendimiento y regeneración local

        private static RoadNetwork BigGrid(RoadTypeCatalog cat, int cells, float spacing)
        {
            var net = new RoadNetwork(cat, new NetworkConfig());
            int m = cells + 1;
            var nodes = new EntityId[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    nodes[i, j] = net.AddNodeRaw(new Vec2(i * spacing, j * spacing), 0f);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    if (i + 1 < m) net.AddSegmentRaw(nodes[i, j], nodes[i + 1, j], Bezier3.FromLine(new Vec2(i * spacing, j * spacing), new Vec2((i + 1) * spacing, j * spacing)), j % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                    if (j + 1 < m) net.AddSegmentRaw(nodes[i, j], nodes[i, j + 1], Bezier3.FromLine(new Vec2(i * spacing, j * spacing), new Vec2(i * spacing, (j + 1) * spacing)), i % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                }
            return net;
        }

        [Test]
        public void RegeneracionLocal_MenosDe5ms_EnUnaRedGrande()
        {
            RoadNetwork net = BigGrid(_catalog, 20, 100f); // 21x21 nodos, 840 segmentos
            var go = new GameObject("test-roads");
            try
            {
                var r = go.AddComponent<RoadChunkRenderer>();
                r.enabled = false; // sin host: se enlaza a mano
                var sw = Stopwatch.StartNew();
                r.Attach(net);
                r.Flush();
                sw.Stop();
                Debug.Log($"[M2-04b] build inicial 840 segmentos: {sw.ElapsedMilliseconds} ms, {r.TotalVertices} vértices, {r.TotalTriangles} triángulos, {r.ChunkCount} chunks");

                // Edición local: añadir y quitar una calle corta que empieza en un nodo de la rejilla y acaba en una intersección libre dentro de la manzana.
                var times = new List<double>();
                var pieces = new List<int>();
                for (int k = 0; k < 40; k++)
                {
                    float ox = 500f + (k % 5) * 100f, oz = 500f + (k / 5 % 5) * 100f;
                    var req = SegmentRequest.Straight(new Vec2(ox, oz), new Vec2(ox + 50f, oz + 50f), RoadTypeIds.Street2);
                    Assert.That(net.CanAddSegment(req), Is.EqualTo(ValidationResult.Ok), "edición " + k);
                    var edit = new NetworkEdit();
                    Assert.That(net.AddSegment(req, edit), Is.True);
                    var sw2 = Stopwatch.StartNew();
                    r.Process(float.MaxValue);
                    r.Process(float.MaxValue);
                    sw2.Stop();
                    times.Add(sw2.Elapsed.TotalMilliseconds);
                    pieces.Add(r.LastBatchPieces);
                    // deshacer: quita lo creado
                    for (int i = edit.CreatedSegments.Length - 1; i >= 0; i--) net.RemoveSegment(edit.CreatedSegments[i], null);
                    r.Process(float.MaxValue);
                    r.Process(float.MaxValue);
                }
                times.Sort();
                double median = times[times.Count / 2], max = times[times.Count - 1];
                Debug.Log($"[M2-04b] regeneración local (añadir calle en rejilla de 840 segmentos, 40 ediciones): mediana {median:F2} ms, máximo {max:F2} ms");
                // El primer par de ediciones paga JIT en el editor; la aceptación se mide sobre la mediana y el percentil 90.
                Assert.That(times[(int)(times.Count * 0.9)], Is.LessThan(5.0), "regeneración local < 5 ms (percentil 90)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Chunks_UnaEdicionSoloReconstruyeSuVecindad()
        {
            RoadNetwork net = BigGrid(_catalog, 20, 100f);
            var go = new GameObject("test-roads2");
            try
            {
                var r = go.AddComponent<RoadChunkRenderer>();
                r.enabled = false;
                r.Attach(net);
                r.Flush();
                var edit = new NetworkEdit();
                var req = SegmentRequest.Straight(new Vec2(1000f, 1000f), new Vec2(1050f, 1050f), RoadTypeIds.Street2);
                Assert.That(net.AddSegment(req, edit), Is.True);
                r.Process(float.MaxValue);
                // Un segmento nuevo + su nodo libre + el nodo de rejilla + sus 4 segmentos incidentes + el nodo de la otra esquina: pocas piezas, no cientos.
                Assert.That(r.LastBatchPieces, Is.LessThan(30));
                Assert.That(r.LastBatchPieces, Is.GreaterThan(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Chunks_DemolerQuitaLaMalla()
        {
            RoadNetwork net = NewNet();
            var a = net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = net.AddNodeRaw(new Vec2(80, 0), 0f);
            EntityId seg = net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(80, 0)), RoadTypeIds.Street2);
            var go = new GameObject("test-roads3");
            try
            {
                var r = go.AddComponent<RoadChunkRenderer>();
                r.enabled = false;
                r.Attach(net);
                r.Flush();
                int before = r.TotalVertices;
                Assert.That(before, Is.GreaterThan(0));
                net.RemoveSegment(seg, null);
                r.Flush();
                Assert.That(r.TotalVertices, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RedDe20000Segmentos_ConstruyeYMideMemoria()
        {
            RoadNetwork net = BigGrid(_catalog, 100, 38f); // 101x101 nodos = 20.200 segmentos
            var go = new GameObject("test-roads4");
            try
            {
                var r = go.AddComponent<RoadChunkRenderer>();
                r.enabled = false;
                var sw = Stopwatch.StartNew();
                r.Attach(net);
                r.Flush();
                sw.Stop();
                Debug.Log($"[M2-04b] red de {net.SegmentCount} segmentos: build completo {sw.ElapsedMilliseconds} ms, {r.TotalVertices} vértices ({r.TotalVertices * 28L / (1024 * 1024)} MB de vértices), {r.TotalTriangles} triángulos, {r.ChunkCount} chunks");
                Assert.That(net.SegmentCount, Is.GreaterThan(20000));
                Assert.That(r.ChunkCount, Is.GreaterThan(30));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
