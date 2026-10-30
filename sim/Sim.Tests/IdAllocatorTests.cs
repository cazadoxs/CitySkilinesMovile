using Urbe.Sim.Ids;
using Xunit;

namespace Urbe.Sim.Tests
{
    public class IdAllocatorTests
    {
        [Fact]
        public void ReuseBumpsGeneration()
        {
            var al = new IdAllocator(2);
            var a = al.Allocate();
            Assert.True(al.IsAlive(a));
            Assert.True(al.Free(a));
            Assert.False(al.IsAlive(a));
            var b = al.Allocate();
            Assert.Equal(a.Index, b.Index);
            Assert.Equal(a.Generation + 1, b.Generation);
            Assert.True(al.IsAlive(b));
            Assert.False(al.IsAlive(a));
            Assert.False(al.Free(a));
        }

        [Fact]
        public void GrowsAndNone()
        {
            var al = new IdAllocator(1);
            for (int i = 0; i < 100; i++) Assert.True(al.IsAlive(al.Allocate()));
            Assert.Equal(100, al.AliveCount);
            Assert.False(al.IsAlive(EntityId.None));
            Assert.False(EntityId.None.IsValid);
        }
    }
}
