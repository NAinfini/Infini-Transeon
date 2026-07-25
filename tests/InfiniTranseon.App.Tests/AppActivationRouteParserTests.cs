using InfiniTranseon.App.Deployment;
using InfiniTranseon.App.State;

namespace InfiniTranseon.App.Tests;

public sealed class AppActivationRouteParserTests
{
    private static readonly Guid ProfileId = Guid.Parse("2f1c6b1e-0f2a-4c31-9a6d-1f2b3c4d5e6f");

    [Fact]
    public void EmptyCommandLineRequestsNoRoute()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine([]);

        Assert.Equal(AppActivationParseStatus.None, result.Status);
        Assert.Null(result.Route);
    }

    [Fact]
    public void ProfileAndStartArgumentsProduceAStartingRoute()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine(
            ["--profile", ProfileId.ToString("D"), "--start"]);

        Assert.Equal(AppActivationParseStatus.Parsed, result.Status);
        Assert.Equal(new AppActivationRoute(ProfileId, WorkspaceSection.Overview, true), result.Route);
    }

    [Fact]
    public void SectionArgumentSelectsTheWorkspaceSection()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine(
            ["--profile", ProfileId.ToString("D"), "--section", "Capture"]);

        Assert.Equal(
            new AppActivationRoute(ProfileId, WorkspaceSection.Capture, false),
            result.Route);
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("--start")]
    public void IncompleteArgumentsAreReportedAsInvalidRatherThanIgnored(string argument)
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine([argument]);

        Assert.Equal(AppActivationParseStatus.Invalid, result.Status);
        Assert.Equal("activation.commandLine.profileMissing", result.ErrorCode);
    }

    [Fact]
    public void MalformedProfileIdIsInvalid()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine(
            ["--profile", "not-a-guid"]);

        Assert.Equal("activation.commandLine.profileInvalid", result.ErrorCode);
    }

    [Fact]
    public void EmptyProfileIdIsInvalid()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine(
            ["--profile", Guid.Empty.ToString("D")]);

        Assert.Equal("activation.commandLine.profileInvalid", result.ErrorCode);
    }

    [Fact]
    public void UnknownArgumentIsInvalid()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine(["--launch-fast"]);

        Assert.Equal("activation.commandLine.unknownArgument", result.ErrorCode);
    }

    [Fact]
    public void UnknownSectionIsInvalid()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine(
            ["--profile", ProfileId.ToString("D"), "--section", "diagnostics"]);

        Assert.Equal("activation.commandLine.sectionUnknown", result.ErrorCode);
    }

    [Fact]
    public void ASoleUriArgumentIsRoutedThroughTheProtocolParser()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseCommandLine(
            [$"infinitranseon://profiles/{ProfileId:D}/capture?start=1"]);

        Assert.Equal(
            new AppActivationRoute(ProfileId, WorkspaceSection.Capture, true),
            result.Route);
    }

    [Fact]
    public void ProtocolUriWithoutSectionOpensTheOverview()
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseUri(
            $"infinitranseon://profiles/{ProfileId:D}");

        Assert.Equal(
            new AppActivationRoute(ProfileId, WorkspaceSection.Overview, false),
            result.Route);
    }

    [Theory]
    [InlineData("start=true", true)]
    [InlineData("start=0", false)]
    [InlineData("start=false", false)]
    public void StartQueryAcceptsBothSpellings(string query, bool expected)
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseUri(
            $"infinitranseon://profiles/{ProfileId:D}?{query}");

        Assert.Equal(expected, result.Route?.StartRequested);
    }

    [Theory]
    [InlineData("https://profiles/x", "activation.uri.schemeUnsupported")]
    [InlineData("infinitranseon://settings/x", "activation.uri.hostUnsupported")]
    [InlineData("infinitranseon://profiles/", "activation.uri.profileMissing")]
    [InlineData("infinitranseon://profiles/not-a-guid", "activation.uri.profileInvalid")]
    [InlineData("infinitranseon://profiles/2f1c6b1e-0f2a-4c31-9a6d-1f2b3c4d5e6f/nope", "activation.uri.sectionUnknown")]
    [InlineData("infinitranseon://profiles/2f1c6b1e-0f2a-4c31-9a6d-1f2b3c4d5e6f/capture/extra", "activation.uri.pathUnsupported")]
    [InlineData("infinitranseon://profiles/2f1c6b1e-0f2a-4c31-9a6d-1f2b3c4d5e6f?begin=1", "activation.uri.queryUnsupported")]
    public void MalformedUrisCarryASpecificErrorCode(string uri, string expectedCode)
    {
        AppActivationParseResult result = AppActivationRouteParser.ParseUri(uri);

        Assert.Equal(AppActivationParseStatus.Invalid, result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
    }
}
