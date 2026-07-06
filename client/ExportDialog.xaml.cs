using System.Windows;
using System.Windows.Media;

namespace Hotix.InvoiceClient;

public partial class ExportDialog : Window
{
    public enum ExportMode
    {
        CreateNew,
        AppendExisting
    }

    public ExportMode SelectedMode { get; private set; } = ExportMode.CreateNew;

    public ExportDialog()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Default: Create New selected
        UpdateSelection(ExportMode.CreateNew);
    }

    private void NewCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateSelection(ExportMode.CreateNew);
    }

    private void AppendCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateSelection(ExportMode.AppendExisting);
    }

    private void UpdateSelection(ExportMode mode)
    {
        SelectedMode = mode;

        bool isNew = mode == ExportMode.CreateNew;

        NewRadioDot.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        AppendRadioDot.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

        NewCard.BorderBrush = isNew
            ? (Brush)Application.Current.FindResource("BrushAccent")
            : (Brush)Application.Current.FindResource("BrushBorder");
        AppendCard.BorderBrush = isNew
            ? (Brush)Application.Current.FindResource("BrushBorder")
            : (Brush)Application.Current.FindResource("BrushAccent");

        NewCard.Background = isNew
            ? (Brush)Application.Current.FindResource("BrushSelected")
            : Brushes.Transparent;
        AppendCard.Background = isNew
            ? Brushes.Transparent
            : (Brush)Application.Current.FindResource("BrushSelected");
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
