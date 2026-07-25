using System.Runtime.InteropServices;

namespace InfiniTranseon.Core.Translation.Local;

public static class LocalModelRuntimeAvailability
{
    public const string PhraseTableRuntime = "phrase-table-v1";
    public const string CTranslate2MadladRuntime = "ctranslate2-madlad-v1";
    public const string WorkerExecutableName = "InfiniTranseon.ModelWorker.exe";
    public const string NativeLibraryName = "InfiniTranseon.ModelRuntime.Native.dll";
    private const uint NativeAbiVersion = 1;

    public static bool IsReady(ModelCatalogEntry model, string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        string root = Path.GetFullPath(applicationDirectory);
        if (!File.Exists(Path.Combine(root, WorkerExecutableName)))
            return false;
        return model.Runtime switch
        {
            PhraseTableRuntime => true,
            CTranslate2MadladRuntime => HasCompatibleNativeLibrary(root),
            _ => false,
        };
    }

    public static bool HasCompatibleNativeLibrary(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        string path = Path.Combine(
            Path.GetFullPath(applicationDirectory),
            NativeLibraryName);
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out IntPtr library))
            return false;
        try
        {
            if (!NativeLibrary.TryGetExport(
                    library,
                    "infini_model_runtime_abi_version",
                    out IntPtr symbol))
                return false;
            var version = Marshal.GetDelegateForFunctionPointer<AbiVersionDelegate>(symbol);
            return version() == NativeAbiVersion;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionDelegate();
}
