using System.Net;
using System.Reflection;
using InfiniTranseon.Core.Updates;

namespace InfiniTranseon.App.Presentation.Services;

internal static class ReleaseUpdateComposition
{
    internal static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/NAinfini/Infini-Transeon/releases/latest");

    internal static IReleaseUpdateClient CreateClient(AppDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubReleaseUpdateService(
            static () =>
            {
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression =
                        DecompressionMethods.Brotli |
                        DecompressionMethods.Deflate |
                        DecompressionMethods.GZip,
                };
                return new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30),
                };
            },
            LatestReleaseApi,
            new SignatureVerifier(ProductionReleaseTrustRoot.Create()),
            new FileSignedSequenceState(
                options.UpdateSequencePath,
                "github:NAinfini/Infini-Transeon:stable:win-x64"));
    }

    internal static Version CurrentApplicationVersion()
    {
        Assembly assembly = typeof(RealAppUpdateService).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        string candidate = informational?
            .Split('+', 2)[0]
            .Split('-', 2)[0] ?? string.Empty;
        if (Version.TryParse(candidate, out Version? version))
            return version;
        return assembly.GetName().Version ??
            throw new InvalidOperationException("The application assembly has no parseable version.");
    }
}
