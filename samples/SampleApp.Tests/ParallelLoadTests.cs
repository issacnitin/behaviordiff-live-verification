using System;
using System.Threading.Tasks;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    /// <summary>
    /// Volume tests split across classes. xunit runs distinct classes as distinct collections in parallel,
    /// so these emit events from several threads at once and exercise the buffer's bounded queue.
    /// </summary>
    public static class LoadGenerator
    {
        internal const int Iterations = 400;

        internal static int Run(string tag)
        {
            int total = 0;

            // Deliberately not the proof fixtures. Sharing them would bury the proof events in hundreds of
            // identical ones and inflate the counters the proofs read.
            for (int i = 0; i < Iterations; i++)
            {
                total += ErrorProbes.Readable(new Wrapper<int>(tag, i));
                total += Probes.Stamp(new Stamped(Guid.NewGuid(), DateTime.UtcNow, tag));
            }

            return total;
        }
    }

    public sealed class ParallelLoadTestsA
    {
        [Fact]
        public void Emits_many_events() => Assert.Equal(LoadGenerator.Iterations * 2, LoadGenerator.Run("a"));
    }

    public sealed class ParallelLoadTestsB
    {
        [Fact]
        public void Emits_many_events() => Assert.Equal(LoadGenerator.Iterations * 2, LoadGenerator.Run("b"));
    }

    public sealed class ParallelLoadTestsC
    {
        [Fact]
        public void Emits_many_events() => Assert.Equal(LoadGenerator.Iterations * 2, LoadGenerator.Run("c"));
    }

    public sealed class ParallelLoadTestsD
    {
        [Fact]
        public void Emits_many_events() => Assert.Equal(LoadGenerator.Iterations * 2, LoadGenerator.Run("d"));
    }
}
