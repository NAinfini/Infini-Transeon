using System;
using System.Collections.Generic;
using System.Globalization;
using InfiniTranseon.App.State;

namespace InfiniTranseon.App.Deployment;

public enum AppActivationParseStatus
{
    /// <summary>No routing information was supplied; the app opens on its default destination.</summary>
    None,

    /// <summary>A complete, valid route was supplied.</summary>
    Parsed,

    /// <summary>Routing information was supplied but is malformed. Never treated as <see cref="None"/>.</summary>
    Invalid,
}

/// <summary>Resolved deep-link target: which profile workspace to open and whether to start it.</summary>
public sealed record AppActivationRoute(Guid ProfileId, WorkspaceSection Section, bool StartRequested);

public sealed record AppActivationParseResult(
    AppActivationParseStatus Status,
    AppActivationRoute? Route,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static readonly AppActivationParseResult None = new(AppActivationParseStatus.None, null, null, null);

    public static AppActivationParseResult Parsed(AppActivationRoute route) =>
        new(AppActivationParseStatus.Parsed, route, null, null);

    public static AppActivationParseResult Invalid(string errorCode, string errorDetail) =>
        new(AppActivationParseStatus.Invalid, null, errorCode, errorDetail);
}

/// <summary>
/// Parses the two activation surfaces that address a profile directly: the <c>infinitranseon://</c>
/// protocol and the <c>--profile {id} [--section {name}] [--start]</c> command line. Both are pure
/// string-to-route translations so cold-start and hot-activation routing share one tested code path.
/// Malformed input is reported as <see cref="AppActivationParseStatus.Invalid"/> with a specific error
/// code — it is never downgraded to "no route", which would hide a broken shortcut behind a normal launch.
/// </summary>
public static class AppActivationRouteParser
{
    public const string UriScheme = "infinitranseon";
    public const string ProfilesHost = "profiles";
    public const string ProfileArgument = "--profile";
    public const string SectionArgument = "--section";
    public const string StartArgument = "--start";

