using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;

namespace InfiniTranseon.Core.Tests.Ocr;

public sealed class CloudOcrResponseParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EnsureCapacityThrowsExactlyAtTheBoxCeiling()
    {
        int ceiling = RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult;

        CloudOcrResponseParser.EnsureCapacity(ceiling - 1, "TestProvider");
        OcrRoutingException error = Assert.Throws<OcrRoutingException>(
            () => CloudOcrResponseParser.EnsureCapacity(ceiling, "TestProvider"));
        Assert.Equal("ocr.provider.tooManyLines", error.Code);
    }

    [Fact]
    public void RequiredTextReturnsPresentStringValues()
    {
        JsonElement element = Parse("""{ "text": "勇者" }""");

        Assert.Equal("勇者", CloudOcrResponseParser.RequiredText(element, "text", "TestProvider"));
    }

    [Theory]
    [InlineData("""{ "other": "x" }""")]
    [InlineData("""{ "text": 42 }""")]
    [InlineData("""{ "text": null }""")]
    public void RequiredTextRejectsMissingOrNonStringValues(string json)
    {
        OcrRoutingException error = Assert.Throws<OcrRoutingException>(
            () => CloudOcrResponseParser.RequiredText(Parse(json), "text", "TestProvider"));
        Assert.Equal("ocr.malformedResponse", error.Code);
    }

    [Fact]
    public void RequiredTextRejectsOverlongLines()
    {
        string longText = new('a', RuntimeCapabilities.VersionOne.MaxSourceChars + 1);
        JsonElement element = Parse($$"""{ "text": "{{longText}}" }""");

        OcrRoutingException error = Assert.Throws<OcrRoutingException>(
            () => CloudOcrResponseParser.RequiredText(element, "text", "TestProvider"));
        Assert.Equal("ocr.provider.outputLimit", error.Code);
    }

    [Fact]
    public void PolygonProducesANormalizedBoundingRectangle()
    {
        JsonElement vertices = Parse("""[ { "x": 10, "y": 20 }, { "x": 110, "y": 220 } ]""");

        NormalizedRect rect = CloudOcrResponseParser.Polygon(vertices, width: 1000, height: 1000);

        Assert.Equal(0.01, rect.X, 6);
        Assert.Equal(0.02, rect.Y, 6);
        Assert.Equal(0.10, rect.Width, 6);
        Assert.Equal(0.20, rect.Height, 6);
    }

    [Fact]
    public void PolygonClampsCoordinatesThatExceedTheCrop()
    {
        JsonElement vertices = Parse("""[ { "x": -50, "y": -50 }, { "x": 2000, "y": 2000 } ]""");

        NormalizedRect rect = CloudOcrResponseParser.Polygon(vertices, width: 1000, height: 1000);

        Assert.Equal(0.0, rect.X, 6);
        Assert.Equal(0.0, rect.Y, 6);
        Assert.Equal(1.0, rect.Width, 6);
        Assert.Equal(1.0, rect.Height, 6);
    }

    [Theory]
    [InlineData("""{ "not": "an-array" }""")]
    [InlineData("""[ { "x": 5, "y": 5 } ]""")]
    [InlineData("""[ { "x": 5, "y": 5 }, { "x": 5, "y": 5 } ]""")]
    public void PolygonRejectsNonArraySingleVertexOrDegenerateShapes(string json)
    {
        OcrRoutingException error = Assert.Throws<OcrRoutingException>(
            () => CloudOcrResponseParser.Polygon(Parse(json), width: 100, height: 100));
        Assert.Equal("ocr.malformedResponse", error.Code);
    }

    [Fact]
    public void GoogleParagraphTextAssemblesSymbolsAndHonoursDetectedBreaks()
    {
        JsonElement paragraph = Parse("""
            {
              "words": [
                {
                  "symbols": [
                    { "text": "He" },
                    { "text": "llo", "property": { "detectedBreak": { "type": "SPACE" } } },
                    { "text": "co", "property": { "detectedBreak": { "type": "HYPHEN" } } },
                    { "text": "de", "property": { "detectedBreak": { "type": "LINE_BREAK" } } }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("Hello co-de", CloudOcrResponseParser.GoogleParagraphText(paragraph));
    }

    [Theory]
    [InlineData("""{ "notWords": [] }""")]
    [InlineData("""{ "words": [ { "noSymbols": [] } ] }""")]
    public void GoogleParagraphTextRejectsMissingWordsOrSymbols(string json)
    {
        OcrRoutingException error = Assert.Throws<OcrRoutingException>(
            () => CloudOcrResponseParser.GoogleParagraphText(Parse(json)));
        Assert.Equal("ocr.malformedResponse", error.Code);
    }
}
