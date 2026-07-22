using System.Runtime.InteropServices;

namespace InfiniTranseon.Core.Runtime;

public static class WindowsPlatformGuard
{
    public const int MinimumBuild = 22621;

    public static bool IsSupported(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Major == 10 && version.Minor == 0 && version.Build >= MinimumBuild;
    }

    public static void EnsureCurrentSystemSupported()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Infini-Transeon supports only 64-bit Windows 11 build 22621 or newer.");
        var version = new OsVersionInfo
        {
            Size = (uint)Marshal.SizeOf<OsVersionInfo>(),
        };
        int status = RtlGetVersion(ref version);
        if (status != 0)
            throw new PlatformNotSupportedException(
                $"Windows version detection failed with NTSTATUS 0x{status:X8}.");
        if (!Environment.Is64BitOperatingSystem ||
            !IsSupported(new Version(
                checked((int)version.Major),
                checked((int)version.Minor),
                checked((int)version.Build))))
            throw new PlatformNotSupportedException(
                "Infini-Transeon requires 64-bit Windows 11 build 22621 or newer.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfo versionInformation);
}
