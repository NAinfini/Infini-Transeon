using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;

namespace InfiniTranseon.App.Tests;

public sealed class WorkbenchViewModelTests
{
    [Fact]
    public async Task Bounds_edit_clamps_and_round_trips_through_undo_redo()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        WorkbenchRegionItem region = Assert.IsType<WorkbenchRegionItem>(
            viewModel.SelectedRegion);

        viewModel.SetRegionBounds(
            region,
            x: 0.9,
            y: 0.95,
            width: 0.5,
            height: 0.4,
            createUndoPoint: true);

        Assert.Equal(0.5, region.X);
        Assert.Equal(0.6, region.Y);
        Assert.True(viewModel.CanUndo);
        viewModel.Undo();
        Assert.Equal(0.1, viewModel.SelectedRegion!.X);
        Assert.Equal(0.65, viewModel.SelectedRegion.Y);
        Assert.True(viewModel.CanRedo);
        viewModel.Redo();
        Assert.Equal(0.5, viewModel.SelectedRegion!.X);
        Assert.Equal(0.6, viewModel.SelectedRegion.Y);
    }

    [Fact]
    public async Task Region_enabled_toggle_creates_a_real_undo_point()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        WorkbenchRegionItem region = viewModel.SelectedRegion!;

        viewModel.SetRegionEnabled(region, enabled: false);

        Assert.False(region.Enabled);
        viewModel.Undo();
        Assert.True(viewModel.SelectedRegion!.Enabled);
    }

    [Fact]
    public async Task Save_includes_every_target_and_reports_runtime_apply_state()
    {
        WorkbenchProfileDraft draft = CreateDraft();
        var service = new StubWorkbenchService(draft)
        {
            Result = ProfileRuntimeApplyResult.HotApplied,
        };
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            draft.ProfileId,
            TestContext.Current.CancellationToken);
        WorkbenchTargetItem second = viewModel.Targets[1];
        viewModel.SelectedTarget = second;
        viewModel.SelectedRegion = second.Regions[0];
        viewModel.SetRegionBounds(
            second.Regions[0],
            0.25,
            0.3,
            0.5,
            0.4,
            createUndoPoint: true);

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        WorkbenchProfileDraft saved = Assert.IsType<WorkbenchProfileDraft>(service.Saved);
        Assert.Equal(2, saved.Targets.Count);
        Assert.Equal(0.25, saved.Targets[1].Regions[0].X);
        Assert.Equal("HotApplied", viewModel.ApplyState);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task Channel_mutations_enforce_runtime_limit_normalize_order_and_support_undo()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        WorkbenchRegionItem region = viewModel.SelectedRegion!;

        for (int index = 1; index < viewModel.MaxTranslationChannels; index++)
        {
            Assert.True(viewModel.TryAddChannel(
                region,
                new WorkbenchChannelDraft(
                    Guid.NewGuid(),
                    $"translation.test-{index}",
                    $"Test {index}",
                    true,
                    99)));
        }
        Assert.False(viewModel.TryAddChannel(
            region,
            new WorkbenchChannelDraft(
                Guid.NewGuid(),
                "translation.too-many",
                "Too many",
                true,
                99)));
        Assert.Equal(
            Enumerable.Range(0, viewModel.MaxTranslationChannels),
            region.Channels.Select(channel => channel.DisplayOrder));

        WorkbenchChannelDraft last = region.Channels[^1];
        Assert.True(viewModel.MoveChannel(region, last, -1));
        Assert.Equal(last.ChannelId, region.Channels[^2].ChannelId);
        Assert.Equal(
            Enumerable.Range(0, viewModel.MaxTranslationChannels),
            region.Channels.Select(channel => channel.DisplayOrder));

        viewModel.Undo();
        Assert.Equal(last.ChannelId, viewModel.SelectedRegion!.Channels[^1].ChannelId);
    }

    [Fact]
    public async Task Channel_add_is_blocked_exactly_at_the_runtime_cap()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        WorkbenchRegionItem region = viewModel.SelectedRegion!;
        int startingCount = region.Channels.Count;

        for (int index = startingCount; index < viewModel.MaxTranslationChannels; index++)
        {
            Assert.True(viewModel.TryAddChannel(
                region,
                new WorkbenchChannelDraft(Guid.NewGuid(), $"translation.cap-{index}", $"Cap {index}", true, 99)));
        }
        Assert.Equal(viewModel.MaxTranslationChannels, region.Channels.Count);

        bool addedOverCap = viewModel.TryAddChannel(
            region,
            new WorkbenchChannelDraft(Guid.NewGuid(), "translation.over-cap", "Over cap", true, 99));

        Assert.False(addedOverCap);
        Assert.Equal(viewModel.MaxTranslationChannels, region.Channels.Count);
    }

    [Theory]
    [InlineData("translation.deepl", "translation.gpt", "translation.claude", new[] { "translation.gpt", "translation.claude" })]
    [InlineData("translation.deepl", "translation.deepl", "translation.claude", new[] { "translation.claude" })]
    [InlineData("translation.deepl", "translation.gpt", "translation.gpt", new[] { "translation.gpt" })]
    [InlineData("translation.deepl", null, "translation.claude", new[] { "translation.claude" })]
    [InlineData("translation.deepl", "", "translation.claude", new[] { "translation.claude" })]
    [InlineData("translation.deepl", null, null, new string[0])]
    public void BuildRefinerIds_drops_empties_and_ids_matching_the_initial_or_each_other(
        string initialProviderId,
        string? firstRefinerId,
        string? secondRefinerId,
        string[] expected)
    {
        IReadOnlyList<string> actual = WorkbenchViewModel.BuildRefinerIds(
            initialProviderId,
            firstRefinerId,
            secondRefinerId);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SetRegionBounds_clamps_out_of_range_values_into_the_valid_normalized_space()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        WorkbenchRegionItem region = viewModel.SelectedRegion!;

        viewModel.SetRegionBounds(region, x: -5, y: -5, width: 0, height: 0, createUndoPoint: true);
        Assert.Equal(0.0, region.X);
        Assert.Equal(0.0, region.Y);
        Assert.Equal(0.01, region.Width);
        Assert.Equal(0.01, region.Height);

        viewModel.SetRegionBounds(region, x: 0.5, y: 0.5, width: 5, height: 5, createUndoPoint: true);
        Assert.Equal(0.0, region.X);
        Assert.Equal(0.0, region.Y);
        Assert.Equal(1.0, region.Width);
        Assert.Equal(1.0, region.Height);

        viewModel.SetRegionBounds(region, x: 0.9, y: 0.9, width: 0.5, height: 0.4, createUndoPoint: true);
        Assert.Equal(0.5, region.X);
        Assert.Equal(0.6, region.Y);
    }

    [Fact]
    public async Task Overlay_style_fields_round_trip_through_undo_redo()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        WorkbenchRegionItem region = viewModel.SelectedRegion!;
        double originalFontSize = region.PreferredFontSize;
        string? originalOutlineColor = region.OutlineColor;
        double originalOutlineWidth = region.OutlineWidth;

        viewModel.MarkEditorChanged(createUndoPoint: true);
        region.PreferredFontSize = 30;
        region.OutlineColor = "#FF001122";
        region.OutlineWidth = 2;

        Assert.Equal(30, region.PreferredFontSize);
        Assert.Equal("#FF001122", region.OutlineColor);
        Assert.Equal(2, region.OutlineWidth);
        Assert.True(viewModel.CanUndo);

        viewModel.Undo();
        Assert.Equal(originalFontSize, viewModel.SelectedRegion!.PreferredFontSize);
        Assert.Equal(originalOutlineColor, viewModel.SelectedRegion.OutlineColor);
        Assert.Equal(originalOutlineWidth, viewModel.SelectedRegion.OutlineWidth);

        viewModel.Redo();
        Assert.Equal(30, viewModel.SelectedRegion!.PreferredFontSize);
        Assert.Equal("#FF001122", viewModel.SelectedRegion.OutlineColor);
        Assert.Equal(2, viewModel.SelectedRegion.OutlineWidth);

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);
        WorkbenchProfileDraft saved = Assert.IsType<WorkbenchProfileDraft>(service.Saved);
        WorkbenchRegionDraft savedRegion = saved.Targets[0].Regions[0];
        Assert.Equal(30, savedRegion.PreferredFontSize);
        Assert.Equal("#FF001122", savedRegion.OutlineColor);
        Assert.Equal(2, savedRegion.OutlineWidth);
    }

    [Fact]
    public async Task Ensure_loaded_keeps_unsaved_edits_when_the_same_profile_is_requested_again()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        viewModel.GameName = "Edited while switching sections";
        viewModel.MarkEditorChanged();

        await viewModel.EnsureLoadedAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);

        Assert.Equal("Edited while switching sections", viewModel.GameName);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(1, service.LoadCount);
    }

    [Fact]
    public async Task Ensure_loaded_reloads_when_a_different_profile_is_requested()
    {
        WorkbenchProfileDraft first = CreateDraft();
        var service = new MultiProfileWorkbenchService([first, CreateDraft()]);
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.EnsureLoadedAsync(
            first.ProfileId,
            TestContext.Current.CancellationToken);
        viewModel.GameName = "Edited";
        viewModel.MarkEditorChanged();

        await viewModel.EnsureLoadedAsync(
            service.Drafts[1].ProfileId,
            TestContext.Current.CancellationToken);

        Assert.Equal(service.Drafts[1].GameName, viewModel.GameName);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task Discard_changes_reloads_the_stored_profile()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        viewModel.AddRegion();
        int editedRegionCount = viewModel.SelectedTarget!.Regions.Count;

        await viewModel.DiscardChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.CanUndo);
        Assert.Equal(editedRegionCount - 1, viewModel.SelectedTarget!.Regions.Count);
        Assert.Null(service.Saved);
    }

    [Fact]
    public async Task Discard_changes_is_a_no_op_before_anything_is_loaded()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);

        await viewModel.DiscardChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, service.LoadCount);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Save_rejects_a_profile_whose_source_and_target_languages_match()
    {
        var service = new StubWorkbenchService(CreateDraft());
        var viewModel = new WorkbenchViewModel(service);
        await viewModel.LoadAsync(
            service.Draft.ProfileId,
            TestContext.Current.CancellationToken);
        viewModel.TargetLanguage = viewModel.SourceLanguage.ToUpperInvariant();

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasError);
        Assert.Null(service.Saved);
    }

    private static WorkbenchProfileDraft CreateDraft()
    {
        Guid profileId = Guid.NewGuid();
        return new WorkbenchProfileDraft(
            profileId,
            "Game profile",
            "ja",
            "en",
            "Game",
            "Description",
            6,
            [
                Target("Game window", 0.1, 0.65),
                Target("Guide window", 0.2, 0.2),
            ]);
    }

    private static WorkbenchTargetDraft Target(string name, double x, double y) =>
        new(
            Guid.NewGuid(),
            name,
            "Window",
            true,
            1920,
            false,
            1000,
            [
                new WorkbenchRegionDraft(
                    Guid.NewGuid(),
                    "Dialogue",
                    true,
                    x,
                    y,
                    0.7,
                    0.25,
                    RegionPriorityLevel.P0,
                    250,
                    "auto",
                    "auto",
                    1,
                    true,
                    false,
                    0,
                    true,
                    [
                        new WorkbenchChannelDraft(
                            Guid.NewGuid(),
                            "translation.deepl",
                            "DeepL",
                            true,
                            0),
                    ],
                    "PreserveLines",
                    null,
                    64,
                    "Auto",
                    RegionContextRole.Dialogue,
                    "Replace",
                    "AutomaticContrastBlur",
                    null,
                    null,
                    0.85,
                    12,
                    false),
            ]);

    private sealed class StubWorkbenchService(WorkbenchProfileDraft draft)
        : IWorkbenchService
    {
        public WorkbenchProfileDraft Draft { get; } = draft;
        public WorkbenchProfileDraft? Saved { get; private set; }
        public int LoadCount { get; private set; }
        public ProfileRuntimeApplyResult Result { get; init; } =
            ProfileRuntimeApplyResult.SavedOnly;

        public Task<WorkbenchProfileDraft?> LoadAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult<WorkbenchProfileDraft?>(
                profileId == Draft.ProfileId ? Draft : null);
        }

        public Task<ProfileRuntimeApplyResult> SaveAndApplyAsync(
            WorkbenchProfileDraft profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved = profile;
            return Task.FromResult(Result);
        }
    }

    private sealed class MultiProfileWorkbenchService(
        IReadOnlyList<WorkbenchProfileDraft> drafts)
        : IWorkbenchService
    {
        public IReadOnlyList<WorkbenchProfileDraft> Drafts { get; } = drafts;

        public Task<WorkbenchProfileDraft?> LoadAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Drafts.FirstOrDefault(draft => draft.ProfileId == profileId));
        }

        public Task<ProfileRuntimeApplyResult> SaveAndApplyAsync(
            WorkbenchProfileDraft profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ProfileRuntimeApplyResult.SavedOnly);
        }
    }
}
