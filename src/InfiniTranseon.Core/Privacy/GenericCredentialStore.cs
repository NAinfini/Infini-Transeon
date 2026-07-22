using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InfiniTranseon.Core.Privacy;

public sealed class GenericCredentialStore : IBoundCredentialStore
{
    private const int CredentialTypeGeneric = 1;
    private const int PersistenceLocalMachine = 2;
    private const int MaximumCredentialBlobBytes = 2560;
    private const string TargetPrefix = "InfiniTranseon/Provider/";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public ValueTask WriteAsync(
        string reference,
        string secret,
        CredentialBinding binding,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(binding);
        var envelope = new CredentialEnvelope(1, binding.Normalize(), secret);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        if (payload.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentOutOfRangeException(nameof(secret), "Credential exceeds the Windows Credential Manager limit.");
        }

        IntPtr blob = Marshal.AllocCoTaskMem(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, blob, payload.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = GetTargetName(reference),
                CredentialBlobSize = (uint)payload.Length,
                CredentialBlob = blob,
                Persist = PersistenceLocalMachine,
                UserName = binding.ProviderId,
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            int payloadLength = payload.Length;
            CryptographicOperations.ZeroMemory(payload);
            ZeroAndFree(blob, payloadLength);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadAsync(
        string reference,
        CredentialBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (!CredRead(GetTargetName(reference), CredentialTypeGeneric, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return ValueTask.FromResult<string?>(null);
            throw new Win32Exception(error);
        }

        byte[]? payload = null;
        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize > MaximumCredentialBlobBytes)
                throw new InvalidDataException("Stored credential exceeds the supported limit.");
            payload = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, payload, 0, payload.Length);
            CredentialEnvelope envelope = JsonSerializer.Deserialize<CredentialEnvelope>(payload, SerializerOptions) ??
                throw new InvalidDataException("Stored credential is malformed.");
            if (envelope.Version != 1 || envelope.Binding.Normalize() != expectedBinding.Normalize())
            {
                throw new CredentialBindingException(
                    "Credential origin, authentication purpose, or proxy policy changed; explicit reconfirmation is required.");
            }
            return ValueTask.FromResult<string?>(envelope.Secret);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored credential is malformed.", exception);
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            CredFree(pointer);
        }
    }

    public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        if (!CredDelete(GetTargetName(reference), CredentialTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error);
        }
        return ValueTask.CompletedTask;
    }

    private static string GetTargetName(string reference) => TargetPrefix + reference;

    private static void ValidateReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.Length > 128 || reference.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Credential references must be 1-128 ASCII identifier characters.", nameof(reference));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
    }

    private static void ZeroAndFree(IntPtr pointer, int byteCount)
    {
        if (pointer == IntPtr.Zero) return;
        int bytes = Math.Min(MaximumCredentialBlobBytes, Math.Max(0, byteCount));
        for (int index = 0; index < bytes; index++) Marshal.WriteByte(pointer, index, 0);
        Marshal.FreeCoTaskMem(pointer);
    }

    private sealed record CredentialEnvelope(int Version, CredentialBinding Binding, string Secret);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
