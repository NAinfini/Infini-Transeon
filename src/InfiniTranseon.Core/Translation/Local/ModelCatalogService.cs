using System.Text.Json;
using InfiniTranseon.Contracts.Security;
using InfiniTranseon.Core.Updates;

namespace InfiniTranseon.Core.Translation.Local;

public sealed record ModelCatalogFile(string RelativePath, long ByteSize, string Sha256);

public sealed record ModelCatalogEntry(
    string ModelId,
    string Version,
    string LicenseSpdx,
    string Runtime,
    int Opset,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<Uri> DownloadOrigins,
    IReadOnlyList<ModelCatalogFile> Files)
{
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> SourceLanguages { get; init; } = [];
    public IReadOnlyList<string> TargetLanguages { get; init; } = [];
}

public sealed record ModelCatalogDocument(
    int SchemaVersion,
    long CatalogSequence,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<ModelCatalogEntry> Models,
    IReadOnlyList<SignatureEntry> Signatures);

public sealed class VerifiedModelCatalog
{
    private readonly IReadOnlyList<ModelCatalogEntry> _models;

    internal VerifiedModelCatalog(ModelCatalogDocument document, string verifiedByKeyId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedByKeyId);
        SchemaVersion = document.SchemaVersion;
        CatalogSequence = document.CatalogSequence;
        PublishedAtUtc = document.PublishedAtUtc;
        VerifiedByKeyId = verifiedByKeyId;
        _models = Array.AsReadOnly(document.Models.Select(Clone).ToArray());
    }

    public int SchemaVersion { get; }
    public long CatalogSequence { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public string VerifiedByKeyId { get; }
    public IReadOnlyList<ModelCatalogEntry> Models => _models;

    internal ModelCatalogEntry ResolveModel(string modelId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return _models.SingleOrDefault(model =>
                string.Equals(model.ModelId, modelId, StringComparison.Ordinal) &&
                string.Equals(model.Version, version, StringComparison.Ordinal)) ??
            throw new InvalidDataException("The requested model is absent from the verified catalog.");
    }

    private static ModelCatalogEntry Clone(ModelCatalogEntry model) => model with
    {
        Architectures = Array.AsReadOnly(model.Architectures.ToArray()),
        DownloadOrigins = Array.AsReadOnly(model.DownloadOrigins.ToArray()),
        Files = Array.AsReadOnly(model.Files.ToArray()),
        SourceLanguages = Array.AsReadOnly(model.SourceLanguages.ToArray()),
        TargetLanguages = Array.AsReadOnly(model.TargetLanguages.ToArray()),
    };
}

public sealed class ModelCatalogService
{
    private static readonly HashSet<string> ExecutableExtensions = new(
        [".bat", ".cmd", ".com", ".dll", ".exe", ".js", ".msi", ".ps1", ".py", ".scr", ".vbs"],
        StringComparer.OrdinalIgnoreCase);

    private readonly SignatureVerifier _verifier;
    private readonly ISignedSequenceState _sequence;

