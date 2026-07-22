using InfiniTranseon.Core.Diagnostics;

namespace InfiniTranseon.Core.Tests.Diagnostics;

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("apiKey")]
    [InlineData("api-key")]
    [InlineData("X-Secret-Header")]
    [InlineData("accessToken")]
    [InlineData("screenshotData")]
    [InlineData("sourceText")]
    public void SensitiveArgumentNamesAreReplacedWithARedactionMarker(string name)
    {
        Assert.True(LogRedactor.IsSensitiveName(name));

        IReadOnlyDictionary<string, object?> redacted = LogRedactor.RedactArguments(
            new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = "highly-secret" });

        Assert.Equal("[REDACTED]", redacted[name]);
    }

    [Theory]
    [InlineData("latencyMilliseconds")]
    [InlineData("regionId")]
    [InlineData("frameIndex")]
    public void NonSensitiveNamesAreNotFlagged(string name)
    {
        Assert.False(LogRedactor.IsSensitiveName(name));
    }

    [Fact]
    public void RedactValueKeepsStructuredScalarsButScrubsFreeText()
    {
        var when = DateTimeOffset.UnixEpoch;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["count"] = 5,
            ["ratio"] = 1.5d,
            ["enabled"] = true,
            ["when"] = when,
            ["severity"] = StatusEventSeverity.Warning,
            ["freeform"] = "some user dialogue",
            ["missing"] = null,
            ["unsupported"] = new object(),
        };

        IReadOnlyDictionary<string, object?> redacted = LogRedactor.RedactArguments(arguments);

        Assert.Equal(5, redacted["count"]);
        Assert.Equal(1.5d, redacted["ratio"]);
        Assert.Equal(true, redacted["enabled"]);
        Assert.Equal(when, redacted["when"]);
        Assert.Equal("Warning", redacted["severity"]);
        Assert.Equal("[REDACTED_TEXT]", redacted["freeform"]);
        Assert.Null(redacted["missing"]);
        Assert.Equal("[UNSUPPORTED]", redacted["unsupported"]);
    }

    [Fact]
    public void StableIdentifierValuesPassThroughUnredacted()
    {
        var identifier = new StatusIdentifier("capture.target_1:region/0");

        IReadOnlyDictionary<string, object?> redacted = LogRedactor.RedactArguments(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["identity"] = identifier });

        Assert.Equal("capture.target_1:region/0", redacted["identity"]);
    }

    [Fact]
    public void RedactArgumentsRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => LogRedactor.RedactArguments(null!));
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdEFGH12345678", "Authorization: Bearer [REDACTED]")]
    [InlineData("token=sk-live-9f8e7d6c", "token=[REDACTED]")]
    [InlineData("api-key: 1234567890abcdef", "api-key: [REDACTED]")]
    [InlineData("secret = topsecretvalue", "secret = [REDACTED]")]
    public void RedactTextScrubsBearerAndKeyValueSecrets(string input, string expected)
    {
        Assert.Equal(expected, LogRedactor.RedactText(input));
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\model.onnx")]
    [InlineData(@"\\server\share\dump.bin")]
    public void RedactTextReplacesWindowsPathsWithAPlaceholder(string path)
    {
        Assert.Equal("[PATH]", LogRedactor.RedactText(path));
    }

    [Fact]
    public void RedactTextRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => LogRedactor.RedactText(null!));
    }

    [Theory]
    [InlineData("valid.id_1:region/0", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("has#hash", false)]
    public void StableIdentifierValidationEnforcesTheAllowedCharacterSet(string value, bool expected)
    {
        Assert.Equal(expected, LogRedactor.IsStableIdentifier(value));
    }

    [Fact]
    public void StableIdentifierRejectsOverlongValues()
    {
        Assert.False(LogRedactor.IsStableIdentifier(new string('a', 129)));
        Assert.True(LogRedactor.IsStableIdentifier(new string('a', 128)));
    }
}
