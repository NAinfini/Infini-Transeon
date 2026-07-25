using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace InfiniTranseon.ModelWorker;

public sealed partial class CTranslate2MadladRuntime : ILocalModelRuntime
{
    public const string RuntimeId = "ctranslate2-madlad-v1";
    private const string NativeLibraryName = "InfiniTranseon.ModelRuntime.Native";
    private const uint ExpectedAbiVersion = 1;
    private const int ErrorBufferBytes = 256;
    private readonly string _modelId;
    private readonly NativeRuntimeHandle _runtime;

    private static readonly IReadOnlyDictionary<string, string> TargetTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "<2de>",
            ["en"] = "<2en>",
            ["es"] = "<2es>",
            ["fr"] = "<2fr>",
            ["it"] = "<2it>",
            ["ja"] = "<2ja>",
            ["ko"] = "<2ko>",
            ["pl"] = "<2pl>",
            ["pt"] = "<2pt>",
            ["ru"] = "<2ru>",
            ["th"] = "<2th>",
            ["tr"] = "<2tr>",
            ["vi"] = "<2vi>",
            ["zh"] = "<2zh>",
            ["zh-Hans"] = "<2zh>",
            ["zh-Hant"] = "<2zh>",
        };

    public CTranslate2MadladRuntime(string packageDirectory, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (NativeMethods.AbiVersion() != ExpectedAbiVersion)
            throw new InvalidDataException("The local model native runtime ABI is incompatible.");

        string root = Path.GetFullPath(packageDirectory);
        string tokenizer = ResolveDataFile(root, "spiece.model");
        foreach (string required in new[]
        {
            "config.json",
            "model.bin",
            "shared_vocabulary.json",
        })
        {
            _ = ResolveDataFile(root, required);
        }

        byte[] error = new byte[ErrorBufferBytes];
        int status = NativeMethods.Create(
            root,
            tokenizer,
            out NativeRuntimeHandle runtime,
            error,
            (nuint)error.Length);
        if (status != 0 || runtime.IsInvalid)
        {
            runtime?.Dispose();
            throw new InvalidDataException(
                $"The local model could not be loaded: {ReadUtf8(error)}");
        }
        _modelId = modelId;
        _runtime = runtime;
    }

    public PhraseTableTranslationResult Translate(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int maximumOutputCharacters)
    {
        if (!string.Equals(modelId, _modelId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sourceLanguage) ||
            string.IsNullOrWhiteSpace(targetLanguage) ||
            string.IsNullOrWhiteSpace(text) ||
            maximumOutputCharacters is < 1 or > 16_384)
            return new(false, null, "local.invalidRequest");
        if (!TargetTokens.TryGetValue(targetLanguage, out string? targetToken))
            return new(false, null, "local.languageUnsupported");

        int outputCapacity = checked(maximumOutputCharacters * 4 + 1);
        byte[] output = new byte[outputCapacity];
        byte[] error = new byte[ErrorBufferBytes];
        int status = NativeMethods.Translate(
            _runtime,
            targetToken,
            text,
            (nuint)maximumOutputCharacters,
            output,
            (nuint)output.Length,
            out nuint outputBytes,
            error,
            (nuint)error.Length);
        if (status != 0)
        {
            return new(
                false,
                null,
                status == 4 ? "local.outputLimit" : "local.translationFailed");
        }
        if (outputBytes == 0 || outputBytes >= (nuint)output.Length)
            return new(false, null, "local.invalidResponse");
        string translated;
        try
        {
            translated = new UTF8Encoding(false, true).GetString(
                output,
                0,
                checked((int)outputBytes));
        }
        catch (DecoderFallbackException)
        {
            return new(false, null, "local.invalidResponse");
        }
        return translated.Length <= maximumOutputCharacters
            ? new(true, translated, null)
            : new(false, null, "local.outputLimit");
    }

    public void Dispose() => _runtime.Dispose();

    private static string ResolveDataFile(string root, string relativePath)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, relativePath));
        var file = new FileInfo(path);
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !file.Exists ||
            file.Length < 1 ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"The local model data file '{relativePath}' is invalid.");
        return path;
    }

    private static string ReadUtf8(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private sealed class NativeRuntimeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeRuntimeHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.Destroy(handle);
            return true;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport(
            NativeLibraryName,
            EntryPoint = "infini_model_runtime_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial uint AbiVersion();

        [LibraryImport(
            NativeLibraryName,
            EntryPoint = "infini_model_runtime_create",
            StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int Create(
            string modelDirectory,
            string tokenizerPath,
            out NativeRuntimeHandle runtime,
            [Out] byte[] error,
            nuint errorCapacity);

        [LibraryImport(
            NativeLibraryName,
            EntryPoint = "infini_model_runtime_translate",
            StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int Translate(
            NativeRuntimeHandle runtime,
            string targetToken,
            string sourceText,
            nuint maximumOutputCharacters,
            [Out] byte[] output,
            nuint outputCapacity,
            out nuint outputBytes,
            [Out] byte[] error,
            nuint errorCapacity);

        [LibraryImport(
            NativeLibraryName,
            EntryPoint = "infini_model_runtime_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial void Destroy(IntPtr runtime);
    }
}
