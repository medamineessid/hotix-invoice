using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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

    private async void Save_Click(object sender, RoutedEventArgs e)
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
            // Update VM state if available
            if (DataContext is MainViewModel vm)
            {
                vm.GeminiKeyInput = key;
                // vm.SaveGeminiKeyCommand is a RelayCommand, we can't easily await it if it's async in VM but it is.
                // Better call private method logic or use the public method if we made it public (which I did in MultiReplace).
            }

            // Manually save and verify as requested
            string appSettingsPath = @"C:\hotix-invoice\server\appsettings.json";
            var settings = new { gemini_api_key = key, default_engine = "auto" };
            File.WriteAllText(appSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8000") };
            var response = await client.GetAsync("/engine-status");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                var status = JsonSerializer.Deserialize<JsonElement>(body);
                if (status.GetProperty("gemini_available").GetBoolean())
                {
                    MessageLabel.Text = "Clé valide — Gemini activé";
                    MessageLabel.Foreground = System.Windows.Media.Brushes.LimeGreen;
                    await Task.Delay(1500);
                    Close();
                }
                else
                {
                    MessageLabel.Text = "Clé invalide ou quota dépassé";
                    MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            else
            {
                MessageLabel.Text = "Erreur de connexion au serveur OCR";
                MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            MessageLabel.Text = $"Erreur : {ex.Message}";
            MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
        }
    }
}
