namespace InfiniTranseon.PlatformSpike;

/// <summary>Process exit codes, mirroring the CaptureSpike sysexits-style convention.</summary>
internal static class ExitCodes
{
    internal const int Success = 0;
    internal const int InvalidUsage = 64;
    internal const int PlatformError = 70;
    internal const int PipeError = 71;
    internal const int FocusViolation = 72;
}
