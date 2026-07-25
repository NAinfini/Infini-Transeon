using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.Controls;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;
using InfiniTranseon.Core.Translation;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using ApplicationSettingsRepository = InfiniTranseon.Core.Settings.ApplicationSettingsRepository;

namespace InfiniTranseon.App.Tests;

// Round-trip tests exercising the real Core-backed services against an isolated temp-directory
// database, so persistence is verified end to end without touching the user's real data root. Each
// test creates its own directory and deletes it afterward; the credential-store test uses a
// test-prefixed reference and always removes what it writes.
public sealed class RealServiceIntegrationTests
{
    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "InfiniTranseonTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProfileEditModel NewDraft(
        string name = "Elden Ring",
        string provider = "DeepL") =>
        new(Guid.Empty, name, "ja", "zh-Hans", Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "ELDEN RING", "Window", "3840x2160 - 144dpi", provider,
            [new ProfileRegionDraft("Dialogue", RegionPriorityLevel.P0)]);

    [Fact]
    public async Task History_reads_all_profiles_and_persists_exact_text_corrections()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var profiles = new ProfileRepository(options.DatabasePath);
            ProfileDocument first = ProfileDocument.Create("First", "ja", "zh-Hans");
            ProfileDocument second = ProfileDocument.Create("Second", "en", "zh-Hans");
            await profiles.SaveAsync(first, ct);
            await profiles.SaveAsync(second, ct);
            var history = new HistoryRepository(
                options.DatabasePath,
                new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(30)));
            await history.SaveAsync(new HistoryRecord(
                first.ProfileId,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "source one",
                [new HistoryTranslationResult(
                    Guid.NewGuid(),
                    "translation.deepl",
                    "result one",
                    "Initial",
                    10,
                    null,
                    null,
                    CacheHit: false,
                    ErrorCode: null)]), ct);
            await history.SaveAsync(new HistoryRecord(
                second.ProfileId,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "source two",
                [new HistoryTranslationResult(
                    Guid.NewGuid(),
                    "translation.azure",
                    "result two",
                    "Initial",
                    20,
                    null,
                    null,
                    CacheHit: false,
                    ErrorCode: null)]), ct);
            var service = new RealHistoryService(
                options,
                profiles,
                new FakeSettingsService());

            IReadOnlyList<HistoryEvent> global = await service.GetEventsAsync(ct);

            Assert.Equal(2, global.Count);
            Assert.Equal("Second", global[0].ProfileName);
            service.SelectProfile(first.ProfileId);
            HistoryEvent selected = Assert.Single(await service.GetEventsAsync(ct));
            Assert.Equal(first.ProfileId, selected.ProfileId);

            await service.SaveCorrectionAsync(selected, "corrected one", ct);

            TranslationCorrection? correction = await new CorrectionStore(options.DatabasePath)
                .FindAsync(
                    new CorrectionScope(
                        first.ProfileId,
                        null,
                        first.SourceLanguage,
                        first.TargetLanguage,
                        "1"),
                    selected.SourceText,
                    ct);
            Assert.Equal("corrected one", correction?.Corrected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_saves_and_survives_a_new_repository_instance()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var service = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            Guid id = await service.SaveAsync(NewDraft(), ct);
            Assert.NotEqual(Guid.Empty, id);

            // Simulate an app restart: a brand-new repository over the same database file.
            var reopened = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            IReadOnlyList<ProfileCard> cards = await reopened.GetProfilesAsync(ct);
            Assert.Contains(cards, card => card.ProfileId == id && card.Name == "Elden Ring");

            ProfileEditModel? loaded = await reopened.LoadForEditAsync(id, ct);
            Assert.NotNull(loaded);
            Assert.Equal("ja", loaded!.SourceLanguage);
            Assert.Equal("zh-Hans", loaded.TargetLanguage);
            Assert.Single(loaded.Regions);

            await reopened.DeleteAsync(id, ct);
            Assert.Empty(await reopened.GetProfilesAsync(ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_metadata_save_preserves_region_geometry_and_other_targets()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var repository = new ProfileRepository(options.DatabasePath);
            Guid primaryTargetId = Guid.NewGuid();
            Guid regionId = Guid.NewGuid();
            Guid secondaryTargetId = Guid.NewGuid();
            var original = ProfileDocument.Create("Geometry", "ja", "zh-Hans") with
            {
                Targets =
                [
                    ProfileTarget.Create("Game", CaptureTargetKind.Window) with
                    {
                        TargetId = primaryTargetId,
                        Regions =
                        [
                            ProfileRegion.Create(
                                "Dialogue",
                                new NormalizedRect(0.1, 0.65, 0.8, 0.25)) with
                            {
                                RegionId = regionId,
                                Priority = RegionPriority.P0,
                            },
                        ],
                    },
                    ProfileTarget.Create("Guide", CaptureTargetKind.Display) with
                    {
                        TargetId = secondaryTargetId,
                    },
                ],
            };
            await repository.SaveAsync(original, ct);
            var service = new RealProfileService(repository, options.DatabasePath);
            ProfileEditModel loaded = Assert.IsType<ProfileEditModel>(
                await service.LoadForEditAsync(original.ProfileId, ct));

            await service.SaveAsync(loaded with { Name = "Geometry renamed" }, ct);

            ProfileDocument saved = Assert.IsType<ProfileDocument>(
                await repository.LoadAsync(original.ProfileId, ct));
            Assert.Equal(2, saved.Targets.Count);
            Assert.Contains(saved.Targets, target => target.TargetId == secondaryTargetId);
            ProfileRegion region = Assert.Single(
                saved.Targets.Single(target => target.TargetId == primaryTargetId).Regions);
            Assert.Equal(regionId, region.RegionId);
            Assert.Equal(new NormalizedRect(0.1, 0.65, 0.8, 0.25), region.Bounds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Workbench_saves_all_targets_and_returns_runtime_apply_result()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var repository = new ProfileRepository(options.DatabasePath);
            ProfileRegion region = ProfileRegion.Create(
                "Dialogue",
                new NormalizedRect(0.1, 0.65, 0.8, 0.25)) with
            {
                Priority = RegionPriority.P0,
                TranslationChannels =
                [
                    ProfileTranslationChannel.Create("translation.deepl"),
                ],
            };
            var original = ProfileDocument.Create("Workbench", "ja", "en") with
            {
                Targets =
                [
                    ProfileTarget.Create("Game", CaptureTargetKind.Window) with
                    {
                        Regions = [region],
                    },
                    ProfileTarget.Create("Guide", CaptureTargetKind.Display) with
                    {
                        Regions =
                        [
                            ProfileRegion.Create(
                                "Guide text",
                                new NormalizedRect(0.2, 0.2, 0.6, 0.6)) with
                            {
                                TranslationChannels =
                                [
                                    ProfileTranslationChannel.Create(
                                        "translation.deepl"),
                                ],
                            },
                        ],
                    },
                ],
            };
            await repository.SaveAsync(original, ct);
            var runtime = new FakeRuntimeControlService();
            await runtime.StartAsync(original.ProfileId, ct);
            var service = new RealWorkbenchService(
                repository,
                runtime,
                new RuntimeCapabilitiesService());
            WorkbenchProfileDraft loaded = Assert.IsType<WorkbenchProfileDraft>(
                await service.LoadAsync(original.ProfileId, ct));
            WorkbenchTargetDraft first = loaded.Targets[0];
            WorkbenchRegionDraft changedRegion = first.Regions[0] with
            {
                X = 0.2,
                Y = 0.55,
                Width = 0.7,
                Height = 0.3,
                RecognitionIntervalMilliseconds = 750,
                PreferredFontSize = 30,
                OutlineColor = "#FF123456",
                OutlineWidth = 2,
                Channels =
                [
                    first.Regions[0].Channels[0] with
                    {
                        RefinementProviderIds =
                        [
                            "llm.openai",
                            "llm.anthropic",
                        ],
                    },
                ],
            };
            WorkbenchProfileDraft changed = loaded with
            {
                Targets =
                [
                    first with
                    {
                        ScanRemainingArea = true,
                        RemainingAreaIntervalMilliseconds = 1500,
                        Regions = [changedRegion],
                    },
                    loaded.Targets[1],
                ],
            };

            ProfileRuntimeApplyResult result =
                await service.SaveAndApplyAsync(changed, ct);

            Assert.Equal(ProfileRuntimeApplyResult.HotApplied, result);
            ProfileDocument saved = Assert.IsType<ProfileDocument>(
                await repository.LoadAsync(original.ProfileId, ct));
            Assert.Equal(2, saved.Targets.Count);
            Assert.Equal(
                new NormalizedRect(0.2, 0.55, 0.7, 0.3),
                saved.Targets[0].Regions[0].Bounds);
            Assert.Equal(
                750,
                saved.Targets[0].Regions[0].RecognitionInterval.TotalMilliseconds);
            Assert.Equal(30, saved.Targets[0].Regions[0].Overlay.PreferredFontSize);
            Assert.Equal("#FF123456", saved.Targets[0].Regions[0].Overlay.OutlineColor);
            Assert.Equal(2, saved.Targets[0].Regions[0].Overlay.OutlineWidth);
            Assert.Equal(
                ["llm.openai", "llm.anthropic"],
                saved.Targets[0].Regions[0].TranslationChannels[0]
                    .RefinementSteps.Select(step => step.ProviderId));
            ProfileRegion remaining = Assert.IsType<ProfileRegion>(
                saved.Targets[0].RemainingAreaRegion);
            Assert.True(saved.Targets[0].ScanRemainingArea);
            Assert.Equal(CaptureAreaKind.RemainingArea, remaining.AreaMode);
            Assert.Equal(1500, remaining.RecognitionInterval.TotalMilliseconds);
            Assert.Equal(
                "translation.deepl",
                Assert.Single(remaining.TranslationChannels).InitialProviderId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_export_import_creates_a_copy_without_overwriting_the_source()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var service = new RealProfileService(
                new ProfileRepository(options.DatabasePath),
                options.DatabasePath);
            Guid originalId = await service.SaveAsync(NewDraft("Portable"), ct);
            using var archive = new MemoryStream();
            await service.ExportAsync(originalId, archive, ct);
            archive.Position = 0;

            Guid importedId = await service.ImportAsync(archive, ct);

            Assert.NotEqual(originalId, importedId);
            IReadOnlyList<ProfileCard> profiles = await service.GetProfilesAsync(ct);
            Assert.Equal(2, profiles.Count);
            Assert.All(profiles, profile => Assert.Equal("Portable", profile.Name));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_rejects_a_local_model_that_is_not_runtime_ready()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var service = new RealProfileService(
                new ProfileRepository(options.DatabasePath),
                options.DatabasePath);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SaveAsync(
                    NewDraft(provider: "Local MADLAD-400 3B"),
                    ct));

            Assert.Contains("not installed", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_report_unwired_local_model_as_not_installed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var settings = new RealSettingsService(
                new ApplicationSettingsRepository(options.DatabasePath),
                new FakeSecretReferenceService());

            ProviderRow local = Assert.Single(
                await settings.GetProvidersAsync(ct),
                provider => provider.Name == "Local MADLAD-400 3B");

            Assert.Contains("Not installed", local.StateText, StringComparison.Ordinal);
            Assert.Equal(StatusSeverity.Neutral, local.StateSeverity);
            Assert.False(local.IsSelectable);
            Assert.True(local.IsLocalModel);
            Assert.False(local.CanConfigure);
            Assert.False(local.CanDownloadModel);

            ProviderRow cloudOcr = Assert.Single(
                await settings.GetProvidersAsync(ct),
                provider => provider.Name == "Google Cloud Vision");
            Assert.False(cloudOcr.IsTranslationProvider);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Glossary_entry_persists_into_active_profile_and_survives_reopen()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var profiles = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            await profiles.SaveAsync(NewDraft(), ct);

            var glossary = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            var entry = new GlossaryEntry("term-src", "term-dst", "Profile", CaseSensitive: false, Protected: true, "item");
            await glossary.AddOrUpdateAsync(entry, replacingSourceTerm: null, ct);

            var reopened = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            GlossarySnapshot snapshot = await reopened.GetEntriesAsync(ct);
            Assert.True(snapshot.HasActiveProfile);
            Assert.Contains(snapshot.Entries, e => e.SourceTerm == "term-src" && e.Protected);

            await reopened.RemoveAsync("term-src", ct);
            Assert.DoesNotContain((await reopened.GetEntriesAsync(ct)).Entries, e => e.SourceTerm == "term-src");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Covers the Glossary inline-edit rename path (spec 5.7): editing a row calls AddOrUpdateAsync with
    // replacingSourceTerm set to the original source term, so the old entry is actually removed instead
    // of leaving a duplicate behind (the audited "only delete-then-readd" defect).
    [Fact]
    public async Task Glossary_rename_via_replacingSourceTerm_removes_the_original_term()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var profiles = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            await profiles.SaveAsync(NewDraft(), ct);

            var glossary = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            var original = new GlossaryEntry("old-src", "old-dst", "Profile", CaseSensitive: false, Protected: false, "");
            await glossary.AddOrUpdateAsync(original, replacingSourceTerm: null, ct);

            var renamed = new GlossaryEntry("new-src", "new-dst", "Profile", CaseSensitive: false, Protected: true, "renamed");
            await glossary.AddOrUpdateAsync(renamed, replacingSourceTerm: "old-src", ct);

            GlossarySnapshot snapshot = await glossary.GetEntriesAsync(ct);
            Assert.DoesNotContain(snapshot.Entries, e => e.SourceTerm == "old-src");
            GlossaryEntry kept = Assert.Single(snapshot.Entries, e => e.SourceTerm == "new-src");
            Assert.Equal("new-dst", kept.TargetTerm);
            Assert.True(kept.Protected);

            var reopened = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            GlossarySnapshot afterReopen = await reopened.GetEntriesAsync(ct);
            Assert.Single(afterReopen.Entries);
            Assert.DoesNotContain(afterReopen.Entries, e => e.SourceTerm == "old-src");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Glossary_edit_without_a_profile_fails_loudly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var glossary = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            var entry = new GlossaryEntry("x", "y", "Profile", CaseSensitive: false, Protected: false, "");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => glossary.AddOrUpdateAsync(entry, replacingSourceTerm: null, ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Style_prompt_versions_persist_and_active_version_changes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var profiles = new RealProfileService(
                new ProfileRepository(options.DatabasePath),
                options.DatabasePath);
            Guid profileId = await profiles.SaveAsync(NewDraft(), ct);
            var glossary = new RealGlossaryService(
                new ProfileRepository(options.DatabasePath));
            glossary.SelectProfile(profileId);

            await glossary.SaveStylePromptVersionAsync(
                "Natural",
                "Use short, natural game dialogue.",
                ct);
            await glossary.SaveStylePromptVersionAsync(
                "Literal",
                "Prefer literal terminology.",
                ct);
            GlossarySnapshot snapshot = await glossary.GetEntriesAsync(ct);
            Assert.Equal(2, snapshot.ActiveStylePromptVersion);
            Assert.Equal(2, snapshot.StylePromptVersions.Count);

            await glossary.ActivateStylePromptVersionAsync(1, ct);
            GlossarySnapshot reopened = await new RealGlossaryService(
                    new ProfileRepository(options.DatabasePath))
                .GetEntriesAsync(ct);
            Assert.Equal(1, reopened.ActiveStylePromptVersion);
            Assert.Equal(
                "Use short, natural game dialogue.",
                reopened.StylePromptVersions.Single(item => item.Version == 1).Template);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_round_trip_across_new_store_instances()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            ISettingsService Make() => new RealSettingsService(
                new ApplicationSettingsRepository(options.DatabasePath),
                new FakeSecretReferenceService());

            var settings = new ApplicationSettings(
                UiThemePreference.Dark,
                StrictOffline: true,
                HistoryRetention.Days90,
                "zh-CN",
                PerformancePreset: AppPerformancePreset.Performance,
                ReducedMotion: true);
            await Make().UpdateAsync(settings, ct);

            ApplicationSettings loaded = await Make().GetSettingsAsync(ct);
            Assert.Equal(UiThemePreference.Dark, loaded.Theme);
            Assert.True(loaded.StrictOffline);
            Assert.Equal(HistoryRetention.Days90, loaded.HistoryRetention);
            Assert.Equal("zh-CN", loaded.UiLanguage);
            Assert.Equal(AppPerformancePreset.Performance, loaded.PerformancePreset);
            Assert.True(loaded.ReducedMotion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Covers Home profile pinning (decision #8 / D1): pinned profile IDs persist through the
    // Core-backed settings store across a fresh service instance, the same round-trip shape the app
    // relies on when it reloads after a restart.
    [Fact]
    public async Task Settings_pinned_profile_ids_persist_across_new_store_instances()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            ISettingsService Make() => new RealSettingsService(
                new ApplicationSettingsRepository(options.DatabasePath),
                new FakeSecretReferenceService());

            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            ApplicationSettings baseline = await Make().GetSettingsAsync(ct);
            Assert.Empty(baseline.EffectivePinnedProfileIds);

            await Make().UpdateAsync(
                baseline with { PinnedProfileIds = [first, second] },
                ct);

            ApplicationSettings loaded = await Make().GetSettingsAsync(ct);
            Assert.Equal([first, second], loaded.EffectivePinnedProfileIds);

            await Make().UpdateAsync(loaded with { PinnedProfileIds = [second] }, ct);
            ApplicationSettings unpinnedFirst = await Make().GetSettingsAsync(ct);
            Assert.Equal([second], unpinnedFirst.EffectivePinnedProfileIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_center_reports_empty_state_on_fresh_database()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var service = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            var viewModel = new ProfileCenterViewModel(service);

            await viewModel.InitializeAsync(ct);

            Assert.False(viewModel.HasError);
            Assert.Empty(viewModel.Profiles);
            Assert.True(viewModel.IsEmpty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Glossary_reports_no_active_profile_on_fresh_database()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var viewModel = new GlossaryViewModel(new RealGlossaryService(new ProfileRepository(options.DatabasePath)));

            await viewModel.InitializeAsync(ct);

            Assert.False(viewModel.HasError);
            Assert.False(viewModel.HasActiveProfile);
            Assert.True(viewModel.NoActiveProfile);
            Assert.Empty(viewModel.Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Setup_wizard_saves_a_profile_built_from_the_selected_target_and_regions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var spy = new SpyProfileService();
        var wizard = new SetupWizardViewModel(
            new FakeCaptureProbe(), new FakeOcrProbe(), new FakeTranslationProbe(),
            new FakeSettingsService(), new FakeSecretReferenceService(), spy);
        await wizard.InitializeAsync(ct);

        wizard.ProfileName = "My profile";
        Assert.NotNull(wizard.SelectedTarget);
        CaptureProbeTarget secondTarget = wizard.Targets[1];
        wizard.SetSelectedTargets([wizard.SelectedTarget!, secondTarget]);
        wizard.AddRegion("HUD", RegionPriorityLevel.P1);
        Assert.True(wizard.CanSave);

        await wizard.SaveCommand.ExecuteAsync(null);

        Assert.False(wizard.HasError);
        Assert.NotEqual(Guid.Empty, wizard.SavedProfileId);
        Assert.NotNull(spy.LastSaved);
        Assert.Equal("My profile", spy.LastSaved!.Name);
        Assert.Equal(wizard.SelectedTarget!.DisplayName, spy.LastSaved.TargetName);
        Assert.Equal(2, spy.LastSaved.EffectiveCaptureTargets.Count);
        Assert.Contains(spy.LastSaved.EffectiveCaptureTargets,
            target => target.Name == secondTarget.DisplayName);
        Assert.Contains(spy.LastSaved.Regions, region => region.Name == "HUD");
    }

    [Fact]
    public async Task Secret_reference_service_round_trips_through_credential_store_with_test_prefix()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A test-only catalog whose credential reference is prefixed so it can never collide with a
        // user's real provider credential in Windows Credential Manager.
        string reference = "test-wp5-" + Guid.NewGuid().ToString("N");
        var binding = new CredentialBinding(
            reference, "translation", "https", "api.example.test", 443, "bearer", ProxyPolicy.System);
        var catalog = new List<CatalogProvider>
        {
            new(reference, "Test Provider", "NMT - cloud", reference, binding, "test only"),
        };
        var service = new RealSecretReferenceService(new GenericCredentialStore(), catalog);

        try
        {
            Assert.False(await service.HasSecretAsync(reference, ct));

            await service.SetSecretAsync(reference, "s3cr3t-value", ct);
            Assert.True(await service.HasSecretAsync(reference, ct));

            IReadOnlyList<SecretReference> references = await service.GetReferencesAsync(ct);
            Assert.Contains(references, item => item.ReferenceId == reference && item.IsPresent);
        }
        finally
        {
            // Always remove the credential this test wrote, even if an assertion above failed.
            await service.ClearSecretAsync(reference, ct);
        }

        Assert.False(await service.HasSecretAsync(reference, ct));
    }

    [Fact]
    public async Task Multi_credential_provider_is_connected_only_when_every_field_is_bound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string providerId = "test-multi-" + Guid.NewGuid().ToString("N");
        string firstReference = providerId + "-id";
        string secondReference = providerId + "-secret";
        var firstBinding = new CredentialBinding(
            providerId, "client-id", "https", "api.example.test", 443,
            "two-part", ProxyPolicy.System);
        var secondBinding = firstBinding with { Purpose = "client-secret" };
        var catalog = new List<CatalogProvider>
        {
            new(
                providerId,
                "Test Multi Provider",
                "OCR - cloud",
                [
                    new(firstReference, "Client ID", firstBinding),
                    new(secondReference, "Client secret", secondBinding),
                ],
                "test only"),
        };
        var service = new RealSecretReferenceService(
            new BoundCredentialStore(new MemoryCredentialStore()),
            catalog);

        Assert.False(await service.HasSecretAsync(providerId, ct));
        await service.SetSecretAsync(providerId, firstReference, "first", ct);
        Assert.False(await service.HasSecretAsync(providerId, ct));
        await service.SetSecretAsync(providerId, secondReference, "second", ct);
        Assert.True(await service.HasSecretAsync(providerId, ct));

        IReadOnlyList<SecretReference> references = await service.GetReferencesAsync(ct);
        Assert.Equal(2, references.Count);
        Assert.All(references, reference => Assert.True(reference.IsPresent));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetSecretAsync(providerId, "ambiguous", ct));

        await service.ClearSecretAsync(providerId, firstReference, ct);
        Assert.False(await service.HasSecretAsync(providerId, ct));
    }

    private sealed class SpyProfileService : IProfileService
    {
        public ProfileEditModel? LastSaved { get; private set; }

        public Task<IReadOnlyList<ProfileCard>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProfileCard>>([]);

        public Task<ProfileEditModel?> LoadForEditAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProfileEditModel?>(null);

        public Task<IReadOnlyList<string>> GetTranslationProviderIdsAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<Guid> SaveAsync(ProfileEditModel profile, CancellationToken cancellationToken = default)
        {
            LastSaved = profile;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExportAsync(
            Guid profileId,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Guid> ImportAsync(
            Stream source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());
    }
}
