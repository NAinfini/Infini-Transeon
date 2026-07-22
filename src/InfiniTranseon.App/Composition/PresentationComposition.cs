using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Probes;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniTranseon.App.Composition;

/// <summary>
/// Composition root for the control application. Registration is kept free of WinUI types so the
/// same graph is exercised by unit tests. Fakes are registered as the current implementations;
/// real backend-integrated services replace them at their integration gates.
/// </summary>
public static class PresentationComposition
{
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Presentation services (fakes until backend gates).
        services.AddSingleton<IProfileService, FakeProfileService>();
        services.AddSingleton<IRuntimeControlService, FakeRuntimeControlService>();
        services.AddSingleton<IHistoryService, FakeHistoryService>();
        services.AddSingleton<IDiagnosticsService, FakeDiagnosticsService>();
        services.AddSingleton<IGlossaryService, FakeGlossaryService>();
        services.AddSingleton<ISettingsService, FakeSettingsService>();
        services.AddSingleton<ISecretReferenceService, FakeSecretReferenceService>();
        services.AddSingleton<IRuntimeCapabilitiesService, RuntimeCapabilitiesService>();

        // Probe doubles for setup/workbench UI (real integrations at backend Tasks 3/6/8/11).
        services.AddSingleton<ICaptureProbe, FakeCaptureProbe>();
        services.AddSingleton<IOcrProbe, FakeOcrProbe>();
        services.AddSingleton<ITranslationProbe, FakeTranslationProbe>();
        services.AddSingleton<IOverlayPreviewRenderer, FakeOverlayPreviewRenderer>();

        // Single runtime state store.
        services.AddSingleton<RuntimeStateStore>();

        // View models (new instance per navigation).
        services.AddTransient<ProfileCenterViewModel>();
        services.AddTransient<RunningTargetsViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddTransient<GlossaryViewModel>();
        services.AddTransient<ServicesModelsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SetupWizardViewModel>();

        return services;
    }

    /// <summary>
    /// Builds the service provider. Any registration or construction failure propagates so the
    /// caller can open a local recovery window instead of reporting fake success.
    /// </summary>
    public static ServiceProvider Build()
    {
        ServiceCollection services = new();
        services.AddPresentationServices();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
