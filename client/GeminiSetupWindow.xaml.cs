using System.Diagnostics;
using System.Windows;
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string key = GeminiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageLabel.Text = "Veuillez entrer une clé ou cliquer sur Ignorer.";
            MessageLabel.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        try
        {
            if (DataContext is MainViewModel vm)
            {
                vm.GeminiKeyInput = key;
                vm.SaveGeminiKeyCommand.Execute(null);
                MessageLabel.Text = "Clé enregistrée";
                MessageLabel.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
        }
        catch (Exception ex)
        {
            MessageLabel.Text = $"Erreur : {ex.Message}";
            MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
        }
    }
}
