using static InfiniTranseon.PlatformSpike.NativeMethods;

namespace InfiniTranseon.PlatformSpike;

internal static class MessageLoop
{
    /// <summary>
    /// Pumps the calling thread's message queue for <paramref name="duration"/> without a
    /// blocking GetMessage wait, so probes exit cleanly on a bounded timeout. Returns early
    /// if a WM_QUIT is posted or <paramref name="stop"/> becomes true.
    /// </summary>
    internal static void PumpFor(TimeSpan duration, Func<bool>? stop = null)
    {
        long deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            while (PeekMessage(out MSG msg, 0, 0, 0, 0x0001 /* PM_REMOVE */))
            {
                if (msg.Message == WM_QUIT)
                {
                    return;
                }

                _ = TranslateMessage(ref msg);
                _ = DispatchMessage(ref msg);
            }

            if (stop is not null && stop())
            {
                return;
            }

            Thread.Sleep(10);
        }
    }
}
