using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace InfiniTranseon.Contracts.Translation;

public enum RestHttpMethod
{
    Get,
    Post,
}

public sealed record DeclarativeRestAdapterDefinition
{
    private static readonly Regex Placeholder = new(
        "\\{\\{(?<name>[a-zA-Z][a-zA-Z0-9:.]*)\\}\\}",
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
        IEnumerable<string> credentialReferences)
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
            if (!IsSafeHeader(name) || value.Contains('\r') || value.Contains('\n'))
            {
                throw new ArgumentException("REST adapter headers contain invalid characters.", nameof(headers));
            }
            ValidateTemplate(value, credentialIds);
            ownedHeaders.Add(name, value);
        }
        if (bodyTemplate is not null) ValidateTemplate(bodyTemplate, credentialIds);

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
