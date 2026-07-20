using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace InfiniTranseon.Core.Runtime;

public sealed class RuntimeEngineHostSession : IAsyncDisposable
{
    private readonly SafeProcessHandle _processHandle;
    private readonly SafeFileHandle _jobHandle;
    private int _disposed;

    internal RuntimeEngineHostSession(
        int processId,
        Guid runtimeEpoch,
        RuntimeNamedPipeConnection connection,
        SafeProcessHandle processHandle,
        SafeFileHandle jobHandle)
    {
        ProcessId = processId;
        RuntimeEpoch = runtimeEpoch;
        Connection = connection;
        _processHandle = processHandle;
        _jobHandle = jobHandle;
    }

    public int ProcessId { get; }

    public Guid RuntimeEpoch { get; }

    public RuntimeNamedPipeConnection Connection { get; }

    public bool HasExited
    {
        get
        {
            if (!RuntimeProcessNative.GetExitCodeProcess(_processHandle, out uint exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return exitCode != RuntimeProcessNative.StillActive;
        }
    }

    public bool IsSupervised
    {
        get
        {
            if (!RuntimeProcessNative.IsProcessInJob(_processHandle, _jobHandle, out bool result))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _jobHandle.Dispose();
            _ = RuntimeProcessNative.WaitForSingleObject(_processHandle, 5_000U);
            _processHandle.Dispose();
        }
    }
}

public static class RuntimeEngineHostLauncher
{
    private const uint BootstrapMagic = 0x42525449U;
    private const int BootstrapFixedBytes = 64;

    public static async ValueTask<RuntimeEngineHostSession> LaunchAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath) ||
            !string.Equals(Path.GetExtension(fullExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("EngineHost executable was not found.", fullExecutablePath);
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        string pipeName = RuntimePipeName.Create();
        Guid runtimeEpoch = Guid.NewGuid();
        byte[] nonce = RandomNumberGenerator.GetBytes(Contracts.Runtime.RuntimeProtocol.BootstrapNonceBytes);
        SafeFileHandle? bootstrapRead = null;
        SafeFileHandle? bootstrapWrite = null;
        SafeProcessHandle? processHandle = null;
        SafeFileHandle? jobHandle = null;
        RuntimeNamedPipeConnection? connection = null;
        try
        {
            var securityAttributes = new RuntimeProcessNative.SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<RuntimeProcessNative.SecurityAttributes>(),
                InheritHandle = true,
            };
            if (!RuntimeProcessNative.CreatePipe(
                out bootstrapRead,
                out bootstrapWrite,
                ref securityAttributes,
                0U))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!RuntimeProcessNative.SetHandleInformation(
                bootstrapWrite,
                RuntimeProcessNative.HandleFlagInherit,
                0U))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var attributeList = new RuntimeProcessAttributeList(bootstrapRead);
            RuntimeProcessNative.StartupInfoEx startupInfo = attributeList.CreateStartupInfo();
            jobHandle = CreateKillOnCloseJob();
            string commandLine = $"\"{fullExecutablePath}\" --bootstrap-handle={bootstrapRead.DangerousGetHandle().ToInt64()}";
            var mutableCommandLine = new StringBuilder(commandLine);
            if (!RuntimeProcessNative.CreateProcess(
                fullExecutablePath,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                RuntimeProcessNative.ExtendedStartupInfoPresent |
                    RuntimeProcessNative.CreateNoWindow |
                    RuntimeProcessNative.CreateSuspended,
                IntPtr.Zero,
                Path.GetDirectoryName(fullExecutablePath),
                ref startupInfo,
                out RuntimeProcessNative.ProcessInformation processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            processHandle = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            bootstrapRead.Dispose();
            bootstrapRead = null;

            if (!RuntimeProcessNative.AssignProcessToJobObject(jobHandle, processHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            byte[] bootstrapPayload = CreateBootstrapPayload(
                Environment.ProcessId,
                runtimeEpoch,
                nonce,
                pipeName);
            try
            {
                await using var bootstrapStream = new FileStream(
                    bootstrapWrite,
                    FileAccess.Write,
                    bufferSize: 4_096,
                    isAsync: false);
                bootstrapWrite = null;
                await bootstrapStream.WriteAsync(bootstrapPayload, cancellationToken).ConfigureAwait(false);
                await bootstrapStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bootstrapPayload);
            }

            if (RuntimeProcessNative.ResumeThread(threadHandle) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            connection = await RuntimeNamedPipeClient.ConnectAsync(
                pipeName,
                checked((int)processInformation.ProcessId),
                runtimeEpoch,
                nonce,
                timeout,
                cancellationToken).ConfigureAwait(false);

            var session = new RuntimeEngineHostSession(
                checked((int)processInformation.ProcessId),
                runtimeEpoch,
                connection,
                processHandle,
                jobHandle);
            connection = null;
            processHandle = null;
            jobHandle = null;
            return session;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            jobHandle?.Dispose();
            if (processHandle is not null && !processHandle.IsInvalid)
            {
                _ = RuntimeProcessNative.TerminateProcess(processHandle, 1U);
                _ = RuntimeProcessNative.WaitForSingleObject(processHandle, 5_000U);
                processHandle.Dispose();
            }
            throw;
        }
        finally
        {
            bootstrapRead?.Dispose();
            bootstrapWrite?.Dispose();
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static byte[] CreateBootstrapPayload(
        int expectedClientProcessId,
        Guid runtimeEpoch,
        ReadOnlySpan<byte> nonce,
        string pipeName)
    {
        RuntimePipeName.Validate(pipeName);
        int pipeNameBytes = Encoding.ASCII.GetByteCount(pipeName);
        byte[] payload = new byte[sizeof(int) + BootstrapFixedBytes + pipeNameBytes];
        Span<byte> body = payload.AsSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(payload, body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body, BootstrapMagic);
        BinaryPrimitives.WriteInt32LittleEndian(body[4..], Contracts.Runtime.RuntimeProtocol.CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(body[8..], expectedClientProcessId);
        runtimeEpoch.TryWriteBytes(body[12..28]);
        nonce.CopyTo(body[28..60]);
        BinaryPrimitives.WriteInt32LittleEndian(body[60..], pipeNameBytes);
        Encoding.ASCII.GetBytes(pipeName, body[BootstrapFixedBytes..]);
        return payload;
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        SafeFileHandle job = RuntimeProcessNative.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        RuntimeProcessNative.JobObjectExtendedLimitInformation limits = default;
        limits.BasicLimitInformation.LimitFlags = RuntimeProcessNative.JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<RuntimeProcessNative.JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!RuntimeProcessNative.SetInformationJobObject(
                job,
                RuntimeProcessNative.JobObjectExtendedLimitInformationClass,
                buffer,
                (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal sealed class RuntimeProcessAttributeList : IDisposable
{
    private IntPtr _list;
    private IntPtr _handleValue;

    public RuntimeProcessAttributeList(SafeFileHandle inheritedHandle)
    {
        try
        {
            nuint bytes = 0;
            _ = RuntimeProcessNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0U, ref bytes);
            _list = Marshal.AllocHGlobal(checked((int)bytes));
            if (!RuntimeProcessNative.InitializeProcThreadAttributeList(_list, 1, 0U, ref bytes))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _handleValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_handleValue, inheritedHandle.DangerousGetHandle());
            if (!RuntimeProcessNative.UpdateProcThreadAttribute(
                _list,
                0U,
                RuntimeProcessNative.ProcThreadAttributeHandleList,
                _handleValue,
                (nuint)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            Dispose();
            throw;
        }
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
        if (_handleValue != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_handleValue);
            _handleValue = IntPtr.Zero;
        }
    }
}

internal static class RuntimeProcessNative
{
    internal const uint HandleFlagInherit = 0x00000001U;
    internal const uint ExtendedStartupInfoPresent = 0x00080000U;
    internal const uint CreateNoWindow = 0x08000000U;
    internal const uint CreateSuspended = 0x00000004U;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000U;
    internal const int JobObjectExtendedLimitInformationClass = 9;
    internal const uint StillActive = 259U;
    internal static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal uint Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2Bytes;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(
        SafeProcessHandle process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
