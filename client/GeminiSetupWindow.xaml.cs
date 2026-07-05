using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using Hotix.InvoiceClient.ViewModels;

namespace Hotix.InvoiceClient;

public partial class GeminiSetupWindow : Window
{
    public GeminiSetupWindow()
    {
        InitializeComponent();
    }

    private void GetApiKey_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://aistudio.google.com/app/apikey") { UseShellExecute = true });
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            TranslationSource.Get("GeminiClearConfirm"),
            TranslationSource.Get("GeminiClearTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ClearGeminiKeyCommand.Execute(null);
                GeminiKeyBox.Password = string.Empty;
                MessageLabel.Text = TranslationSource.Get("GeminiCleared");
                MessageLabel.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }
        catch (Exception ex)
        {
            MessageLabel.Text = TranslationSource.Fmt("GeminiSaveError", ex.Message);
            MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string key = GeminiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageLabel.Text = TranslationSource.Get("GeminiEnterKey");
            MessageLabel.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        if (DataContext is not MainViewModel vm)
            return;

        // ── Show spinner & disable buttons during validation ──
        SetValidatingState(true);

        try
        {
            MessageLabel.Text = TranslationSource.Get("GeminiVerifying");
            MessageLabel.Foreground = System.Windows.Media.Brushes.Gray;

            var (isValid, errorMessage) = await vm.ValidateGeminiKeyAsync(key);

            if (!isValid)
            {
                MessageLabel.Text = errorMessage ?? TranslationSource.Get("GeminiInvalid");
                MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            // Key is valid — save it
            vm.GeminiKeyInput = key;
            vm.SaveGeminiKeyCommand.Execute(null);
            MessageLabel.Text = TranslationSource.Get("GeminiSaved");
            MessageLabel.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
        catch (Exception ex)
        {
            MessageLabel.Text = TranslationSource.Fmt("GeminiSaveError", ex.Message);
            MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            // ── Restore buttons ──
            SetValidatingState(false);
        }
    }

    private void SetValidatingState(bool isValidating)
    {
        // Show/hide spinner
        SpinnerIndicator.Visibility = isValidating ? Visibility.Visible : Visibility.Collapsed;

        if (isValidating)
        {
            // Start the spinner animation
            var sb = (Storyboard)FindResource("SpinnerStoryboard");
            sb.Begin(SpinnerArc);
        }
        else
        {
            // Stop the spinner animation
            var sb = (Storyboard)FindResource("SpinnerStoryboard");
            sb.Stop(SpinnerArc);
        }

        // Disable/enable buttons during validation
        GetKeyButton.IsEnabled = !isValidating;
        ClearKeyButton.IsEnabled = !isValidating;
        IgnoreButton.IsEnabled = !isValidating;
        SaveButton.IsEnabled = !isValidating;
        GeminiKeyBox.IsEnabled = !isValidating;
    }
}
