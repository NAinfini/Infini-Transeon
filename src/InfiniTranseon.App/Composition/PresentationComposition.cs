using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Probes;
using InfiniTranseon.Core.Settings;
using InfiniTranseon.Core.Storage;
using InfiniTranseon.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniTranseon.App.Composition;

/// <summary>
/// Composition root for the control application. Registration is kept free of WinUI types so the same
/// graph is exercised by unit tests. View models are shared across both compositions; production
/// startup wires the real backend-integrated services (<see cref="AddRealPresentationServices"/>)
/// while tests and design-time tooling use fakes (<see cref="AddFakePresentationServices"/>).
/// </summary>
public static class PresentationComposition
{
    /// <summary>
    /// View models, runtime state, capabilities, and replaceable default registrations. Production
    /// composition replaces every runtime/probe double below with its real implementation.
    /// </summary>
    public static IServiceCollection AddPresentationViewModels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<RuntimeCapabilitiesService>();

        // Deterministic doubles for the runtime seams; the real graph overrides every one of these
        // with the EngineHost-backed implementations in AddRealPresentationServices.
        services.AddSingleton<IRuntimeControlService, FakeRuntimeControlService>();
        services.AddSingleton<ICaptureProbe, FakeCaptureProbe>();
        services.AddSingleton<IStillFrameProbe, FakeStillFrameProbe>();
        services.AddSingleton<IOcrProbe, FakeOcrProbe>();
        services.AddSingleton<ITranslationProbe, FakeTranslationProbe>();
        services.AddSingleton<IOverlayPreviewRenderer, FakeOverlayPreviewRenderer>();

        services.AddSingleton<RuntimeStateStore>();
        services.AddSingleton<RuntimeEventHub>();
        services.AddSingleton<AppNavigationState>();

        services.AddTransient<ProfileCenterViewModel>();
        services.AddTransient<RunningTargetsViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddTransient<GlossaryViewModel>();
        services.AddTransient<ServicesModelsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SetupWizardViewModel>();
        // One workspace, one draft: capture, channels, overlay and language all edit the same profile.
        // A transient registration gave every section its own view model, so section switches silently
        // dropped unsaved edits and the unsaved-changes guard could never observe them.
        services.AddSingleton<WorkbenchViewModel>();

