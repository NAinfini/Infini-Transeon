using InfiniTranseon.App.Composition;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniTranseon.App.Tests;

public sealed class BootstrapTests
{
    private static ServiceProvider Build() => PresentationComposition.Build();

    public static TheoryData<Type> PresentationServiceTypes { get; } = new(
        typeof(IProfileService),
        typeof(IRuntimeControlService),
        typeof(IHistoryService),
        typeof(IDiagnosticsService),
        typeof(IGlossaryService),
        typeof(ISettingsService),
        typeof(IAppUpdateService),
        typeof(ISecretReferenceService),
        typeof(RuntimeCapabilitiesService),
        typeof(ICaptureProbe),
        typeof(IOcrProbe),
        typeof(ITranslationProbe),
        typeof(IOverlayPreviewRenderer),
        typeof(RuntimeStateStore));

    public static TheoryData<Type> ViewModelTypes { get; } = new(
        typeof(ProfileCenterViewModel),
        typeof(RunningTargetsViewModel),
        typeof(HistoryViewModel),
        typeof(DiagnosticsViewModel),
        typeof(GlossaryViewModel),
        typeof(ServicesModelsViewModel),
        typeof(SettingsViewModel),
        typeof(SetupWizardViewModel));

    [Fact]
    public void Composition_builds_and_validates()
    {
        using ServiceProvider provider = Build();
        Assert.NotNull(provider);
    }

    [Theory]
    [MemberData(nameof(PresentationServiceTypes))]
    public void Every_presentation_service_resolves(Type serviceType)
    {
        using ServiceProvider provider = Build();
        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    [Theory]
    [MemberData(nameof(ViewModelTypes))]
    public void Every_view_model_resolves(Type viewModelType)
    {
        using ServiceProvider provider = Build();
        Assert.NotNull(provider.GetRequiredService(viewModelType));
    }

    [Fact]
    public void Capabilities_service_exposes_protocol_ceilings()
    {
        using ServiceProvider provider = Build();
        var capabilities = provider.GetRequiredService<RuntimeCapabilitiesService>();

        Assert.Equal(RuntimeCapabilities.VersionOne.MaxTargets, capabilities.Capabilities.MaxTargets);
        Assert.Equal(
            RuntimeCapabilities.VersionOne.MaxTranslationChannelsPerRegion,
            capabilities.Capabilities.MaxTranslationChannelsPerRegion);
        Assert.Null(capabilities.LatestBudget);
        Assert.Equal(0, capabilities.ReconnectRevision);
    }

    [Fact]
    public void Capabilities_service_tracks_reconnect_revision_and_budget()
    {
        var service = new Presentation.Services.RuntimeCapabilitiesService();
        Guid epoch = Guid.NewGuid();
        var budget = new RuntimeBudgetSnapshot(
            RuntimeProtocol.CurrentVersion,
            epoch,
            [new RuntimeBudgetPool("gpu", 100, 10, 0)]);
        var reconnect = new RuntimeReconnectSnapshot(
            epoch,
            profileRevision: 1,
            policyRevision: 1,
            targets: [],
            capabilities: RuntimeCapabilities.VersionOne,
            budget: budget);

        service.ApplyReconnect(reconnect);

        Assert.Equal(1, service.ReconnectRevision);
        Assert.Same(budget, service.LatestBudget);
    }
}
