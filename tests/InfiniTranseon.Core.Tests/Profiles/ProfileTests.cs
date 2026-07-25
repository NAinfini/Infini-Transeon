using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Tests.Profiles;

public sealed class ProfileTests
{
    [Fact]
    public void CurrentDocumentRoundTripsUnknownExtensionDataWithInvariantNumbers()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "profileId": "11111111-1111-1111-1111-111111111111",
              "name": "Game",
              "sourceLanguage": "ja",
              "targetLanguage": "en",
              "targets": [],
              "hotkeys": [],
              "history": { "enabled": false, "maxAgeDays": 30, "maxBytes": 524288000 },
              "futureOption": { "threshold": 0.25 }
            }
            """;
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            ProfileDocument document = ProfileJson.Deserialize(json);
            string serialized = ProfileJson.Serialize(document);

            Assert.Equal(ProfileDocument.CurrentVersion, document.SchemaVersion);
            Assert.Contains("\"futureOption\"", serialized, StringComparison.Ordinal);
            Assert.Contains("0.25", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("0,25", serialized, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void DuplicateIdsAreErrorsAndOverLimitChannelsRemainDisabledWithReason()
    {
        Guid duplicate = Guid.NewGuid();
        ProfileDocument document = ProfileDocument.Create("Game", "ja", "en");
        var region = ProfileRegion.Create("Dialogue", new NormalizedRect(0.1, 0.7, 0.8, 0.2));
        region.TranslationChannels.AddRange(Enumerable.Range(0, 5)
            .Select(index => ProfileTranslationChannel.Create($"provider-{index}")));
        ProfileTarget firstTarget = ProfileTarget.Create("Game Window", CaptureTargetKind.Window) with
        {
            TargetId = duplicate,
        };
        firstTarget.Regions.Add(region);
        document.Targets.Add(firstTarget);
        document.Targets.Add(ProfileTarget.Create("Duplicate", CaptureTargetKind.Window) with
        {
            TargetId = duplicate,
        });

        ProfileValidationResult result = new ProfileValidator().Validate(
            document,
            RuntimeCapabilities.VersionOne,
            document.Targets.SelectMany(target => target.Regions)
                .SelectMany(item => item.TranslationChannels)
                .Select(channel => channel.InitialProviderId)
                .ToHashSet(StringComparer.Ordinal));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "profile.id.duplicate");
        Assert.False(result.Document.Targets[0].Regions[0].TranslationChannels[4].Enabled);
        Assert.Equal("profile.limit.translationChannels", result.Document.Targets[0]
            .Regions[0].TranslationChannels[4].DisabledReasonCode);
    }

    [Fact]
    public void Desktop_fixed_region_requires_valid_physical_pixel_bounds()
    {
        ProfileDocument missing = ProfileDocument.Create("Desktop crop", "ja", "en");
        missing.Targets.Add(ProfileTarget.Create(
            "Display crop",
            CaptureTargetKind.DesktopFixedRegion));

        ProfileValidationResult missingResult = new ProfileValidator().Validate(
            missing,
            RuntimeCapabilities.VersionOne,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.False(missingResult.IsValid);
        Assert.Contains(missingResult.Issues,
            issue => issue.Code == "profile.target.desktopRegionRequired");

        ProfileDocument valid = missing with
        {
            Targets =
            [
                missing.Targets[0] with
                {
                    DesktopRegion = new OverlayPixelRect(-1920, 100, 1280, 720),
                },
            ],
        };

        ProfileValidationResult validResult = new ProfileValidator().Validate(
            valid,
            RuntimeCapabilities.VersionOne,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.True(validResult.IsValid);
    }

    [Fact]
    public void FutureVersionIsRejectedAndVersionZeroMigratesOneWay()
    {
        var migrator = new ProfileMigrator();
        const string versionZero = """
            {"schemaVersion":0,"profileId":"11111111-1111-1111-1111-111111111111","name":"Old","sourceLanguage":"ja","targetLanguage":"en"}
            """;

        ProfileDocument migrated = migrator.Migrate(versionZero);

        Assert.Equal(ProfileDocument.CurrentVersion, migrated.SchemaVersion);
        Assert.Empty(migrated.Targets);
        Assert.Throws<ProfileMigrationException>(() => migrator.Migrate(
            "{\"schemaVersion\":999}"));
    }

    [Fact]
    public void ExportRemovesMachineBindingSecretsHistoryAndPersonalPaths()
    {
        ProfileDocument document = ProfileDocument.Create("Game", "ja", "en");
        document.Targets.Add(ProfileTarget.Create("Window", CaptureTargetKind.Window) with
        {
            MachineBinding = new TargetMachineBinding(1234, "C:\\Users\\person\\game.exe", "secret title"),
        });
        document.ExtensionData["apiKey"] = JsonSerializer.SerializeToElement("secret-value");
        document.ExtensionData["api-key"] = JsonSerializer.SerializeToElement("alternate-secret");
        document.ExtensionData["historyRecords"] = JsonSerializer.SerializeToElement(new[] { "private text" });
        document.ExtensionData["futureSettings"] = JsonSerializer.SerializeToElement(new
        {
            cache = "C:\\Users\\person\\AppData\\Local\\private-cache",
            nested = new[] { "safe", "file:///C:/Users/person/private-model.bin" },
        });
        using var archive = new MemoryStream();

        new ProfileArchiveService().Export(document, archive);
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry profileEntry = Assert.Single(zip.Entries, entry => entry.FullName == "profile.json");
        using var reader = new StreamReader(profileEntry.Open());
        string exported = reader.ReadToEnd();

        Assert.DoesNotContain("secret-value", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("alternate-secret", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("private text", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("person", exported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machineBinding", exported, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptDocumentsAndUnknownProviderReferencesAreRejectedExplicitly()
    {
        Assert.Throws<ProfileMigrationException>(() => new ProfileMigrator().Migrate("{broken"));
        ProfileDocument document = ProfileDocument.Create("Game", "ja", "en");
        var region = ProfileRegion.Create("Dialogue", new NormalizedRect(0, 0, 1, 1));
        region.TranslationChannels.Add(ProfileTranslationChannel.Create("missing-rest-adapter"));
        ProfileTarget target = ProfileTarget.Create("Window", CaptureTargetKind.Window);
        target.Regions.Add(region);
        document.Targets.Add(target);

        ProfileValidationResult result = new ProfileValidator().Validate(
            document,
            RuntimeCapabilities.VersionOne,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "profile.provider.unknown");
    }

    [Fact]
    public void CompleteRegionConfigurationRoundTripsWithoutLosingUserFreedom()
    {
        ProfileDocument document = ProfileDocument.Create("Game", "ja", "zh-Hans") with
        {
            Context = new ProfileContextSettings
            {
                GameName = "Example",
                GameDescription = "Tactical role-playing game",
                RecentLineCount = 8,
            },
        };
        var region = ProfileRegion.Create("Stats", new NormalizedRect(0.1, 0.1, 0.3, 0.4)) with
        {
            Ocr = new ProfileOcrSettings
            {
                ProviderId = "windows-ocr",
                RecognitionLanguage = "en-US",
                DetectionScale = 0.75,
            },
            Overlay = new ProfileOverlaySettings
            {
                Mode = OverlayMode.Replace,
                BackgroundMode = OverlayBackgroundMode.Solid,
                BackgroundColor = "#FFFFFFFF",
                TextColor = "#FF000000",
            },
            LineBreakMode = LineBreakMode.KeyValueRows,
        };
        ProfileTarget target = ProfileTarget.Create("Window", CaptureTargetKind.Window);
        target.LayoutVariants.Add(new ProfileLayoutVariant
        {
            Name = "16:9",
            MinimumAspectRatio = 1.7,
            MaximumAspectRatio = 1.8,
            BoundsScale = 1,
        });
        target.Regions.Add(region);
        document.Targets.Add(target);

        ProfileDocument copy = ProfileJson.Deserialize(ProfileJson.Serialize(document));

        Assert.Equal("Example", copy.Context.GameName);
        Assert.Equal(0.75, copy.Targets[0].Regions[0].Ocr.DetectionScale);
        Assert.Equal(OverlayBackgroundMode.Solid, copy.Targets[0].Regions[0].Overlay.BackgroundMode);
        Assert.Equal("16:9", copy.Targets[0].LayoutVariants[0].Name);
    }

    [Fact]
    public void ProfileTargetBuildsCompleteBoundedRuntimeProcessingConfiguration()
    {
        ProfileTarget target = ProfileTarget.Create("Game", CaptureTargetKind.Window) with
        {
            DetectionLongEdge = 1600,
            ScanRemainingArea = true,
            RemainingAreaInterval = TimeSpan.FromSeconds(2),
            RemainingAreaRegion = ProfileRegion.Create(
                "Automatic", new NormalizedRect(0, 0, 1, 1)) with
            {
                AreaMode = CaptureAreaKind.RemainingArea,
                Ocr = new ProfileOcrSettings
                {
                    ProviderId = "ocr.windows.media",
                    RecognitionLanguage = "ja-JP",
                },
            },
        };
        target.Regions.Add(ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0.1, 0.65, 0.8, 0.25)) with
        {
            Priority = RegionPriority.P0,
            RecognitionInterval = TimeSpan.FromMilliseconds(125),
            LockDegradation = true,
            LineBreakMode = LineBreakMode.KeyValueRows,
            Ocr = new ProfileOcrSettings
            {
                ProviderId = "ocr.windows.media",
                RecognitionLanguage = "ja-JP",
                DetectionScale = 0.75,
                DetectOrientation = true,
                UseCloudOcr = true,
                CloudConsentPolicyRevision = 9,
                PreprocessingSteps = ["grayscale", "threshold"],
            },
        });
        target.Regions.Add(ProfileRegion.Create(
            "Disabled", new NormalizedRect(0, 0, 0.1, 0.1)) with { Enabled = false });

        RuntimeProcessingConfiguration configuration = ProfileRuntimeConfigurationFactory.Create(
            target,
            new TargetInstanceId(Guid.NewGuid()),
            configurationRevision: 8,
            profileId: Guid.NewGuid(),
            profileRevision: 3);

        Assert.Equal(1600, configuration.DetectionLongEdge);
        Assert.True(configuration.ScanRemainingArea);
        Assert.Equal(2000, configuration.RemainingAreaIntervalMilliseconds);
        Assert.Equal(2, configuration.Regions.Count);
        RuntimeProcessingRegion region = Assert.Single(
            configuration.Regions, item => item.AreaMode == CaptureAreaKind.UserRegion);
        Assert.Equal(RuntimeRegionPriority.P0, region.Priority);
        Assert.Equal(125, region.RecognitionIntervalMilliseconds);
        Assert.True(region.LockDegradation);
        Assert.Equal(9, region.CloudConsentPolicyRevision);
        Assert.Equal("ocr.windows.media", region.OcrProviderId);
        Assert.Equal("[\"grayscale\",\"threshold\"]", region.PreprocessingPipeline);
        RuntimeProcessingRegion automatic = Assert.Single(
            configuration.Regions, item => item.AreaMode == CaptureAreaKind.RemainingArea);
        Assert.Equal(2000, automatic.RecognitionIntervalMilliseconds);
    }

    [Fact]
    public void RemainingAreaScanRequiresDedicatedRegionSettings()
    {
        ProfileTarget target = ProfileTarget.Create("Game", CaptureTargetKind.Window) with
        {
            ScanRemainingArea = true,
        };

        Assert.Throws<ArgumentException>(() => ProfileRuntimeConfigurationFactory.Create(
            target,
            new TargetInstanceId(Guid.NewGuid()),
            configurationRevision: 1,
            profileId: Guid.NewGuid(),
            profileRevision: 1));

        ProfileTarget cloudTarget = target with
        {
            RemainingAreaRegion = ProfileRegion.Create(
                "Automatic", new NormalizedRect(0, 0, 1, 1)) with
            {
                AreaMode = CaptureAreaKind.RemainingArea,
                Ocr = new ProfileOcrSettings
                {
                    ProviderId = "ocr.cloud",
                    UseCloudOcr = true,
                    CloudConsentPolicyRevision = 1,
                },
            },
        };
        RuntimeProcessingConfiguration cloudConfiguration = ProfileRuntimeConfigurationFactory.Create(
            cloudTarget,
            new TargetInstanceId(Guid.NewGuid()),
            configurationRevision: 1,
            profileId: Guid.NewGuid(),
            profileRevision: 1);
        Assert.True(Assert.Single(cloudConfiguration.Regions).UseCloudOcr);
    }

    [Fact]
    public void ProfileOverlayMapsOffsetModeToIndependentPixelDestination()
    {
        ProfileRegion region = ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0.1, 0.6, 0.8, 0.2)) with
        {
            LineAlignment = LineAlignment.Center,
            MaximumLines = 3,
            Overlay = new ProfileOverlaySettings
            {
                Mode = OverlayMode.Offset,
                BackgroundMode = OverlayBackgroundMode.Translucent,
                BackgroundColor = "#CC101010",
                TextColor = "#FFFFFFFF",
                OutlineColor = "#FF112233",
                OutlineWidth = 2,
                PreferredFontSize = 30,
                MinimumDwell = TimeSpan.FromMilliseconds(650),
                CrossfadeDuration = TimeSpan.FromMilliseconds(140),
                OffsetDestination = new NormalizedRect(0.7, 0.1, 0.25, 0.3),
            },
        };

        (OverlayPixelRect bounds, OverlayRegionStyleSnapshot style) =
            ProfileOverlaySnapshotFactory.Create(region, 1920, 1080);

        Assert.Equal(new OverlayPixelRect(192, 648, 1536, 216), bounds);
        Assert.Equal(OverlayBackgroundTreatment.Offset, style.Background);
        Assert.Equal(OverlayTextAlignment.Center, style.Alignment);
        Assert.Equal(new OverlayPixelRect(1344, 108, 480, 324), style.DestinationBounds);
        Assert.Equal("#FF112233", style.OutlineColor);
        Assert.Equal(2, style.OutlineWidth);
        Assert.Equal(30, style.PreferredFontSize);
        Assert.Equal(3, style.MaximumLines);
        Assert.Equal(650, style.MinimumDwellMilliseconds);
        Assert.Equal(140, style.CrossfadeMilliseconds);

        (_, OverlayRegionStyleSnapshot reducedMotion) =
            ProfileOverlaySnapshotFactory.Create(region, 1920, 1080, reducedMotion: true);
        Assert.True(reducedMotion.ReducedMotion);
        Assert.Equal(0, reducedMotion.CrossfadeMilliseconds);
    }

    [Fact]
    public void ArchiveWithoutManifestOrWithFutureArchiveVersionIsRejected()
    {
        using var missingManifest = new MemoryStream();
        using (var archive = new ZipArchive(missingManifest, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("profile.json").Open());
            writer.Write(ProfileJson.Serialize(ProfileDocument.Create("Game", "ja", "en")));
        }
        missingManifest.Position = 0;
        Assert.Throws<InvalidDataException>(() => new ProfileArchiveService().Import(missingManifest));

        using var future = new MemoryStream();
        using (var archive = new ZipArchive(future, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write("{\"archiveVersion\":999}");
            using (var writer = new StreamWriter(archive.CreateEntry("profile.json").Open()))
                writer.Write(ProfileJson.Serialize(ProfileDocument.Create("Game", "ja", "en")));
        }
        future.Position = 0;
        Assert.Throws<InvalidDataException>(() => new ProfileArchiveService().Import(future));
    }

    [Fact]
    public void ArchiveImportRejectsUnexpectedOrDuplicateEntries()
    {
        var service = new ProfileArchiveService();
        using var unexpected = new MemoryStream();
        service.Export(ProfileDocument.Create("Game", "ja", "en"), unexpected);
        using (var archive = new ZipArchive(unexpected, ZipArchiveMode.Update, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("secrets.txt").Open()))
            writer.Write("must never travel with a profile");
        unexpected.Position = 0;
        Assert.Throws<InvalidDataException>(() => service.Import(unexpected));

        using var duplicate = new MemoryStream();
        using (var archive = new ZipArchive(duplicate, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write("{\"archiveVersion\":1}");
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write("{\"archiveVersion\":1}");
            using (var writer = new StreamWriter(archive.CreateEntry("profile.json").Open()))
                writer.Write(ProfileJson.Serialize(ProfileDocument.Create("Game", "ja", "en")));
        }
        duplicate.Position = 0;
        Assert.Throws<InvalidDataException>(() => service.Import(duplicate));
    }

    [Fact]
    public void OfflineHistoryAndLayoutReferenceViolationsAreReported()
    {
        ProfileDocument document = ProfileDocument.Create("Game", "ja", "en") with
        {
            StrictOffline = true,
            History = new ProfileHistorySettings { Enabled = true, MaxAgeDays = 0, MaxBytes = 0 },
        };
        var region = ProfileRegion.Create("Dialogue", new NormalizedRect(0, 0, 1, 1)) with
        {
            Ocr = new ProfileOcrSettings
            {
                ProviderId = "cloud-ocr",
                UseCloudOcr = true,
                CloudConsentPolicyRevision = 1,
            },
            TranslationEnabled = false,
        };
        ProfileTarget target = ProfileTarget.Create("Window", CaptureTargetKind.Window);
        target.Regions.Add(region);
        target.LayoutVariants.Add(new ProfileLayoutVariant
        {
            Name = "Invalid override",
            RegionBounds =
            {
                [Guid.NewGuid()] = new NormalizedRect(0, 0, 1, 1),
            },
        });
        document.Targets.Add(target);

        ProfileValidationResult result = new ProfileValidator().Validate(
            document,
            RuntimeCapabilities.VersionOne,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "profile.offline.cloudOcr");
        Assert.Contains(result.Issues, issue => issue.Code == "profile.history.retentionInvalid");
        Assert.Contains(result.Issues, issue => issue.Code == "profile.layout.regionUnknown");
    }
}
