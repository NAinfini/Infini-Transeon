using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace InfiniTranseon.Core.Translation.Local;

public interface ILocalWorkerSession : IAsyncDisposable
{
    ILocalTranslationClient Client { get; }
}

public sealed class LocalWorkerSandboxSession : ILocalWorkerSession
{
    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle _job;
    private int _disposed;

    internal LocalWorkerSandboxSession(
        int processId,
        Guid workerSessionEpoch,
        LocalWorkerClient client,
        SafeProcessHandle process,
        SafeFileHandle job)
    {
        ProcessId = processId;
        WorkerSessionEpoch = workerSessionEpoch;
        Client = client;
        _process = process;
        _job = job;
    }

    public int ProcessId { get; }
    public Guid WorkerSessionEpoch { get; }
    public LocalWorkerClient Client { get; }
    ILocalTranslationClient ILocalWorkerSession.Client => Client;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Client.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            _job.Dispose();
            _ = RuntimeProcessNative.WaitForSingleObject(_process, 5_000U);
            _process.Dispose();
        }
    }
}

public static class LocalWorkerSandboxLauncher
{
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    public static async ValueTask<LocalWorkerSandboxSession> LaunchAsync(
        LocalWorkerSandboxOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            throw new PlatformNotSupportedException("The local worker sandbox requires Windows 11 build 22621 or later.");
        string host = Path.GetFullPath(options.HostExecutablePath);
        string? assembly = options.WorkerAssemblyPath is null ? null : Path.GetFullPath(options.WorkerAssemblyPath);
        if (!File.Exists(host)) throw new FileNotFoundException("Local worker host was not found.", host);
        if (assembly is not null && !File.Exists(assembly))
            throw new FileNotFoundException("Local worker assembly was not found.", assembly);

        IntPtr appContainerSid = IntPtr.Zero;
        SafeFileHandle? bootstrapRead = null;
        SafeFileHandle? bootstrapWrite = null;
        SafeProcessHandle? process = null;
        SafeFileHandle? job = null;
        NamedPipeServerStream? pipe = null;
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        Guid epoch = Guid.NewGuid();
        try
        {
            appContainerSid = GetOrCreateAppContainerSid(options.AppContainerName);
            string sid = SidToString(appContainerSid);
            Directory.CreateDirectory(options.ManagedModelDirectory);
            Directory.CreateDirectory(options.SandboxScratchDirectory);
            ApplyDirectoryAcl(options.ManagedModelDirectory, sid, "GRGX");
            ApplyDirectoryAcl(options.SandboxScratchDirectory, sid, "GA");
            GrantDirectoryReadExecute(Path.GetDirectoryName(assembly ?? host)!, appContainerSid);

            string pipeName = "InfiniTranseon.ModelWorker." + Guid.NewGuid().ToString("N");
            pipe = CreateSecurePipe(pipeName, sid);
            var securityAttributes = new RuntimeProcessNative.SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<RuntimeProcessNative.SecurityAttributes>(),
                InheritHandle = true,
            };
            if (!RuntimeProcessNative.CreatePipe(out bootstrapRead, out bootstrapWrite, ref securityAttributes, 0U) ||
                !RuntimeProcessNative.SetHandleInformation(
                    bootstrapWrite, RuntimeProcessNative.HandleFlagInherit, 0U))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            using var attributes = new AppContainerProcessAttributeList(bootstrapRead, appContainerSid);
            RuntimeProcessNative.StartupInfoEx startup = attributes.CreateStartupInfo();
            job = CreateWorkerJob(options.MaximumCommittedBytes);
            string commandLine = BuildCommandLine(host, assembly, bootstrapRead.DangerousGetHandle());
            using var environment = SanitizedEnvironmentBlock.Create(options.SandboxScratchDirectory, host);
            if (!RuntimeProcessNative.CreateProcess(
                    host,
                    new StringBuilder(commandLine),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    RuntimeProcessNative.ExtendedStartupInfoPresent |
                        RuntimeProcessNative.CreateNoWindow |
                        RuntimeProcessNative.CreateSuspended |
                        0x00000400U,
                    environment.Pointer,
                    Path.GetDirectoryName(assembly ?? host),
                    ref startup,
                    out RuntimeProcessNative.ProcessInformation processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            bootstrapRead.Dispose();
            bootstrapRead = null;
            if (!RuntimeProcessNative.AssignProcessToJobObject(job, process))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var bootstrap = new LocalWorkerBootstrap(
                LocalWorkerProtocol.Version,
                pipeName,
                Environment.ProcessId,
                epoch,
                Convert.ToBase64String(secret),
                Path.GetFullPath(options.ManagedModelDirectory));
            await using (var stream = new FileStream(
                bootstrapWrite, FileAccess.Write, 4096, isAsync: false))
            {
                bootstrapWrite = null;
                await LocalWorkerFrameCodec.WriteAsync(stream, bootstrap, cancellationToken).ConfigureAwait(false);
            }
            if (RuntimeProcessNative.ResumeThread(thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.HandshakeTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!LocalWorkerSandboxNative.GetNamedPipeClientProcessId(
                    pipe.SafePipeHandle, out uint clientProcessId) ||
                clientProcessId != processInformation.ProcessId)
                throw new InvalidDataException("Local worker pipe client PID does not match the launched process.");
            LocalWorkerHandshake handshake = await LocalWorkerFrameCodec.ReadAsync<LocalWorkerHandshake>(
                pipe, timeout.Token).ConfigureAwait(false);
            byte[] identity = Encoding.UTF8.GetBytes($"{epoch:D}:{processInformation.ProcessId}");
            byte[] expectedProof = HMACSHA256.HashData(secret, identity);
            byte[] actualProof;
            try { actualProof = Convert.FromBase64String(handshake.Proof); }
            catch (FormatException exception) { throw new InvalidDataException("Local worker handshake proof is malformed.", exception); }
            try
            {
                if (handshake.ProtocolVersion != LocalWorkerProtocol.Version ||
                    handshake.WorkerSessionEpoch != epoch ||
                    handshake.WorkerProcessId != processInformation.ProcessId ||
                    !CryptographicOperations.FixedTimeEquals(expectedProof, actualProof))
                    throw new InvalidDataException("Local worker handshake authentication failed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedProof);
                CryptographicOperations.ZeroMemory(actualProof);
            }

            var client = new LocalWorkerClient(pipe, epoch);
            var session = new LocalWorkerSandboxSession(
                checked((int)processInformation.ProcessId), epoch, client, process, job);
            pipe = null;
            process = null;
            job = null;
            return session;
        }
        catch
        {
            if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
            job?.Dispose();
            if (process is not null && !process.IsInvalid)
            {
                _ = RuntimeProcessNative.TerminateProcess(process, 1U);
                _ = RuntimeProcessNative.WaitForSingleObject(process, 5_000U);
                process.Dispose();
            }
            throw;
        }
        finally
        {
            bootstrapRead?.Dispose();
            bootstrapWrite?.Dispose();
            if (appContainerSid != IntPtr.Zero) _ = LocalWorkerSandboxNative.FreeSid(appContainerSid);
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static IntPtr GetOrCreateAppContainerSid(string name)
    {
        int result = LocalWorkerSandboxNative.CreateAppContainerProfile(
            name, "Infini-Transeon Model Worker", "Offline local model worker", IntPtr.Zero, 0U, out IntPtr sid);
        if (result == 0) return sid;
        if (result != ErrorAlreadyExists ||
            LocalWorkerSandboxNative.DeriveAppContainerSidFromAppContainerName(name, out sid) != 0)
            Marshal.ThrowExceptionForHR(result);
        return sid;
    }

    private static string SidToString(IntPtr sid)
    {
        if (!LocalWorkerSandboxNative.ConvertSidToStringSid(sid, out IntPtr text))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("AppContainer SID is empty."); }
        finally { _ = LocalWorkerSandboxNative.LocalFree(text); }
    }

    private static void ApplyDirectoryAcl(string path, string sid, string access)
    {
        string sddl = $"D:P(A;OICI;FA;;;OW)(A;OICI;FA;;;SY)(A;OICI;{access};;;{sid})";
        if (!LocalWorkerSandboxNative.ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, 1U, out IntPtr descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LocalWorkerSandboxNative.SetFileSecurity(
                    path, 0x80000004U, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { _ = LocalWorkerSandboxNative.LocalFree(descriptor); }
    }

    private static void GrantDirectoryReadExecute(string path, IntPtr appContainerSid)
    {
        uint result = LocalWorkerSandboxNative.GetNamedSecurityInfo(
            path, 1, 4U, out _, out _, out IntPtr oldDacl, out _, out IntPtr securityDescriptor);
        if (result != 0U) throw new Win32Exception(checked((int)result));
        IntPtr trusteeSid = appContainerSid;
        var access = new LocalWorkerSandboxNative.ExplicitAccess
        {
            AccessPermissions = 0x80000000U | 0x20000000U,
            AccessMode = 1,
            Inheritance = 3U,
            Trustee = new LocalWorkerSandboxNative.Trustee
            {
                TrusteeForm = 0,
                TrusteeType = 1,
                Name = trusteeSid,
            },
        };
        try
        {
            result = LocalWorkerSandboxNative.SetEntriesInAcl(1U, ref access, oldDacl, out IntPtr newDacl);
            if (result != 0U) throw new Win32Exception(checked((int)result));
            try
            {
                result = LocalWorkerSandboxNative.SetNamedSecurityInfo(
                    path, 1, 4U, IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
                if (result != 0U) throw new Win32Exception(checked((int)result));
            }
            finally { _ = LocalWorkerSandboxNative.LocalFree(newDacl); }
        }
        finally { _ = LocalWorkerSandboxNative.LocalFree(securityDescriptor); }
    }

    private static NamedPipeServerStream CreateSecurePipe(string pipeName, string sid)
    {
        string sddl = $"D:P(A;;GA;;;OW)(A;;GA;;;SY)(A;;GA;;;{sid})";
        if (!LocalWorkerSandboxNative.ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, 1U, out IntPtr descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new RuntimeProcessNative.SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<RuntimeProcessNative.SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false,
            };
            SafePipeHandle handle = LocalWorkerSandboxNative.CreateNamedPipe(
                @"\\.\pipe\" + pipeName,
                0x40000003U,
                0x00000008U,
                1U,
                LocalWorkerProtocol.MaximumFrameBytes,
                LocalWorkerProtocol.MaximumFrameBytes,
                10_000U,
                ref attributes);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { _ = LocalWorkerSandboxNative.LocalFree(descriptor); }
    }

    private static SafeFileHandle CreateWorkerJob(long maximumBytes)
    {
        SafeFileHandle job = RuntimeProcessNative.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        RuntimeProcessNative.JobObjectExtendedLimitInformation limits = default;
        limits.BasicLimitInformation.LimitFlags = 0x00002000U | 0x00000100U | 0x00000008U;
        limits.BasicLimitInformation.ActiveProcessLimit = 1U;
        limits.ProcessMemoryLimit = checked((nuint)maximumBytes);
        int size = Marshal.SizeOf<RuntimeProcessNative.JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!RuntimeProcessNative.SetInformationJobObject(job, 9, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string BuildCommandLine(string host, string? assembly, IntPtr bootstrapHandle) =>
        assembly is null
            ? $"\"{host}\" --bootstrap-handle={bootstrapHandle.ToInt64()}"
            : $"\"{host}\" \"{assembly}\" --bootstrap-handle={bootstrapHandle.ToInt64()}";

    private sealed record LocalWorkerHandshake(
        int ProtocolVersion,
        Guid WorkerSessionEpoch,
        uint WorkerProcessId,
        string Proof);
}

internal sealed class AppContainerProcessAttributeList : IDisposable
{
    private IntPtr _list;
    private IntPtr _handle;
    private IntPtr _capabilities;

    public AppContainerProcessAttributeList(SafeFileHandle inheritedHandle, IntPtr appContainerSid)
    {
        try
        {
            nuint bytes = 0;
            _ = RuntimeProcessNative.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0U, ref bytes);
            _list = Marshal.AllocHGlobal(checked((int)bytes));
            if (!RuntimeProcessNative.InitializeProcThreadAttributeList(_list, 2, 0U, ref bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _handle = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_handle, inheritedHandle.DangerousGetHandle());
            if (!RuntimeProcessNative.UpdateProcThreadAttribute(
                    _list, 0U, RuntimeProcessNative.ProcThreadAttributeHandleList,
                    _handle, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var capabilities = new LocalWorkerSandboxNative.SecurityCapabilities
            {
                AppContainerSid = appContainerSid,
            };
            int size = Marshal.SizeOf<LocalWorkerSandboxNative.SecurityCapabilities>();
            _capabilities = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(capabilities, _capabilities, false);
            if (!RuntimeProcessNative.UpdateProcThreadAttribute(
                    _list, 0U, (IntPtr)0x00020009, _capabilities,
                    (nuint)size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    public RuntimeProcessNative.StartupInfoEx CreateStartupInfo() => new()
    {
        StartupInfo = new RuntimeProcessNative.StartupInfo
        {
            Size = (uint)Marshal.SizeOf<RuntimeProcessNative.StartupInfoEx>(),
        },
        AttributeList = _list,
    };

    public void Dispose()
    {
        if (_list != IntPtr.Zero)
        {
            RuntimeProcessNative.DeleteProcThreadAttributeList(_list);
            Marshal.FreeHGlobal(_list);
            _list = IntPtr.Zero;
        }
        if (_handle != IntPtr.Zero) { Marshal.FreeHGlobal(_handle); _handle = IntPtr.Zero; }
        if (_capabilities != IntPtr.Zero) { Marshal.FreeHGlobal(_capabilities); _capabilities = IntPtr.Zero; }
    }
}

internal sealed class SanitizedEnvironmentBlock : IDisposable
{
    private SanitizedEnvironmentBlock(IntPtr pointer) => Pointer = pointer;
    public IntPtr Pointer { get; private set; }

    public static SanitizedEnvironmentBlock Create(string scratch, string host)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["TEMP"] = scratch,
            ["TMP"] = scratch,
            ["PATH"] = string.Join(';', Path.GetDirectoryName(host), Environment.SystemDirectory),
            ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = scratch,
        };
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot)) values["DOTNET_ROOT"] = dotnetRoot;
        string block = string.Join('\0', values.Select(item => item.Key + "=" + item.Value)) + "\0\0";
        return new SanitizedEnvironmentBlock(Marshal.StringToHGlobalUni(block));
    }

    public void Dispose()
    {
        if (Pointer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(Pointer);
        Pointer = IntPtr.Zero;
    }
}

internal static class LocalWorkerSandboxNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Trustee
    {
        internal IntPtr MultipleTrustee;
        internal int MultipleTrusteeOperation;
        internal int TrusteeForm;
        internal int TrusteeType;
        internal IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExplicitAccess
    {
        internal uint AccessPermissions;
        internal int AccessMode;
        internal uint Inheritance;
        internal Trustee Trustee;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityCapabilities
    {
        internal IntPtr AppContainerSid;
        internal IntPtr Capabilities;
        internal uint CapabilityCount;
        internal uint Reserved;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(
        string appContainerName, string displayName, string description,
        IntPtr capabilities, uint capabilityCount, out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName, out IntPtr appContainerSid);

    [DllImport("advapi32.dll")]
    internal static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint stringSDRevision,
        out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileSecurity(
        string fileName, uint securityInformation, IntPtr securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
    internal static extern uint GetNamedSecurityInfo(
        string objectName, int objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
    internal static extern uint SetNamedSecurityInfo(
        string objectName, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll", EntryPoint = "SetEntriesInAclW", CharSet = CharSet.Unicode)]
    internal static extern uint SetEntriesInAcl(
        uint count, ref ExplicitAccess entries, IntPtr oldAcl, out IntPtr newAcl);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafePipeHandle CreateNamedPipe(
        string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout,
        ref RuntimeProcessNative.SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
}
