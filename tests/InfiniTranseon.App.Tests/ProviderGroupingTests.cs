using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;

namespace InfiniTranseon.App.Tests;

public sealed class ProviderGroupingTests
{
    [Fact]
    public void CustomAdapterIsClassifiedAsCustomRegardlessOfOtherFlags()
    {
        var provider = new ProviderRow("Adapter", "REST", "Ready", StatusSeverity.Success, "detail")
        {
            IsCustom = true,
            IsLocalModel = true,
            IsTranslationProvider = true,
        };

        Assert.Equal(
            ServicesModelsViewModel.ProviderGroup.Custom,
            ServicesModelsViewModel.ClassifyGroup(provider));
    }

    [Fact]
    public void LocalModelIsClassifiedAsLocalModelWhenNotCustom()
    {
        var provider = new ProviderRow("Local", "Local model", "Ready", StatusSeverity.Success, "detail")
        {
            IsLocalModel = true,
            IsTranslationProvider = true,
        };

        Assert.Equal(
            ServicesModelsViewModel.ProviderGroup.LocalModel,
            ServicesModelsViewModel.ClassifyGroup(provider));
    }

    [Fact]
    public void CloudTranslationProviderIsClassifiedAsCloudTranslation()
    {
        var provider = new ProviderRow("DeepL", "Cloud", "Ready", StatusSeverity.Success, "detail")
        {
            IsTranslationProvider = true,
        };

        Assert.Equal(
            ServicesModelsViewModel.ProviderGroup.CloudTranslation,
            ServicesModelsViewModel.ClassifyGroup(provider));
    }

    [Fact]
    public void CloudOcrProviderIsClassifiedByKindWhenNotATranslationProvider()
    {
        var provider = new ProviderRow("Vision OCR", "OCR - cloud", "Ready", StatusSeverity.Success, "detail")
        {
            IsTranslationProvider = false,
        };

        Assert.Equal(
            ServicesModelsViewModel.ProviderGroup.CloudOcr,
            ServicesModelsViewModel.ClassifyGroup(provider));
    }

    [Fact]
    public void UnclassifiableProviderFallsBackToOtherInsteadOfBeingDropped()
    {
        var provider = new ProviderRow("Mystery", "Unlabeled", "Ready", StatusSeverity.Success, "detail")
        {
            IsTranslationProvider = false,
        };

        Assert.Equal(
            ServicesModelsViewModel.ProviderGroup.Other,
            ServicesModelsViewModel.ClassifyGroup(provider));
    }

    [Fact]
    public void ClassifyGroupThrowsOnNullProvider()
    {
        Assert.Throws<ArgumentNullException>(
            () => ServicesModelsViewModel.ClassifyGroup(null!));
    }
}
