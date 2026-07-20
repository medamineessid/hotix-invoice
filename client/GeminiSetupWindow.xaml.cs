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

    /// <summary>Known default models shown even before API validation.</summary>
    private static readonly Dictionary<string, (string label, string tooltip)> DefaultModels = new()
    {
        { "gemini-2.5-flash", ("Gemini 2.5 Flash (Recommended)", "Fastest model. Optimized for invoice extraction. Low cost.") },
        { "gemini-2.0-flash", ("Gemini 2.0 Flash", "Previous generation. Still accurate. Slightly slower.") },
        { "gemini-1.5-pro", ("Gemini 1.5 Pro", "Most capable. Highest cost. Best for complex documents.") },
        { "grok-4.3", ("Grok 4.3 (Recommended)", "Alternative provider. Similar quality to Gemini. Good fallback.") },
        { "grok-3.0", ("Grok 3.0", "Previous Grok version. Still works.") },
    };

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
            // (UpdateKeyBoxFromProvider handles showing masked dots + "✓ Saved" badge)
            UpdateKeyBoxFromProvider();

            // Pre-populate models immediately (not waiting for API validation)
            PopulateDefaultModels();

            // If a key is already configured, also try API-populated models (async)
            string currentKey = _isGeminiProvider ? vm.GeminiKeyInput : vm.GrokKeyInput;
            if (!string.IsNullOrEmpty(currentKey))
            {
                _ = TryPopulateModelsAsync(vm, currentKey);
            }
        }

        Activate();
        GeminiKeyBox.Focus();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Don't save the masked placeholder ("••••••••") back to the ViewModel —
        // the real key is already saved via Save_Click or was loaded from settings.
        // Only save a real (non-placeholder) value if the user typed something.
        if (DataContext is MainViewModel vm &&
            !string.IsNullOrEmpty(GeminiKeyBox.Password) &&
            GeminiKeyBox.Password != "••••••••")
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

        // Repopulate model list for the new provider
        PopulateDefaultModels();
    }

    private void UpdateKeyBoxFromProvider()
    {
        if (DataContext is MainViewModel vm)
        {
            string key = _isGeminiProvider ? vm.GeminiKeyInput : vm.GrokKeyInput;
            if (!string.IsNullOrEmpty(key))
            {
                GeminiKeyBox.Password = "••••••••";
                KeyStatusIcon.Text = "✓ Saved";
                KeyStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("BrushSuccess");
            }
            else
            {
                GeminiKeyBox.Password = string.Empty;
                KeyStatusIcon.Text = string.Empty;
            }
        }
        MessageLabel.Text = string.Empty;
    }

    /// <summary>
    /// Populates the model dropdown with known default models for the current provider.
    /// Called on window load and when the provider switches.
    /// </summary>
    private void PopulateDefaultModels()
    {
        ModelCombo.Items.Clear();

        foreach (var kvp in DefaultModels)
        {
            string modelId = kvp.Key;
            var (label, tooltip) = kvp.Value;

            // Only show models matching the current provider
            if ((_isGeminiProvider && modelId.StartsWith("gemini")) ||
                (!_isGeminiProvider && modelId.StartsWith("grok")))
            {
                string display = label;
                string toolTipText = tooltip;

                // Mark the default/recommended model
                if (modelId == "gemini-2.5-flash" || modelId == "grok-4.3")
                {
                    display = "⭐ " + label;
                }

                var item = new System.Windows.Controls.ComboBoxItem
                {
                    Content = display,
                    Tag = modelId,
                    ToolTip = toolTipText,
                };
                ModelCombo.Items.Add(item);
            }
        }

        // Select the first model (recommended)
        if (ModelCombo.Items.Count > 0)
        {
            ModelCombo.SelectedIndex = 0;
        }

        ModelSelector.Visibility = System.Windows.Visibility.Visible;
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

        // Repopulate model dropdown
        PopulateDefaultModels();

        // If key is present, also try API-populated models (async)
        if (DataContext is MainViewModel vm2)
        {
            string key = _isGeminiProvider ? vm2.GeminiKeyInput : vm2.GrokKeyInput;
            if (!string.IsNullOrEmpty(key))
            {
                _ = TryPopulateModelsAsync(vm2, key);
            }
        }
    }

    /// <summary>
    /// Fetches available models for the current provider and merges them into the dropdown.
    /// Default models are already populated by PopulateDefaultModels() — this method
    /// supplements with API-returned models (Gemini) or hardcoded known models (Grok).
    /// If the API call fails, the pre-populated defaults remain visible.
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
                // No models from API — keep the pre-populated defaults visible
                return;
            }

            // Remember the currently selected model before clearing
            string currentModel = _isGeminiProvider ? vm.GeminiModel : vm.GrokModel;
            string? selectedTag = ModelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem currentItem
                ? currentItem.Tag as string
                : null;

            // Populate the dropdown with API models
            ModelCombo.Items.Clear();

            foreach (var model in models.OrderBy(m => m))
            {
                var item = new System.Windows.Controls.ComboBoxItem
                {
                    Content = model,
                    Tag = model,
                };
                ModelCombo.Items.Add(item);
            }

            // Select the current model if it's in the API list, otherwise pick first
            if (!string.IsNullOrEmpty(currentModel))
            {
                bool found = false;
                foreach (System.Windows.Controls.ComboBoxItem item in ModelCombo.Items)
                {
                    if (item.Tag is string tag && tag == currentModel)
                    {
                        ModelCombo.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
                if (!found && ModelCombo.Items.Count > 0)
                    ModelCombo.SelectedIndex = 0;
            }
            else if (ModelCombo.Items.Count > 0)
            {
                ModelCombo.SelectedIndex = 0;
            }

            // Dropdown is already visible from PopulateDefaultModels, keep it visible
        }
        catch
        {
            // API call failed — keep the pre-populated defaults visible
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
                KeyStatusIcon.Text = string.Empty;
                MessageLabel.Foreground = System.Windows.Media.Brushes.Orange;
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

        // If the masked placeholder is shown, use the already-saved real key
        if (key == "••••••••" && DataContext is MainViewModel existingVm)
        {
            key = _isGeminiProvider ? existingVm.GeminiKeyInput : existingVm.GrokKeyInput;
        }

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

            // Show saved indicator
            GeminiKeyBox.Password = "••••••••";
            KeyStatusIcon.Text = "✓ Saved";
            KeyStatusIcon.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSuccess");

            // ── Populate model dropdown now that key is saved ──
            _ = TryPopulateModelsAsync(vm, key);

            // ── Auto-close after successful save ──
            // Show toast briefly, then close.
            await Task.Delay(1000);
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

    // ── PasswordBox show/hide toggle ──────────────────────────────────

    private void KeyVisibilityToggle_Click(object sender, RoutedEventArgs e)
    {
        if (KeyVisibilityToggle.IsChecked == true)
        {
            // Show the key as plain text
            GeminiKeyTextBox.Text = GeminiKeyBox.Password;
            GeminiKeyBox.Visibility = Visibility.Collapsed;
            GeminiKeyTextBox.Visibility = Visibility.Visible;
            GeminiKeyTextBox.Focus();
            GeminiKeyTextBox.SelectionStart = GeminiKeyTextBox.Text.Length;
            ToggleEyeIcon.Text = "👁";
        }
        else
        {
            // Hide the key back to password mode
            GeminiKeyBox.Password = GeminiKeyTextBox.Text;
            GeminiKeyTextBox.Visibility = Visibility.Collapsed;
            GeminiKeyBox.Visibility = Visibility.Visible;
            GeminiKeyBox.Focus();
            ToggleEyeIcon.Text = "👁";
        }
    }

    private void GeminiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Clear status icon when key changes (unless showing masked placeholder)
        if (GeminiKeyBox.Password != "••••••••")
        {
            KeyStatusIcon.Text = string.Empty;
        }
    }

    private void GeminiKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        KeyStatusIcon.Text = string.Empty;
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
