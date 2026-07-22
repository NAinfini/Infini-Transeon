using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation.Local;

public sealed record LocalWorkerSandboxOptions(
    string HostExecutablePath,
    string? WorkerAssemblyPath,
    string ManagedModelDirectory,
    string SandboxScratchDirectory,
    long MaximumCommittedBytes,
    TimeSpan HandshakeTimeout,
    string AppContainerName = "InfiniTranseon.ModelWorker")
{
    public void Validate()
    {
        foreach (string path in new[]
        {
            HostExecutablePath,
            ManagedModelDirectory,
            SandboxScratchDirectory,
        })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException("Local worker paths must be absolute.");
        }
        if (WorkerAssemblyPath is not null && !Path.IsPathFullyQualified(WorkerAssemblyPath))
            throw new ArgumentException("Worker assembly path must be absolute.", nameof(WorkerAssemblyPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(AppContainerName);
        if (AppContainerName.Length > 64 || AppContainerName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            throw new ArgumentException("AppContainer name is invalid.", nameof(AppContainerName));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumCommittedBytes, 512L * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            MaximumCommittedBytes, LocalWorkerProtocol.DefaultMaximumCommittedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(HandshakeTimeout, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HandshakeTimeout, TimeSpan.FromMinutes(1));
        string modelRoot = Path.GetFullPath(ManagedModelDirectory);
        string scratch = Path.GetFullPath(SandboxScratchDirectory);
        string modelPrefix = modelRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string scratchPrefix = scratch.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (scratch.Equals(modelRoot, StringComparison.OrdinalIgnoreCase) ||
            scratch.StartsWith(modelPrefix, StringComparison.OrdinalIgnoreCase) ||
            modelRoot.StartsWith(scratchPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sandbox scratch and model directories must not contain each other.");
    }
}
