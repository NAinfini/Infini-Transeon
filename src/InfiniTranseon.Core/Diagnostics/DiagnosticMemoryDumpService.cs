using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InfiniTranseon.Core.Diagnostics;

public sealed record DiagnosticMemoryDumpOptions(string RootDirectory, TimeSpan Retention);

public sealed record MemoryDumpConsent(
    bool ExplicitlyAccepted,
    DateTimeOffset AcceptedAtUtc,
    string WarningVersion)
{
    public const string CurrentWarningVersion = "memory-dump-warning-v1";
}

public sealed record DiagnosticMemoryDumpResult(
    string DumpPath,
    DateTimeOffset ExpiresAtUtc,
    bool ContainsSensitiveProcessMemory,
    bool UploadAvailable,
    string WarningVersion);

internal interface IMemoryDumpWriter
{
    bool Write(int processId, SafeFileHandle destination, out int nativeError);
}

internal interface IPrivatePathAcl
{
    void Apply(string path);
}

public sealed class DiagnosticMemoryDumpService
{
    private readonly string _dumpDirectory;
    private readonly TimeSpan _retention;
    private readonly IMemoryDumpWriter _writer;
    private readonly IPrivatePathAcl _acl;

    public DiagnosticMemoryDumpService(DiagnosticMemoryDumpOptions options)
        : this(options, new NativeMemoryDumpWriter(), new WindowsPrivatePathAcl()) { }

    internal DiagnosticMemoryDumpService(
        DiagnosticMemoryDumpOptions options,
        IMemoryDumpWriter writer,
        IPrivatePathAcl acl)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        if (options.Retention < TimeSpan.FromMinutes(1) || options.Retention > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(options),
                "Diagnostic memory-dump retention must be between one minute and 24 hours.");
        _dumpDirectory = Path.Combine(Path.GetFullPath(options.RootDirectory), "memory-dumps");
        _retention = options.Retention;
        _writer = writer;
        _acl = acl;
    }

    public ValueTask<DiagnosticMemoryDumpResult> CreateAsync(
        int processId,
        MemoryDumpConsent consent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consent);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!consent.ExplicitlyAccepted ||
            consent.WarningVersion != MemoryDumpConsent.CurrentWarningVersion ||
            consent.AcceptedAtUtc < now.AddMinutes(-10) ||
            consent.AcceptedAtUtc > now.AddMinutes(1))
        {
            throw new InvalidOperationException(
                "A fresh explicit acknowledgement of the current memory-dump warning is required.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        EnsurePrivateDirectory();
        string path = Path.Combine(
            _dumpDirectory,
            $"dump-{now:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.dmp");
        try
        {
            using (var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                if (!_writer.Write(processId, destination.SafeFileHandle, out int nativeError))
                    throw new Win32Exception(nativeError, "MiniDumpWriteDump failed.");
                destination.Flush(flushToDisk: true);
            }
            _acl.Apply(path);
            return ValueTask.FromResult(new DiagnosticMemoryDumpResult(
                path,
                now + _retention,
                ContainsSensitiveProcessMemory: true,
                UploadAvailable: false,
                MemoryDumpConsent.CurrentWarningVersion));
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_dumpDirectory)) return ValueTask.FromResult(0);
        RejectReparsePoint(_dumpDirectory);
        int deleted = 0;
        foreach (string path in Directory.EnumerateFiles(_dumpDirectory, "*.dmp", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(path) > (now - _retention).UtcDateTime) continue;
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (FileNotFoundException) { }
        }
        return ValueTask.FromResult(deleted);
    }

    private void EnsurePrivateDirectory()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            throw new PlatformNotSupportedException("Diagnostic memory dumps require supported Windows 11.");
        Directory.CreateDirectory(_dumpDirectory);
        RejectReparsePoint(_dumpDirectory);
        _acl.Apply(_dumpDirectory);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Diagnostic memory-dump directory cannot be a reparse point.");
    }

    private sealed class NativeMemoryDumpWriter : IMemoryDumpWriter
    {
        public bool Write(int processId, SafeFileHandle destination, out int nativeError)
        {
            using Process process = Process.GetProcessById(processId);
            bool result = MiniDumpWriteDump(
                process.Handle,
                unchecked((uint)processId),
                destination,
                MiniDumpType.WithDataSegments |
                    MiniDumpType.WithHandleData |
                    MiniDumpType.WithUnloadedModules |
                    MiniDumpType.WithPrivateReadWriteMemory |
                    MiniDumpType.WithFullMemoryInfo |
                    MiniDumpType.WithThreadInfo,
                nint.Zero,
                nint.Zero,
                nint.Zero);
            nativeError = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }

        [Flags]
        private enum MiniDumpType : uint
        {
            WithDataSegments = 0x00000001,
            WithHandleData = 0x00000004,
            WithUnloadedModules = 0x00000020,
            WithPrivateReadWriteMemory = 0x00000200,
            WithFullMemoryInfo = 0x00000800,
            WithThreadInfo = 0x00001000,
        }

        [DllImport("Dbghelp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MiniDumpWriteDump(
            nint process,
            uint processId,
            SafeFileHandle file,
            MiniDumpType dumpType,
            nint exceptionParam,
            nint userStreamParam,
            nint callbackParam);
    }

    private sealed class WindowsPrivatePathAcl : IPrivatePathAcl
    {
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;

        public void Apply(string path)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                "D:P(A;;FA;;;SY)(A;;FA;;;OW)", 1, out nint descriptor, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not create the private diagnostic ACL.");
            }
            try
            {
                if (!SetFileSecurity(
                    path,
                    DaclSecurityInformation | ProtectedDaclSecurityInformation,
                    descriptor))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not apply the private diagnostic ACL.");
                }
            }
            finally
            {
                _ = LocalFree(descriptor);
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor,
            uint stringSdRevision,
            out nint securityDescriptor,
            out uint securityDescriptorSize);

        [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileSecurity(
            string fileName,
            uint securityInformation,
            nint securityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern nint LocalFree(nint memory);
    }
}
