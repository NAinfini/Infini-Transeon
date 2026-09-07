using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.Contracts.Probes;

namespace InfiniTranseon.App.Tests;

public sealed class SetupWizardTranslationTestTests
{
    [Fact]
    public async Task Connectivity_sample_is_declared_as_english_without_changing_the_ocr_language()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var probe = new RecordingTranslationProbe();
        var viewModel = new SetupWizardViewModel(
            new FakeCaptureProbe(),
            new FakeOcrProbe(),
            probe,
            new FakeSettingsService(),
            new FakeSecretReferenceService(),
            new FakeProfileService());
        await viewModel.InitializeAsync(ct);
        viewModel.SourceLanguage = "ja";
        viewModel.TargetLanguage = "zh-Hans";
        viewModel.SelectedProvider = viewModel.Providers.First();

        await viewModel.TestTranslationCommand.ExecuteAsync(null);

        Assert.NotNull(probe.Request);
        Assert.Equal("Hello, adventurer.", probe.Request.SourceText);
        Assert.Equal("en", probe.Request.SourceLanguage);
        Assert.Equal("zh-Hans", probe.Request.TargetLanguage);
        Assert.Equal(viewModel.SelectedProvider.Id, probe.Request.ProviderId);
        Assert.Equal("ja", viewModel.SourceLanguage);
    }

    private sealed class RecordingTranslationProbe : ITranslationProbe
    {
        public TranslationProbeRequest? Request { get; private set; }

        public ValueTask<TranslationProbeResult> TranslateAsync(
            TranslationProbeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(new TranslationProbeResult(
                request.ProviderId ?? string.Empty,
                "你好，冒险者。",
                TimeSpan.FromMilliseconds(1),
                ErrorCode: null));
        }
    }
}
