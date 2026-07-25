using System.Security.Cryptography;

namespace InfiniTranseon.Core.Artifacts;

internal static class VerifiedArtifactWriter
{
    private const int BufferSize = 128 * 1024;

    public static async Task WriteAsync(
        Stream input,
        string destination,
        long expectedByteSize,
        string expectedSha256,
        string artifactName,
        Action<long>? reportBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedByteSize, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);

        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        reportBytes?.Invoke(0);
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > expectedByteSize)
            {
                throw new InvalidDataException($"{artifactName} exceeded the signed byte size.");
            }
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            reportBytes?.Invoke(total);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (total != expectedByteSize ||
            !Convert.ToHexString(hash.GetHashAndReset())
                .Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{artifactName} checksum verification failed.");
        }
    }
}
