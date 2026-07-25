using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// Contract behaviour of the in-process OCR probe. Recognising actual text is not asserted here: the
/// result depends on which OCR language packs the machine has installed, and a test that passes only
/// on one developer's box is worse than no test. What is asserted is that every refusal is typed and
/// coded, because the alternative failure mode — recognising Japanese with an English model — returns
/// confident nonsense rather than an error.
/// </summary>
public sealed class WindowsMediaOcrProbeTests
{
    private static OcrProbeRequest Request(byte[] crop, string? languageTag) =>
        new(new RegionId(Guid.NewGuid()), 32, 32, crop, languageTag);

    [Fact]
    public async Task An_empty_crop_is_refused_before_any_engine_is_created()
    {
        OcrProbeUnavailableException failure =
            await Assert.ThrowsAsync<OcrProbeUnavailableException>(async () =>
                await new WindowsMediaOcrProbe().RecognizeAsync(
                    Request([], "en"),
                    TestContext.Current.CancellationToken));

        Assert.Equal(OcrProbeUnavailableException.UndecodableCropCode, failure.ErrorCode);
    }

    [Fact]
    public async Task A_language_without_an_installed_pack_is_named_in_the_failure()
    {
        // Hawaiian is a well-formed BCP-47 tag that Windows OCR has never shipped a recognizer for,
        // so TryCreateFromLanguage returns null on every machine rather than only on unlucky ones.
        OcrProbeUnavailableException failure =
            await Assert.ThrowsAsync<OcrProbeUnavailableException>(async () =>
                await new WindowsMediaOcrProbe().RecognizeAsync(
                    Request([1], "haw"),
                    TestContext.Current.CancellationToken));

        Assert.Equal(OcrProbeUnavailableException.LanguageUnavailableCode, failure.ErrorCode);
        // The user has to act on this, so the message must say what is missing and what is present.
        Assert.Contains("haw", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Installed:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_any_work() =>
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new WindowsMediaOcrProbe().RecognizeAsync(
                Request([1], "en"),
                new CancellationToken(canceled: true)));

    [Fact]
    public async Task A_crop_that_is_not_an_image_fails_loudly_rather_than_returning_empty_text() =>
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await new WindowsMediaOcrProbe().RecognizeAsync(
                Request([0x00, 0x01, 0x02, 0x03], "en"),
                TestContext.Current.CancellationToken));
}
