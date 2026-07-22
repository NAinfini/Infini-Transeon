using System.Collections.Concurrent;
using System.Globalization;

namespace InfiniTranseon.Core.Privacy;

public enum ProxyPolicy
{
    None,
    System,
    Explicit,
}

public sealed record CredentialBinding(
    string ProviderId,
    string Purpose,
    string Scheme,
    string Host,
    int Port,
    string AuthenticationScheme,
    ProxyPolicy ProxyPolicy)
{
    public CredentialBinding Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(Scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(AuthenticationScheme);
        if (!Enum.IsDefined(ProxyPolicy))
            throw new ArgumentException("Credential proxy policy is invalid.", nameof(ProxyPolicy));
        if (!Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Credential bindings require HTTPS.", nameof(Scheme));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        return this with
        {
            Scheme = Scheme.ToLowerInvariant(),
            Host = new IdnMapping().GetAscii(Host).ToLowerInvariant(),
            AuthenticationScheme = AuthenticationScheme.ToLowerInvariant(),
        };
    }
}

public sealed class CredentialBindingException(string message) : InvalidOperationException(message);

public interface ISecretStore
{
    ValueTask WriteSecretAsync(string reference, string secret, CancellationToken cancellationToken);
    ValueTask<string?> ReadSecretAsync(string reference, CancellationToken cancellationToken);
    ValueTask DeleteSecretAsync(string reference, CancellationToken cancellationToken);
}

public interface IBoundCredentialStore
{
    ValueTask WriteAsync(
        string reference,
        string secret,
        CredentialBinding binding,
        CancellationToken cancellationToken);

    ValueTask<string?> ReadAsync(
        string reference,
        CredentialBinding expectedBinding,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(string reference, CancellationToken cancellationToken);
}

public sealed class MemoryCredentialStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public ValueTask WriteSecretAsync(
        string reference,
        string secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _secrets[reference] = secret;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadSecretAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        _secrets.TryGetValue(reference, out string? secret);
        return ValueTask.FromResult(secret);
    }

    public ValueTask DeleteSecretAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        _secrets.TryRemove(reference, out _);
        return ValueTask.CompletedTask;
    }
}

public sealed class BoundCredentialStore : IBoundCredentialStore
{
    private const int MaximumSecretCharacters = 16_384;
    private readonly ISecretStore _inner;
    private readonly ConcurrentDictionary<string, CredentialBinding> _bindings = new(StringComparer.Ordinal);

    public BoundCredentialStore(ISecretStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public async ValueTask WriteAsync(
        string reference,
        string secret,
        CredentialBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(secret.Length, MaximumSecretCharacters);
        ArgumentNullException.ThrowIfNull(binding);
        CredentialBinding normalized = binding.Normalize();
        await _inner.WriteSecretAsync(reference, secret, cancellationToken).ConfigureAwait(false);
        _bindings[reference] = normalized;
    }

    public async ValueTask<string?> ReadAsync(
        string reference,
        CredentialBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (!_bindings.TryGetValue(reference, out CredentialBinding? actual) ||
            actual != expectedBinding.Normalize())
        {
            throw new CredentialBindingException(
                "Credential origin, authentication purpose, or proxy policy changed; explicit reconfirmation is required.");
        }
        return await _inner.ReadSecretAsync(reference, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        await _inner.DeleteSecretAsync(reference, cancellationToken).ConfigureAwait(false);
        _bindings.TryRemove(reference, out _);
    }
}