    public ModelCatalogService(SignatureVerifier verifier, ISignedSequenceState sequence)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(sequence);
        _verifier = verifier;
        _sequence = sequence;
    }

    public VerifiedModelCatalog LoadVerified(ReadOnlySpan<byte> json)
    {
        string verifiedByKeyId = _verifier.VerifyCanonicalJson(json);
        ModelCatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ModelCatalogDocument>(
                json.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
                throw new InvalidDataException("Model catalog is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Model catalog is malformed.", exception);
        }
        Validate(document);
        if (!_sequence.TryAccept(document.CatalogSequence))
            throw new InvalidDataException("Model catalog rollback was rejected.");
        return new VerifiedModelCatalog(document, verifiedByKeyId);
    }

    internal static void Validate(ModelCatalogDocument document)
    {
        if (document.SchemaVersion != 1 ||
            document.CatalogSequence < 1 ||
            document.Models is null ||
            document.Models.Count > 64)
            throw new InvalidDataException("Model catalog header is invalid.");
        if (document.Models.Select(item => (item.ModelId, item.Version)).Distinct().Count() != document.Models.Count)
            throw new InvalidDataException("Model catalog identities must be unique.");
        foreach (ModelCatalogEntry model in document.Models)
        {
            if (model is null)
                throw new InvalidDataException("Model catalog contains a null entry.");
            ArgumentException.ThrowIfNullOrWhiteSpace(model.ModelId);
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(model.LicenseSpdx);
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Runtime);
            if (model.Architectures is null ||
                model.DownloadOrigins is null ||
                model.Files is null ||
                model.SourceLanguages is null ||
                model.TargetLanguages is null ||
                !model.Architectures.Contains("win-x64", StringComparer.Ordinal) ||
                model.DownloadOrigins.Count == 0 ||
                model.DownloadOrigins.Count > 16 ||
                model.Files.Count is 0 or > 1024 ||
                model.Opset is < 0 or > 100 ||
                model.DownloadOrigins.Any(origin => !origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
                    !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) ||
                    !string.IsNullOrEmpty(origin.Fragment) || !origin.AbsolutePath.EndsWith('/')))
                throw new InvalidDataException($"Model '{model.ModelId}' has invalid platform or origins.");
            if (!IsIdentifier(model.ModelId))
                throw new InvalidDataException("Model identifier is not path-safe.");
            if (model.DisplayName is { Length: > 128 } ||
                model.SourceLanguages.Count > 64 ||
                model.TargetLanguages.Count > 64 ||
                model.SourceLanguages.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    model.SourceLanguages.Count ||
                model.TargetLanguages.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    model.TargetLanguages.Count ||
                model.SourceLanguages.Concat(model.TargetLanguages).Any(language =>
                    string.IsNullOrWhiteSpace(language) ||
                    language.Length > 35 ||
                    language.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                        character is '-' or '_'))))
                throw new InvalidDataException($"Model '{model.ModelId}' has invalid display metadata.");
            if (model.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
                model.Files.Count)
                throw new InvalidDataException($"Model '{model.ModelId}' contains duplicate file paths.");
            if (string.Equals(model.Runtime, "phrase-table-v1", StringComparison.Ordinal) &&
                (model.Opset != 0 || model.Files.Count != 1 ||
                    model.Files[0].RelativePath != $"phrase-tables/{model.ModelId}.json"))
                throw new InvalidDataException(
                    $"Phrase table '{model.ModelId}' must use the worker-owned canonical path.");
            if (string.Equals(
                    model.Runtime,
                    "ctranslate2-madlad-v1",
                    StringComparison.Ordinal) &&
                (model.Opset != 0 ||
                    !new[]
                    {
                        "config.json",
                        "model.bin",
                        "shared_vocabulary.json",
                        "spiece.model",
                    }.All(required => model.Files.Any(file =>
                        string.Equals(
                            file.RelativePath,
                            required,
                            StringComparison.Ordinal)))))
                throw new InvalidDataException(
                    $"CTranslate2 model '{model.ModelId}' is missing a required data file.");
            long totalBytes = 0;
            foreach (ModelCatalogFile file in model.Files)
            {
                ModelPathPolicy.ValidateRelativePath(file.RelativePath);
                if (file.ByteSize < 1 ||
                    file.Sha256 is null ||
                    file.Sha256.Length != 64 ||
                    !file.Sha256.All(character => char.IsAsciiHexDigit(character)))
                    throw new InvalidDataException($"Model '{model.ModelId}' has an invalid file.");
                try
                {
                    totalBytes = checked(totalBytes + file.ByteSize);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException(
                        $"Model '{model.ModelId}' has an invalid total size.",
                        exception);
                }
                if (ExecutableExtensions.Contains(Path.GetExtension(file.RelativePath)))
                    throw new InvalidDataException(
                        $"Model '{model.ModelId}' contains executable content. Model packages may contain data only.");
            }
        }
    }

    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');
}

public static class ModelPathPolicy
{
    public static string ResolveManagedPath(string managedRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ValidateRelativePath(relativePath);
        string root = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model path escapes the managed model directory.");
        string? current = Path.GetDirectoryName(candidate);
        while (current is not null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Model path traverses a reparse point.");
            if (current.TrimEnd(Path.DirectorySeparatorChar).Equals(
                    root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        return candidate;
    }

    public static void ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath) || relativePath.Contains('\\') ||
            relativePath.Split('/').Any(segment => segment is "" or "." or "..") ||
            relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidDataException("Model file path must be a normalized relative path.");
    }
}
