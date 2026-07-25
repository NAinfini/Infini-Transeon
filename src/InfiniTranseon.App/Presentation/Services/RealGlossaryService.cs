using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Real glossary service. The glossary lives inside the active profile document's extension data and
/// persists through the Core <see cref="ProfileRepository"/>. The active profile is the most recently
/// updated one (profiles are listed newest-first); every save refreshes that ordering, so glossary
/// edits stay pinned to the same profile. With no profile yet, the snapshot reports an empty state.
/// </summary>
public sealed class RealGlossaryService : IGlossaryService
{
    private readonly ProfileRepository _repository;
    private readonly IRuntimeControlService? _runtime;
    private Guid _selectedProfileId;

    public RealGlossaryService(
        ProfileRepository repository,
        IRuntimeControlService? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _runtime = runtime;
    }

    public void SelectProfile(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }
        _selectedProfileId = profileId;
    }

    public async Task<GlossarySnapshot> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        ProfileDocument? active = await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return GlossarySnapshot.Empty;
        }

        return new GlossarySnapshot(
            active.ProfileId,
            active.Name,
            ProfileDocumentData.ReadGlossary(active),
            active.StylePrompt.Versions
                .OrderByDescending(version => version.Version)
                .Select(version => new StylePromptVersion(
                    version.Version,
                    version.Name,
                    version.Template,
                    version.CreatedAt))
                .ToArray(),
            active.StylePrompt.ActiveVersion);
    }

    public async Task AddOrUpdateAsync(
        GlossaryEntry entry,
        string? replacingSourceTerm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SourceTerm);
        ProfileDocument active = await RequireActiveAsync(cancellationToken).ConfigureAwait(false);

        List<GlossaryEntry> entries = [.. ProfileDocumentData.ReadGlossary(active)];
        string removeKey = string.IsNullOrWhiteSpace(replacingSourceTerm) ? entry.SourceTerm : replacingSourceTerm;
        entries.RemoveAll(existing =>
            string.Equals(existing.SourceTerm, removeKey, StringComparison.Ordinal) ||
            string.Equals(existing.SourceTerm, entry.SourceTerm, StringComparison.Ordinal));
        entries.Add(entry);

        await SaveGlossaryAsync(active, entries, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string sourceTerm, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTerm);
        ProfileDocument active = await RequireActiveAsync(cancellationToken).ConfigureAwait(false);

        List<GlossaryEntry> entries = [.. ProfileDocumentData.ReadGlossary(active)];
        entries.RemoveAll(existing => string.Equals(existing.SourceTerm, sourceTerm, StringComparison.Ordinal));

        await SaveGlossaryAsync(active, entries, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportAsync(
        IReadOnlyList<GlossaryEntry> imported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imported);
        ProfileDocument active = await RequireActiveAsync(cancellationToken).ConfigureAwait(false);
        var entries = ProfileDocumentData.ReadGlossary(active)
            .ToDictionary(entry => entry.SourceTerm, StringComparer.Ordinal);
        foreach (GlossaryEntry entry in imported)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.SourceTerm);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.TargetTerm);
            entries[entry.SourceTerm] = entry;
        }
        await SaveGlossaryAsync(
            active,
            entries.Values.OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal).ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveStylePromptVersionAsync(
        string name,
        string template,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        if (name.Length > 128 || template.Length > 8_192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(template),
                "Style prompt names are limited to 128 characters and templates to 8192 characters.");
        }

        ProfileDocument active = await RequireActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.StylePrompt.Versions.Count >= 32)
        {
            throw new InvalidOperationException(
                "A profile can retain at most 32 style prompt versions.");
        }
        int version = active.StylePrompt.Versions.Count == 0
            ? 1
            : checked(active.StylePrompt.Versions.Max(item => item.Version) + 1);
        List<ProfileStylePromptVersion> versions = [.. active.StylePrompt.Versions];
        versions.Add(new ProfileStylePromptVersion
        {
            Version = version,
            Name = name.Trim(),
            Template = template.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        ProfileDocument updated = active with
        {
            StylePrompt = new ProfileStylePromptSettings
            {
                ActiveVersion = version,
                Versions = versions,
            },
        };
        await SaveAndApplyAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateStylePromptVersionAsync(
        int version,
        CancellationToken cancellationToken = default)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
        ProfileDocument active = await RequireActiveAsync(cancellationToken).ConfigureAwait(false);
        if (!active.StylePrompt.Versions.Any(item => item.Version == version))
        {
            throw new KeyNotFoundException($"Style prompt version {version} was not found.");
        }
        await SaveAndApplyAsync(
            active with
            {
                StylePrompt = active.StylePrompt with { ActiveVersion = version },
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveGlossaryAsync(
        ProfileDocument active,
        IReadOnlyList<GlossaryEntry> entries,
        CancellationToken cancellationToken)
    {
        ProfileDocument updated = ProfileDocumentData.WithGlossary(active, entries);
        await SaveAndApplyAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAndApplyAsync(
        ProfileDocument document,
        CancellationToken cancellationToken)
    {
        await _repository.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        if (_runtime is not null)
        {
            await _runtime
                .ApplyProfileAsync(document.ProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<ProfileDocument?> GetActiveAsync(CancellationToken cancellationToken)
    {
        if (_selectedProfileId != Guid.Empty)
        {
            return await _repository.LoadAsync(_selectedProfileId, cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<ProfileDocument> documents = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return documents.FirstOrDefault();
    }

    private async Task<ProfileDocument> RequireActiveAsync(CancellationToken cancellationToken) =>
        await GetActiveAsync(cancellationToken).ConfigureAwait(false) ??
        throw new InvalidOperationException("No profile exists; create a profile before editing the glossary.");
}
