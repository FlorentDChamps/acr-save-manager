using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ACRSaveManager.Services;

namespace ACRSaveManager.UI;

/// <summary>
/// Read-only view of one setup's parameters, or of several setups of a car side
/// by side, the parameters they set apart highlighted. A snapshot: it shows the
/// setups as they were when it opened, and writes nothing.
/// </summary>
public partial class SetupViewer : Window
{
    private readonly ICollectionView _rows;

    private SetupViewer(Window owner, IReadOnlyList<SetupExportData> setups)
    {
        Owner = owner;
        // before the XAML loads: its styles come from the main window
        Resources.MergedDictionaries.Add(owner.Resources);
        InitializeComponent();

        var rows = SetupView.Rows(setups);
        string car = SetupShareJson.FriendlyName(setups[0].Car);
        Title = setups.Count == 1
            ? $"{setups[0].Name} — {car}"
            : $"Compare {setups.Count} setups — {car}";

        GridParams.Resources["TrimmedText"] = TrimmedText();
        GridParams.Columns.Add(LabelColumn());
        for (int i = 0; i < setups.Count; i++)
        {
            var width = setups.Count == 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(170);
            GridParams.Columns.Add(SetupColumn(setups[i].Name, i, width));
        }
        GridParams.FrozenColumnCount = 1;

        // the setup's own fields start folded: the parameters are what is looked for
        var sections = rows.Select(r => r.Section).Distinct()
            .ToDictionary(s => s, s => new SectionState(s, s != SetupViewRow.SetupSection));
        _rows = CollectionViewSource.GetDefaultView(rows);
        _rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SetupViewRow.Section), new SectionOf(sections)));
        _rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SetupViewRow.Subsection)));
        GridParams.ItemsSource = _rows;

        int parameters = rows.Count(r => r.Section != SetupViewRow.SetupSection);
        int differ = rows.Count(r => r.Differs && r.Section != SetupViewRow.SetupSection);
        if (setups.Count == 1)
        {
            ChkDiffOnly.Visibility = Visibility.Collapsed;
            TxtSummary.Text = $"{parameters} parameters";
        }
        else
        {
            TxtSummary.Text = differ == 0
                ? $"{parameters} parameters, all the same"
                : $"{parameters} parameters, {differ} differ";
            Width = Math.Min(250 + 172 * setups.Count, SystemParameters.WorkArea.Width - 40);
        }
    }

    /// <summary>Opens the viewer, without blocking <paramref name="owner"/>.</summary>
    public static void Show(Window owner, IReadOnlyList<SetupExportData> setups) =>
        new SetupViewer(owner, setups).Show();

    private void ChkDiffOnly_Changed(object sender, RoutedEventArgs e)
    {
        bool only = ChkDiffOnly.IsChecked == true;
        _rows.Filter = only ? o => o is SetupViewRow r && (r.Differs || r.IsName) : null;
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A text that ends in "…" where it does not fit, and then shows whole in
    /// a tooltip.
    /// </summary>
    private static Style TrimmedText()
    {
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        text.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self }));
        text.Setters.Add(new EventSetter(ToolTipOpeningEvent, new ToolTipEventHandler(OnlyWhenTrimmed)));
        return text;
    }

    private DataGridTextColumn LabelColumn() => new()
    {
        Header = "Parameter",
        HeaderTemplate = TrimmedHeader,
        HeaderStyle = HeaderStyle("Parameter"),
        Binding = new Binding(nameof(SetupViewRow.Label)),
        ElementStyle = TrimmedText(),
        MinWidth = 90,
        Width = new DataGridLength(200),
    };

    /// <summary>
    /// The column of setup <paramref name="index"/>: each cell drawn after its
    /// row's kind, and copied (Ctrl+C) as the one-line text.
    /// </summary>
    private DataGridTemplateColumn SetupColumn(string header, int index, DataGridLength width)
    {
        var cell = new FrameworkElementFactory(typeof(ContentPresenter));
        cell.SetBinding(ContentPresenter.ContentProperty, new Binding($"{nameof(SetupViewRow.Cells)}[{index}]"));
        cell.SetBinding(ContentPresenter.ContentTemplateProperty,
            // the row's, as the presenter's own data context is its content
            new Binding($"{nameof(DataGridRow.Item)}.{nameof(SetupViewRow.Kind)}")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1),
                Converter = KindTemplate.Instance,
                ConverterParameter = GridParams,
            });
        var template = new DataTemplate { VisualTree = cell };
        template.Seal();

        return new DataGridTemplateColumn
        {
            Header = header,            // a string, which Ctrl+C copies as is
            HeaderTemplate = TrimmedHeader,
            HeaderStyle = HeaderStyle(header),
            CellTemplate = template,
            CellStyle = (Style)GridParams.FindResource("SetupCell"),
            ClipboardContentBinding = new Binding($"{nameof(SetupViewRow.Values)}[{index}]"),
            MinWidth = 90,
            Width = width,
        };
    }

    /// <summary>The grid's header style, with the full header as tooltip: setup names are the player's.</summary>
    private Style HeaderStyle(string header)
    {
        var head = new Style(typeof(DataGridColumnHeader), TryFindResource(typeof(DataGridColumnHeader)) as Style);
        head.Setters.Add(new Setter(ToolTipProperty, header));
        return head;
    }

    private static readonly DataTemplate TrimmedHeader = MakeTrimmedHeader();

    private static DataTemplate MakeTrimmedHeader()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var template = new DataTemplate { VisualTree = text };
        template.Seal();
        return template;
    }

    /// <summary>A cell's tooltip opens only when its text is cut short.</summary>
    private static void OnlyWhenTrimmed(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock tb || string.IsNullOrEmpty(tb.Text)) { e.Handled = true; return; }
        var full = new FormattedText(tb.Text, CultureInfo.CurrentCulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        if (full.WidthIncludingTrailingWhitespace <= tb.ActualWidth + 0.5) e.Handled = true;
    }

    // ── Sections ──────────────────────────────────────────────────────────────

    /// <summary>A section of the grid, and whether it is unfolded.</summary>
    public sealed class SectionState(string title, bool isExpanded) : INotifyPropertyChanged
    {
        public string Title { get; } = title;

        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public override string ToString() => Title;
    }

    /// <summary>
    /// The template of a cell after its row's kind, from the grid's resources
    /// (the converter parameter).
    /// </summary>
    private sealed class KindTemplate : IValueConverter
    {
        public static readonly KindTemplate Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var grid = (FrameworkElement)parameter;
            string key = value switch
            {
                SetupViewKind.Corners => "CornersCell",
                SetupViewKind.Axles => "AxlesCell",
                _ => "ValueCell",
            };
            return grid.FindResource(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Groups the rows under the one state object of their section.</summary>
    private sealed class SectionOf(Dictionary<string, SectionState> sections) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            sections[(string)value];

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private const string MaximizeGlyph = "";
    private const string RestoreGlyph = "";

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        MaximizeBtn.Content = WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WorkAreaMaximize.Attach(this);
    }
}
