using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation.Collections;

namespace InfiniTranseon.App.Controls;

/// <summary>
/// A labelled selector whose list opens below the box, and whose options can carry a second line.
///
/// WinUI's ComboBox positions its popup so the selected item covers the box, and exposes no property
/// to change that: on a page of stacked selectors the list appeared over the field the user was
/// pointing at, hiding both it and its neighbours. This is a DropDownButton with a bottom-placed
/// flyout instead, which also leaves room for each option's category — a provider list of bare names
/// gave no way to tell a cloud translator from a local model.
///
/// Options come either from <see cref="ItemsSource"/> or as <see cref="ListViewItem"/> children
/// declared in XAML, whose Tag carries the value a page reads back and whose x:Uid localizes the
/// label. Whichever way they arrive, the list itself is the one source of truth for the selection and
/// <see cref="SelectedIndex"/> and <see cref="SelectedItem"/> mirror it.
/// </summary>
[ContentProperty(Name = nameof(Options))]
public sealed partial class SelectBox : UserControl
{
    private INotifyCollectionChanged? _observedSource;
    private bool _syncing;

    public SelectBox()
    {
        InitializeComponent();
        OptionList.Items.VectorChanged += OnOptionsChanged;
        Loaded += OnLoaded;
    }

    public event EventHandler? SelectionChanged;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SelectBox),
        new PropertyMetadata(string.Empty, (box, _) => ((SelectBox)box).OnHeaderChanged()));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(SelectBox),
        new PropertyMetadata(string.Empty, (box, _) => ((SelectBox)box).OnPlaceholderChanged()));

    public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.Register(
        nameof(DisplayMemberPath), typeof(string), typeof(SelectBox),
        new PropertyMetadata(string.Empty, (box, _) => ((SelectBox)box).RebuildOptions()));

    public static readonly DependencyProperty CategoryMemberPathProperty = DependencyProperty.Register(
        nameof(CategoryMemberPath), typeof(string), typeof(SelectBox),
        new PropertyMetadata(string.Empty, (box, _) => ((SelectBox)box).RebuildOptions()));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(SelectBox),
        new PropertyMetadata(null, (box, _) => ((SelectBox)box).OnItemsSourceChanged()));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(SelectBox),
        new PropertyMetadata(null, (box, _) => ((SelectBox)box).RebuildOptions()));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(SelectBox),
        new PropertyMetadata(null, (box, _) => ((SelectBox)box).OnSelectedItemChanged()));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SelectBox),
        new PropertyMetadata(-1, (box, _) => ((SelectBox)box).OnSelectedIndexChanged()));

    /// <summary>Caption above the box. Empty hides it.</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Shown while nothing is selected.</summary>
    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Property holding an option's label. Empty uses the item's own ToString().</summary>
    public string DisplayMemberPath
    {
        get => (string)GetValue(DisplayMemberPathProperty);
        set => SetValue(DisplayMemberPathProperty, value);
    }

    /// <summary>Property holding an option's secondary line. Empty renders one line only.</summary>
    public string CategoryMemberPath
    {
        get => (string)GetValue(CategoryMemberPathProperty);
        set => SetValue(CategoryMemberPathProperty, value);
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Renders an option from <see cref="ItemsSource"/>; the box itself still shows text.</summary>
    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>
    /// The selected value: an item of <see cref="ItemsSource"/>, or a declared option's Tag. A value
    /// the list does not offer stays set and shows as no selection, so assigning it before the options
    /// arrive still selects it, and a stored value the catalog dropped is never silently rewritten.
    /// </summary>
    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Options declared in XAML, whose Tag carries the value the page reads back.</summary>
    public IList<object> Options => OptionList.Items;

    /// <summary>The values currently offered, in the order they were supplied.</summary>
    public IEnumerable<object> Items =>
        OptionList.Items.OfType<ListViewItem>().Select(option => option.Tag).OfType<object>();

    private void OnHeaderChanged()
    {
        HeaderText.Text = Header;
        HeaderText.Visibility = string.IsNullOrEmpty(Header) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Toggle, Header);
    }

    private void OnPlaceholderChanged()
    {
        if (OptionList.SelectedIndex < 0) ValueText.Text = PlaceholderText;
    }

    private void OnItemsSourceChanged()
    {
        if (_observedSource is not null) _observedSource.CollectionChanged -= OnSourceChanged;
        _observedSource = ItemsSource as INotifyCollectionChanged;
        if (_observedSource is not null) _observedSource.CollectionChanged += OnSourceChanged;
        RebuildOptions();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildOptions();

    private void RebuildOptions()
    {
        if (ItemsSource is not IEnumerable source) return;
        _syncing = true;
        OptionList.Items.Clear();
        foreach (object item in source)
        {
            OptionList.Items.Add(BuildOption(item));
        }
        _syncing = false;
        Select(IndexOf(SelectedItem), notify: false);
    }

    // XAML applies SelectedIndex before it adds the options that index counts, and a page may assign
    // SelectedItem either side of ItemsSource. Resolving the standing request whenever the list
    // changes is what makes every one of those orders end up on the same option.
    private void OnOptionsChanged(IObservableVector<object> sender, IVectorChangedEventArgs args)
    {
        if (_syncing) return;
        Select(
            SelectedItem is not null ? IndexOf(SelectedItem)
                : SelectedIndex < OptionList.Items.Count ? SelectedIndex
                : -1,
            notify: false);
    }

    private void OnSelectedItemChanged()
    {
        if (_syncing) return;
        Select(IndexOf(SelectedItem), notify: false);
    }

    private void OnSelectedIndexChanged()
    {
        // An index past the end is a request for an option that has not been added yet.
        if (_syncing || SelectedIndex >= OptionList.Items.Count) return;
        Select(SelectedIndex, notify: false);
    }

    private void Select(int index, bool notify)
    {
        _syncing = true;
        OptionList.SelectedIndex = index;
        SelectedIndex = index;
        if (index >= 0) SelectedItem = ValueAt(index);
        ValueText.Text = index < 0 ? PlaceholderText : Label(index);
        _syncing = false;
        if (notify) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private int IndexOf(object? value)
    {
        if (value is null) return -1;
        for (int index = 0; index < OptionList.Items.Count; index++)
        {
            if (Matches(ValueAt(index), value)) return index;
        }
        return -1;
    }

    // Two texts are the same option whatever their case: a mode stored by an older build still finds
    // the option carrying it, and reading the selection back hands the page the catalog's spelling.
    private static bool Matches(object? option, object? value) =>
        option is string left && value is string right
            ? string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            : Equals(option, value);

    private object? ValueAt(int index) =>
        OptionList.Items[index] is ListViewItem option ? option.Tag : null;

    private string Label(int index) => OptionList.Items[index] switch
    {
        // A declared option carries its localized label as its content; a built one is rendered from
        // the item, which the box shows as a single line even when the list shows two.
        ListViewItem { Content: string text } => text,
        ListViewItem option => Label(option.Tag),
        object other => other.ToString() ?? string.Empty,
    };

    private ListViewItem BuildOption(object item)
    {
        if (ItemTemplate is not null)
        {
            var templated = new ListViewItem { Content = item, ContentTemplate = ItemTemplate, Tag = item };
            // A templated option holds the item itself, so without this a screen reader announces the
            // whole record instead of the name the template puts on screen.
            AutomationProperties.SetName(templated, Label(item));
            return templated;
        }

        string category = Text(item, CategoryMemberPath);
        var label = new TextBlock { Text = Label(item), TextWrapping = TextWrapping.NoWrap };
        if (category.Length == 0)
        {
            return new ListViewItem { Content = label, Tag = item };
        }

        var stack = new StackPanel();
        stack.Children.Add(label);
        stack.Children.Add(new TextBlock
        {
            Text = category,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        });
        return new ListViewItem { Content = stack, Tag = item };
    }

    private string Label(object? item) =>
        item is null ? string.Empty : Text(item, DisplayMemberPath) is { Length: > 0 } text
            ? text
            : item.ToString() ?? string.Empty;

    private static string Text(object item, string memberPath)
    {
        if (memberPath.Length == 0)
        {
            return string.Empty;
        }
        PropertyInfo? property = item.GetType().GetProperty(memberPath);
        return property?.GetValue(item)?.ToString() ?? string.Empty;
    }

    // The page labels the box, but the button inside it is what a screen reader lands on, so the label
    // has to travel one level down or the control announces itself unnamed.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AutomationProperties.GetLabeledBy(this) is { } label)
        {
            AutomationProperties.SetLabeledBy(Toggle, label);
        }
        if (AutomationProperties.GetName(this) is { Length: > 0 } name)
        {
            AutomationProperties.SetName(Toggle, name);
        }
    }

    // A flyout is measured before it is placed, so the list only knows how wide the box is by the
    // time it opens. Without this the list shrank to its content and floated inside the field.
    private void OnOpening(object? sender, object e) => OptionList.MinWidth = Toggle.ActualWidth;

    private void OnOptionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        Select(OptionList.SelectedIndex, notify: true);
        OptionsFlyout.Hide();
    }
}
