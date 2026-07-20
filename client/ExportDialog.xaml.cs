using System.Windows;
using System.Windows.Media;

namespace Hotix.InvoiceClient;

public partial class ExportDialog : Window
{
    /// <summary>Which rows to include in the export.</summary>
    public enum FilterMode
    {
        ResultsOnly,
        MissingOnly,
        Both
    }

    /// <summary>Whether to create a new file or append to an existing one.</summary>
    public enum DestinationMode
    {
        CreateNew,
        AppendExisting
    }

    public FilterMode SelectedFilter { get; private set; } = FilterMode.Both;
    public DestinationMode SelectedDestination { get; private set; } = DestinationMode.CreateNew;

    public ExportDialog()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Default: Both selected, Create New
        UpdateFilterSelection(FilterMode.Both);
        AppendCheckBox.IsChecked = false;
    }

    private void FilterResultsOnly_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateFilterSelection(FilterMode.ResultsOnly);
    }

    private void FilterMissingOnly_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateFilterSelection(FilterMode.MissingOnly);
    }

    private void FilterBoth_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateFilterSelection(FilterMode.Both);
    }

    private void AppendCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AppendCheckBox.IsChecked = !AppendCheckBox.IsChecked;
        UpdateDestination();
    }

    private void AppendCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDestination();
    }

    private void UpdateFilterSelection(FilterMode mode)
    {
        SelectedFilter = mode;

        // Update visibility of radio dots
        FilterResultsDot.Visibility = mode == FilterMode.ResultsOnly ? Visibility.Visible : Visibility.Collapsed;
        FilterMissingDot.Visibility = mode == FilterMode.MissingOnly ? Visibility.Visible : Visibility.Collapsed;
        FilterBothDot.Visibility = mode == FilterMode.Both ? Visibility.Visible : Visibility.Collapsed;

        // Update card borders and backgrounds
        FilterResultsCard.BorderBrush = GetBrush(mode == FilterMode.ResultsOnly ? "BrushAccent" : "BrushBorder");
        FilterResultsCard.BorderThickness = new Thickness(mode == FilterMode.ResultsOnly ? 2 : 1);
        FilterResultsCard.Background = mode == FilterMode.ResultsOnly
            ? GetBrush("BrushSelected")
            : Brushes.Transparent;

        FilterMissingCard.BorderBrush = GetBrush(mode == FilterMode.MissingOnly ? "BrushAccent" : "BrushBorder");
        FilterMissingCard.BorderThickness = new Thickness(mode == FilterMode.MissingOnly ? 2 : 1);
        FilterMissingCard.Background = mode == FilterMode.MissingOnly
            ? GetBrush("BrushSelected")
            : Brushes.Transparent;

        FilterBothCard.BorderBrush = GetBrush(mode == FilterMode.Both ? "BrushAccent" : "BrushBorder");
        FilterBothCard.BorderThickness = new Thickness(mode == FilterMode.Both ? 2 : 1);
        FilterBothCard.Background = mode == FilterMode.Both
            ? GetBrush("BrushSelected")
            : Brushes.Transparent;
    }

    private void UpdateDestination()
    {
        SelectedDestination = AppendCheckBox.IsChecked == true
            ? DestinationMode.AppendExisting
            : DestinationMode.CreateNew;

        // Highlight append card when checked
        AppendCard.BorderBrush = AppendCheckBox.IsChecked == true
            ? GetBrush("BrushAccent")
            : GetBrush("BrushBorder");
        AppendCard.BorderThickness = new Thickness(AppendCheckBox.IsChecked == true ? 2 : 1);
        AppendCard.Background = AppendCheckBox.IsChecked == true
            ? GetBrush("BrushSelected")
            : Brushes.Transparent;
    }

    private static Brush GetBrush(string resourceKey)
    {
        return (Brush)Application.Current.FindResource(resourceKey);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
