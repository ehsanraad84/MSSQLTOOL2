using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MSSQLTool.Results
{
    /// <summary>
    /// Standalone results panel: shows the real result set, lets the user sort, filter per column,
    /// show/hide and freeze columns, inspect a full cell value, read per-column statistics, switch
    /// between multiple result sets and export as CSV, JSON or INSERT statements.
    /// </summary>
    public sealed class GridAnalyticsControl : UserControl, IDisposable
    {
        private const int ScanCap = 50000;
        // Cell-by-cell selection costs about 70 ms per 1000 cells; a click on a header stops here.
        private const int ColumnSelectionCap = 20000;
        // Previous/Next walks in-memory hits; a very common substring must not allocate millions.
        private const int MatchHitCap = 20000;

        private readonly TextBlock summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            FontWeight = FontWeights.SemiBold
        };
        private readonly TextBlock status = new TextBlock
        {
            Opacity = 0.75,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        private readonly ComboBox resultSets = new ComboBox
        {
            Width = 190,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = LocalizationManager.T("Result set")
        };
        private readonly ComboBox filterOperators = new ComboBox { Width = 110, Height = 26, Margin = new Thickness(0, 0, 4, 0) };

        // The column picker is a drop-down of checkboxes.  The old single-select combo could only
        // narrow a search to one column, and with "All columns" chosen the value box did nothing
        // at all - that was the reported "Apply has no effect" bug.
        private readonly ToggleButton filterColumnToggle = new ToggleButton
        {
            Height = 26,
            MinWidth = 136,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 4, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = LocalizationManager.T("Choose the columns to search")
        };
        private readonly Popup filterColumnPopup = new Popup
        {
            // StaysOpen = true so the toggle can open and close it deterministically; a popup that
            // closes itself on the outside click also swallows the toggle click and reopens.
            StaysOpen = true,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom
        };
        private readonly StackPanel filterColumnList = new StackPanel();
        private readonly HashSet<int> filterColumnsSelected = new HashSet<int>();
        private readonly Dictionary<int, CheckBox> filterColumnChecks = new Dictionary<int, CheckBox>();
        private bool suppressFilterColumnChange;
        private bool filterColumnListDirty;
        private readonly TextBlock matchCounter = new TextBlock
        {
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        };
        private Button previousMatchButton;
        private Button nextMatchButton;
        private readonly List<MatchHit> matchHits = new List<MatchHit>();
        private readonly Dictionary<DataRow, HashSet<int>> matchedCells = new Dictionary<DataRow, HashSet<int>>();
        private int matchIndex = -1;
        private bool matchHitsCapped;
        private readonly CellMatchConverter cellMatch;
        private readonly TextBox filterValue = new TextBox
        {
            Width = 160,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = LocalizationManager.T("Search the ticked columns (Ctrl+F)")
        };
        private readonly ComboBox freezeCount = new ComboBox
        {
            Width = 150,
            Height = 26,
            Margin = new Thickness(0, 0, 14, 0),
            ToolTip = LocalizationManager.T("Pin the first N visible columns so they stay on screen while scrolling right.")
        };
        private readonly TextBox columnSearch = new TextBox
        {
            Width = 170,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = LocalizationManager.T("Find a column by name or type; press Enter to jump to the next matching column.")
        };
        private readonly DataGrid dataGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            SelectionUnit = DataGridSelectionUnit.Cell,
            SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            HeadersVisibility = DataGridHeadersVisibility.All,
            RowHeaderWidth = 52,
            RowHeight = 26,
            ColumnHeaderHeight = 32,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };
        private readonly DataGrid statsGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            SelectionMode = DataGridSelectionMode.Single,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            RowHeight = 26,
            ColumnHeaderHeight = 32,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        private readonly TextBox statsFilter = new TextBox
        {
            Width = 170,
            Height = 24,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = LocalizationManager.T("Filter columns by name or type")
        };
        private readonly TextBox fieldFilter = new TextBox
        {
            Height = 26,
            Margin = new Thickness(0, 0, 0, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = LocalizationManager.T("Filter fields by name or type")
        };
        private readonly StackPanel fieldList = new StackPanel();
        private const double MinimumFieldWidth = 150;
        private const double DefaultFieldWidth = 240;
        private const double CollapsedFieldWidth = 26;
        // Auto-fit: wide enough for the content, never wide enough to push the other columns away.
        private const double MinimumAutoFitWidth = 64;
        private const double MaximumAutoFitWidth = 320;
        private const int AutoFitSampleRows = 200;
        private Style buttonStyle;
        private readonly Dictionary<int, Border> fieldRows = new Dictionary<int, Border>();
        private readonly CellTextConverter cellText = new CellTextConverter();
        private readonly CheckBox showNull = new CheckBox
        {
            Content = LocalizationManager.T("Show NULL"),
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 8, 4),
            ToolTip = LocalizationManager.T("Show NULL instead of an empty cell")
        };
        private ColumnDefinition fieldColumn;
        private GridSplitter fieldSplitter;
        private FrameworkElement fieldPanel;
        private FrameworkElement collapsedFieldStrip;
        private double expandedFieldWidth = DefaultFieldWidth;
        private bool fieldPanelCollapsed;
        private static readonly Brush FieldHoverBrush = new SolidColorBrush(Color.FromRgb(238, 243, 250));
        private static readonly Brush FieldActiveBrush = new SolidColorBrush(Color.FromRgb(214, 231, 250));
        private static readonly Brush ColumnHighlightBrush = new SolidColorBrush(Color.FromRgb(226, 238, 252));
        private Border activeFieldRow;
        private Style columnHighlightStyle;
        private string columnsSignature;
        private List<GridColumnInsight> fieldQueue;
        private int fieldIndex;
        private int fieldGeneration;
        private readonly TextBlock statsText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(10, 4, 10, 4),
            Padding = new Thickness(10, 4, 10, 4),
            MaxHeight = 90
        };

        private readonly TextBlock selectionSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(16, 0, 0, 0))
        };
        private bool refreshing;
        private bool updatingStats;
        private bool bulkSelecting;
        private DateTime lastRefreshUtc;
        private readonly DispatcherTimer applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private CancellationTokenSource analysisCts;
        private ICollectionView statsView;
        private string viewNote = string.Empty;
        private string statsNote = string.Empty;
        private readonly Dictionary<DataTable, GridAnalyticsResult> statsCache = new Dictionary<DataTable, GridAnalyticsResult>();
        private readonly List<DataTable> statsCacheOrder = new List<DataTable>();

        private List<DataTable> tables = new List<DataTable>();
        private DataTable source;
        private DataTable displayed;
        private GridAnalyticsResult analytics;
        private List<GridColumnInsight> allFields;
        private readonly List<SortKey> sorts = new List<SortKey>();
        private readonly List<int> hiddenOrdinals = new List<int>();
        private int frozenColumns;
        private bool suppressFreezeChange;
        private int lastFoundOrdinal = -1;
        private string lastColumnNeedle = string.Empty;
        private bool suppressResultSetChange;

        private static readonly string[] OperatorKeys =
        {
            "Contains", "Equals", "Not equals", "Starts with", "Ends with", "Greater than", "Less than"
        };

        public GridAnalyticsControl()
        {
            var panelBackground = ThemeBrush("ToolThemeBackgroundBrush", SystemColors.WindowBrush);
            Background = panelBackground;
            // Inherited text colour: without it the controls fall back to whatever the shell leaves
            // behind, which is where the washed-out button labels came from.
            Foreground = EnsureReadable(ThemeBrush("ToolThemeForegroundBrush", SystemColors.ControlTextBrush), panelBackground);
            cellMatch = new CellMatchConverter(this);
            ApplyGridLook(dataGrid);
            ApplyGridLook(statsGrid);

            var root = new Grid { Margin = new Thickness(10) };
            // Three columns: the field list, a splitter that resizes it, and the results area.  The
            // field list collapses to a narrow strip (see ApplyFieldPanelState) instead of vanishing.
            fieldColumn = new ColumnDefinition { Width = new GridLength(expandedFieldWidth), MinWidth = MinimumFieldWidth };
            root.ColumnDefinitions.Add(fieldColumn);
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var right = new Grid { Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(right, 2);
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 130 });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(summary, 0);
            right.Children.Add(summary);

            var filterBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            filterBar.Children.Add(resultSets);
            filterBar.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Find value:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            filterBar.Children.Add(filterColumnToggle);
            filterBar.Children.Add(filterOperators);
            filterBar.Children.Add(filterValue);
            filterBar.Children.Add(CreateButton("Apply", ApplyView));
            filterBar.Children.Add(CreateButton("Clear", ClearFilters));
            previousMatchButton = CreateButton("Previous match", GoToPreviousMatch);
            nextMatchButton = CreateButton("Next match", GoToNextMatch);
            previousMatchButton.IsEnabled = false;
            nextMatchButton.IsEnabled = false;
            filterBar.Children.Add(previousMatchButton);
            filterBar.Children.Add(nextMatchButton);
            filterBar.Children.Add(matchCounter);
            filterBar.Children.Add(filterColumnPopup);
            Grid.SetRow(filterBar, 1);
            right.Children.Add(filterBar);

            var actionBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            actionBar.Children.Add(CreateButton("Refresh", Refresh));
            actionBar.Children.Add(CreateButton("Export CSV...", ExportCsv));
            actionBar.Children.Add(CreateButton("Export JSON...", ExportJson));
            actionBar.Children.Add(CreateButton("Export INSERT...", ExportInsert));
            actionBar.Children.Add(CreateButton("Copy statistics", CopyStatistics));
            actionBar.Children.Add(CreateButton("Auto-fit columns", AutoFitColumns));
            actionBar.Children.Add(new TextBlock
            {
                Text = T("Freeze columns:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 6, 0)
            });
            actionBar.Children.Add(freezeCount);
            actionBar.Children.Add(showNull);
            Grid.SetRow(actionBar, 2);
            right.Children.Add(actionBar);

            Grid.SetRow(dataGrid, 4);
            right.Children.Add(dataGrid);

            var statsHost = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(14, 0, 0, 0)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            statsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            statsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            statsHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            statsHost.Children.Add(selectionSummary);
            var statsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            statsScroll.Content = statsText;
            Grid.SetRow(statsScroll, 2);
            statsHost.Children.Add(statsScroll);
            Grid.SetRow(statsHost, 5);
            right.Children.Add(statsHost);

            Grid.SetRow(status, 6);
            right.Children.Add(status);

            fieldPanel = CreateFieldPanel();
            Grid.SetColumn(fieldPanel, 0);
            root.Children.Add(fieldPanel);

            fieldSplitter = CreateFieldSplitter();
            Grid.SetColumn(fieldSplitter, 1);
            root.Children.Add(fieldSplitter);

            collapsedFieldStrip = CreateCollapsedFieldStrip();
            Grid.SetColumn(collapsedFieldStrip, 0);
            root.Children.Add(collapsedFieldStrip);

            root.Children.Add(right);

            ApplyFieldPanelState();

            Content = root;

            BuildStatsColumns();
            BuildFilterControls();
            BuildFreezeSelector();
            BuildFilterColumnPopup();

            filterValue.TextChanged += (_, __) => ScheduleApplyView();
            columnSearch.KeyDown += OnColumnSearchKeyDown;
            columnSearch.TextChanged += (_, __) => { lastFoundOrdinal = -1; lastColumnNeedle = string.Empty; };
            freezeCount.SelectionChanged += (_, __) => { if (!suppressFreezeChange) SetFrozenColumns(freezeCount.SelectedIndex, true); };
            filterOperators.SelectionChanged += (_, __) => ApplyView();
            applyTimer.Tick += (_, __) => { applyTimer.Stop(); ApplyView(); };
            resultSets.SelectionChanged += (_, __) => OnResultSetChanged();
            dataGrid.Sorting += OnSorting;
            dataGrid.SelectedCellsChanged += (_, __) =>
            {
                // A bulk column selection is summarised once by SelectWholeColumn.
                if (bulkSelecting) return;
                ClearColumnHighlight();
                UpdateSelectionStatistics();
                SyncFieldRowFromGrid();
            };
            dataGrid.PreviewMouseLeftButtonUp += OnGridPreviewMouseLeftButtonUp;
            showNull.Checked += (_, __) => OnShowNullChanged();
            showNull.Unchecked += (_, __) => OnShowNullChanged();
            dataGrid.MouseDoubleClick += OnCellDoubleClick;
            dataGrid.LoadingRow += (_, e) => { e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture); };
            // 271 columns: without virtualization every column header and cell factory is built up front.
            dataGrid.EnableColumnVirtualization = true;
            dataGrid.EnableRowVirtualization = true;
            statsGrid.MouseDoubleClick += OnStatsDoubleClick;
            statsGrid.SelectionChanged += OnStatsSelectionChanged;
            dataGrid.CurrentCellChanged += OnCurrentCellChanged;
            statsFilter.TextChanged += (_, __) => ApplyStatsFilter();
            statsFilter.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    statsFilter.Text = string.Empty;
                    e.Handled = true;
                }
            };
            PreviewKeyDown += OnPreviewKeyDown;

            Loaded += (_, __) => Refresh();
            IsVisibleChanged += (_, __) => { if (IsVisible) Refresh(); };
        }

        private static string T(string text) => LocalizationManager.T(text);

        private Brush ThemeBrush(string key, Brush fallback)
            => TryFindResource(key) as Brush
               ?? Application.Current?.TryFindResource(key) as Brush
               ?? fallback;

        /// <summary>
        /// Keeps text readable whatever the shell hands us: when the inherited foreground and the
        /// background are too close in luminance (light grey on white was the reported case), swap
        /// in black or white instead of drawing text nobody can read.
        /// </summary>
        private static Brush EnsureReadable(Brush foreground, Brush background)
        {
            Color foregroundColor = (foreground as SolidColorBrush)?.Color ?? SystemColors.ControlTextColor;
            Color backgroundColor = (background as SolidColorBrush)?.Color ?? SystemColors.WindowColor;

            double lighter = Math.Max(VsThemeBrushResolver.GetRelativeLuminance(foregroundColor),
                                      VsThemeBrushResolver.GetRelativeLuminance(backgroundColor));
            double darker = Math.Min(VsThemeBrushResolver.GetRelativeLuminance(foregroundColor),
                                     VsThemeBrushResolver.GetRelativeLuminance(backgroundColor));
            double contrast = (lighter + 0.05) / (darker + 0.05);
            if (contrast >= 4.5) return foreground;

            return VsThemeBrushResolver.GetRelativeLuminance(backgroundColor) > 0.5 ? Brushes.Black : Brushes.White;
        }

        /// <summary>
        /// Explicit style for the code-built buttons.  They used to rely on the shell's default
        /// button look, which left the labels too light to read on the panel background.
        /// </summary>
        private Style ButtonStyle()
        {
            if (buttonStyle != null) return buttonStyle;

            Brush background = ThemeBrush("ToolThemeButtonBackgroundBrush", new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xF9)));
            Brush foreground = EnsureReadable(ThemeBrush("ToolThemeForegroundBrush", SystemColors.ControlTextBrush), background);
            Brush hover = ThemeBrush("ToolThemeButtonHoverBrush", new SolidColorBrush(Color.FromRgb(0xE2, 0xEB, 0xF7)));
            Brush pressed = ThemeBrush("ToolThemeButtonPressedBrush", new SolidColorBrush(Color.FromRgb(0xD2, 0xE0, 0xF1)));
            Brush border = ThemeBrush("ToolThemeBorderBrush", new SolidColorBrush(Color.FromRgb(0xBF, 0xC7, 0xD1)));

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
            style.Setters.Add(new Setter(Control.BackgroundProperty, background));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Control.BackgroundProperty, hover));
            style.Triggers.Add(over);

            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Control.BackgroundProperty, pressed));
            style.Triggers.Add(down);

            buttonStyle = style;
            return buttonStyle;
        }

        private Button CreateButton(string text, Action action)
        {
            var button = new Button
            {
                Content = T(text),
                Height = 26,
                MinWidth = 64,
                FontSize = 12.5,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 0, 6, 4),
                Style = ButtonStyle(),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            button.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Results analytics action failed.");
                    status.Text = LocalizationManager.Format("The action failed: {0}", ex.Message);
                }
            };
            return button;
        }

        private Button CreateSmallButton(string text, Action action)
        {
            var button = new Button
            {
                Content = T(text),
                Height = 22,
                MinWidth = 0,
                FontSize = 11.5,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Style = ButtonStyle(),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            button.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Results analytics action failed.");
                    status.Text = LocalizationManager.Format("The action failed: {0}", ex.Message);
                }
            };
            return button;
        }

        private void BuildFilterControls()
        {
            foreach (string key in OperatorKeys) filterOperators.Items.Add(T(key));
            filterOperators.SelectedIndex = 0;

            filterValue.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { ApplyView(); e.Handled = true; }
            };

            UpdateFilterColumnCaption();
        }

        /// <summary>
        /// Builds the checkbox drop-down behind the column toggle.  The list is populated lazily on
        /// first open: a 271-column result set would otherwise create 271 checkboxes the moment the
        /// panel loads, before the user ever touches the filter.
        /// </summary>
        private void BuildFilterColumnPopup()
        {
            var panel = new DockPanel { LastChildFill = true, Width = 240 };

            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            actions.Children.Add(CreateSmallButton("Select all", () => SetAllFilterColumns(true)));
            actions.Children.Add(CreateSmallButton("Deselect all", () => SetAllFilterColumns(false)));
            DockPanel.SetDock(actions, Dock.Top);
            panel.Children.Add(actions);

            var scroll = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            scroll.Content = filterColumnList;
            panel.Children.Add(scroll);

            filterColumnPopup.Child = new Border
            {
                Background = ThemeBrush("ToolThemeBackgroundBrush", SystemColors.WindowBrush),
                BorderBrush = ThemeBrush("ToolThemeBorderBrush", new SolidColorBrush(Color.FromRgb(0xBF, 0xC7, 0xD1))),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = panel
            };
            filterColumnPopup.PlacementTarget = filterColumnToggle;
            filterColumnPopup.Opened += (_, __) =>
            {
                EnsureFilterColumnList();
                if (Window.GetWindow(this) is Window host) host.PreviewMouseDown += OnFilterColumnWindowMouseDown;
            };
            filterColumnPopup.Closed += (_, __) =>
            {
                if (Window.GetWindow(this) is Window host) host.PreviewMouseDown -= OnFilterColumnWindowMouseDown;
                filterColumnToggle.IsChecked = false;
            };
            filterColumnToggle.Checked += (_, __) => filterColumnPopup.IsOpen = true;
            filterColumnToggle.Unchecked += (_, __) => filterColumnPopup.IsOpen = false;
        }

        /// <summary>Closes the column drop-down when the click landed outside it and its toggle.</summary>
        private void OnFilterColumnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!filterColumnPopup.IsOpen) return;

            DependencyObject source = e.OriginalSource as DependencyObject;
            if (IsDescendant(source, filterColumnPopup.Child) || IsDescendant(source, filterColumnToggle)) return;
            filterColumnPopup.IsOpen = false;
        }

        private static bool IsDescendant(DependencyObject node, DependencyObject ancestor)
        {
            if (node == null || ancestor == null) return false;

            for (int depth = 0; node != null && depth < 32; depth++)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        /// <summary>Rebuilds the ticked-column state for a new result set: every column starts ticked.</summary>
        private void ResetFilterColumns()
        {
            filterColumnsSelected.Clear();
            if (source != null)
            {
                for (int ordinal = 0; ordinal < source.Columns.Count; ordinal++)
                    filterColumnsSelected.Add(ordinal);
            }

            filterColumnChecks.Clear();
            filterColumnList.Children.Clear();
            filterColumnListDirty = true;
            UpdateFilterColumnCaption();
        }

        private void EnsureFilterColumnList()
        {
            if (!filterColumnListDirty) return;
            filterColumnListDirty = false;

            suppressFilterColumnChange = true;
            try
            {
                filterColumnList.Children.Clear();
                filterColumnChecks.Clear();
                if (source == null) return;

                for (int ordinal = 0; ordinal < source.Columns.Count; ordinal++)
                {
                    int index = ordinal;
                    DataColumn column = source.Columns[ordinal];
                    var check = new CheckBox
                    {
                        Content = GridAnalytics.DisplayName(column),
                        IsChecked = filterColumnsSelected.Contains(ordinal),
                        Margin = new Thickness(0, 2, 0, 2),
                        ToolTip = GridAnalytics.FriendlyTypeName(column)
                    };
                    check.Checked += (_, __) => SetFilterColumnSelected(index, true);
                    check.Unchecked += (_, __) => SetFilterColumnSelected(index, false);
                    filterColumnChecks[ordinal] = check;
                    filterColumnList.Children.Add(check);
                }
            }
            finally
            {
                suppressFilterColumnChange = false;
            }
        }

        private void SetFilterColumnSelected(int ordinal, bool selected)
        {
            if (suppressFilterColumnChange) return;
            if (selected) filterColumnsSelected.Add(ordinal);
            else filterColumnsSelected.Remove(ordinal);
            UpdateFilterColumnCaption();
        }

        private void SetAllFilterColumns(bool selected)
        {
            EnsureFilterColumnList();
            suppressFilterColumnChange = true;
            try
            {
                filterColumnsSelected.Clear();
                if (source != null)
                {
                    for (int ordinal = 0; ordinal < source.Columns.Count; ordinal++)
                        if (selected) filterColumnsSelected.Add(ordinal);
                }

                foreach (KeyValuePair<int, CheckBox> pair in filterColumnChecks)
                    pair.Value.IsChecked = selected;
            }
            finally
            {
                suppressFilterColumnChange = false;
            }

            UpdateFilterColumnCaption();
        }

        private void UpdateFilterColumnCaption()
        {
            int total = source == null ? 0 : source.Columns.Count;
            string text;
            if (total == 0 || filterColumnsSelected.Count >= total) text = T("All columns");
            else if (filterColumnsSelected.Count == 0) text = T("No columns selected");
            else text = LocalizationManager.Format("{0} column(s) selected", filterColumnsSelected.Count);

            filterColumnToggle.Content = text + "  \u25BE";
        }

        /// <summary>The ordinals to search, in column order.  Empty means no column was ticked.</summary>
        private List<int> SelectedFilterOrdinals()
        {
            var list = new List<int>();
            if (source == null) return list;

            foreach (int ordinal in filterColumnsSelected.OrderBy(item => item))
            {
                if (ordinal >= 0 && ordinal < source.Columns.Count) list.Add(ordinal);
            }

            return list;
        }

        /// <summary>
        /// The freeze selector always offers "none" plus the first ten columns.  The actual number
        /// is clamped to the columns on screen, and the header context menu writes back into it.
        /// </summary>
        private void BuildFreezeSelector()
        {
            suppressFreezeChange = true;
            try
            {
                freezeCount.Items.Clear();
                freezeCount.Items.Add(T("No fixed columns"));
                for (int count = 1; count <= 10; count++)
                    freezeCount.Items.Add(LocalizationManager.Format("First {0} column(s)", count));
                freezeCount.SelectedIndex = 0;
            }
            finally
            {
                suppressFreezeChange = false;
            }
        }

        private void SetFrozenColumns(int count, bool announce)
        {
            int effective = Math.Max(0, Math.Min(count, dataGrid.Columns.Count));
            frozenColumns = effective;
            dataGrid.FrozenColumnCount = effective;
            SyncFreezeSelector();

            if (announce)
            {
                status.Text = effective == 0
                    ? T("Columns unfrozen.")
                    : LocalizationManager.Format("First {0} column(s) frozen.", effective);
            }
        }

        private void SyncFreezeSelector()
        {
            if (freezeCount.Items.Count == 0) return;

            suppressFreezeChange = true;
            try
            {
                freezeCount.SelectedIndex = Math.Min(frozenColumns, freezeCount.Items.Count - 1);
            }
            finally
            {
                suppressFreezeChange = false;
            }
        }

        /// <summary>
        /// Statistics columns as data, so a regression test can prove every binding path still
        /// resolves to a readable property (the table used to render blank because the values were
        /// fields, which WPF bindings cannot see).
        /// </summary>
        internal static readonly StatsColumnDefinition[] StatsColumns =
        {
            new StatsColumnDefinition("Column", "Name", 170, false),
            new StatsColumnDefinition("Type", "DataType", 72, true),
            new StatsColumnDefinition("Non-null", "NonNull", 62, true),
            new StatsColumnDefinition("Nulls", "NullCount", 56, true),
            new StatsColumnDefinition("Null %", "NullPercentText", 58, true),
            new StatsColumnDefinition("Distinct", "Distinct", 62, true),
            new StatsColumnDefinition("Max length", "MaxLength", 76, true),
            new StatsColumnDefinition("Min", "Minimum", 120, true),
            new StatsColumnDefinition("Max", "Maximum", 120, true),
            new StatsColumnDefinition("Average", "Average", 86, true),
            new StatsColumnDefinition("Sum", "Sum", 96, true),
            new StatsColumnDefinition("Most common", "MostCommonText", 190, false)
        };

        internal sealed class StatsColumnDefinition
        {
            public StatsColumnDefinition(string header, string path, double width, bool rightAlign)
            {
                Header = header;
                Path = path;
                Width = width;
                RightAlign = rightAlign;
            }

            public string Header { get; }
            public string Path { get; }
            public double Width { get; }
            public bool RightAlign { get; }
        }

        private void BuildStatsColumns()
        {
            foreach (StatsColumnDefinition definition in StatsColumns)
                statsGrid.Columns.Add(TextColumn(definition.Header, definition.Path, definition.Width, definition.RightAlign));
        }

        private static DataGridTextColumn TextColumn(string header, string path, double width, bool rightAlign = false)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0)));
            if (rightAlign) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));

            return new DataGridTextColumn
            {
                Header = T(header),
                Binding = new Binding(path) { Mode = BindingMode.OneWay },
                ElementStyle = style,
                Width = new DataGridLength(width)
            };
        }

        /// <summary>Reads the current results grids and shows the first (or the previously chosen) one.</summary>
        public void Refresh()
        {
            if (refreshing) return;
            if ((DateTime.UtcNow - lastRefreshUtc).TotalMilliseconds < 400) return;
            lastRefreshUtc = DateTime.UtcNow;
            refreshing = true;

            try
            {
                int previous = Math.Max(0, resultSets.SelectedIndex);
                tables = GridAccess.GetDataTables() ?? new List<DataTable>();
                tables = tables.Where(table => table != null && table.Columns.Count > 0).ToList();

                suppressResultSetChange = true;
                try
                {
                    resultSets.Items.Clear();
                    for (int index = 0; index < tables.Count; index++)
                    {
                        DataTable table = tables[index];
                        resultSets.Items.Add(LocalizationManager.Format(
                            "Result {0}: {1} row(s) x {2} column(s)", index + 1, table.Rows.Count, table.Columns.Count));
                    }
                    resultSets.Visibility = tables.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                    if (tables.Count > 0) resultSets.SelectedIndex = Math.Min(previous, tables.Count - 1);
                }
                finally
                {
                    suppressResultSetChange = false;
                }

                LoadSelectedResult();

                if (source == null)
                {
                    analytics = new GridAnalyticsResult();
                    displayed = null;
                    dataGrid.ItemsSource = null;
                    dataGrid.FrozenColumnCount = 0;
                    dataGrid.Columns.Clear();
                    frozenColumns = 0;
                    SyncFreezeSelector();
                    statsGrid.ItemsSource = null;
                    statsView = null;
                    viewNote = string.Empty;
                    statsNote = string.Empty;
                    summary.Text = T("No results grid with data is available.");
                    UpdateStatus();
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not analyze the results grid.");
                status.Text = LocalizationManager.Format("Could not analyze the results grid: {0}", ex.Message);
            }
            finally
            {
                refreshing = false;
            }
        }

        private void OnResultSetChanged()
        {
            if (suppressResultSetChange) return;
            LoadSelectedResult();
        }

        private void LoadSelectedResult()
        {
            int index = resultSets.SelectedIndex;
            if (index < 0 || index >= tables.Count) index = 0;
            source = tables.Count > 0 ? tables[index] : null;
            if (source == null)
            {
                CancelAnalysis();
                analytics = new GridAnalyticsResult();
                displayed = null;
                dataGrid.ItemsSource = null;
                dataGrid.FrozenColumnCount = 0;
                dataGrid.Columns.Clear();
                frozenColumns = 0;
                SyncFreezeSelector();
                statsGrid.ItemsSource = null;
                statsView = null;
                selectionSummary.Text = string.Empty;
                viewNote = string.Empty;
                statsNote = string.Empty;
                UpdateStatus();
                return;
            }

            CancelAnalysis();
            hiddenOrdinals.Clear();
            sorts.Clear();
            frozenColumns = 0;
            lastFoundOrdinal = -1;
            lastColumnNeedle = string.Empty;
            ResetFilterColumns();
            selectionSummary.Text = T("Select cells to see their statistics.");

            // Paint the rows first.  The statistics pass used to run before the grid was bound, so a
            // wide result set showed an empty panel until the numbers were done.
            ApplyView();
            AnalyzeAsync(source);
        }

        /// <summary>
        /// The statistics pass is the expensive part on a wide result set, so it runs on a worker
        /// thread while the grid already shows its rows.  A newer request cancels the old scan, and
        /// a repeated look at the same DataTable is served from the small cache instead of rescanning.
        /// </summary>
        private void AnalyzeAsync(DataTable table)
        {
            CancelAnalysis();

            // Opening the window no longer scans every column: the list appears immediately and
            // statistics are produced from the current selection (one cell = that column, several
            // cells = the selected cells together).
            var columns = new List<GridColumnInsight>();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                DataColumn column = table.Columns[i];
                columns.Add(new GridColumnInsight { Ordinal = i, Name = GridAnalytics.DisplayName(column), DataType = column.DataType.Name });
            }

            var placeholder = new GridAnalyticsResult
            {
                RowCount = table.Rows.Count,
                ColumnCount = table.Columns.Count,
                SampledRows = 0,
                Columns = columns
            };
            ApplyStatistics(placeholder);
            allFields = columns;
            RebuildFieldList();
        }

        private void CancelAnalysis()
        {
            CancellationTokenSource previous = analysisCts;
            analysisCts = null;
            if (previous == null) return;

            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void RememberStatistics(DataTable table, GridAnalyticsResult result)
        {
            statsCache[table] = result;
            statsCacheOrder.Remove(table);
            statsCacheOrder.Add(table);

            while (statsCacheOrder.Count > 3)
            {
                DataTable oldest = statsCacheOrder[0];
                statsCacheOrder.RemoveAt(0);
                statsCache.Remove(oldest);
            }
        }

        private void ApplyStatistics(GridAnalyticsResult result)
        {
            analytics = result;

            updatingStats = true;
            try
            {
                statsView = new ListCollectionView(result.Columns);
                statsView.Filter = StatsFilterPredicate;
                statsGrid.ItemsSource = statsView;
            }
            finally
            {
                updatingStats = false;
            }

            summary.Text = LocalizationManager.Format(
                "{0} row(s), {1} column(s) | empty cells {2}% | duplicate rows {3}",
                result.RowCount, result.ColumnCount, result.NullCellPercent, result.DuplicateRows);

            statsNote = result.Truncated
                ? LocalizationManager.Format("Statistics cover a sample of the first {0} of {1} row(s).", result.SampledRows, result.RowCount)
                : T("Statistics cover every row in the grid.");
            UpdateStatus();

            ApplyStatsFilter();
        }

        private bool StatsFilterPredicate(object item)
        {
            GridColumnInsight insight = item as GridColumnInsight;
            if (insight == null) return false;

            string needle = (statsFilter.Text ?? string.Empty).Trim();
            if (needle.Length == 0) return true;

            return insight.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   insight.DataType.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   Convert.ToString(insight.Ordinal, CultureInfo.CurrentCulture) == needle;
        }

        private void ApplyStatsFilter()
        {
            if (statsView == null) return;
            try { statsView.Refresh(); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Could not filter the column statistics."); }
        }

        private void OnStatsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingStats) return;
            FocusGridColumn(statsGrid.SelectedItem as GridColumnInsight);
        }

        /// <summary>Scrolls the data grid so the column behind a statistics row is on screen.</summary>
        private void FocusGridColumn(GridColumnInsight insight)
        {
            if (insight == null || displayed == null) return;

            int ordinal = insight.Ordinal;
            if (ordinal < 0 || ordinal >= displayed.Columns.Count) return;

            string name = displayed.Columns[ordinal].ColumnName;
            DataGridColumn column = dataGrid.Columns.FirstOrDefault(item =>
                string.Equals(item.SortMemberPath, name, StringComparison.Ordinal));
            if (column == null)
            {
                viewNote = LocalizationManager.Format("Column {0} is hidden; tick it in the field list to see it.", insight.Name);
                UpdateStatus();
                return;
            }

            dataGrid.UnselectAll();
            HighlightGridColumn(name);
            SelectFieldRow(insight);
            if (dataGrid.Items.Count == 0) return;
            try { dataGrid.ScrollIntoView(dataGrid.Items[0], column); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Could not scroll to the selected column."); }
        }

        /// <summary>Drops the field-driven column tint, e.g. as soon as the user picks a cell.</summary>
        private void ClearColumnHighlight()
        {
            ClearActiveFieldRow();

            foreach (DataGridColumn column in dataGrid.Columns)
                column.ClearValue(DataGridColumn.CellStyleProperty);
        }

        /// <summary>Tints every cell of one column so the field the user clicked is obvious in the grid.</summary>
        private void HighlightGridColumn(string name)
        {
            foreach (DataGridColumn column in dataGrid.Columns)
            {
                bool hit = string.Equals(column.SortMemberPath, name, StringComparison.Ordinal);
                if (!hit)
                {
                    column.ClearValue(DataGridColumn.CellStyleProperty);
                    continue;
                }

                if (columnHighlightStyle == null)
                {
                    columnHighlightStyle = new Style(typeof(DataGridCell), dataGrid.CellStyle);
                    columnHighlightStyle.Setters.Add(new Setter(Control.BackgroundProperty, ColumnHighlightBrush));
                }
                column.CellStyle = columnHighlightStyle;
            }
        }

        /// <summary>
        /// Selecting a cell in the grid tidies the panel state: the matching statistics row and the
        /// matching field row become the single active selection, whatever was tinted before.
        /// </summary>
        private void OnCurrentCellChanged(object sender, EventArgs e)
        {
            if (bulkSelecting || updatingStats || analytics == null || displayed == null) return;
            if (dataGrid.SelectedCells.Count != 1 || dataGrid.CurrentCell.Column == null) return;

            string name = Convert.ToString(dataGrid.CurrentCell.Column.SortMemberPath);
            if (string.IsNullOrEmpty(name)) return;

            SelectStatisticsRowForColumn(name);
        }

        /// <summary>Highlights the statistics row for a column without running the scroll-back handler.</summary>
        private void SelectStatisticsRow(int ordinal)
        {
            if (analytics == null) return;
            GridColumnInsight insight = analytics.Columns.FirstOrDefault(item => item.Ordinal == ordinal);
            if (insight != null) SelectStatisticsRow(insight);
        }

        /// <summary>
        /// Picks the statistics row by column name.  The analyzer numbers columns from 1 while the
        /// placeholder rows used before the statistics pass are numbered from 0, so matching on the
        /// name keeps both sources correct.
        /// </summary>
        private void SelectStatisticsRowForColumn(string columnName)
        {
            if (displayed == null || string.IsNullOrEmpty(columnName)) return;
            if (!displayed.Columns.Contains(columnName)) return;

            string display = GridAnalytics.DisplayName(displayed.Columns[columnName]);
            // Read the rows the statistics grid is actually showing: ShowSingleInsight swaps the
            // analytics result, but the visible rows keep pointing at the full column list.
            GridColumnInsight insight = null;
            foreach (object item in statsGrid.Items)
            {
                if (item is GridColumnInsight candidate && string.Equals(candidate.Name, display, StringComparison.Ordinal))
                {
                    insight = candidate;
                    break;
                }
            }

            if (insight != null) SelectStatisticsRow(insight);
        }

        private void SelectStatisticsRow(GridColumnInsight insight)
        {
            if (insight == null) return;

            updatingStats = true;
            try
            {
                statsGrid.SelectedItem = insight;
                statsGrid.ScrollIntoView(insight);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not select the statistics row.");
            }
            finally
            {
                updatingStats = false;
            }
        }

        private void UpdateStatus()
        {
            if (string.IsNullOrEmpty(viewNote)) status.Text = statsNote;
            else if (string.IsNullOrEmpty(statsNote)) status.Text = viewNote;
            else status.Text = viewNote + "  " + statsNote;
        }

        /// <summary>
        /// Applies the find value: every ticked column is tested with the chosen operator, and the
        /// cells that show the value are tinted so Previous/Next can step through them.  Unlike the
        /// old filter row it never hides rows: the value stays in context.
        /// </summary>
        private void ApplyView()
        {
            if (source == null) return;

            string operand = (filterValue.Text ?? string.Empty).Trim();
            string opKey = filterOperators.SelectedIndex >= 0 && filterOperators.SelectedIndex < OperatorKeys.Length
                ? OperatorKeys[filterOperators.SelectedIndex]
                : "Contains";

            bool finding = operand.Length > 0;
            List<int> columns = finding ? SelectedFilterOrdinals() : null;
            if (finding && (columns == null || columns.Count == 0))
            {
                status.Text = T("Select at least one column to search.");
                return;
            }

            matchedCells.Clear();
            matchHits.Clear();
            matchHitsCapped = false;
            int scanned = 0, matchingRows = 0;
            if (finding)
            {
                var hits = new List<int>();
                try
                {
                    foreach (DataRow row in source.Rows)
                    {
                        if (scanned >= ScanCap) break;
                        scanned++;

                        if (!MatchRow(row, columns, opKey, operand, hits)) continue;
                        matchingRows++;
                        if (hits.Count == 0) continue;

                        matchedCells[row] = new HashSet<int>(hits);
                        foreach (int ordinal in hits)
                        {
                            if (matchHits.Count >= MatchHitCap) { matchHitsCapped = true; break; }
                            matchHits.Add(new MatchHit { Row = row, Ordinal = ordinal });
                        }
                    }
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Searching the results grid failed.");
                    return;
                }
            }

            // Finding never hides rows: the matches are tinted where they are, so the value stays in
            // context and Previous/Next can walk the grid the user is looking at.
            displayed = source;
            BuildDataColumns();

            var view = new DataView(displayed);
            string sort = BuildSortExpression();
            dataGrid.ItemsSource = view;
            if (sort.Length > 0) view.Sort = sort;

            if (finding)
            {
                if (matchingRows == 0)
                {
                    viewNote = LocalizationManager.Format("Nothing matches '{0}'.", operand);
                }
                else if (matchHits.Count == 0)
                {
                    viewNote = LocalizationManager.Format("{0} matching row(s).", matchingRows);
                }
                else
                {
                    viewNote = LocalizationManager.Format("{0} matching cell(s) in {1} row(s).",
                        matchHits.Count, matchingRows);
                    if (matchHitsCapped)
                        viewNote += "  " + LocalizationManager.Format("Only the first {0} matching cells are listed for Previous/Next.", MatchHitCap);
                    if (scanned < source.Rows.Count)
                        viewNote += "  " + LocalizationManager.Format("Scanned the first {0} row(s).", ScanCap);
                }
            }
            else
            {
                viewNote = string.Empty;
            }

            ResetMatchNavigation();
            UpdateStatus();
            UpdateSortArrows();
        }

        /// <summary>
        /// True when the row matches in at least one of <paramref name="columns"/>, and reports the
        /// ordinals whose text actually shows the searched value (the cells to tint).
        /// </summary>
        internal static bool MatchRow(DataRow row, IList<int> columns, string opKey, string operand, IList<int> hits)
        {
            if (row == null || row.Table == null || columns == null) return false;

            bool matched = false;
            if (hits != null) hits.Clear();
            for (int index = 0; index < columns.Count; index++)
            {
                int ordinal = columns[index];
                if (ordinal < 0 || ordinal >= row.Table.Columns.Count) continue;

                object value = row[ordinal];
                if (!Matches(value, opKey, operand)) continue;

                matched = true;
                if (hits != null && CellContains(value, operand)) hits.Add(ordinal);
            }

            return matched;
        }

        /// <summary>The text of a cell holds the searched value, ignoring case.</summary>
        internal static bool CellContains(object value, string needle)
        {
            if (value == null || value == DBNull.Value || string.IsNullOrEmpty(needle)) return false;
            string text = Convert.ToString(value, CultureInfo.CurrentCulture);
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ResetMatchNavigation()
        {
            matchIndex = -1;
            matchCounter.Text = matchHits.Count == 0
                ? string.Empty
                : "0 / " + matchHits.Count.ToString(CultureInfo.CurrentCulture);

            if (previousMatchButton != null) previousMatchButton.IsEnabled = matchHits.Count > 0;
            if (nextMatchButton != null) nextMatchButton.IsEnabled = matchHits.Count > 0;
        }

        private void GoToPreviousMatch() => GoToMatch(-1);

        private void GoToNextMatch() => GoToMatch(1);

        /// <summary>Steps through the matching cells, wrapping around at either end.</summary>
        private void GoToMatch(int delta)
        {
            if (matchHits.Count == 0)
            {
                status.Text = T("No matching cells. Search for a value first.");
                return;
            }

            if (matchIndex < 0)
                matchIndex = delta < 0 ? matchHits.Count - 1 : 0;
            else
                matchIndex = (matchIndex + delta + matchHits.Count) % matchHits.Count;

            ShowMatch(matchHits[matchIndex], matchIndex + 1);
        }

        /// <summary>Selects one matching cell and scrolls it into view.</summary>
        private void ShowMatch(MatchHit hit, int position)
        {
            if (hit == null || displayed == null) return;
            if (hit.Ordinal < 0 || hit.Ordinal >= displayed.Columns.Count) return;

            DataColumn dataColumn = displayed.Columns[hit.Ordinal];
            DataGridColumn column = dataGrid.Columns.FirstOrDefault(item =>
                string.Equals(item.SortMemberPath, dataColumn.ColumnName, StringComparison.Ordinal));
            if (column == null)
            {
                viewNote = LocalizationManager.Format("Column {0} is hidden; tick it in the field list to see it.",
                    GridAnalytics.DisplayName(dataColumn));
                UpdateStatus();
                return;
            }

            DataRowView rowView = FindRowView(hit.Row);
            if (rowView == null)
            {
                status.Text = T("That row is still loading; try again.");
                return;
            }

            try
            {
                var cell = new DataGridCellInfo(rowView, column);
                dataGrid.SelectedCells.Clear();
                dataGrid.CurrentCell = cell;
                dataGrid.SelectedCells.Add(cell);
                dataGrid.ScrollIntoView(rowView, column);
                dataGrid.Focus();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not jump to the matching cell.");
            }

            matchCounter.Text = string.Format(CultureInfo.CurrentCulture, "{0} / {1}", position, matchHits.Count);
            status.Text = LocalizationManager.Format("Match {0} of {1}: column {2}.",
                position, matchHits.Count, GridAnalytics.DisplayName(dataColumn));
        }

        private DataRowView FindRowView(DataRow row)
        {
            if (row == null) return null;

            foreach (object item in dataGrid.Items)
            {
                if (item is DataRowView view && ReferenceEquals(view.Row, row)) return view;
            }

            return null;
        }

        private string BuildSortExpression()
        {
            var parts = new List<string>();
            foreach (SortKey key in sorts)
            {
                if (key == null || string.IsNullOrEmpty(key.Column)) continue;
                parts.Add("[" + key.Column.Replace("]", "]]") + "] " + (key.Descending ? "DESC" : "ASC"));
            }

            return string.Join(", ", parts);
        }

        private void UpdateSortArrows()
        {
            foreach (DataGridColumn column in dataGrid.Columns)
            {
                SortKey key = sorts.FirstOrDefault(item => item.Column == Convert.ToString(column.SortMemberPath));
                column.SortDirection = key == null ? null : (System.ComponentModel.ListSortDirection?)
                    (key.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending);
            }
        }

        private void BuildDataColumns()
        {
            if (displayed != null)
            {
                // Sorting and visibility changes keep the same columns. Rebuilding 271 column
                // objects on every view change was the biggest cost.
                string signature = ColumnSignature(displayed);
                if (signature == columnsSignature && dataGrid.Columns.Count > 0)
                {
                    SyncFreezeSelector();
                    return;
                }
                columnsSignature = signature;
            }

            // Clearing the columns while a frozen count is set can throw, so release it first.
            dataGrid.FrozenColumnCount = 0;
            dataGrid.Columns.Clear();
            if (displayed == null)
            {
                frozenColumns = 0;
                SyncFreezeSelector();
                return;
            }

            for (int index = 0; index < displayed.Columns.Count; index++)
            {
                if (hiddenOrdinals.Contains(index)) continue;

                DataColumn column = displayed.Columns[index];
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                text.SetValue(TextBlock.PaddingProperty, new Thickness(4, 0, 4, 0));
                text.SetBinding(TextBlock.TextProperty, new Binding(column.ColumnName) { Converter = cellText });
                text.SetBinding(TextBlock.ToolTipProperty, new Binding(column.ColumnName) { Converter = cellText });
                text.SetBinding(TextBlock.FontStyleProperty, new Binding(column.ColumnName) { Converter = cellText, ConverterParameter = "style" });
                text.SetBinding(TextBlock.ForegroundProperty, new Binding(column.ColumnName) { Converter = cellText, ConverterParameter = "brush" });
                // Tint the cells whose text is the value the filter found.  The binding source is
                // the row itself, so the converter only has to look up this column's ordinal.
                text.SetBinding(TextBlock.BackgroundProperty, new Binding(".")
                {
                    Converter = cellMatch,
                    ConverterParameter = index
                });

                var gridColumn = new DataGridTemplateColumn
                {
                    Header = BuildColumnHeader(column, index),
                    CellTemplate = new DataTemplate { VisualTree = text },
                    SortMemberPath = column.ColumnName,
                    CanUserResize = true,
                    Width = ColumnWidth(column)
                };

                dataGrid.Columns.Add(gridColumn);
            }

            dataGrid.FrozenColumnCount = Math.Min(frozenColumns, dataGrid.Columns.Count);
            frozenColumns = dataGrid.FrozenColumnCount;
            SyncFreezeSelector();
        }

        private string ColumnSignature(DataTable table)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(table.Columns.Count).Append('|');
            foreach (DataColumn column in table.Columns) builder.Append(column.ColumnName).Append(',');
            builder.Append('|');
            foreach (int ordinal in hiddenOrdinals.OrderBy(item => item)) builder.Append(ordinal).Append(',');
            return builder.ToString();
        }

        private static DataGridLength ColumnWidth(DataColumn column)
        {
            object stored;
            if (column.ExtendedProperties.Contains("columnWidthInPixels") &&
                (stored = column.ExtendedProperties["columnWidthInPixels"]) != null &&
                double.TryParse(Convert.ToString(stored, CultureInfo.InvariantCulture), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double width) && width >= 40 && width <= 900)
            {
                return new DataGridLength(width);
            }

            return new DataGridLength(150);
        }

        private object BuildColumnHeader(DataColumn column, int ordinal)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(new TextBlock
            {
                Text = GridAnalytics.DisplayName(column),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = GridAnalytics.DisplayName(column)
            });
            panel.Children.Add(new TextBlock
            {
                Text = GridAnalytics.FriendlyTypeName(column),
                FontSize = 10,
                Opacity = 0.65
            });

            panel.ContextMenuOpening += (_, __) =>
            {
                // Built on demand: 271 menus with 9 items each was pure startup cost.
                if (panel.ContextMenu == null) panel.ContextMenu = BuildHeaderMenu(column, ordinal);
            };
            return panel;
        }

        private ContextMenu BuildHeaderMenu(DataColumn column, int ordinal)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Sort ascending", () => SetSort(column.ColumnName, false)));
            menu.Items.Add(MenuItem("Sort descending", () => SetSort(column.ColumnName, true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Hide column", () => { hiddenOrdinals.Add(ordinal); ApplyView(); }));
            menu.Items.Add(MenuItem("Copy column name", () => Clipboard.SetText(GridAnalytics.DisplayName(column))));
            menu.Items.Add(MenuItem("Freeze up to this column", () =>
            {
                int visible = dataGrid.Columns.IndexOf(dataGrid.Columns.FirstOrDefault(item => item.SortMemberPath == column.ColumnName));
                SetFrozenColumns(visible + 1, true);
            }));
            menu.Items.Add(MenuItem("Unfreeze columns", () => SetFrozenColumns(0, true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Top values", () => ShowTopValues(analytics?.Columns.FirstOrDefault(item => item.Name == GridAnalytics.DisplayName(column)))));
            return menu;
        }

        private static MenuItem MenuItem(string text, Action action)
        {
            var item = new MenuItem { Header = T(text) };
            item.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "Results panel command failed."); }
            };
            return item;
        }

        private void SetSort(string columnName, bool descending)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            SortKey existing = sorts.FirstOrDefault(item => item.Column == columnName);
            if (existing != null)
            {
                if (shift || sorts.Count == 1)
                {
                    existing.Descending = descending;
                }
                else
                {
                    sorts.Remove(existing);
                }
            }
            else
            {
                if (!shift) sorts.Clear();
                sorts.Add(new SortKey { Column = columnName, Descending = descending });
            }

            ApplyView();
        }

        /// <summary>
        /// A click on a column header selects the column, it does not sort: on a wide result set the
        /// sort rebound the grid (and dropped the selection) on every stray click.  Sorting stays on
        /// the header context menu.
        /// </summary>
        private void OnSorting(object sender, DataGridSortingEventArgs e)
        {
            // Also suppresses the built-in DataGrid sort, which would rebind the view.
            e.Handled = true;
        }

        private void ShowColumnMenu()
        {
            if (displayed == null) return;

            var menu = new ContextMenu();
            for (int index = 0; index < displayed.Columns.Count; index++)
            {
                int ordinal = index;
                DataColumn column = displayed.Columns[index];
                var item = new MenuItem
                {
                    Header = GridAnalytics.DisplayName(column),
                    IsCheckable = true,
                    IsChecked = !hiddenOrdinals.Contains(index)
                };
                item.Click += (_, __) =>
                {
                    if (hiddenOrdinals.Contains(ordinal)) hiddenOrdinals.Remove(ordinal);
                    else hiddenOrdinals.Add(ordinal);
                    ApplyView();
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Show all columns", () => { hiddenOrdinals.Clear(); ApplyView(); }));
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Jumps to the next column whose name or type contains the search text, wrapping around at
        /// the end.  A hidden match is revealed first, so searching never hits a column that cannot
        /// be seen.  The first cell of the column is selected and the statistics table follows it.
        /// </summary>
        private void FindNextColumn()
        {
            if (displayed == null)
            {
                status.Text = T("No result set is available.");
                return;
            }

            string needle = (columnSearch.Text ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                status.Text = T("Type part of a column name.");
                columnSearch.Focus();
                return;
            }

            if (!string.Equals(needle, lastColumnNeedle, StringComparison.Ordinal))
            {
                lastColumnNeedle = needle;
                lastFoundOrdinal = -1;
            }

            int start = lastFoundOrdinal < 0 ? 0 : lastFoundOrdinal + 1;
            int found = GridAnalytics.FindColumn(displayed.Columns, needle, start);
            if (found < 0)
            {
                status.Text = LocalizationManager.Format("No column matches '{0}'.", needle);
                lastFoundOrdinal = -1;
                return;
            }

            lastFoundOrdinal = found;
            DataColumn column = displayed.Columns[found];
            string name = GridAnalytics.DisplayName(column);
            string field = column.ColumnName;

            if (hiddenOrdinals.Contains(found))
            {
                hiddenOrdinals.Remove(found);
                ApplyView();
            }

            FocusColumn(field, found + 1);
            status.Text = LocalizationManager.Format("Column '{0}'  ({1} of {2})", name, found + 1, displayed.Columns.Count);
        }

        private void OnColumnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                FindNextColumn();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                columnSearch.Text = string.Empty;
                e.Handled = true;
            }
        }

        /// <summary>Scrolls the grid to a column and marks its first cell, without stealing focus.</summary>
        private void FocusColumn(string columnName, int ordinal)
        {
            DataGridColumn gridColumn = dataGrid.Columns.FirstOrDefault(item =>
                string.Equals(item.SortMemberPath, columnName, StringComparison.Ordinal));
            if (gridColumn == null) return;

            if (dataGrid.Items.Count > 0)
            {
                object item = dataGrid.Items[0];
                var cell = new DataGridCellInfo(item, gridColumn);
                try
                {
                    dataGrid.SelectedCells.Clear();
                    dataGrid.CurrentCell = cell;
                    dataGrid.SelectedCells.Add(cell);
                    dataGrid.ScrollIntoView(item, gridColumn);
                    dataGrid.UpdateLayout();
                    dataGrid.ScrollIntoView(item, gridColumn);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Debug(ex, "Could not scroll to the found column.");
                }
            }

            SelectStatisticsRow(ordinal);
        }

        private void ClearFilters()
        {
            filterValue.Text = string.Empty;
            if (filterOperators.Items.Count > 0) filterOperators.SelectedIndex = 0;
            SetAllFilterColumns(true);
            filterColumnPopup.IsOpen = false;
            applyTimer.Stop();
            ApplyView();
        }

        /// <summary>
        /// Typing in the find box rescans the ticked columns; waiting for a short pause keeps the
        /// keystrokes responsive on a wide result set instead of rescanning for every character.
        /// </summary>
        private void ScheduleApplyView()
        {
            applyTimer.Stop();
            applyTimer.Start();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                columnSearch.Focus();
                columnSearch.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                filterValue.Focus();
                filterValue.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F3)
            {
                // F3 walks the found cells when a search is active; otherwise it keeps its old
                // job of cycling through matching column names.
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && matchHits.Count > 0) GoToPreviousMatch();
                else if (matchHits.Count > 0) GoToNextMatch();
                else FindNextColumn();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (columnSearch.IsKeyboardFocusWithin) columnSearch.Text = string.Empty;
                else ClearFilters();
                e.Handled = true;
            }
        }

        private void OnCellDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (dataGrid.SelectedCells.Count == 0) return;
                DataGridCellInfo cell = dataGrid.SelectedCells[0];
                if (cell.Item == null) return;

                DataRowView view = cell.Item as DataRowView;
                if (view == null) return;

                string columnName = Convert.ToString(cell.Column.SortMemberPath);
                DataColumn column = displayed.Columns.Cast<DataColumn>().FirstOrDefault(item => item.ColumnName == columnName);
                if (column == null) return;

                object value = view.Row[column];
                CellValueWindow.Show(this, GridAnalytics.DisplayName(column), GridAnalytics.FriendlyTypeName(column), value);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not show the cell value.");
            }
        }

        private void OnStatsDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ShowTopValues(statsGrid.SelectedItem as GridColumnInsight);
        }

        private void ShowTopValues(GridColumnInsight insight)
        {
            if (insight == null) return;
            if (insight.TopValues.Count == 0)
            {
                status.Text = LocalizationManager.Format("Column {0} has no values to list.", insight.Name);
                return;
            }

            var builder = new StringBuilder();
            int total = 0;
            foreach (KeyValuePair<string, int> pair in insight.TopValues) total += pair.Value;

            builder.AppendLine(LocalizationManager.Format("{0} distinct value(s), {1} non-empty cell(s) sampled",
                insight.Distinct, insight.NonNull));
            builder.AppendLine();
            foreach (KeyValuePair<string, int> pair in insight.TopValues)
            {
                double share = total == 0 ? 0 : pair.Value * 100.0 / total;
                builder.AppendLine(pair.Key + "\t" + pair.Value.ToString(CultureInfo.CurrentCulture) +
                                   "\t" + share.ToString("0.0", CultureInfo.CurrentCulture) + "%");
            }

            TextReportWindow.Show(this, LocalizationManager.Format("Top values: {0}", insight.Name), builder.ToString());
        }

        /// <summary>Summarises the current cell selection: counts, nulls, distinct, length and numeric totals.</summary>
        private void UpdateSelectionStatistics()
        {
            try
            {
                int cells = dataGrid.SelectedCells.Count;
                if (cells == 0)
                {
                    selectionSummary.Text = T("Select cells to see their statistics.");
                    return;
                }

                var rows = new HashSet<DataRowView>();
                var columns = new HashSet<string>(StringComparer.Ordinal);
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                int nulls = 0, nonNull = 0, maxLength = 0, numeric = 0;
                decimal sum = 0;
                decimal? min = null, max = null;

                // A whole-column selection can hold tens of thousands of cells; resolving each
                // column by name once keeps the summary linear instead of searching per cell.
                Dictionary<string, DataColumn> columnsByName = null;
                if (displayed != null)
                {
                    columnsByName = new Dictionary<string, DataColumn>(displayed.Columns.Count, StringComparer.Ordinal);
                    foreach (DataColumn item in displayed.Columns) columnsByName[item.ColumnName] = item;
                }

                foreach (DataGridCellInfo cell in dataGrid.SelectedCells)
                {
                    DataRowView view = cell.Item as DataRowView;
                    if (view == null) continue;

                    string name = Convert.ToString(cell.Column.SortMemberPath);
                    DataColumn column = null;
                    if (columnsByName != null && !string.IsNullOrEmpty(name)) columnsByName.TryGetValue(name, out column);
                    if (column == null) continue;

                    rows.Add(view);
                    columns.Add(GridAnalytics.DisplayName(column));

                    object value = view.Row[column];
                    if (value == null || value == DBNull.Value)
                    {
                        nulls++;
                        continue;
                    }

                    nonNull++;
                    string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                    distinct.Add(text);
                    if (text.Length > maxLength) maxLength = text.Length;

                    if (TryNumber(value, text, out decimal number))
                    {
                        numeric++;
                        sum += number;
                        min = min.HasValue && min.Value <= number ? min : number;
                        max = max.HasValue && max.Value >= number ? max : number;
                    }
                }

                // Only the scope belongs here: non-null / empty / distinct / longest are repeated by
                // the column line just below, so keeping them in both bars was pure duplication.
                selectionSummary.Text = LocalizationManager.Format(
                    "Selection: {0} cell(s), {1} row(s), {2} column(s)",
                    cells, rows.Count, columns.Count);

                // One cell: describe that column over the whole (displayed) table; several cells:
                // describe the selection as a whole.  A full column scan is capped so a million-row
                // grid can never freeze the window.
                GridColumnInsight insight = cells == 1 ? DescribeSelectedColumn(columns, nonNull, nulls, distinct.Count, maxLength, numeric, sum, min, max)
                                                       : BuildSelectionInsight(columns, nonNull, nulls, distinct.Count, maxLength, numeric, sum, min, max);
                ShowSingleInsight(insight);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not summarize the selected cells.");
            }
        }

        private GridColumnInsight BuildSelectionInsight(ICollection<string> columns, int nonNull, int nulls, int distinct,
            int maxLength, int numeric, decimal sum, decimal? min, decimal? max)
        {
            string name = columns.Count == 1 ? columns.First() : T("Selection");
            return new GridColumnInsight
            {
                Ordinal = 0,
                Name = name,
                DataType = LocalizationManager.Format("{0} column(s)", columns.Count),
                NonNull = nonNull,
                NullCount = nulls,
                Distinct = distinct,
                MaxLength = maxLength,
                IsNumeric = numeric > 0,
                Sum = numeric > 0 ? sum.ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Average = numeric > 0 ? (sum / numeric).ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Minimum = min?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-",
                Maximum = max?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-"
            };
        }

        /// <summary>
        /// A single selected cell describes its whole column.  Only that one column is read, and the
        /// scan stops at a fixed row cap so a million-row grid can never block the window.
        /// </summary>
        private GridColumnInsight DescribeSelectedColumn(ICollection<string> columns, int nonNull, int nulls, int distinct,
            int maxLength, int numeric, decimal sum, decimal? min, decimal? max)
        {
            string name = columns.Count == 1 ? columns.First() : null;
            DataTable source = displayed;
            if (string.IsNullOrEmpty(name) || source == null || !source.Columns.Contains(name))
                return BuildSelectionInsight(columns, nonNull, nulls, distinct, maxLength, numeric, sum, min, max);

            int columnIndex = source.Columns[name].Ordinal;
            const int rowCap = 100000;
            int limit = Math.Min(source.Rows.Count, rowCap);
            var uniques = new HashSet<string>(StringComparer.Ordinal);
            int seenNull = 0, seenNonNull = 0, seenMaxLength = 0, seenNumeric = 0;
            decimal total = 0;
            decimal? low = null, high = null;

            for (int row = 0; row < limit; row++)
            {
                object value = source.Rows[row][columnIndex];
                if (value == null || value == DBNull.Value) { seenNull++; continue; }

                seenNonNull++;
                string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                uniques.Add(text);
                if (text.Length > seenMaxLength) seenMaxLength = text.Length;

                if (TryNumber(value, text, out decimal number))
                {
                    seenNumeric++;
                    total += number;
                    low = low.HasValue && low.Value <= number ? low : number;
                    high = high.HasValue && high.Value >= number ? high : number;
                }
            }

            return new GridColumnInsight
            {
                Ordinal = columnIndex,
                Name = name,
                DataType = source.Columns[columnIndex].DataType.Name,
                NonNull = seenNonNull,
                NullCount = seenNull,
                Distinct = uniques.Count,
                MaxLength = seenMaxLength,
                IsNumeric = seenNumeric > 0,
                Sum = seenNumeric > 0 ? total.ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Average = seenNumeric > 0 ? (total / seenNumeric).ToString("0.####", CultureInfo.CurrentCulture) : "-",
                Minimum = low?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-",
                Maximum = high?.ToString("0.####", CultureInfo.CurrentCulture) ?? "-"
            };
        }

        private void ShowSingleInsight(GridColumnInsight insight)
        {
            if (insight == null) return;

            var result = new GridAnalyticsResult
            {
                RowCount = analytics == null ? 0 : analytics.RowCount,
                ColumnCount = analytics == null ? 0 : analytics.ColumnCount,
                Columns = new List<GridColumnInsight> { insight }
            };
            analytics = result;
            statsText.Text = FormatInsight(insight);
        }

        private FrameworkElement CreateFieldPanel()
        {
            var panel = new DockPanel { LastChildFill = true };

            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var collapse = new Button
            {
                Content = "\u25C0",
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                FontSize = 9,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = LocalizationManager.T("Collapse the field list")
            };
            collapse.Click += (_, __) => SetFieldPanelCollapsed(true);
            DockPanel.SetDock(collapse, Dock.Right);
            header.Children.Add(collapse);
            header.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Fields"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);

            fieldFilter.TextChanged += (_, __) => RebuildFieldList();
            DockPanel.SetDock(fieldFilter, Dock.Top);
            panel.Children.Add(fieldFilter);

            // Bulk visibility: ticking 271 checkboxes one by one was the only way to bring a wide
            // result set back.  The buttons act on the rows the field filter currently shows.
            var fieldActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var selectAllFields = CreateSmallButton("Select all", () => SetAllFieldsVisible(true));
            selectAllFields.ToolTip = LocalizationManager.T("Show every field in the list.");
            var deselectAllFields = CreateSmallButton("Deselect all", () => SetAllFieldsVisible(false));
            deselectAllFields.ToolTip = LocalizationManager.T("Hide every field in the list.");
            fieldActions.Children.Add(selectAllFields);
            fieldActions.Children.Add(deselectAllFields);
            DockPanel.SetDock(fieldActions, Dock.Top);
            panel.Children.Add(fieldActions);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0)
            };
            scroll.Content = fieldList;
            panel.Children.Add(scroll);
            return panel;
        }

        /// <summary>Drag handle between the field list and the results area.</summary>
        private GridSplitter CreateFieldSplitter()
        {
            var style = new Style(typeof(GridSplitter));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(18, 0, 0, 0))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(198, 215, 236))));
            style.Triggers.Add(hover);

            var splitter = new GridSplitter
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Cursor = Cursors.SizeWE,
                Style = style,
                ToolTip = LocalizationManager.T("Drag to resize the field list")
            };
            splitter.DragCompleted += (_, __) => RememberFieldWidth();
            return splitter;
        }

        /// <summary>
        /// The narrow strip left behind when the field list is collapsed.  Clicking it brings the
        /// list back, so collapsing never strands the user without a way to restore the panel.
        /// </summary>
        private FrameworkElement CreateCollapsedFieldStrip()
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = "\u25B6",
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 8)
            });
            content.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Fields"),
                LayoutTransform = new RotateTransform(90),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Opacity = 0.75
            });

            var strip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(14, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationManager.T("Expand the field list"),
                Child = content
            };
            strip.MouseLeftButtonUp += (_, __) => SetFieldPanelCollapsed(false);
            return strip;
        }

        /// <summary>
        /// Shows or hides every field the field filter currently lists.  One ApplyView call covers
        /// the whole batch: 271 individual visibility changes would rebuild the grid 271 times.
        /// </summary>
        private void SetAllFieldsVisible(bool visible)
        {
            if (allFields == null || allFields.Count == 0) return;

            string needle = (fieldFilter.Text ?? string.Empty).Trim();
            var affected = new List<int>();
            foreach (GridColumnInsight field in allFields)
            {
                if (needle.Length > 0 && !MatchesField(field, needle)) continue;
                affected.Add(field.Ordinal);
            }

            if (affected.Count == 0) return;

            string message;
            if (visible)
            {
                foreach (int ordinal in affected) hiddenOrdinals.Remove(ordinal);
                message = LocalizationManager.Format("Showing {0} field(s).", affected.Count);
            }
            else
            {
                foreach (int ordinal in affected)
                    if (!hiddenOrdinals.Contains(ordinal)) hiddenOrdinals.Add(ordinal);
                message = LocalizationManager.Format("Hiding {0} field(s).", affected.Count);
            }

            ApplyView();
            RebuildFieldList();
            status.Text = message;
        }

        private void SetFieldPanelCollapsed(bool collapsed)
        {
            if (fieldPanel == null || fieldPanelCollapsed == collapsed) return;
            if (collapsed) RememberFieldWidth();
            fieldPanelCollapsed = collapsed;
            ApplyFieldPanelState();
        }

        /// <summary>Keeps the expanded width across a collapse/expand round trip.</summary>
        private void RememberFieldWidth()
        {
            if (fieldColumn == null || !fieldColumn.Width.IsAbsolute) return;
            double width = fieldColumn.Width.Value;
            if (width >= MinimumFieldWidth) expandedFieldWidth = width;
        }

        private void ApplyFieldPanelState()
        {
            if (fieldPanel == null || collapsedFieldStrip == null || fieldColumn == null) return;

            bool collapsed = fieldPanelCollapsed;
            fieldPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            collapsedFieldStrip.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            fieldSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            fieldColumn.MinWidth = collapsed ? 0 : MinimumFieldWidth;
            fieldColumn.Width = new GridLength(collapsed ? CollapsedFieldWidth : expandedFieldWidth);
        }

        /// <summary>
        /// One row per column: the checkbox drives its visibility in the grid, the name jumps to it.
        /// Rebuilt when the column set or the filter changes, never on every grid keystroke.
        /// </summary>
        private void RebuildFieldList()
        {
            fieldGeneration++;
            fieldQueue = null;
            fieldList.Children.Clear();
            fieldRows.Clear();
            activeFieldRow = null;
            List<GridColumnInsight> fields = allFields;
            if (fields == null) return;

            string needle = (fieldFilter.Text ?? string.Empty).Trim();
            var visible = new List<GridColumnInsight>();
            foreach (GridColumnInsight column in fields)
            {
                if (needle.Length > 0 && !MatchesField(column, needle)) continue;
                visible.Add(column);
            }

            // 271 controls cannot be built in one go without a visible freeze; 40 per dispatcher
            // pass keeps the window responsive.
            fieldQueue = visible;
            fieldIndex = 0;
            LoadFieldBatch(fieldGeneration);
        }

        private void LoadFieldBatch(int generation)
        {
            if (generation != fieldGeneration || fieldQueue == null) return;

            int end = Math.Min(fieldQueue.Count, fieldIndex + 40);
            for (; fieldIndex < end; fieldIndex++)
                fieldList.Children.Add(CreateFieldRow(fieldQueue[fieldIndex]));

            if (fieldIndex < fieldQueue.Count)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => LoadFieldBatch(generation)));
            else
                fieldQueue = null;
        }

        private static bool MatchesField(GridColumnInsight column, string needle)
        {
            if (column == null) return false;
            return (column.Name ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || (column.DataType ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private UIElement CreateFieldRow(GridColumnInsight column)
        {
            int ordinal = column.Ordinal;
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };

            var check = new CheckBox
            {
                IsChecked = !hiddenOrdinals.Contains(ordinal),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = LocalizationManager.T("Show or hide this column")
            };
            check.Checked += (_, __) => { hiddenOrdinals.Remove(ordinal); ApplyView(); };
            check.Unchecked += (_, __) =>
            {
                if (!hiddenOrdinals.Contains(ordinal)) hiddenOrdinals.Add(ordinal);
                ApplyView();
            };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);

            var name = new TextBlock
            {
                Text = column.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = LocalizationManager.Format("{0} ({1}) - click to jump to this column", column.Name, column.DataType)
            };
            name.MouseLeftButtonUp += (_, __) => { };
            row.Children.Add(name);

            var host = new Border
            {
                Child = row,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand
            };
            host.MouseEnter += (_, __) =>
            {
                if (!ReferenceEquals(activeFieldRow, host)) host.Background = FieldHoverBrush;
            };
            host.MouseLeave += (_, __) =>
            {
                if (!ReferenceEquals(activeFieldRow, host)) host.Background = Brushes.Transparent;
            };
            host.MouseLeftButtonUp += (_, e) =>
            {
                if (IsInsideCheckBox(e.OriginalSource as DependencyObject)) return;
                // Jump first: it clears the grid selection (and with it the previous tint), then the
                // field row is marked active so the tint survives.
                FocusGridColumn(column);
                SelectFieldRow(host);
            };
            fieldRows[ordinal] = host;
            return host;
        }

        /// <summary>Keeps one field row tinted as the active selection.</summary>
        private void SelectFieldRow(Border row)
        {
            if (activeFieldRow != null && !ReferenceEquals(activeFieldRow, row))
                activeFieldRow.Background = Brushes.Transparent;
            activeFieldRow = row;
            row.Background = FieldActiveBrush;
        }

        private void ClearActiveFieldRow()
        {
            if (activeFieldRow == null) return;
            activeFieldRow.Background = Brushes.Transparent;
            activeFieldRow = null;
        }

        /// <summary>Marks the field row of a column, if that row has been built yet.</summary>
        private void SelectFieldRow(GridColumnInsight insight)
        {
            if (insight != null && fieldRows.TryGetValue(insight.Ordinal, out Border row))
            {
                SelectFieldRow(row);
                return;
            }

            ClearActiveFieldRow();
        }

        private GridColumnInsight FieldForColumn(string columnName)
        {
            if (allFields == null || displayed == null || string.IsNullOrEmpty(columnName)) return null;
            if (!displayed.Columns.Contains(columnName)) return null;

            int index = displayed.Columns.IndexOf(columnName);
            return allFields.FirstOrDefault(item => item.Ordinal == index);
        }

        /// <summary>Marks the field list on a column clicked in the grid.</summary>
        private void SelectFieldRowForColumn(string columnName)
            => SelectFieldRow(FieldForColumn(columnName));

        /// <summary>
        /// Keeps the field list on the column holding the caret.  Called after a cell selection
        /// change, which clears the previous tint, so exactly one row is ever highlighted.
        /// </summary>
        private void SyncFieldRowFromGrid()
        {
            DataGridColumn column = dataGrid.CurrentCell.Column;
            string name = column == null ? null : Convert.ToString(column.SortMemberPath);
            if (string.IsNullOrEmpty(name))
            {
                ClearActiveFieldRow();
                return;
            }

            SelectFieldRowForColumn(name);
        }

        /// <summary>
        /// A click on a column header selects every cell of that column and points the field list and
        /// the statistics grid at the same column.
        /// </summary>
        private void OnGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Resizing a column also ends with a mouse-up over its header; that is not a click
                // on the column and must not touch the selection.
                if (IsInsideHeaderGripper(e.OriginalSource as DependencyObject)) return;

                DataGridColumnHeader header = FindColumnHeader(e.OriginalSource as DependencyObject);
                DataGridColumn column = header == null ? null : header.Column;
                string name = column == null ? null : Convert.ToString(column.SortMemberPath);
                if (string.IsNullOrEmpty(name)) return;

                // The header click no longer sorts, so nothing rebinds the view between the click
                // and this selection; select straight away instead of waiting for a dispatcher pass.
                SelectWholeColumn(name);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not select the clicked column.");
            }
        }

        /// <summary>
        /// Selects every cell of one column.  Each cell is added individually, which costs roughly
        /// 70 ms per 1000 cells, so a very tall grid stops at <see cref="ColumnSelectionCap"/> and the
        /// status line says how many cells were left out instead of freezing the window.
        /// </summary>
        private void SelectWholeColumn(string columnName)
        {
            if (displayed == null || string.IsNullOrEmpty(columnName)) return;

            DataGridColumn column = dataGrid.Columns.FirstOrDefault(item =>
                string.Equals(item.SortMemberPath, columnName, StringComparison.Ordinal));
            if (column == null)
            {
                viewNote = LocalizationManager.Format("Column {0} is hidden; tick it in the field list to see it.",
                    GridAnalytics.DisplayName(displayed.Columns[columnName]));
                UpdateStatus();
                return;
            }

            ScrollViewer scroll = FindScrollViewer(dataGrid);
            double horizontal = scroll == null ? 0 : scroll.HorizontalOffset;
            double vertical = scroll == null ? 0 : scroll.VerticalOffset;

            int total = dataGrid.Items.Count;
            if (total == 0)
            {
                // Nothing to select yet: still point the other panels at the column.
                ClearColumnHighlight();
                SelectFieldRowForColumn(columnName);
                SelectStatisticsRowForColumn(columnName);
                return;
            }

            int count = Math.Min(total, ColumnSelectionCap);

            ClearColumnHighlight();
            bulkSelecting = true;
            try
            {
                dataGrid.SelectedCells.Clear();
                for (int index = 0; index < count; index++)
                    dataGrid.SelectedCells.Add(new DataGridCellInfo(dataGrid.Items[index], column));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not select every cell of the column.");
            }
            finally
            {
                bulkSelecting = false;
            }

            // Adding cells can push the viewport; put it back where the user was.
            if (scroll != null)
            {
                scroll.ScrollToHorizontalOffset(horizontal);
                scroll.ScrollToVerticalOffset(vertical);
            }

            UpdateSelectionStatistics();
            SelectFieldRowForColumn(columnName);
            SelectStatisticsRowForColumn(columnName);

            string display = GridAnalytics.DisplayName(displayed.Columns[columnName]);
            status.Text = count < total
                ? LocalizationManager.Format("Selected the first {0} of {1} cells in column {2}.", count, total, display)
                : LocalizationManager.Format("Selected all {0} cells of column {1}.", total, display);
        }

        private static ScrollViewer FindScrollViewer(DependencyObject node)
        {
            if (node == null) return null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                DependencyObject current = queue.Dequeue();
                if (current is ScrollViewer viewer) return viewer;

                int children = VisualTreeHelper.GetChildrenCount(current);
                for (int index = 0; index < children; index++)
                    queue.Enqueue(VisualTreeHelper.GetChild(current, index));
            }

            return null;
        }

        private static DataGridColumnHeader FindColumnHeader(DependencyObject node)
        {
            for (int depth = 0; node != null && depth < 12; depth++)
            {
                if (node is DataGridColumnHeader header) return header;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }

        private static bool IsInsideHeaderGripper(DependencyObject node)
        {
            for (int depth = 0; node != null && depth < 8; depth++)
            {
                if (node is Thumb thumb && thumb.Name != null &&
                    thumb.Name.EndsWith("HeaderGripper", StringComparison.Ordinal))
                {
                    return true;
                }

                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        /// <summary>Switches between the "NULL" marker and a blank cell without rebuilding columns.</summary>
        private void OnShowNullChanged()
        {
            bool visible = showNull.IsChecked == true;
            if (cellText.ShowNull == visible) return;

            cellText.ShowNull = visible;
            try
            {
                // The binding reads ShowNull on every evaluation, so re-realising the rows applies
                // the new text to the visible cells without touching column widths or the freeze.
                dataGrid.Items.Refresh();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "Could not refresh the cells after switching NULL display.");
            }

            status.Text = visible
                ? T("Empty cells show NULL.")
                : T("Empty cells are left blank.");
        }

        private static bool IsInsideCheckBox(DependencyObject node)
        {
            for (int depth = 0; node != null && depth < 6; depth++)
            {
                if (node is CheckBox) return true;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        /// <summary>One line per selected scope, shown as text instead of a second grid.</summary>
        private static string FormatInsight(GridColumnInsight insight)
        {
            if (insight == null) return string.Empty;
            return LocalizationManager.Format(
                "{0}  ({1})   non-null {2}   empty {3} ({4})   distinct {5}   longest {6}   sum {7}   average {8}   min {9}   max {10}",
                insight.Name, insight.DataType, insight.NonNull, insight.NullCount, insight.NullPercentText,
                insight.Distinct, insight.MaxLength, insight.Sum, insight.Average, insight.Minimum, insight.Maximum);
        }

        private static void ApplyGridLook(DataGrid grid)
        {
            // Shared look for both grids: roomier rows, a bold header and hairline separators.
            // Kept in code because this window builds its own layout (no XAML).
            grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));
            grid.BorderThickness = new Thickness(0, 1, 0, 0);
            grid.BorderBrush = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));

            var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            header.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(40, 0, 0, 0))));
            grid.ColumnHeaderStyle = header;

            var cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            cell.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            // The default system highlight (white on saturated blue) plus a dotted focus rectangle
            // looked heavy on a 271-column grid; use a soft tint and no focus adorners.
            cell.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            var selectedCell = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selectedCell.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(223, 235, 250))));
            selectedCell.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(20, 28, 40))));
            cell.Triggers.Add(selectedCell);
            grid.CellStyle = cell;
        }

        private static bool TryNumber(object value, string text, out decimal number)        {
            number = 0;
            if (value is bool) return false;
            if (IsNumericType(value.GetType()))
            {
                try { number = Convert.ToDecimal(value, CultureInfo.CurrentCulture); return true; }
                catch (Exception) { return false; }
            }

            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
        }

        private void CopyStatistics()
        {
            if (analytics == null) return;
            Clipboard.SetText(GridAnalytics.ToText(analytics));
            status.Text = T("Statistics copied to the clipboard.");
        }

        /// <summary>
        /// Resizes every visible column to its content: the header and a sample of the rows are
        /// measured with the grid's own font, then clamped between 64 and 320 px so one long
        /// varchar or XML blob cannot push every other column off the screen.
        /// </summary>
        private void AutoFitColumns()
        {
            if (displayed == null || dataGrid.Columns.Count == 0)
            {
                status.Text = T("Run a query first; there is no result set to fit.");
                return;
            }

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(dataGrid.FontFamily, dataGrid.FontStyle, dataGrid.FontWeight, dataGrid.FontStretch);
            var headerTypeface = new Typeface(dataGrid.FontFamily, dataGrid.FontStyle, FontWeights.SemiBold, dataGrid.FontStretch);
            double fontSize = dataGrid.FontSize > 0 ? dataGrid.FontSize : 12;
            int sampleRows = Math.Min(AutoFitSampleRows, displayed.Rows.Count);

            foreach (DataGridColumn gridColumn in dataGrid.Columns)
            {
                string name = Convert.ToString(gridColumn.SortMemberPath);
                if (string.IsNullOrEmpty(name) || !displayed.Columns.Contains(name)) continue;

                DataColumn column = displayed.Columns[name];
                string widest = string.Empty;
                for (int row = 0; row < sampleRows; row++)
                {
                    object value = displayed.Rows[row][column];
                    if (value == null || value == DBNull.Value) continue;

                    string text = ReadableCellText(Convert.ToString(value, CultureInfo.CurrentCulture));
                    if (text.Length > widest.Length) widest = text;
                }

                double width = MeasureText(widest, typeface, fontSize, pixelsPerDip);
                double header = MeasureText(GridAnalytics.DisplayName(column), headerTypeface, fontSize, pixelsPerDip);
                width = Math.Max(width, header) + 22; // cell padding plus the grid lines

                gridColumn.Width = new DataGridLength(Math.Round(
                    Math.Max(MinimumAutoFitWidth, Math.Min(MaximumAutoFitWidth, width))));
            }

            status.Text = LocalizationManager.Format(
                "Column widths fitted to the content ({0}-{1} px).",
                MinimumAutoFitWidth.ToString("0", CultureInfo.CurrentCulture),
                MaximumAutoFitWidth.ToString("0", CultureInfo.CurrentCulture));
        }

        /// <summary>The cell text as one measurable line, bounded so a huge value stays cheap.</summary>
        private static string ReadableCellText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string text = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return text.Length <= 200 ? text : text.Substring(0, 200);
        }

        private static double MeasureText(string text, Typeface typeface, double fontSize, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            try
            {
                var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, Brushes.Black, pixelsPerDip);
                return formatted.WidthIncludingTrailingWhitespace;
            }
            catch (Exception)
            {
                // Fall back to a character-count estimate rather than failing the whole action.
                return text.Length * fontSize * 0.62;
            }
        }

        private void ExportCsv()
        {
            if (displayed == null) return;

            var dialog = new SaveFileDialog
            {
                Title = T("Export results to CSV"),
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "results-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;

            var builder = new StringBuilder();
            builder.AppendLine(string.Join(",", displayed.Columns.Cast<DataColumn>().Select(column => Csv(GridAnalytics.DisplayName(column)))));
            foreach (DataRow row in displayed.Rows)
            {
                builder.AppendLine(string.Join(",", row.ItemArray.Select(value =>
                    value == null || value == DBNull.Value ? string.Empty : Csv(Convert.ToString(value, CultureInfo.CurrentCulture)))));
            }

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            status.Text = LocalizationManager.Format("Exported {0} row(s) to {1}.", displayed.Rows.Count, dialog.FileName);
        }

        private void ExportJson()
        {
            if (displayed == null) return;

            var dialog = new SaveFileDialog
            {
                Title = T("Export results to JSON"),
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = "results-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;

            var names = displayed.Columns.Cast<DataColumn>().Select(GridAnalytics.DisplayName).ToList();
            var builder = new StringBuilder();
            builder.AppendLine("[");
            for (int index = 0; index < displayed.Rows.Count; index++)
            {
                DataRow row = displayed.Rows[index];
                builder.Append("  {");
                for (int columnIndex = 0; columnIndex < names.Count; columnIndex++)
                {
                    if (columnIndex > 0) builder.Append(", ");
                    builder.Append(Json(names[columnIndex])).Append(": ");
                    object value = row[columnIndex];
                    if (value == null || value == DBNull.Value) builder.Append("null");
                    else if (value is bool) builder.Append(((bool)value) ? "true" : "false");
                    else if (IsNumericType(value.GetType())) builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    else builder.Append(Json(Convert.ToString(value, CultureInfo.CurrentCulture)));
                }
                builder.Append("}");
                if (index < displayed.Rows.Count - 1) builder.Append(',');
                builder.AppendLine();
            }
            builder.AppendLine("]");

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            status.Text = LocalizationManager.Format("Exported {0} row(s) to {1}.", displayed.Rows.Count, dialog.FileName);
        }

        private void ExportInsert()
        {
            if (displayed == null) return;

            string table = TextPromptWindow.Ask(this, T("Export INSERT script"), T("Table name"), "dbo.Table");
            if (string.IsNullOrWhiteSpace(table)) return;

            var builder = new StringBuilder();
            var names = displayed.Columns.Cast<DataColumn>()
                .Select(column => "[" + GridAnalytics.DisplayName(column).Replace("]", "]]") + "]")
                .ToList();

            foreach (DataRow row in displayed.Rows)
            {
                var values = new List<string>();
                for (int index = 0; index < names.Count; index++)
                {
                    object value = row[index];
                    values.Add(SqlLiteral(value));
                }

                builder.Append("INSERT INTO ").Append(table.Trim()).Append(" (").Append(string.Join(", ", names))
                    .Append(") VALUES (").Append(string.Join(", ", values)).AppendLine(");");
            }

            var dialog = new SaveFileDialog
            {
                Title = T("Export results to an INSERT script"),
                Filter = "SQL (*.sql)|*.sql|All files (*.*)|*.*",
                FileName = "insert-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".sql",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            status.Text = LocalizationManager.Format("Exported {0} row(s) to {1}.", displayed.Rows.Count, dialog.FileName);
        }

        private static string SqlLiteral(object value)
        {
            if (value == null || value == DBNull.Value) return "NULL";
            if (value is bool) return ((bool)value) ? "1" : "0";
            if (IsNumericType(value.GetType())) return Convert.ToString(value, CultureInfo.InvariantCulture);

            string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            return "N'" + text.Replace("'", "''") + "'";
        }

        private static bool IsNumericType(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        private static string Json(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (char character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        private static string Csv(string value)
        {
            string text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static bool Matches(object value, string opKey, string operand)
        {
            string text = value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;

            switch (opKey)
            {
                case "Contains": return text.IndexOf(operand, StringComparison.OrdinalIgnoreCase) >= 0;
                case "Equals": return string.Equals(text, operand, StringComparison.OrdinalIgnoreCase);
                case "Not equals": return !string.Equals(text, operand, StringComparison.OrdinalIgnoreCase);
                case "Starts with": return text.StartsWith(operand, StringComparison.OrdinalIgnoreCase);
                case "Ends with": return text.EndsWith(operand, StringComparison.OrdinalIgnoreCase);
                case "Greater than": return Compare(value, text, operand) > 0;
                case "Less than": return Compare(value, text, operand) < 0;
                default: return text.IndexOf(operand, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static int Compare(object value, string text, string operand)
        {
            if (value is DateTime && DateTime.TryParse(operand, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime moment))
                return ((DateTime)value).CompareTo(moment);

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal left) &&
                decimal.TryParse(operand, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal right))
            {
                return left.CompareTo(right);
            }

            return string.Compare(text, operand, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The host turns Delete/Backspace into editor commands; this edits the focused box.</summary>
        internal bool TryHandleDeleteKey(bool back)
        {
            // Any text box in the panel must keep its own editing keys, not only the two boxes that
            // existed when this guard was written: the field filter was missing, so Backspace there
            // fell through to SSMS and deleted text in the SQL editor instead.
            Window host = Window.GetWindow(this);
            if (host == null || !host.IsActive) return false;

            var box = Keyboard.FocusedElement as TextBox;
            if (box == null || !IsAncestorOf(box)) return true;

            try
            {
                if (box.SelectionLength > 0)
                {
                    box.SelectedText = string.Empty;
                    return true;
                }

                int caret = box.CaretIndex;
                if (back)
                {
                    if (caret <= 0) return true;
                    box.Select(caret - 1, 1);
                }
                else
                {
                    if (caret >= box.Text.Length) return true;
                    box.Select(caret, 1);
                }

                box.SelectedText = string.Empty;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                CancelAnalysis();
                dataGrid.ItemsSource = null;
                statsGrid.ItemsSource = null;
                statsView = null;
                statsCache.Clear();
                statsCacheOrder.Clear();
            }
            catch (Exception)
            {
            }
        }

        private sealed class SortKey
        {
            public string Column;
            public bool Descending;
        }

        /// <summary>One matching cell, kept in display order for Previous/Next navigation.</summary>
        private sealed class MatchHit
        {
            public DataRow Row;
            public int Ordinal;
        }

        private static readonly Brush MatchCellBrush = new SolidColorBrush(Color.FromArgb(150, 255, 214, 0));

        /// <summary>
        /// The tint behind a cell whose value the filter found.  The binding source is the row
        /// (DataContext), so each cell only asks whether its own column ordinal is in the matched
        /// set; only the cells the grid actually realises ever evaluate it.
        /// </summary>
        private Brush MatchCellBackground(object item, object parameter)
        {
            if (matchedCells.Count == 0) return Brushes.Transparent;

            DataRowView rowView = item as DataRowView;
            if (rowView == null) return Brushes.Transparent;
            if (!int.TryParse(Convert.ToString(parameter, CultureInfo.InvariantCulture), out int ordinal))
                return Brushes.Transparent;

            return matchedCells.TryGetValue(rowView.Row, out HashSet<int> ordinals) && ordinals.Contains(ordinal)
                ? MatchCellBrush
                : Brushes.Transparent;
        }

        private sealed class CellMatchConverter : IValueConverter
        {
            private readonly GridAnalyticsControl owner;

            public CellMatchConverter(GridAnalyticsControl owner) => this.owner = owner;

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
                => owner.MatchCellBackground(value, parameter);

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
                => Binding.DoNothing;
        }

        /// <summary>Formats a cell: real text, NULL in italic grey, and the tooltip of the full value.</summary>
        private sealed class CellTextConverter : IValueConverter
        {
            /// <summary>When false, a NULL cell renders blank instead of the "NULL" marker.</summary>
            public bool ShowNull { get; set; } = true;

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                bool isNull = value == null || value == DBNull.Value;
                string mode = parameter == null ? "text" : System.Convert.ToString(parameter);
                if (mode == "text")
                {
                    if (!isNull) return System.Convert.ToString(value, culture);
                    return ShowNull ? "NULL" : string.Empty;
                }

                if (mode == "style") return isNull ? FontStyles.Italic : FontStyles.Normal;
                if (mode == "brush") return isNull ? Brushes.Gray : SystemColors.ControlTextBrush;
                return null;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }
    }

    /// <summary>Read-only viewer for a single cell: full value, type and length.</summary>
    internal sealed class CellValueWindow : Window
    {
        private CellValueWindow(string column, string type, object value)
        {
            Title = LocalizationManager.Format("Cell value: {0}", column);
            Width = 640;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            string text = value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value, CultureInfo.CurrentCulture);
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock
            {
                Text = LocalizationManager.Format("{0} | {1} | {2} character(s){3}", column, type, (text ?? string.Empty).Length,
                    value == null || value == DBNull.Value ? " | NULL" : string.Empty),
                Margin = new Thickness(0, 0, 0, 6),
                Opacity = 0.8
            });

            var box = new TextBox
            {
                Text = text ?? string.Empty,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas")
            };
            Grid.SetRow(box, 2);
            root.Children.Add(box);

            // The raw value is often XML or JSON stored in a text column; let the user pick how to
            // show it instead of forcing them to read one long unbroken line.
            var formatBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            formatBar.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Format:"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            var formatBox = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
            formatBox.Items.Add(LocalizationManager.T("Original"));
            formatBox.Items.Add("JSON");
            formatBox.Items.Add("XML");
            formatBar.Children.Add(formatBox);
            var hint = new TextBlock
            {
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            formatBar.Children.Add(hint);
            Grid.SetRow(formatBar, 1);
            root.Children.Add(formatBar);

            Action applyFormat = () =>
            {
                string kind = formatBox.SelectedIndex == 1 ? "JSON" : formatBox.SelectedIndex == 2 ? "XML" : string.Empty;
                bool ok = true;
                box.Text = string.IsNullOrEmpty(kind) ? text ?? string.Empty : PrettyPrint(kind, text, out ok);
                hint.Text = ok ? string.Empty : LocalizationManager.Format("Not valid {0}; showing the raw value.", kind);
            };
            formatBox.SelectionChanged += (_, __) => applyFormat();
            formatBox.SelectedIndex = DetectFormat(text);
            applyFormat();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var copy = new Button { Content = LocalizationManager.T("Copy"), Width = 90, Height = 26, Margin = new Thickness(0, 0, 6, 0) };
            copy.Click += (_, __) => { try { Clipboard.SetText(box.Text); } catch (Exception) { } };
            var close = new Button { Content = LocalizationManager.T("Close"), Width = 90, Height = 26, IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>Picks the format that already fits the value, so most cells need no clicks.</summary>
        private static int DetectFormat(string text)
        {
            string trimmed = (text ?? string.Empty).TrimStart();
            if (trimmed.StartsWith("<", StringComparison.Ordinal))
            {
                bool ok;
                PrettyPrint("XML", text, out ok);
                if (ok) return 2;
            }
            else if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                bool ok;
                PrettyPrint("JSON", text, out ok);
                if (ok) return 1;
            }
            return 0;
        }

        /// <summary>Indents XML/JSON; returns the original text and ok=false when it does not parse.</summary>
        private static string PrettyPrint(string kind, string text, out bool ok)
        {
            ok = true;
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            try
            {
                if (kind == "JSON")
                    return Newtonsoft.Json.Linq.JToken.Parse(text).ToString(Newtonsoft.Json.Formatting.Indented);
                if (kind == "XML")
                    return System.Xml.Linq.XDocument.Parse(text).ToString();
            }
            catch (Exception)
            {
                ok = false;
            }
            return text ?? string.Empty;
        }

        internal static void Show(FrameworkElement owner, string column, string type, object value)
        {
            var window = new CellValueWindow(column, type, value);
            try { window.Owner = Window.GetWindow(owner); } catch (Exception) { }
            window.ShowDialog();
        }
    }

    /// <summary>Plain text report window, used for the top-value list.</summary>
    internal sealed class TextReportWindow : Window
    {
        private TextReportWindow(string title, string text)
        {
            Title = title;
            Width = 460;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var box = new TextBox
            {
                Text = text ?? string.Empty,
                IsReadOnly = true,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            root.Children.Add(box);

            var close = new Button { Content = LocalizationManager.T("Close"), Width = 90, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), IsCancel = true };
            close.Click += (_, __) => Close();
            Grid.SetRow(close, 1);
            root.Children.Add(close);

            Content = root;
        }

        internal static void Show(FrameworkElement owner, string title, string text)
        {
            var window = new TextReportWindow(title, text);
            try { window.Owner = Window.GetWindow(owner); } catch (Exception) { }
            window.ShowDialog();
        }
    }

    /// <summary>Single-line prompt used for the INSERT target table.</summary>
    internal sealed class TextPromptWindow : Window
    {
        private readonly TextBox box;
        private string result;

        private TextPromptWindow(string title, string label, string initial)
        {
            Title = title;
            Width = 460;
            Height = 170;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            box = new TextBox { Text = initial ?? string.Empty, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetRow(box, 1);
            root.Children.Add(box);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = LocalizationManager.T("OK"), Width = 90, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (_, __) => { result = box.Text; DialogResult = true; };
            var cancel = new Button { Content = LocalizationManager.T("Cancel"), Width = 90, Height = 26, IsCancel = true };
            cancel.Click += (_, __) => { DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (_, __) => { box.Focus(); box.SelectAll(); };
        }

        internal static string Ask(FrameworkElement owner, string title, string label, string initial)
        {
            var window = new TextPromptWindow(title, label, initial);
            try { window.Owner = Window.GetWindow(owner); } catch (Exception) { }
            return window.ShowDialog() == true ? window.result : null;
        }
    }
}
