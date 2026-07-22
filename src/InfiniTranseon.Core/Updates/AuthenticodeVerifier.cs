using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace InfiniTranseon.Core.Updates;

public static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static void Verify(string filePath, string expectedPublisher)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string path = Path.GetFullPath(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPublisher);
        var fileInfo = new WinTrustFileInfo(path);
        IntPtr fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData(fileInfoPointer);
            int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            data.StateAction = 2;
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            if (result != 0) throw new Win32Exception(result, "Authenticode verification failed.");
#pragma warning disable SYSLIB0057 // WinVerifyTrust validates integrity; this API only reads the verified signer certificate.
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate2 = new X509Certificate2(certificate);
            if (!certificate2.Subject.Equals(expectedPublisher, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Authenticode publisher does not match the signed manifest.");
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public uint Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string filePath) => FilePath = filePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 1;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 1;
            StateData = IntPtr.Zero;
            UrlReference = null;
            ProviderFlags = 0x00000040;
            UiContext = 0;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, [In] Guid action, ref WinTrustData data);
}
