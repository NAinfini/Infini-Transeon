using System.Linq;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.Workspace;

/// <summary>
/// Workspace · translation channels (spec 5.5). Replaces the former modal ContentDialog channel
/// editor with inline pipeline cards: initial translator, up to two inline refiners, an advanced
/// expander, and up/down reordering. A region selector and a channel-quota / budget preview sit
/// in the page header.
/// </summary>
public sealed partial class ChannelsSectionPage : Page
{
    private sealed record ProviderChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private Guid _requestedProfileId;
    private ProviderChoice[] _providerCatalog = [];
    private bool _rendering;

    public ChannelsSectionPage()
    {
        ViewModel = App.GetService<WorkbenchViewModel>();
        _profiles = App.GetService<IProfileService>();
        _settings = App.GetService<ISettingsService>();
        InitializeComponent();
    }

    public WorkbenchViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _requestedProfileId = e.Parameter switch
        {
            ProfileWorkspaceNavigation route => route.ProfileId,
            Guid profileId => profileId,
            _ => Guid.Empty,
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Guid profileId = _requestedProfileId;
        if (profileId == Guid.Empty)
        {
            IReadOnlyList<ProfileCard> profiles = await _profiles.GetProfilesAsync();
            profileId = profiles.FirstOrDefault()?.ProfileId ?? Guid.Empty;
        }
        if (profileId == Guid.Empty)
        {
            ShowInfo(
                InfoBarSeverity.Informational,
                Strings.GetString("WorkbenchNoProfileTitle"),
                Strings.GetString("WorkbenchNoProfileMessage"));
            return;
        }

        await ViewModel.EnsureLoadedAsync(profileId);
        if (ViewModel.HasError)
        {
            ShowInfo(InfoBarSeverity.Error, Strings.GetString("WorkbenchLoadErrorTitle"), ViewModel.ErrorMessage);
            return;
        }

        IReadOnlyList<ProviderRow> providerRows = await _settings.GetProvidersAsync();
        _providerCatalog = providerRows
            .Where(provider => provider.IsSelectable && provider.IsTranslationProvider)
            .Select(provider => new ProviderChoice(provider.Id, provider.Name))
            .OrderBy(provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        TargetSelector.ItemsSource = ViewModel.Targets;
        TargetSelector.SelectedItem = ViewModel.SelectedTarget;
        RefreshRegionSelector();
    }

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetSelector.SelectedItem is WorkbenchTargetItem target)
        {
            ViewModel.SelectedTarget = target;
        }
        RefreshRegionSelector();
    }

    private void RefreshRegionSelector()
    {
        WorkbenchTargetItem? target = ViewModel.SelectedTarget;
        RegionSelector.ItemsSource = target?.Regions;
        ViewModel.SelectedRegion = target?.Regions.FirstOrDefault();
        RegionSelector.SelectedItem = ViewModel.SelectedRegion;
        RefreshPage();
    }

    private void OnRegionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegionSelector.SelectedItem is WorkbenchRegionItem region)
        {
            ViewModel.SelectedRegion = region;
        }
        RefreshPage();
    }

    private void OnTranslationEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        ViewModel.MarkEditorChanged(createUndoPoint: true);
        region.TranslationEnabled = TranslationEnabledToggle.IsOn;
        RefreshPage();
    }

    private void RefreshPage()
    {
        WorkbenchRegionItem? region = ViewModel.SelectedRegion;
        bool hasRegion = region is not null;
        TranslationEnabledToggle.IsEnabled = hasRegion;
        _rendering = true;
        TranslationEnabledToggle.IsOn = region?.TranslationEnabled ?? false;
        _rendering = false;

        bool showCards = hasRegion && region!.TranslationEnabled && region.Channels.Count > 0;
        CardsHost.Visibility = showCards ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateHost.Visibility = showCards ? Visibility.Collapsed : Visibility.Visible;
        HeaderPanel.Visibility = hasRegion ? Visibility.Visible : Visibility.Collapsed;

        if (!hasRegion)
        {
            EmptyStateHost.Title = Strings.GetString("ChannelsEmptyNoRegionTitle");
            EmptyStateHost.Body = Strings.GetString("ChannelsEmptyNoRegionBody");
            EmptyStateHost.ActionContent = null;
            return;
        }

        if (!region!.TranslationEnabled)
        {
            EmptyStateHost.Title = Strings.GetString("ChannelsEmptyDisabledTitle");
            EmptyStateHost.Body = Strings.GetString("ChannelsEmptyDisabledBody");
            EmptyStateHost.ActionContent = null;
        }
        else if (region.Channels.Count == 0)
        {
            EmptyStateHost.Title = Strings.GetString("ChannelsEmptyNoChannelsTitle");
            EmptyStateHost.Body = Strings.GetString("ChannelsEmptyNoChannelsBody");
            var addButton = new Button();
            addButton.SetValue(AutomationProperties.NameProperty, Strings.GetString("WorkbenchAddChannelButton/Content"));
            addButton.Content = Strings.GetString("WorkbenchAddChannelButton/Content");
            addButton.Click += OnAddChannelClick;
            EmptyStateHost.ActionContent = addButton;
        }

        RefreshQuota(region);
        RenderChannelCards(region);
    }

    private void RefreshQuota(WorkbenchRegionItem region)
    {
        QuotaText.Text = string.Format(
            Strings.GetString("ChannelsQuotaLabel"),
            region.Channels.Count,
            ViewModel.MaxTranslationChannels);
        bool atCap = region.Channels.Count >= ViewModel.MaxTranslationChannels;
        bool hasProviders = _providerCatalog.Length > 0;
        AddChannelButton.IsEnabled = region.TranslationEnabled && !atCap && hasProviders;
        QuotaLimitText.Visibility = atCap ? Visibility.Visible : Visibility.Collapsed;

        if (region.TranslationEnabled && !hasProviders)
        {
            ShowInfo(
                InfoBarSeverity.Warning,
                Strings.GetString("WorkbenchNoProvidersTitle"),
                Strings.GetString("WorkbenchNoProvidersMessage"));
        }

        BudgetPreview.UsedChannels = region.Channels.Count;
        BudgetPreview.MaximumChannels = ViewModel.MaxTranslationChannels;
        BudgetPreview.WorstCaseRequests = region.Channels.Sum(
            channel => 1 + channel.EffectiveRefinementProviderIds.Count);
    }

    private void RenderChannelCards(WorkbenchRegionItem region)
    {
        CardsHost.Children.Clear();
        List<WorkbenchChannelDraft> channels = [.. region.Channels.OrderBy(channel => channel.DisplayOrder)];
        for (int index = 0; index < channels.Count; index++)
        {
            CardsHost.Children.Add(BuildChannelCard(region, channels[index], index, channels.Count));
        }
    }

    private ProviderChoice[] CatalogWithSaved(WorkbenchChannelDraft channel)
    {
        List<ProviderChoice> choices = [.. _providerCatalog];
        IEnumerable<string> saved = [channel.ProviderId, .. channel.EffectiveRefinementProviderIds];
        foreach (string id in saved.Where(id => id.Length > 0))
        {
            if (choices.Any(choice => string.Equals(choice.Id, id, StringComparison.Ordinal)))
            {
                continue;
            }
            choices.Add(new ProviderChoice(
                id,
                string.Format(Strings.GetString("WorkbenchUnavailableProviderOption"), id)));
        }
        return [.. choices];
    }

    private bool IsProviderKnown(string providerId) =>
        _providerCatalog.Any(choice => string.Equals(choice.Id, providerId, StringComparison.Ordinal));

    private UIElement BuildChannelCard(
        WorkbenchRegionItem region,
        WorkbenchChannelDraft channel,
        int index,
        int count)
    {
        ProviderChoice[] catalog = CatalogWithSaved(channel);
        ProviderChoice none = new(string.Empty, Strings.GetString("WorkbenchNoRefinerOption"));
        ProviderChoice[] refinerChoices = [none, .. catalog];

        var root = new StackPanel { Spacing = 8 };

        var pipelineRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var initialBox = new ComboBox
        {
            ItemsSource = catalog,
            MinWidth = 160,
        };
        AutomationProperties.SetName(initialBox, Strings.GetString("WorkbenchInitialTranslatorBox/Header"));
        initialBox.SelectedItem = catalog.FirstOrDefault(p =>
            string.Equals(p.Id, channel.ProviderId, StringComparison.Ordinal)) ?? catalog.FirstOrDefault();
        pipelineRow.Children.Add(initialBox);

        var firstRefinerBox = new ComboBox
        {
            ItemsSource = refinerChoices,
            MinWidth = 160,
        };
        AutomationProperties.SetName(firstRefinerBox, Strings.GetString("WorkbenchFirstRefinerBox/Header"));
        string? firstRefinerId = channel.EffectiveRefinementProviderIds.ElementAtOrDefault(0);
        firstRefinerBox.SelectedItem = refinerChoices.FirstOrDefault(p =>
            string.Equals(p.Id, firstRefinerId, StringComparison.Ordinal)) ?? none;
        pipelineRow.Children.Add(firstRefinerBox);

        var secondRefinerBox = new ComboBox
        {
            ItemsSource = refinerChoices,
            MinWidth = 160,
            Visibility = Equals(firstRefinerBox.SelectedItem, none) ? Visibility.Collapsed : Visibility.Visible,
        };
        AutomationProperties.SetName(secondRefinerBox, Strings.GetString("WorkbenchSecondRefinerBox/Header"));
        string? secondRefinerId = channel.EffectiveRefinementProviderIds.ElementAtOrDefault(1);
        secondRefinerBox.SelectedItem = refinerChoices.FirstOrDefault(p =>
            string.Equals(p.Id, secondRefinerId, StringComparison.Ordinal)) ?? none;
        pipelineRow.Children.Add(secondRefinerBox);
        root.Children.Add(pipelineRow);

        if (!IsProviderKnown(channel.ProviderId))
        {
            root.Children.Add(new TextBlock
            {
                Text = Strings.GetString("ChannelsProviderUnavailableBadge"),
                Foreground = (SolidColorBrush)Application.Current.Resources["StatusCriticalBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            });
        }

        void Commit()
        {
            if (_rendering ||
                initialBox.SelectedItem is not ProviderChoice initial)
            {
                return;
            }
            var first = firstRefinerBox.SelectedItem as ProviderChoice;
            var second = secondRefinerBox.SelectedItem as ProviderChoice;
            secondRefinerBox.Visibility = first is null || first == none
                ? Visibility.Collapsed
                : Visibility.Visible;
            string[] refinerIds = [.. WorkbenchViewModel.BuildRefinerIds(initial.Id, first?.Id, second?.Id)];
            var updated = channel with
            {
                ProviderId = initial.Id,
                Label = initial.Name,
                RefinementProviderIds = refinerIds,
            };
            ViewModel.TryUpdateChannel(region, channel, updated);
            RefreshQuota(region);
            RenderChannelCards(region);
        }

        initialBox.SelectionChanged += (_, _) => Commit();
        firstRefinerBox.SelectionChanged += (_, _) => Commit();
        secondRefinerBox.SelectionChanged += (_, _) => Commit();

        var advanced = new Expander
        {
            Header = Strings.GetString("ChannelsAdvancedHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var advancedPanel = new StackPanel { Spacing = 8, Padding = new Thickness(4, 8, 4, 8) };
        var labelBox = new TextBox
        {
            Header = Strings.GetString("ChannelsDisplayLabelHeader"),
            Text = channel.Label,
        };
        labelBox.LostFocus += (_, _) =>
        {
            if (_rendering)
            {
                return;
            }
            string label = string.IsNullOrWhiteSpace(labelBox.Text) ? channel.Label : labelBox.Text.Trim();
            ViewModel.TryUpdateChannel(region, channel, channel with { Label = label });
            RenderChannelCards(region);
        };
        advancedPanel.Children.Add(labelBox);
        advancedPanel.Children.Add(new TextBlock
        {
            Text = Strings.GetString("ChannelsAdvancedGapNote"),
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        advanced.Content = advancedPanel;
        root.Children.Add(advanced);

        var commandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        var upButton = new Button { MinWidth = 32, MinHeight = 32, Padding = new Thickness(6), IsEnabled = index > 0 };
        AutomationProperties.SetName(upButton, Strings.GetString("WorkbenchMoveChannelUpButtonName"));
        upButton.Content = new FontIcon { Glyph = "\uE74A", FontSize = 12 };
        upButton.Click += (_, _) => MoveChannel(region, channel, -1);
        commandRow.Children.Add(upButton);

        var downButton = new Button { MinWidth = 32, MinHeight = 32, Padding = new Thickness(6), IsEnabled = index < count - 1 };
        AutomationProperties.SetName(downButton, Strings.GetString("WorkbenchMoveChannelDownButtonName"));
        downButton.Content = new FontIcon { Glyph = "\uE74B", FontSize = 12 };
        downButton.Click += (_, _) => MoveChannel(region, channel, 1);
        commandRow.Children.Add(downButton);

        var deleteButton = new Button { MinWidth = 32, MinHeight = 32, Padding = new Thickness(6) };
        AutomationProperties.SetName(deleteButton, Strings.GetString("WorkbenchRemoveChannelButtonName"));
        deleteButton.Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 };
        deleteButton.Click += (_, _) =>
        {
            ViewModel.RemoveChannel(region, channel);
            RefreshQuota(region);
            RefreshPage();
        };
        commandRow.Children.Add(deleteButton);
        root.Children.Add(commandRow);

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Padding = new Thickness(12),
            Child = root,
        };
    }

    private void MoveChannel(WorkbenchRegionItem region, WorkbenchChannelDraft channel, int delta)
    {
        if (!ViewModel.MoveChannel(region, channel, delta))
        {
            return;
        }
        RenderChannelCards(region);
    }

    private void OnAddChannelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRegion is not { } region || _providerCatalog.Length == 0)
        {
            return;
        }
        ProviderChoice initial = _providerCatalog[0];
        var draft = new WorkbenchChannelDraft(
            Guid.NewGuid(),
            initial.Id,
            initial.Name,
            true,
            region.Channels.Count);
        if (!ViewModel.TryAddChannel(region, draft))
        {
            return;
        }
        RefreshPage();
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        PageInfoBar.Severity = severity;
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }
}
