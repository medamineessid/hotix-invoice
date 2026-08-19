using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Hotix.InvoiceClient.ViewModels;
using Xunit;

namespace Hotix.InvoiceClient.Tests;

/// <summary>
/// Reproduction harness for the reported preview bug: after clicking a results
/// row, the row highlights and its inline details expand (grid-internal
/// selection works), yet the preview panel keeps showing the "Select an
/// invoice…" hint — meaning <see cref="MainViewModel.HasSelectedRow"/> never
/// flips true.
///
/// MainWindow.xaml has NO TabControl: both ResultsGrid and IncompleteGrid are
/// always in the visual tree, each bound TwoWay to the single
/// <see cref="MainViewModel.SelectedRow"/> over a DIFFERENT ListCollectionView
/// (ResultsView / IncompleteView). These tests recreate that topology on a real
/// STA UI thread and simulate selection, to find the condition under which the
/// second grid's coercion clobbers SelectedRow back to null.
/// </summary>
public sealed class PreviewSelectionClobberTests
{
    private static void RunOnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
    }

    /// <summary>Drain the dispatcher queue down to ContextIdle so layout,
    /// container generation, and binding updates all settle.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class Harness
    {
        public required MainViewModel Vm;
        public required DataGrid ResultsGrid;
        public required DataGrid IncompleteGrid;
        public required Window Window;
        public required List<string> Log;
        public required InvoiceRowViewModel CompleteRow;
        public required InvoiceRowViewModel IncompleteRow;

        public string Describe(string expectation) =>
            $"{expectation} Actual: SelectedRow={Vm.SelectedRow?.FileName ?? "null"}, " +
            $"HasSelectedRow={Vm.HasSelectedRow}, " +
            $"resultsGrid.SelectedItem={(ResultsGrid.SelectedItem as InvoiceRowViewModel)?.FileName ?? "null"}, " +
            $"incompleteGrid.SelectedItem={(IncompleteGrid.SelectedItem as InvoiceRowViewModel)?.FileName ?? "null"}. " +
            $"Sequence: [{string.Join(" | ", Log)}]";
    }

    private static DataGrid MakeGrid(object itemsSource, MainViewModel vm)
    {
        var grid = new DataGrid
        {
            ItemsSource = (System.Collections.IEnumerable)itemsSource,
            AutoGenerateColumns = false,
            EnableRowVirtualization = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
        };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding("FileName") });
        grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedRow))
        {
            Source = vm,
            Mode = BindingMode.TwoWay,
        });
        return grid;
    }

    /// <summary>Builds the two-grid harness on the current (STA) thread, shows
    /// it off-screen, and seeds one row into Results and one into
    /// IncompleteResults (mirroring a finished extraction with nothing selected).</summary>
    private static Harness Build(bool collapseIncompleteGrid)
    {
        var vm = new MainViewModel();
        var log = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedRow))
                log.Add($"SelectedRow={vm.SelectedRow?.FileName ?? "null"}");
            else if (e.PropertyName == nameof(MainViewModel.HasSelectedRow))
                log.Add($"HasSelectedRow={vm.HasSelectedRow}");
        };

        var resultsGrid = MakeGrid(vm.ResultsView!, vm);
        var incompleteGrid = MakeGrid(vm.IncompleteView!, vm);
        if (collapseIncompleteGrid)
            incompleteGrid.Visibility = Visibility.Collapsed;

        var root = new StackPanel();
        root.Children.Add(resultsGrid);
        root.Children.Add(incompleteGrid);

        var window = new Window
        {
            Content = root,
            Width = 800,
            Height = 600,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        Pump();

        var completeRow = InvoiceRowViewModel.FromError(@"C:\hotix-clobber-complete.png", "seed");
        var incompleteRow = InvoiceRowViewModel.FromError(@"C:\hotix-clobber-incomplete.png", "seed");
        vm.Results.Add(completeRow);
        vm.IncompleteResults.Add(incompleteRow);
        Pump();

        return new Harness
        {
            Vm = vm,
            ResultsGrid = resultsGrid,
            IncompleteGrid = incompleteGrid,
            Window = window,
            Log = log,
            CompleteRow = completeRow,
            IncompleteRow = incompleteRow,
        };
    }

    [Fact]
    public void FreshSelection_BothGridsVisible_KeepsSelectedRowNonNull()
    {
        RunOnSta(() =>
        {
            var h = Build(collapseIncompleteGrid: false);
            try
            {
                Assert.Null(h.Vm.SelectedRow); // no auto-selection on load
                h.Log.Add("--- click complete row ---");
                h.ResultsGrid.SelectedItem = h.CompleteRow;
                Pump();

                Assert.True(
                    ReferenceEquals(h.Vm.SelectedRow, h.CompleteRow) && h.Vm.HasSelectedRow,
                    h.Describe("Fresh selection (both grids visible): expected SelectedRow=complete, HasSelectedRow=true."));
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void FreshSelection_InactiveGridCollapsed_KeepsSelectedRowNonNull()
    {
        RunOnSta(() =>
        {
            // Mirrors the real app on the Results tab: IncompleteGrid is Collapsed
            // but still in the tree with an active SelectedItem binding.
            var h = Build(collapseIncompleteGrid: true);
            try
            {
                Assert.Null(h.Vm.SelectedRow);
                h.Log.Add("--- click complete row (incomplete grid collapsed) ---");
                h.ResultsGrid.SelectedItem = h.CompleteRow;
                Pump();

                Assert.True(
                    ReferenceEquals(h.Vm.SelectedRow, h.CompleteRow) && h.Vm.HasSelectedRow,
                    h.Describe("Fresh selection (inactive grid collapsed): expected SelectedRow=complete, HasSelectedRow=true."));
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void CrossGridReselection_FromIncompleteToComplete_KeepsSelectedRowNonNull()
    {
        RunOnSta(() =>
        {
            var h = Build(collapseIncompleteGrid: false);
            try
            {
                // First select an incomplete row (the OTHER grid).
                h.IncompleteGrid.SelectedItem = h.IncompleteRow;
                Pump();
                Assert.True(ReferenceEquals(h.Vm.SelectedRow, h.IncompleteRow),
                    h.Describe("Precondition: incomplete row should be selected first."));

                h.Log.Add("--- switch: click complete row ---");
                // Now click a complete row in the Results grid.
                h.ResultsGrid.SelectedItem = h.CompleteRow;
                Pump();

                Assert.True(
                    ReferenceEquals(h.Vm.SelectedRow, h.CompleteRow) && h.Vm.HasSelectedRow,
                    h.Describe("Cross-grid reselection: expected SelectedRow=complete, HasSelectedRow=true."));
            }
            finally { h.Window.Close(); }
        });
    }

    /// <summary>
    /// The exact state from the user's pipeline.log: ONE successful result,
    /// ZERO incomplete rows. The user clicks the single Results row; the grid
    /// highlights and expands, but the "Select an invoice…" hint persists.
    ///
    /// This test reproduces that state end-to-end with INHERITED DataContext
    /// (Window.DataContext = vm, like MainWindow) and asserts not just the VM
    /// state but the actual rendered Visibility of the empty-state Border and
    /// the preview Border — the real chain HasSelectedRow → UI.
    /// </summary>
    [Fact]
    public void RealState_OneResultZeroIncomplete_CollapsesHintAndShowsPreview()
    {
        RunOnSta(() =>
        {
            var vm = new MainViewModel();
            var log = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedRow))
                    log.Add($"SelectedRow={vm.SelectedRow?.FileName ?? "null"}");
                else if (e.PropertyName == nameof(MainViewModel.HasSelectedRow))
                    log.Add($"HasSelectedRow={vm.HasSelectedRow}");
            };

            // Grids bound the SAME way MainWindow does: inherited DataContext,
            // ItemsSource and SelectedItem via plain {Binding ...} (no Source).
            DataGrid MakeInheritedGrid(string itemsSourcePath)
            {
                var g = new DataGrid
                {
                    AutoGenerateColumns = false,
                    EnableRowVirtualization = false,
                    SelectionMode = DataGridSelectionMode.Single,
                    SelectionUnit = DataGridSelectionUnit.FullRow,
                };
                g.Columns.Add(new DataGridTextColumn { Binding = new Binding("FileName") });
                g.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsSourcePath));
                g.SetBinding(DataGrid.SelectedItemProperty,
                    new Binding(nameof(MainViewModel.SelectedRow)) { Mode = BindingMode.TwoWay });
                return g;
            }

            var resultsGrid = MakeInheritedGrid(nameof(MainViewModel.ResultsView));
            var incompleteGrid = MakeInheritedGrid(nameof(MainViewModel.IncompleteView));

            // Empty-state Border with the EXACT style logic from MainWindow.xaml.
            var emptyState = new Border();
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible));
            var selectedTrigger = new DataTrigger { Binding = new Binding(nameof(MainViewModel.HasSelectedRow)), Value = true };
            selectedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(selectedTrigger);
            var noRowsTrigger = new MultiDataTrigger();
            noRowsTrigger.Conditions.Add(new Condition(new Binding("Results.Count"), 0));
            noRowsTrigger.Conditions.Add(new Condition(new Binding("IncompleteResults.Count"), 0));
            noRowsTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(noRowsTrigger);
            emptyState.Style = style;

            // Preview Border: Visibility bound to HasSelectedRow via the converter.
            var preview = new Border();
            preview.SetBinding(UIElement.VisibilityProperty,
                new Binding(nameof(MainViewModel.HasSelectedRow)) { Converter = new BooleanToVisibilityConverter() });

            var root = new Grid();
            root.Children.Add(resultsGrid);
            root.Children.Add(incompleteGrid);
            root.Children.Add(emptyState);
            root.Children.Add(preview);

            var window = new Window
            {
                Content = root,
                DataContext = vm, // inherited by the whole tree, exactly like MainWindow
                Width = 800,
                Height = 600,
                Left = -4000,
                Top = -4000,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            try
            {
                window.Show();
                Pump();

                // One successful result, zero incomplete — the logged real state.
                var row = InvoiceRowViewModel.FromError(@"C:\hotix-real-state.jpg", "seed");
                vm.Results.Add(row);
                Pump();

                // Before selection: hint visible, preview hidden.
                Assert.Null(vm.SelectedRow);
                Assert.Equal(Visibility.Visible, emptyState.Visibility);
                Assert.Equal(Visibility.Collapsed, preview.Visibility);

                log.Add("--- click the only row ---");
                resultsGrid.SelectedItem = row;
                Pump();

                Assert.True(ReferenceEquals(vm.SelectedRow, row) && vm.HasSelectedRow,
                    $"VM after selecting the only row. SelectedRow={vm.SelectedRow?.FileName ?? "null"}, " +
                    $"HasSelectedRow={vm.HasSelectedRow}, resultsGrid.SelectedItem=" +
                    $"{(resultsGrid.SelectedItem as InvoiceRowViewModel)?.FileName ?? "null"}, " +
                    $"incompleteGrid.SelectedItem={(incompleteGrid.SelectedItem as InvoiceRowViewModel)?.FileName ?? "null"}. " +
                    $"Sequence: [{string.Join(" | ", log)}]");

                // After selection the hint must collapse and the preview must show.
                Assert.Equal(Visibility.Collapsed, emptyState.Visibility);
                Assert.Equal(Visibility.Visible, preview.Visibility);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// DECISIVE: binds SelectedItem EXACTLY like MainWindow.xaml — "{Binding SelectedRow}"
    /// with NO Mode specified — and checks whether a grid-internal selection writes
    /// back to the view-model. If DataGrid.SelectedItem is not TwoWay-by-default, the
    /// click highlights the row but SelectedRow stays null → HasSelectedRow stays false
    /// → the "Select an invoice…" hint never goes away. That is the reported bug.
    /// </summary>
    [Fact]
    public void SelectedItem_DefaultBindingMode_WritesBackToSelectedRow()
    {
        RunOnSta(() =>
        {
            var vm = new MainViewModel();
            var row = InvoiceRowViewModel.FromError(@"C:\hotix-default-mode.jpg", "seed");
            vm.Results.Add(row);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                EnableRowVirtualization = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
            };
            grid.Columns.Add(new DataGridTextColumn { Binding = new Binding("FileName") });
            grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.ResultsView)));
            // EXACTLY like MainWindow.xaml line 931/1414: no Mode specified.
            grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedRow)));

            var window = new Window
            {
                Content = grid,
                DataContext = vm,
                Width = 400,
                Height = 300,
                Left = -4000,
                Top = -4000,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            try
            {
                window.Show();
                Pump();

                var meta = DataGrid.SelectedItemProperty.GetMetadata(typeof(DataGrid)) as FrameworkPropertyMetadata;
                var bindsTwoWayByDefault = meta?.BindsTwoWayByDefault;
                var declaredMode = grid.GetBindingExpression(DataGrid.SelectedItemProperty)?.ParentBinding.Mode;

                // Simulate the click's internal selection (row highlights either way).
                grid.SelectedItem = row;
                Pump();

                Assert.True(ReferenceEquals(vm.SelectedRow, row),
                    $"A default-mode SelectedItem binding must write back to SelectedRow. " +
                    $"BindsTwoWayByDefault={bindsTwoWayByDefault?.ToString() ?? "null"}, " +
                    $"declaredMode={declaredMode?.ToString() ?? "null"}, " +
                    $"vm.SelectedRow={vm.SelectedRow?.FileName ?? "null"}, " +
                    $"grid.SelectedItem={(grid.SelectedItem as InvoiceRowViewModel)?.FileName ?? "null"}.");
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// Attempt at a full end-to-end test against the REAL MainWindow.xaml tree.
    /// SKIPPED: MainWindow cannot be constructed in the test host because its
    /// Window.Icon="hotix_icon.ico" (a compiled Resource in the client assembly)
    /// resolves its relative pack URI against the test host's entry assembly, not
    /// the client assembly, throwing IOException at InitializeComponent. The App
    /// resource dictionaries (Themes/*.xaml, converters) DO load fine in-host —
    /// only the window icon blocks construction. The selection mechanism itself is
    /// covered by the passing tests above; the live defect is diagnosed by
    /// instrumenting the real SelectedRow setter (pipeline.log) instead.
    /// </summary>
    [Fact(Skip = "Real MainWindow can't be constructed in-host: Window.Icon pack URI resolves to the test host assembly. Mechanism is covered by the tests above.")]
    public void RealMainWindow_SelectingOnlyResultRow_FlipsHasSelectedRow()
    {
        RunOnSta(() =>
        {
            // Load the real App resource dictionaries (Themes/*.xaml + converters)
            // so MainWindow.xaml's StaticResource references resolve.
            var clientAsm = typeof(global::Hotix.InvoiceClient.App).Assembly;
            try { if (Application.ResourceAssembly is null) Application.ResourceAssembly = clientAsm; }
            catch { /* already set/used — fine */ }
            if (Application.Current is null)
            {
                var app = new global::Hotix.InvoiceClient.App();
                app.InitializeComponent();
            }

            var vm = new MainViewModel();

            global::Hotix.InvoiceClient.MainWindow window;
            try
            {
                window = new global::Hotix.InvoiceClient.MainWindow
                {
                    DataContext = vm,
                    Left = -4000,
                    Top = -4000,
                    WindowStyle = WindowStyle.None,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Width = 1200,
                    Height = 800,
                };
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException("Real MainWindow failed to construct: " + ex);
            }

            try
            {
                window.Show();
                Pump();

                var row = InvoiceRowViewModel.FromError(@"C:\hotix-real-window.jpg", "seed");
                vm.Results.Add(row);
                Pump();

                var grid = window.FindName("ResultsGrid") as DataGrid;
                Assert.True(grid != null, "Could not find ResultsGrid by name in the real MainWindow.");

                // Simulate the click's selection through the real grid + real binding.
                grid!.SelectedItem = row;
                grid.UpdateLayout();
                Pump();

                Assert.True(vm.HasSelectedRow,
                    $"Real MainWindow: selecting the only Results row must set HasSelectedRow=true. " +
                    $"vm.SelectedRow={vm.SelectedRow?.FileName ?? "null"}, " +
                    $"grid.SelectedItem={(grid.SelectedItem as InvoiceRowViewModel)?.FileName ?? "null"}, " +
                    $"grid.SelectedIndex={grid.SelectedIndex}, grid.Items.Count={grid.Items.Count}, " +
                    $"Results.Count={vm.Results.Count}, IncompleteResults.Count={vm.IncompleteResults.Count}.");
            }
            finally { window.Close(); }
        });
    }
}