    private static readonly Dictionary<string, WorkspaceSection> Sections =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["overview"] = WorkspaceSection.Overview,
            ["capture"] = WorkspaceSection.Capture,
            ["channels"] = WorkspaceSection.Channels,
            ["overlay"] = WorkspaceSection.Overlay,
            ["language"] = WorkspaceSection.Language,
            ["history"] = WorkspaceSection.History,
        };

    /// <summary>
    /// Parses process arguments. A single argument carrying the protocol scheme is routed to
    /// <see cref="ParseUri"/>, which is how unpackaged protocol activation reaches the process.
    /// </summary>
    public static AppActivationParseResult ParseCommandLine(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return AppActivationParseResult.None;
        }

        if (arguments.Count == 1 &&
            arguments[0].StartsWith(UriScheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return ParseUri(arguments[0]);
        }

        Guid? profileId = null;
        WorkspaceSection section = WorkspaceSection.Overview;
        bool start = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, StartArgument, StringComparison.OrdinalIgnoreCase))
            {
                start = true;
                continue;
            }

            if (string.Equals(argument, ProfileArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count)
                {
                    return AppActivationParseResult.Invalid(
                        "activation.commandLine.profileMissing",
                        $"{ProfileArgument} requires a profile ID.");
                }

                string value = arguments[++index];
                if (!Guid.TryParse(value, CultureInfo.InvariantCulture, out Guid parsed) ||
                    parsed == Guid.Empty)
                {
                    return AppActivationParseResult.Invalid(
                        "activation.commandLine.profileInvalid",
                        $"'{value}' is not a valid profile ID.");
                }

                profileId = parsed;
                continue;
            }

            if (string.Equals(argument, SectionArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count)
                {
                    return AppActivationParseResult.Invalid(
                        "activation.commandLine.sectionMissing",
                        $"{SectionArgument} requires a section name.");
                }

                string value = arguments[++index];
                if (!Sections.TryGetValue(value, out section))
                {
                    return AppActivationParseResult.Invalid(
                        "activation.commandLine.sectionUnknown",
                        $"'{value}' is not a workspace section.");
                }

                continue;
            }

            return AppActivationParseResult.Invalid(
                "activation.commandLine.unknownArgument",
                $"'{argument}' is not a recognized argument.");
        }

        if (profileId is not Guid target)
        {
            return start
                ? AppActivationParseResult.Invalid(
                    "activation.commandLine.profileMissing",
                    $"{StartArgument} requires {ProfileArgument} {{id}}.")
                : AppActivationParseResult.None;
        }

        return AppActivationParseResult.Parsed(new AppActivationRoute(target, section, start));
    }

    /// <summary>
    /// Parses a raw command-line string as delivered by a redirected launch activation. The leading
    /// token is the executable path whenever it is neither a flag nor a protocol URI, so it is dropped
    /// before the flags are read — argv[0] is not an argument, and dropping it is not error suppression.
    /// </summary>
    public static AppActivationParseResult ParseCommandLineString(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return AppActivationParseResult.None;
        }

        List<string> tokens = Tokenize(commandLine);
        if (tokens.Count > 0 &&
            !tokens[0].StartsWith('-') &&
            !tokens[0].StartsWith(UriScheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        return ParseCommandLine(tokens);
    }

    private static List<string> Tokenize(string commandLine)
    {
        List<string> tokens = [];
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Parses <c>infinitranseon://profiles/{id}[/{section}][?start=1]</c>.</summary>
    public static AppActivationParseResult ParseUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return AppActivationParseResult.None;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsedUri))
        {
            return AppActivationParseResult.Invalid(
                "activation.uri.malformed",
                $"'{uri}' is not an absolute URI.");
        }

        if (!string.Equals(parsedUri.Scheme, UriScheme, StringComparison.OrdinalIgnoreCase))
        {
            return AppActivationParseResult.Invalid(
                "activation.uri.schemeUnsupported",
                $"'{parsedUri.Scheme}' is not the {UriScheme} scheme.");
        }

        if (!string.Equals(parsedUri.Host, ProfilesHost, StringComparison.OrdinalIgnoreCase))
        {
            return AppActivationParseResult.Invalid(
                "activation.uri.hostUnsupported",
                $"'{parsedUri.Host}' is not a routable host.");
        }

        string[] segments = parsedUri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return AppActivationParseResult.Invalid(
                "activation.uri.profileMissing",
                "The URI carries no profile ID.");
        }

        if (segments.Length > 2)
        {
            return AppActivationParseResult.Invalid(
                "activation.uri.pathUnsupported",
                $"'{parsedUri.AbsolutePath}' has more segments than profiles/{{id}}/{{section}}.");
        }

        if (!Guid.TryParse(segments[0], CultureInfo.InvariantCulture, out Guid profileId) ||
            profileId == Guid.Empty)
        {
            return AppActivationParseResult.Invalid(
                "activation.uri.profileInvalid",
                $"'{segments[0]}' is not a valid profile ID.");
        }

        WorkspaceSection section = WorkspaceSection.Overview;
        if (segments.Length == 2 && !Sections.TryGetValue(segments[1], out section))
        {
            return AppActivationParseResult.Invalid(
                "activation.uri.sectionUnknown",
                $"'{segments[1]}' is not a workspace section.");
        }

        return ParseStartQuery(parsedUri.Query) switch
        {
            null => AppActivationParseResult.Invalid(
                "activation.uri.queryUnsupported",
                $"'{parsedUri.Query}' is not a recognized query."),
            bool start => AppActivationParseResult.Parsed(
                new AppActivationRoute(profileId, section, start)),
        };
    }

    /// <summary>Returns the requested start flag, or null when the query is not recognized.</summary>
    private static bool? ParseStartQuery(string query)
    {
        string trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return false;
        }

        bool start = false;
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (!string.Equals(parts[0], "start", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string value = parts.Length == 2 ? parts[1] : "1";
            if (value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                start = true;
                continue;
            }

            if (value is "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                start = false;
                continue;
            }

            return null;
        }

        return start;
    }
}