        return services;
    }

    /// <summary>Deterministic in-memory content services for unit tests and design-time.</summary>
    public static IServiceCollection AddFakePresentationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProfileService, FakeProfileService>();
        services.AddSingleton<IWorkbenchService, FakeWorkbenchService>();
        services.AddSingleton<IHistoryService, FakeHistoryService>();
        services.AddSingleton<IDiagnosticsService, FakeDiagnosticsService>();
        services.AddSingleton<IGlossaryService, FakeGlossaryService>();
        services.AddSingleton<ISettingsService, FakeSettingsService>();
        services.AddSingleton<IAppUpdateService, FakeAppUpdateService>();
        services.AddSingleton<ISecretReferenceService, FakeSecretReferenceService>();

        return services;
    }

    /// <summary>
    /// Real Core-backed content services. The data root directory is created eagerly when each store is
    /// constructed; a failure to create it or open the database propagates out of <see cref="BuildReal"/>
    /// so startup opens the recovery window instead of silently falling back to in-memory data.
    /// </summary>
    public static IServiceCollection AddRealPresentationServices(
        this IServiceCollection services,
        AppDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        services.AddSingleton(_ =>
        {
            options.EnsureRootExists();
            return new ProfileRepository(options.DatabasePath);
        });
        services.AddSingleton(_ =>
        {
            options.EnsureRootExists();
            return new ApplicationSettingsRepository(options.DatabasePath);
        });
        services.AddSingleton(_ =>
        {
            options.EnsureRootExists();
            return new CustomRestAdapterStore(options.CustomRestAdaptersPath);
        });
        services.AddSingleton<IBoundCredentialStore, GenericCredentialStore>();
        services.AddSingleton<AppStatusLog>();
        services.AddSingleton<AppCrashReporter>();
        services.AddSingleton(provider => LocalModelManagementService.CreateProduction(
            options,
            provider.GetRequiredService<AppStatusLog>()));

        services.AddSingleton<ISecretReferenceService>(provider =>
        {
            CustomRestAdapterStore customAdapters =
                provider.GetRequiredService<CustomRestAdapterStore>();
            return new RealSecretReferenceService(
                provider.GetRequiredService<IBoundCredentialStore>(),
                provider.GetRequiredService<ApplicationSettingsRepository>(),
                () => ProviderCatalog.Default
                    .Concat(customAdapters.GetCatalogProviders())
                    .ToArray());
        });
        services.AddSingleton<ISettingsService>(provider => new RealSettingsService(
            provider.GetRequiredService<ApplicationSettingsRepository>(),
            provider.GetRequiredService<ISecretReferenceService>(),
            provider.GetRequiredService<CustomRestAdapterStore>(),
            provider.GetRequiredService<LocalModelManagementService>()));
        services.AddSingleton<IReleaseUpdateClient>(_ =>
            ReleaseUpdateComposition.CreateClient(options));
        services.AddSingleton<IAppUpdateService>(provider => new RealAppUpdateService(
            provider.GetRequiredService<IReleaseUpdateClient>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IRuntimeControlService>(),
            options,
            ReleaseUpdateComposition.CurrentApplicationVersion(),
            provider.GetRequiredService<AppStatusLog>()));
        services.AddSingleton<IProfileService>(provider => new RealProfileService(
            provider.GetRequiredService<ProfileRepository>(),
            options.DatabasePath));
        services.AddSingleton<IWorkbenchService>(provider => new RealWorkbenchService(
            provider.GetRequiredService<ProfileRepository>(),
            provider.GetRequiredService<IRuntimeControlService>(),
            provider.GetRequiredService<RuntimeCapabilitiesService>(),
            provider.GetRequiredService<CustomRestAdapterStore>()));
        services.AddSingleton<IHistoryService>(provider => new RealHistoryService(
            options,
            provider.GetRequiredService<ProfileRepository>(),
            provider.GetRequiredService<ISettingsService>()));
        services.AddSingleton<IDiagnosticsService>(_ => new RealDiagnosticsService(options));
        services.AddSingleton<IGlossaryService>(provider => new RealGlossaryService(
            provider.GetRequiredService<ProfileRepository>(),
            provider.GetRequiredService<IRuntimeControlService>()));

        // Real probes: capture enumerates live windows/monitors; OCR crop and overlay pixel preview
        // are not carried by protocol v1 and throw the typed unsupported exception; the translation
        // probe exercises the provider the caller selected, with that provider's own credential
        // bindings — the same ones the runtime uses.
        services.AddSingleton<ICaptureProbe, CaptureProbe>();
        services.AddSingleton<IStillFrameProbe, StillFrameProbe>();
        services.AddSingleton<IOcrProbe, OcrProbe>();
        services.AddSingleton<IOverlayPreviewRenderer, OverlayPreviewRenderer>();
        services.AddSingleton<ITranslationProbe>(provider => new CatalogTranslationProbe(
            provider.GetRequiredService<IBoundCredentialStore>(),
            provider.GetRequiredService<CustomRestAdapterStore>()));

        // Real engine runtime facade: locates/launches EngineHost per start, streams live targets.
        services.AddSingleton<IRuntimeControlService>(provider => new RealRuntimeControlService(
            provider.GetRequiredService<ProfileRepository>(),
            provider.GetRequiredService<ICaptureProbe>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IBoundCredentialStore>(),
            provider.GetRequiredService<RuntimeCapabilitiesService>(),
            provider.GetRequiredService<RuntimeStateStore>(),
            provider.GetRequiredService<RuntimeEventHub>(),
            options,
            provider.GetRequiredService<AppStatusLog>(),
            provider.GetRequiredService<CustomRestAdapterStore>(),
            provider.GetRequiredService<LocalModelManagementService>()));

        return services;
    }

    /// <summary>Builds the fake-backed graph used by unit tests and design-time.</summary>
    public static ServiceProvider Build()
    {
        ServiceCollection services = new();
        services.AddPresentationViewModels();
        services.AddFakePresentationServices();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Builds the real graph used at startup. Any registration or construction failure (including a
    /// database that cannot be opened) propagates so the caller can open a local recovery window.
    /// </summary>
    public static ServiceProvider BuildReal(AppDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ServiceCollection services = new();
        services.AddPresentationViewModels();
        services.AddRealPresentationServices(options);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
