namespace EnvSync.Testing;

/// <summary>
/// Measures managed allocations on the calling thread so "zero allocation" claims are asserted, not assumed.
/// </summary>
internal static class AllocationProbe
{
    private const int WarmUpRuns = 5;

    /// <summary>Runs <paramref name="action"/> once after warm-up and returns the bytes it allocated.</summary>
    public static long Measure(Action action)
    {
        for (var i = 0; i < WarmUpRuns; i++)
        {
            action();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
