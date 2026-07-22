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

    public RealGlossaryService(ProfileRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<GlossarySnapshot> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        ProfileDocument? active = await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return GlossarySnapshot.Empty;
        }

        return new GlossarySnapshot(active.ProfileId, active.Name, ProfileDocumentData.ReadGlossary(active));
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

    private async Task SaveGlossaryAsync(
        ProfileDocument active,
        IReadOnlyList<GlossaryEntry> entries,
        CancellationToken cancellationToken)
    {
        ProfileDocument updated = ProfileDocumentData.WithGlossary(active, entries);
        await _repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileDocument?> GetActiveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfileDocument> documents = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return documents.FirstOrDefault();
    }

    private async Task<ProfileDocument> RequireActiveAsync(CancellationToken cancellationToken) =>
        await GetActiveAsync(cancellationToken).ConfigureAwait(false) ??
        throw new InvalidOperationException("No profile exists; create a profile before editing the glossary.");
}
