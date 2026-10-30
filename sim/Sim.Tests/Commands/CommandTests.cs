using System.Collections.Generic;
using Urbe.Sim.Commands;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Commands
{
    public class CommandTests
    {
        private static RoadNetwork NewNet() => new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());

        private static BuildRoadCommand Build(float x0, float z0, float x1, float z1, ushort type = RoadTypeIds.Street2)
            => new BuildRoadCommand(SegmentRequest.Straight(new Vec2(x0, z0), new Vec2(x1, z1), type));

        private static void Do(RoadNetwork n, CommandHistory h, ICommand c)
            => Assert.Equal(ValidationResult.Ok, h.Execute(n, c));

        private static List<EntityId> Alive(RoadNetwork n)
        {
            var list = new List<EntityId>();
            for (int i = 0; i < n.SegmentIndexBound; i++)
            {
                EntityId id = n.SegmentIdAt(i);
                if (id.IsValid && n.IsSegmentAlive(id)) list.Add(id);
            }
            return list;
        }

        private static void BuildUndoCheck(RoadNetwork n, CommandHistory h, ICommand c)
        {
            ulong before = n.ComputeHash();
            int nodes = n.NodeCount, segs = n.SegmentCount;
            Do(n, h, c);
            Assert.NotEqual(before, n.ComputeHash());
            ulong after = n.ComputeHash();
            Assert.True(h.Undo(n));
            Assert.Equal(before, n.ComputeHash());
            Assert.Equal(nodes, n.NodeCount);
            Assert.Equal(segs, n.SegmentCount);
            Assert.True(h.Redo(n));
            Assert.Equal(after, n.ComputeHash());
            Assert.True(h.Undo(n));
            Assert.Equal(before, n.ComputeHash());
        }

        [Fact]
        public void BuildFreeLine_UndoRestoresHash()
        {
            var n = NewNet();
            ulong empty = n.ComputeHash();
            BuildUndoCheck(n, new CommandHistory(), Build(0, 0, 100, 0));
            Assert.Equal(empty, n.ComputeHash());
            Assert.Equal(0, n.SegmentCount);
        }

        [Fact]
        public void BuildWithNodeSnap_UndoRestoresHash()
        {
            var n = NewNet();
            var h = new CommandHistory();
            Do(n, h, Build(0, 0, 100, 0));
            BuildUndoCheck(n, h, Build(102, 2, 100, 100));
            Assert.Equal(1, n.SegmentCount);
        }

        [Fact]
        public void BuildTSplit_UndoRestoresHash()
        {
            var n = NewNet();
            var h = new CommandHistory();
            Do(n, h, Build(0, 0, 200, 0));
            BuildUndoCheck(n, h, Build(100, 80, 100, 2));
            Assert.Equal(1, n.SegmentCount);
        }

        [Fact]
        public void BuildCrossX_UndoRestoresHash()
        {
            var n = NewNet();
            var h = new CommandHistory();
            Do(n, h, Build(0, 0, 200, 0));
            BuildUndoCheck(n, h, Build(100, -80, 100, 80));
        }

        [Fact]
        public void BuildThreeCrossings_UndoRestoresHash()
        {
            var n = NewNet();
            var h = new CommandHistory();
            Do(n, h, Build(0, 0, 0, 150));
            Do(n, h, Build(60, 0, 60, 150));
            Do(n, h, Build(120, 0, 120, 150));
            BuildUndoCheck(n, h, Build(-40, 75, 160, 75));
            Do(n, h, Build(-40, 75, 160, 75));
            Assert.Equal(6 + 4, n.SegmentCount);
        }

        [Fact]
        public void Demolish_UndoRestoresHash()
        {
            var n = NewNet();
            var h = new CommandHistory();
            Do(n, h, Build(0, 0, 200, 0));
            Do(n, h, Build(100, -80, 100, 80));
            Do(n, h, Build(0, 60, 200, 60, RoadTypeIds.Avenue4));
            foreach (EntityId seg in Alive(n))
            {
                ulong before = n.ComputeHash();
                Do(n, h, new DemolishCommand(seg));
                Assert.NotEqual(before, n.ComputeHash());
                h.Undo(n);
                Assert.Equal(before, n.ComputeHash());
            }
        }

        [Fact]
        public void DemolishList_UndoRestoresHash()
        {
            var n = NewNet();
            var h = new CommandHistory();
            Do(n, h, Build(0, 0, 200, 0));
            Do(n, h, Build(100, -80, 100, 80));
            ulong before = n.ComputeHash();
            Do(n, h, new DemolishCommand(Alive(n).ToArray()));
            Assert.Equal(0, n.SegmentCount);
            Assert.Equal(0, n.NodeCount);
            h.Undo(n);
            Assert.Equal(before, n.ComputeHash());
            h.Redo(n);
            Assert.Equal(0, n.SegmentCount);
        }

        [Fact]
        public void BuildSeveral_UndoAll_EmptyHash_RedoAll_SameHash()
        {
            var n = NewNet();
            ulong empty = n.ComputeHash();
            var h = new CommandHistory();
            var hashes = new List<ulong> { empty };
            float[][] roads =
            {
                new[] { 0f, 0f, 200f, 0f }, new[] { 100f, -80f, 100f, 80f }, new[] { 0f, 50f, 200f, 50f },
                new[] { 50f, -80f, 50f, 120f }, new[] { 202f, 2f, 300f, 100f },
            };
            foreach (float[] r in roads)
            {
                Do(n, h, Build(r[0], r[1], r[2], r[3]));
                hashes.Add(n.ComputeHash());
            }
            for (int i = roads.Length - 1; i >= 0; i--)
            {
                Assert.True(h.Undo(n));
                Assert.True(hashes[i] == n.ComputeHash(), "undo to " + i + " segs=" + n.SegmentCount + " nodes=" + n.NodeCount);
            }
            Assert.Equal(empty, n.ComputeHash());
            Assert.Equal(0, n.NodeCount);
            Assert.False(h.Undo(n));
            for (int i = 1; i <= roads.Length; i++)
            {
                Assert.True(h.Redo(n));
                Assert.Equal(hashes[i], n.ComputeHash());
            }
            Assert.False(h.Redo(n));
        }

        [Fact]
        public void InvalidCommand_ChangesNothing()
        {
            var n = NewNet();
            var h = new CommandHistory();
            Do(n, h, Build(0, 0, 100, 0));
            ulong hash = n.ComputeHash();
            ulong epoch = n.Epoch;
            Assert.Equal(ValidationResult.TooShort, h.Execute(n, Build(0, 50, 3, 50)));
            Assert.Equal(ValidationResult.UnknownRoadType, h.Execute(n, Build(0, 50, 100, 50, 999)));
            Assert.Equal(ValidationResult.InvalidTarget, h.Execute(n, new DemolishCommand(EntityId.None)));
            Assert.Equal(ValidationResult.InvalidTarget, h.Execute(n, new DemolishCommand(new EntityId(77, 3))));
            EntityId s = Alive(n)[0];
            Assert.Equal(ValidationResult.InvalidTarget, h.Execute(n, new DemolishCommand(new[] { s, s })));
            Assert.Equal(hash, n.ComputeHash());
            Assert.Equal(epoch, n.Epoch);
            Assert.Equal(1, h.UndoCount);
        }

        [Fact]
        public void Execute_ClearsRedo_AndHistoryIsBounded()
        {
            var n = NewNet();
            var h = new CommandHistory(3);
            for (int i = 0; i < 5; i++) Do(n, h, Build(0, i * 40, 100, i * 40));
            Assert.Equal(3, h.UndoCount);
            Assert.True(h.Undo(n));
            Assert.Equal(1, h.RedoCount);
            Do(n, h, Build(300, 0, 400, 0));
            Assert.Equal(0, h.RedoCount);
            Assert.False(h.Redo(n));
            while (h.Undo(n)) { }
            Assert.Equal(2, n.SegmentCount);
        }

        [Fact]
        public void Queue_AppliesInOrder_AndDropsInvalid()
        {
            var n = NewNet();
            var h = new CommandHistory();
            var q = new CommandQueue();
            q.Enqueue(Build(0, 0, 100, 0));
            q.Enqueue(Build(0, 50, 2, 50)); // demasiado corto
            q.Enqueue(Build(100, 0, 100, 100)); // depende del primero (snap al nodo)
            Assert.Equal(3, q.Count);
            Assert.Equal(2, q.Drain(n, h));
            Assert.Equal(1, q.LastRejected);
            Assert.Equal(0, q.Count);
            Assert.Equal(2, n.SegmentCount);
            Assert.Equal(3, n.NodeCount);
        }

        [Fact]
        public void RandomSequence_MatchesRecordedHashes()
        {
            for (ulong seed = 1; seed <= 3; seed++) RunRandom(seed);
        }

        private static void RunRandom(ulong seed)
        {
            var n = NewNet();
            var h = new CommandHistory(1000);
            var rng = new Pcg32(seed, 7);
            var hashes = new List<ulong> { n.ComputeHash() };
            int cursor = 0;
            int applied = 0, undone = 0, redone = 0;
            for (int step = 0; step < 200; step++)
            {
                int op = rng.NextInt(10);
                if (op < 5)
                {
                    float x = rng.NextInt(-150, 150), z = rng.NextInt(-150, 150);
                    float ang = rng.NextFloat() * 6.2831853f;
                    float len = 15 + rng.NextInt(0, 140);
                    var cmd = Build(x, z, x + (float)System.Math.Cos(ang) * len, z + (float)System.Math.Sin(ang) * len,
                        (ushort)rng.NextInt(0, 6));
                    ulong before = n.ComputeHash();
                    if (h.Execute(n, cmd) == ValidationResult.Ok) Record(n, hashes, ref cursor, ref applied);
                    else Assert.Equal(before, n.ComputeHash());
                }
                else if (op < 7)
                {
                    List<EntityId> alive = Alive(n);
                    if (alive.Count == 0) continue;
                    ICommand cmd;
                    if (rng.NextInt(4) == 0 && alive.Count >= 2)
                        cmd = new DemolishCommand(new[] { alive[rng.NextInt(alive.Count)], alive[rng.NextInt(alive.Count)] });
                    else
                        cmd = new DemolishCommand(alive[rng.NextInt(alive.Count)]);
                    if (h.Execute(n, cmd) == ValidationResult.Ok) Record(n, hashes, ref cursor, ref applied);
                }
                else if (op < 9)
                {
                    if (h.Undo(n)) { cursor--; undone++; Assert.Equal(hashes[cursor], n.ComputeHash()); }
                    else Assert.Equal(0, cursor);
                }
                else
                {
                    if (h.Redo(n)) { cursor++; redone++; Assert.Equal(hashes[cursor], n.ComputeHash()); }
                    else Assert.Equal(hashes.Count - 1, cursor);
                }
            }
            Assert.True(applied > 20 && undone > 5 && redone > 0, applied + "/" + undone + "/" + redone);
            while (h.Undo(n)) { cursor--; Assert.Equal(hashes[cursor], n.ComputeHash()); }
            Assert.Equal(hashes[0], n.ComputeHash());
        }

        private static void Record(RoadNetwork n, List<ulong> hashes, ref int cursor, ref int applied)
        {
            hashes.RemoveRange(cursor + 1, hashes.Count - cursor - 1);
            hashes.Add(n.ComputeHash());
            cursor++;
            applied++;
        }
    }
}
