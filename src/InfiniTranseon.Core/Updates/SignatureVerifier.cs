using System.Security.Cryptography;
using System.Text.Json;
using InfiniTranseon.Contracts.Security;

namespace InfiniTranseon.Core.Updates;

public sealed record SignatureEntry(string KeyId, string Algorithm, string Signature);

public sealed class SignatureVerificationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class SignatureVerifier
{
    private readonly Ed25519TrustRootSet _trustRoot;

    public SignatureVerifier(Ed25519TrustRootSet trustRoot)
    {
        ArgumentNullException.ThrowIfNull(trustRoot);
        _trustRoot = trustRoot;
    }

    public string VerifyCanonicalJson(ReadOnlySpan<byte> documentBytes)
    {
        if (documentBytes.Length is < 2 or > 4 * 1024 * 1024)
            throw new SignatureVerificationException("signature.documentSize", "Signed document size is invalid.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(documentBytes.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            RejectDuplicateProperties(root);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("signatures", out JsonElement signatures) ||
                signatures.ValueKind != JsonValueKind.Array || signatures.GetArrayLength() is < 1 or > 2)
            {
                throw new SignatureVerificationException("signature.missing", "Signed document has no valid signature list.");
            }
            byte[] canonical = CanonicalizeWithoutSignatures(root);
            foreach (JsonElement item in signatures.EnumerateArray())
            {
                SignatureEntry? signature = item.Deserialize<SignatureEntry>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (signature is null || signature.Algorithm != "Ed25519") continue;
                if (_trustRoot.RevokedKeyIds.Contains(signature.KeyId, StringComparer.Ordinal)) continue;
                Ed25519PublicKey? key = _trustRoot.ActiveKeys.SingleOrDefault(
                    candidate => candidate.KeyId == signature.KeyId);
                if (key is null) continue;
                byte[] signatureBytes;
                try { signatureBytes = Convert.FromBase64String(signature.Signature); }
                catch (FormatException) { continue; }
                if (VerifyDetached(canonical, signatureBytes, key)) return key.KeyId;
            }
            throw new SignatureVerificationException("signature.untrusted", "No active trusted signature verified.");
        }
        catch (JsonException exception)
        {
            throw new SignatureVerificationException("signature.malformedJson", exception.Message);
        }
    }

    public bool VerifyDetached(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, Ed25519PublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (signature.Length != 64) return false;
        return Ed25519Verifier.Verify(data, signature, key.KeyBytes.Span);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new SignatureVerificationException(
                        "signature.duplicateProperty",
                        "Signed JSON contains a duplicate property name.");
                RejectDuplicateProperties(property.Value);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement item in element.EnumerateArray())
            RejectDuplicateProperties(item);
    }

    public static byte[] CanonicalizeWithoutSignatures(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, root, skipRootSignatures: true, isRoot: true);
        return stream.ToArray();
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        bool skipRootSignatures,
        bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (isRoot && skipRootSignatures && property.NameEquals("signatures")) continue;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, false, false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray()) WriteCanonical(writer, item, false, false);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long integer)) writer.WriteNumberValue(integer);
                else if (element.TryGetDecimal(out decimal decimalValue)) writer.WriteNumberValue(decimalValue);
                else writer.WriteNumberValue(element.GetDouble());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("Unsupported JSON token in signed document.");
        }
    }
}
