using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Hotix.InvoiceClient.ViewModels;

namespace Hotix.InvoiceClient;

public partial class GeminiSetupWindow : Window
{
    private bool _isGeminiProvider = true;

    public GeminiSetupWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Default to Gemini provider; check which key is already set
        if (DataContext is MainViewModel vm)
        {
            bool hasGemini = !string.IsNullOrEmpty(vm.GeminiKeyInput);
            bool hasGrok = !string.IsNullOrEmpty(vm.GrokKeyInput);

            if (hasGrok && !hasGemini)
            {
                ProviderGrokRadio.IsChecked = true;
            }
            else
            {
                ProviderGeminiRadio.IsChecked = true;
            }

            // Pre-fill the key box with the selected provider's existing key
            UpdateKeyBoxFromProvider();

            // If a key is already configured, populate the model dropdown
            if (_isGeminiProvider && hasGemini)
            {
                _ = TryPopulateModelsAsync(vm, vm.GeminiKeyInput);
            }
            else if (!_isGeminiProvider && hasGrok)
            {
                _ = TryPopulateModelsAsync(vm, vm.GrokKeyInput);
            }
        }

        Activate();
        GeminiKeyBox.Focus();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save the current key box contents back to the correct provider field
        if (DataContext is MainViewModel vm && !string.IsNullOrEmpty(GeminiKeyBox.Password))
        {
            if (_isGeminiProvider)
            {
                vm.GeminiKeyInput = GeminiKeyBox.Password;
            }
            else
            {
                vm.GrokKeyInput = GeminiKeyBox.Password;
            }
        }
    }

    private void Provider_Checked(object sender, RoutedEventArgs e)
    {
        // Save current box contents before switching
        if (DataContext is MainViewModel vm)
        {
            if (_isGeminiProvider)
            {
                vm.GeminiKeyInput = GeminiKeyBox.Password;
            }
            else
            {
                vm.GrokKeyInput = GeminiKeyBox.Password;
            }
        }

        _isGeminiProvider = ProviderGeminiRadio.IsChecked == true;
        UpdateKeyBoxFromProvider();
        UpdateUIForProvider();
    }

    private void UpdateKeyBoxFromProvider()
    {
        if (DataContext is MainViewModel vm)
        {
            GeminiKeyBox.Password = _isGeminiProvider ? vm.GeminiKeyInput : vm.GrokKeyInput;
        }
        MessageLabel.Text = string.Empty;
    }

    private void UpdateUIForProvider()
    {
        if (_isGeminiProvider)
        {
            Title = TranslationSource.Get("GeminiTitle");
            HeaderTitle.Text = TranslationSource.Get("GeminiHeader");
            HeaderDescription.Text = TranslationSource.Get("GeminiDescription");
            KeyLabel.Text = TranslationSource.Get("GeminiSubheader");
            GetKeyButton.Content = TranslationSource.Get("GeminiGetKeyBtn");
            SaveButton.Content = TranslationSource.Get("GeminiSaveBtn");
            ClearKeyButton.Content = TranslationSource.Get("GeminiClearBtn");
            ModelLabel.Text = TranslationSource.Get("GeminiModelLabel");
        }
        else
        {
            Title = TranslationSource.Get("GrokTitle");
            HeaderTitle.Text = TranslationSource.Get("GrokHeader");
            HeaderDescription.Text = TranslationSource.Get("GrokDescription");
            KeyLabel.Text = TranslationSource.Get("GrokSubheader");
            GetKeyButton.Content = TranslationSource.Get("GrokGetKeyBtn");
            SaveButton.Content = TranslationSource.Get("GrokSaveBtn");
            ClearKeyButton.Content = TranslationSource.Get("GrokClearBtn");
            ModelLabel.Text = TranslationSource.Get("GrokModelLabel");
        }

        // Repopulate model dropdown if key is present
        if (DataContext is MainViewModel vm2)
        {
            string key = _isGeminiProvider ? vm2.GeminiKeyInput : vm2.GrokKeyInput;
            if (!string.IsNullOrEmpty(key))
            {
                _ = TryPopulateModelsAsync(vm2, key);
            }
            else
            {
                ModelSelector.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Fetches available models for the current provider and populates the dropdown.
    /// Gemini: calls the Gemini models.list API. Grok: uses hardcoded known models.
    /// If the API call fails, the dropdown is hidden and the default model is used silently.
    /// </summary>
    private async Task TryPopulateModelsAsync(MainViewModel vm, string apiKey)
    {
        try
        {
            var models = new List<string>();

            if (_isGeminiProvider)
            {
                // Fetch Gemini models from the API
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await client.GetAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                    {
                        foreach (var m in modelsArray.EnumerateArray())
                        {
                            string? name = m.GetProperty("name").GetString();
                            string? supportedActions = m.TryGetProperty("supportedGenerationMethods", out var actions)
                                ? string.Join(",", actions.EnumerateArray().Select(a => a.GetString()))
                                : "";

                            // Only include models that support generateContent and are gemini-* (vision capable)
                            if (name != null && name.StartsWith("models/gemini-") &&
                                supportedActions.Contains("generateContent"))
                            {
                                models.Add(name.Replace("models/", ""));
                            }
                        }
                    }
                }
            }
            else
            {
                // Hardcode known Grok models for now
                models.Add("grok-4.3");
                models.Add("grok-2");
                models.Add("grok-2-vision");
            }

            if (models.Count == 0)
            {
                // No models found — hide the dropdown silently
                ModelSelector.Visibility = Visibility.Collapsed;
                return;
            }

            // Populate the dropdown
            ModelCombo.Items.Clear();

            // Add "Default" option first (uses the current hardcoded default)
            var defaultItem = new System.Windows.Controls.ComboBoxItem
            {
                Content = TranslationSource.Get("ModelDefault"),
                Tag = "",
            };
            ModelCombo.Items.Add(defaultItem);

            foreach (var model in models.OrderBy(m => m))
            {
                var item = new System.Windows.Controls.ComboBoxItem
                {
                    Content = model,
                    Tag = model,
                };
                ModelCombo.Items.Add(item);
            }

            // Select the current model if already configured
            string currentModel = _isGeminiProvider ? vm.GeminiModel : vm.GrokModel;
            if (!string.IsNullOrEmpty(currentModel))
            {
                foreach (System.Windows.Controls.ComboBoxItem item in ModelCombo.Items)
                {
                    if (item.Tag is string tag && tag == currentModel)
                    {
                        ModelCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            else
            {
                ModelCombo.SelectedIndex = 0; // Default
            }

            ModelSelector.Visibility = Visibility.Visible;
        }
        catch
        {
            // Silently hide the dropdown on any error
            ModelSelector.Visibility = Visibility.Collapsed;
        }
    }

    private void InfoKeyIcon_Click(object sender, MouseButtonEventArgs e)
    {
        InfoKeyPopup.IsOpen = !InfoKeyPopup.IsOpen;
        e.Handled = true;
    }

    private void GetApiKey_Click(object sender, RoutedEventArgs e)
    {
        string url = _isGeminiProvider
            ? "https://aistudio.google.com/app/apikey"
            : "https://accounts.x.ai";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        var title = TranslationSource.Get(_isGeminiProvider ? "GeminiClearTitle" : "GrokClearTitle");
        var confirm = TranslationSource.Get(_isGeminiProvider ? "GeminiClearConfirm" : "GrokClearConfirm");

        var result = MessageBox.Show(confirm, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (DataContext is MainViewModel vm)
            {
                if (_isGeminiProvider)
                {
                    vm.ClearGeminiKeyCommand.Execute(null);
                    MessageLabel.Text = TranslationSource.Get("GeminiCleared");
                }
                else
                {
                    vm.ClearGrokKeyCommand.Execute(null);
                    MessageLabel.Text = TranslationSource.Get("GrokCleared");
                }
                GeminiKeyBox.Password = string.Empty;
                MessageLabel.Foreground = System.Windows.Media.Brushes.Orange;
                ModelSelector.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            string errorKey = _isGeminiProvider ? "GeminiSaveError" : "GrokSaveError";
            MessageLabel.Text = TranslationSource.Fmt(errorKey, $"{ex.GetType().Name}: {ex.Message}");
            MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string key = GeminiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            string enterKey = _isGeminiProvider ? "GeminiEnterKey" : "GrokEnterKey";
            MessageLabel.Text = TranslationSource.Get(enterKey);
            MessageLabel.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        if (DataContext is not MainViewModel vm)
            return;

        // ── Show spinner & disable buttons during validation ──
        SetValidatingState(true);

        try
        {
            string verifyingKey = _isGeminiProvider ? "GeminiVerifying" : "GrokVerifying";
            MessageLabel.Text = TranslationSource.Get(verifyingKey);
            MessageLabel.Foreground = System.Windows.Media.Brushes.Gray;

            if (_isGeminiProvider)
            {
                var (isValid, errorMessage) = await vm.ValidateGeminiKeyAsync(key);

                if (!isValid)
                {
                    string invalidKey = _isGeminiProvider ? "GeminiInvalid" : "GrokInvalid";
                    MessageLabel.Text = errorMessage ?? TranslationSource.Get(invalidKey);
                    MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }

                // Key is valid — save it
                vm.GeminiKeyInput = key;
                vm.GeminiAvailable = true;

                // Save model selection if it's set
                if (ModelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                    selectedItem.Tag is string modelTag && !string.IsNullOrEmpty(modelTag))
                {
                    vm.GeminiModel = modelTag;
                }
                else
                {
                    vm.GeminiModel = ""; // Use default
                }

                vm.SaveGeminiKeyCommand.Execute(null);
            }
            else
            {
                var (isValid, errorMessage) = await vm.ValidateGrokKeyAsync(key);

                if (!isValid)
                {
                    MessageLabel.Text = errorMessage ?? TranslationSource.Get("GrokInvalid");
                    MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }

                // Key is valid — save it
                vm.GrokKeyInput = key;
                vm.GrokAvailable = true;

                // Save model selection if it's set
                if (ModelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                    selectedItem.Tag is string modelTag && !string.IsNullOrEmpty(modelTag))
                {
                    vm.GrokModel = modelTag;
                }
                else
                {
                    vm.GrokModel = ""; // Use default
                }

                vm.SaveGrokKeyCommand.Execute(null);
            }

            MessageLabel.Text = TranslationSource.Get(_isGeminiProvider ? "GeminiSaved" : "GrokSaved");
            MessageLabel.Foreground = System.Windows.Media.Brushes.LimeGreen;

            // ── Populate model dropdown now that key is saved ──
            _ = TryPopulateModelsAsync(vm, key);

            // ── Auto-close after successful save ──
            // Allow a brief moment for the user to register the success, then close.
            await Task.Delay(800);
            Close();
        }
        catch (Exception ex)
        {
            string errorKey2 = _isGeminiProvider ? "GeminiSaveError" : "GrokSaveError";
            MessageLabel.Text = TranslationSource.Fmt(errorKey2, $"{ex.GetType().Name}: {ex.Message}");
            MessageLabel.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            // ── Restore buttons (only if window is still open) ──
            if (IsLoaded)
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
