using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace InfiniTranseon.Contracts.Translation;

public enum RestHttpMethod
{
    Get,
    Post,
}

public enum RestBodyFormat { JsonUtf8, FormUrlEncodedUtf8 }
public enum RestResponseFormat { Json, ServerSentEvents }

public sealed record RestResponseLimits(
    int MaximumHeaderBytes = 32 * 1024,
    int MaximumCompressedBytes = 2 * 1024 * 1024,
    int MaximumDecompressedBytes = 4 * 1024 * 1024,
    int MaximumJsonDepth = 32,
    int MaximumSseEventBytes = 64 * 1024,
    int MaximumCumulativeCharacters = 1_000_000,
    int TimeoutMilliseconds = 30_000,
    int IdleTimeoutMilliseconds = 15_000);

public sealed record RestStatusMapping(string ErrorCode, bool Retryable);

public sealed record DeclarativeRestAdapterDefinition
{
    private static readonly Regex Placeholder = new(
        "\\{\\{(?<name>[a-zA-Z][a-zA-Z0-9:.-]*)\\}\\}",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedVariables = new(StringComparer.Ordinal)
    {
        "sourceText",
        "sourceLanguage",
        "targetLanguage",
        "context",
        "gameName",
        "gameDescription",
        "glossary",
    };
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Upgrade",
    };

    public DeclarativeRestAdapterDefinition(
        int schemaVersion,
        string id,
        string displayName,
        Uri endpoint,
        RestHttpMethod method,
        IReadOnlyDictionary<string, string> headers,
        string? bodyTemplate,
        string responseTextJsonPointer,
        string? responseErrorJsonPointer,
        IEnumerable<string> credentialReferences,
        RestBodyFormat bodyFormat = RestBodyFormat.JsonUtf8,
        RestResponseFormat responseFormat = RestResponseFormat.Json,
        RestResponseLimits? responseLimits = null,
        IReadOnlyDictionary<int, RestStatusMapping>? statusMappings = null,
        string sseDoneMarker = "[DONE]")
    {
        if (schemaVersion != 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseTextJsonPointer);
        ArgumentNullException.ThrowIfNull(credentialReferences);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("REST adapter endpoints must be absolute HTTPS URIs without user info.", nameof(endpoint));
        }
        if (!responseTextJsonPointer.StartsWith("/", StringComparison.Ordinal) ||
            responseErrorJsonPointer is not null && !responseErrorJsonPointer.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Response selectors must be JSON pointers.", nameof(responseTextJsonPointer));
        }

        string[] credentialIds = credentialReferences.ToArray();
        if (credentialIds.Any(string.IsNullOrWhiteSpace) ||
            credentialIds.Distinct(StringComparer.Ordinal).Count() != credentialIds.Length)
        {
            throw new ArgumentException("Credential references must be non-empty and unique.", nameof(credentialReferences));
        }

        var ownedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in headers)
        {
            if (!IsSafeHeader(name) || ForbiddenHeaders.Contains(name) ||
                value.Contains('\r') || value.Contains('\n'))
            {
                throw new ArgumentException("REST adapter headers contain invalid characters.", nameof(headers));
            }
            ValidateTemplate(value, credentialIds);
            ownedHeaders.Add(name, value);
        }
        if (bodyTemplate is not null) ValidateTemplate(bodyTemplate, credentialIds);
        if (method == RestHttpMethod.Get && bodyTemplate is not null)
            throw new ArgumentException("GET adapters cannot contain a request body.", nameof(bodyTemplate));
        RestResponseLimits limits = responseLimits ?? new RestResponseLimits();
        if (limits.MaximumHeaderBytes is < 1024 or > 128 * 1024 ||
            limits.MaximumCompressedBytes is < 1024 or > 8 * 1024 * 1024 ||
            limits.MaximumDecompressedBytes is < 1024 or > 16 * 1024 * 1024 ||
            limits.MaximumJsonDepth is < 1 or > 64 ||
            limits.MaximumSseEventBytes is < 256 or > 1024 * 1024 ||
            limits.MaximumCumulativeCharacters is < 1 or > 1_000_000 ||
            limits.TimeoutMilliseconds is < 100 or > 300_000 ||
            limits.IdleTimeoutMilliseconds is < 100 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(responseLimits));
        var mappings = new Dictionary<int, RestStatusMapping>();
        foreach ((int status, RestStatusMapping mapping) in statusMappings ??
                     new Dictionary<int, RestStatusMapping>())
        {
            if (status is < 400 or > 599 || string.IsNullOrWhiteSpace(mapping.ErrorCode) ||
                mapping.ErrorCode.Length > 128) throw new ArgumentException("REST status mapping is invalid.", nameof(statusMappings));
            mappings.Add(status, mapping);
        }
        if (responseFormat == RestResponseFormat.ServerSentEvents && string.IsNullOrEmpty(sseDoneMarker))
            throw new ArgumentException("SSE adapters require a done marker.", nameof(sseDoneMarker));

        SchemaVersion = schemaVersion;
        Id = id;
        DisplayName = displayName;
        Endpoint = endpoint;
        Method = method;
        Headers = new ReadOnlyDictionary<string, string>(ownedHeaders);
        BodyTemplate = bodyTemplate;
        ResponseTextJsonPointer = responseTextJsonPointer;
        ResponseErrorJsonPointer = responseErrorJsonPointer;
        CredentialReferences = Array.AsReadOnly(credentialIds);
        BodyFormat = bodyFormat;
        ResponseFormat = responseFormat;
        ResponseLimits = limits;
        StatusMappings = new ReadOnlyDictionary<int, RestStatusMapping>(mappings);
        SseDoneMarker = sseDoneMarker;
    }

    public int SchemaVersion { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public Uri Endpoint { get; }
    public RestHttpMethod Method { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public string? BodyTemplate { get; }
    public string ResponseTextJsonPointer { get; }
    public string? ResponseErrorJsonPointer { get; }
    public IReadOnlyList<string> CredentialReferences { get; }
    public RestBodyFormat BodyFormat { get; }
    public RestResponseFormat ResponseFormat { get; }
    public RestResponseLimits ResponseLimits { get; }
    public IReadOnlyDictionary<int, RestStatusMapping> StatusMappings { get; }
    public string SseDoneMarker { get; }

    private static bool IsSafeHeader(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static void ValidateTemplate(string template, IReadOnlyCollection<string> credentialIds)
    {
        foreach (Match match in Placeholder.Matches(template))
        {
            string name = match.Groups["name"].Value;
            if (AllowedVariables.Contains(name)) continue;
            const string credentialPrefix = "credential:";
            if (name.StartsWith(credentialPrefix, StringComparison.Ordinal) &&
                credentialIds.Contains(name[credentialPrefix.Length..], StringComparer.Ordinal))
            {
                continue;
            }
            throw new ArgumentException($"Template variable '{name}' is not allowed.", nameof(template));
        }

        string withoutPlaceholders = Placeholder.Replace(template, string.Empty);
        if (withoutPlaceholders.Contains("{{", StringComparison.Ordinal) ||
            withoutPlaceholders.Contains("}}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Template contains malformed placeholders.", nameof(template));
        }
    }
}
