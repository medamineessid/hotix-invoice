using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hotix.InvoiceClient.ViewModels;

namespace Hotix.InvoiceClient;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ── Onboarding state ──────────────────────────────────────────────
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hotix", "settings.json");

    private int _currentOnboardingStep;
    private bool _onboardingCompleted;

    private (string TitleKey, string DescKey, Func<FrameworkElement?> TargetResolver)[] _onboardingSteps = null!;

    private void InitOnboardingSteps()
    {
        _onboardingSteps = new (string, string, Func<FrameworkElement?> TargetResolver)[]
        {
            ( "OnboardingStep1Title", "OnboardingStep1Desc", () => AddButton ),
            ( "OnboardingStep2Title", "OnboardingStep2Desc", () => EngineCombo ),
            ( "OnboardingStep3Title", "OnboardingStep3Desc", () => RunButton ),
            ( "OnboardingStep4Title", "OnboardingStep4Desc", () => ResultsGrid ),
            ( "OnboardingStep5Title", "OnboardingStep5Desc", () => ExportButton ),
        };
    }

    public MainWindow()
    {
        InitializeComponent();
        InitOnboardingSteps();
        Loaded  += OnLoaded;
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();

        // Keep the results section at least as tall as the combined height of Steps 1 and 2
        MainContentGrid.SizeChanged += OnMainContentGrid_SizeChanged;

        // Set initial language radio button state
        string currentLang = TranslationSource.Instance.CurrentCulture;
        LangFrenchRadio.IsChecked = currentLang == "fr";
        LangEnglishRadio.IsChecked = currentLang == "en";
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        // Check onboarding (after ~500ms delay for the window to settle)
        await Task.Delay(500);
        CheckOnboarding();

        // Ensure results section has a minimum height equal to the combined height of Steps 1 and 2
        UpdateResultsMinHeight();

        // Check for updates (non-blocking)
        _ = CheckForUpdateAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Clean up event handlers
        SizeChanged -= Onboarding_SizeChanged;
        MainContentGrid.SizeChanged -= OnMainContentGrid_SizeChanged;
        ViewModel.Dispose();
    }

    /// <summary>
    /// Re-positions the spotlight + callout when the window is resized during onboarding.
    /// </summary>
    private void Onboarding_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_onboardingSteps == null) return;
        if (OnboardingOverlay.Visibility != Visibility.Visible) return;
        if (_currentOnboardingStep < 0 || _currentOnboardingStep >= _onboardingSteps.Length) return;

        // Re-show current step to recalculate position
        ShowOnboardingStep(_currentOnboardingStep);
    }

    // ── Title Bar ────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed &&
            WindowState != WindowState.Maximized)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    // ── Language Selection ────────────────────────────────────────────

    private void LangFrench_Checked(object sender, RoutedEventArgs e)
    {
        TranslationSource.Instance.CurrentCulture = "fr";
        SaveLanguagePreference("fr");
    }

    private void LangEnglish_Checked(object sender, RoutedEventArgs e)
    {
        TranslationSource.Instance.CurrentCulture = "en";
        SaveLanguagePreference("en");
    }

    private static void SaveLanguagePreference(string culture)
    {
        try
        {
            string settingsPath = SettingsPath;
            var settings = new Dictionary<string, string> { ["language"] = culture };

            // Preserve any existing engine preference when saving language
            try
            {
                var existing = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (existing.RootElement.TryGetProperty("engine", out var engine))
                    settings["engine"] = engine.GetString() ?? "auto";
                if (existing.RootElement.TryGetProperty("update_last_check", out var update))
                    settings["update_last_check"] = update.GetString() ?? "";
            }
            catch { /* file may not exist yet */ }

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
        }
        catch { /* best-effort */ }
    }

    // ── Drag & Drop ──────────────────────────────────────────────────

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        string? folder = paths.FirstOrDefault(Directory.Exists);
        if (folder != null)
            ViewModel.SetFolderFromDrop(folder);
    }

    // ── Add Button Context Menu ──────────────────────────────────────

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    // ── Sidebar Navigation ───────────────────────────────────────────

    private void NavExtraction_Click(object sender, MouseButtonEventArgs e)
    {
        SetActiveNav(NavExtraction, NavExtractionIcon, NavExtractionText, true);
        SetActiveNav(NavAbout, NavAboutIcon, NavAboutText, false);
    }


    private void NavAbout_Click(object sender, MouseButtonEventArgs e)
    {
        SetActiveNav(NavExtraction, NavExtractionIcon, NavExtractionText, false);
        SetActiveNav(NavAbout, NavAboutIcon, NavAboutText, true);

        MessageBox.Show(
            TranslationSource.Fmt("AboutMessage", TranslationSource.Get("SidebarVersion")),
            TranslationSource.Get("AboutTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        NavExtraction_Click(sender, e);
    }

    private static void SetActiveNav(Border navItem, TextBlock icon, TextBlock text, bool active)
    {
        if (active)
        {
            navItem.Style = (Style)Application.Current.FindResource("NavItemStyleActive");
            icon.Style = (Style)Application.Current.FindResource("NavIconActiveStyle");
            text.Style = (Style)Application.Current.FindResource("NavTextSelectedStyle");
        }
        else
        {
            navItem.Style = (Style)Application.Current.FindResource("NavItemStyle");
            icon.Style = (Style)Application.Current.FindResource("NavIconStyle");
            text.Style = (Style)Application.Current.FindResource("NavTextStyle");
        }
    }

    // ── Results section min-height sync ─────────────────────────────

    /// <summary>
    /// Ensures the results section is at least as tall as the combined height of Step 1 and Step 2.
    /// This prevents the results grid from looking tiny when there are few extracted rows.
    /// </summary>
    private void UpdateResultsMinHeight()
    {
        if (StepOneCard == null || StepTwoCard == null || ResultsSection == null)
            return;

        double step1Height = StepOneCard.ActualHeight;
        double step2Height = StepTwoCard.ActualHeight;

        // Account for margin gap between the two cards (margin-bottom on Step 1 card)
        double gap = StepOneCard.Margin.Bottom;
        double combined = step1Height + step2Height + gap;

        if (combined > 0)
        {
            ResultsSection.MinHeight = combined;
        }
    }

    private void OnMainContentGrid_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateResultsMinHeight();
    }

    // ── Staggered Row Animation ──────────────────────────────────────

    private void ResultsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        int rowIndex = e.Row.GetIndex();
        double delayMs = rowIndex * 40.0;

        e.Row.Opacity = 0;
        e.Row.RenderTransform = new TranslateTransform(0, 6);

        var fadeIn = new DoubleAnimation
        {
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        e.Row.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        var slideUp = new DoubleAnimation
        {
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        e.Row.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    // ── Info Button Click Handler ─────────────────────────────────────

    private void InfoButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            if (element.Parent is Panel parent)
            {
                foreach (var child in parent.Children)
                {
                    if (child is Popup popup)
                    {
                        popup.IsOpen = !popup.IsOpen;
                        e.Handled = true;
                        return;
                    }
                }
            }
        }
    }

    // ── Tab Switching (Results / Incomplete) ─────────────────────────

    private void TabResults_Click(object sender, MouseButtonEventArgs e)
    {
        SetActiveTab(true);
    }

    private void TabIncomplete_Click(object sender, MouseButtonEventArgs e)
    {
        SetActiveTab(false);
    }

    private void SetActiveTab(bool showResults)
    {
        if (showResults)
        {
            PanelResults.Visibility = Visibility.Visible;
            PanelIncomplete.Visibility = Visibility.Collapsed;
            TabResultsText.FontWeight = FontWeights.Medium;
            TabResultsText.Foreground = (Brush)Application.Current.FindResource("BrushTextPrimary");
            TabResultsUnderline.Background = (Brush)Application.Current.FindResource("BrushAccent");
            TabIncompleteText.FontWeight = FontWeights.Normal;
            TabIncompleteText.Foreground = (Brush)Application.Current.FindResource("BrushTextMuted");
            TabIncompleteUnderline.Background = Brushes.Transparent;
        }
        else
        {
            PanelResults.Visibility = Visibility.Collapsed;
            PanelIncomplete.Visibility = Visibility.Visible;
            TabIncompleteText.FontWeight = FontWeights.Medium;
            TabIncompleteText.Foreground = (Brush)Application.Current.FindResource("BrushTextPrimary");
            TabIncompleteUnderline.Background = (Brush)Application.Current.FindResource("BrushAccent");
            TabResultsText.FontWeight = FontWeights.Normal;
            TabResultsText.Foreground = (Brush)Application.Current.FindResource("BrushTextMuted");
            TabResultsUnderline.Background = Brushes.Transparent;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //   ONBOARDING
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if onboarding should be shown (first run only, persisted to appsettings.json).
    /// </summary>
    private void CheckOnboarding()
    {
        if (_onboardingCompleted) return;

        // Check if onboarding was already completed
        try
        {
            string path = ServerPathResolver.ResolveAppSettingsPath();
            if (File.Exists(path))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("onboarding_completed", out var el) && el.GetBoolean())
                {
                    _onboardingCompleted = true;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hotix] Failed to read onboarding status: {ex.GetType().Name}: {ex.Message}");
        }

        // Subscribe to SizeChanged for repositioning during onboarding
        SizeChanged += Onboarding_SizeChanged;

        _currentOnboardingStep = 0;
        ShowOnboardingStep(0);
    }

    /// <summary>
    /// Shows the onboarding step at the given index with fade-in animation.
    /// </summary>
    private void ShowOnboardingStep(int stepIndex)
    {
        if (stepIndex < 0 || stepIndex >= _onboardingSteps.Length)
        {
            HideOnboarding();
            return;
        }

        _currentOnboardingStep = stepIndex;
        var step = _onboardingSteps[stepIndex];

        // Update callout content
        OnboardingStepLabel.Text = TranslationSource.Get(step.TitleKey);
        OnboardingTitle.Text = TranslationSource.Get(step.TitleKey);
        OnboardingDesc.Text = TranslationSource.Get(step.DescKey);

        // Update step counter in label
        OnboardingStepLabel.Text = $"{stepIndex + 1} / {_onboardingSteps.Length}";

        // Try to position spotlight around the target element
        var target = step.TargetResolver();
        if (target != null && target.IsVisible)
        {
            try
            {
                var point = target.TransformToAncestor(this).Transform(new Point(0, 0));
                double w = target.ActualWidth;
                double h = target.ActualHeight;

                Canvas.SetLeft(OnboardingSpotlight, point.X - 6);
                Canvas.SetTop(OnboardingSpotlight, point.Y - 6);
                OnboardingSpotlight.Width = w + 12;
                OnboardingSpotlight.Height = h + 12;
                OnboardingSpotlight.Opacity = 1;

                // Position callout below the spotlight, clamped to window bounds
                double calloutX = point.X;
                double calloutY = point.Y + h + 20;

                // Clamp to keep callout within window bounds (320 = callout width + margin)
                double windowW = ActualWidth > 0 ? ActualWidth : 1280;
                double windowH = ActualHeight > 0 ? ActualHeight : 820;

                if (calloutX + 340 > windowW)
                    calloutX = windowW - 340;
                if (calloutX < 8)
                    calloutX = 8;

                // Keep callout below the title bar row (48px)
                if (calloutY < 56)
                    calloutY = 56;

                // If callout would go below window, flip it above the target
                if (calloutY + 200 > windowH)
                {
                    calloutY = point.Y - 180; // above the spotlight
                    if (calloutY < 56)
                        calloutY = 56; // still too high? clamp to title bar bottom
                }

                Canvas.SetLeft(OnboardingCallout, calloutX);
                Canvas.SetTop(OnboardingCallout, calloutY);
            }
            catch
            {
                // Fallback to center positioning
                CenterOnboardingCallout();
            }
        }
        else
        {
            CenterOnboardingCallout();
        }

        // Show the overlay with fade animation
        if (OnboardingOverlay.Visibility != Visibility.Visible)
        {
            OnboardingOverlay.Visibility = Visibility.Visible;
            OnboardingOverlay.Opacity = 0;
        }

        var fadeInAnim = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        OnboardingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeInAnim);
        OnboardingCallout.BeginAnimation(UIElement.OpacityProperty, fadeInAnim);
    }

    /// <summary>
    /// Centers the callout in the window as a fallback when no target element is available.
    /// </summary>
    private void CenterOnboardingCallout()
    {
        double overlayWidth = OnboardingOverlay.ActualWidth > 0 ? OnboardingOverlay.ActualWidth : 1200;
        double overlayHeight = OnboardingOverlay.ActualHeight > 0 ? OnboardingOverlay.ActualHeight : 800;

        double centerX = (overlayWidth - 320) / 2;
        double centerY = overlayHeight / 3;

        // Clamp to window bounds
        double windowW = ActualWidth > 0 ? ActualWidth : 1280;
        double windowH = ActualHeight > 0 ? ActualHeight : 820;

        if (centerX + 340 > windowW)
            centerX = windowW - 340;
        if (centerX < 8)
            centerX = 8;
        if (centerY < 56)
            centerY = 56;
        if (centerY + 200 > windowH)
            centerY = windowH - 220;

        Canvas.SetLeft(OnboardingCallout, centerX);
        Canvas.SetTop(OnboardingCallout, centerY);

        OnboardingSpotlight.Opacity = 0; // Hide spotlight
    }

    /// <summary>
    /// Hides the onboarding overlay with fade-out animation.
    /// </summary>
    private void HideOnboarding()
    {
        if (OnboardingOverlay.Visibility != Visibility.Visible) return;

        // Unsubscribe SizeChanged handler
        SizeChanged -= Onboarding_SizeChanged;

        var fadeOutAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };

        fadeOutAnim.Completed += (_, _) =>
        {
            OnboardingOverlay.Visibility = Visibility.Collapsed;
        };

        OnboardingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOutAnim);
    }

    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOnboardingStep >= _onboardingSteps.Length - 1)
        {
            // Last step — complete onboarding
            CompleteOnboarding();
            return;
        }

        ShowOnboardingStep(_currentOnboardingStep + 1);
    }

    private void OnboardingSkip_Click(object sender, RoutedEventArgs e)
    {
        CompleteOnboarding();
    }

    /// <summary>
    /// Marks onboarding as completed and persists the flag to appsettings.json.
    /// </summary>
    private void CompleteOnboarding()
    {
        HideOnboarding();
        _onboardingCompleted = true;

        // Persist to appsettings.json
        try
        {
            string path = ServerPathResolver.ResolveAppSettingsPath();
            Dictionary<string, object> settings;

            if (File.Exists(path))
            {
                var existing = JsonDocument.Parse(File.ReadAllText(path));
                settings = new Dictionary<string, object>();

                foreach (var prop in existing.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        settings[prop.Name] = prop.Value.GetString() ?? "";
                    else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        settings[prop.Name] = prop.Value.GetBoolean();
                    else
                        settings[prop.Name] = prop.Value.GetRawText();
                }
            }
            else
            {
                settings = new Dictionary<string, object>();
            }

            settings["onboarding_completed"] = true;
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

            Debug.WriteLine($"[Hotix] Onboarding completed — flag written to {path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hotix] Failed to persist onboarding_completed to {ServerPathResolver.ResolveAppSettingsPath()}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //   UPDATE CHECK
    // ══════════════════════════════════════════════════════════════════

    private const string GitHubReleasesUrl = "https://api.github.com/repos/medamineessid/hotix-invoice/releases/latest";
    private static string AppVersion => BuildInfo.AppVersion;

    /// <summary>
    /// Checks GitHub Releases API for a newer version. Caches check time — only checks once per day.
    /// Shows a dismissible notification bar if a newer version is found.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            // Check cache: only check once per day
            if (WasUpdateCheckedRecently())
                return;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Hotix-Invoice/1.0");

            var response = await client.GetAsync(GitHubReleasesUrl);
            if (!response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            string? latestTag = doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                ? tagEl.GetString()
                : null;

            string? releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? urlEl.GetString()
                : null;

            if (string.IsNullOrEmpty(latestTag) || string.IsNullOrEmpty(releaseUrl))
                return;

            // Compare versions (simple string comparison — assumes semantic versioning with v prefix)
            string currentTag = $"v{AppVersion}";
            bool isNewer = CompareVersions(latestTag, currentTag) > 0;

            if (isNewer)
            {
                ShowUpdateNotification(latestTag, releaseUrl);
            }
        }
        catch
        {
            // Silently fail — this is a non-critical feature
        }
        finally
        {
            // Update last check time regardless of success/failure
            SaveUpdateCheckTime();
        }
    }

    private static bool WasUpdateCheckedRecently()
    {
        try
        {
            string settingsPath = SettingsPath;
            if (!File.Exists(settingsPath)) return false;

            var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!doc.RootElement.TryGetProperty("update_last_check", out var el))
                return false;

            string? lastCheck = el.GetString();
            if (string.IsNullOrEmpty(lastCheck)) return false;

            if (DateTime.TryParse(lastCheck, out var lastCheckDate))
            {
                // Only check once per day
                return (DateTime.UtcNow - lastCheckDate).TotalHours < 24;
            }
        }
        catch { }

        return false;
    }

    private static void SaveUpdateCheckTime()
    {
        try
        {
            string settingsPath = SettingsPath;
            var existing = File.Exists(settingsPath)
                ? JsonDocument.Parse(File.ReadAllText(settingsPath))
                : null;

            var settings = new Dictionary<string, object>();

            if (existing != null)
            {
                foreach (var prop in existing.RootElement.EnumerateObject())
                {
                    if (prop.Name == "update_last_check") continue;
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        settings[prop.Name] = prop.Value.GetString() ?? "";
                    else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        settings[prop.Name] = prop.Value.GetBoolean();
                    else
                        settings[prop.Name] = prop.Value.GetRawText();
                }
            }

            settings["update_last_check"] = DateTime.UtcNow.ToString("o");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static int CompareVersions(string v1, string v2)
    {
        // Strip "v" prefix and compare
        string a = v1.TrimStart('v');
        string b = v2.TrimStart('v');

        var partsA = a.Split('.');
        var partsB = b.Split('.');

        for (int i = 0; i < Math.Max(partsA.Length, partsB.Length); i++)
        {
            int numA = i < partsA.Length && int.TryParse(partsA[i], out var na) ? na : 0;
            int numB = i < partsB.Length && int.TryParse(partsB[i], out var nb) ? nb : 0;

            if (numA > numB) return 1;
            if (numA < numB) return -1;
        }

        return 0;
    }

    private string? _updateReleaseUrl;
    private bool _updateDismissed;

    private void ShowUpdateNotification(string latestVersion, string releaseUrl)
    {
        if (_updateDismissed) return;

        _updateReleaseUrl = releaseUrl;
        UpdateNotificationText.Text = TranslationSource.Fmt("UpdateCheckNewVersion", latestVersion, $"v{AppVersion}");
        UpdateNotification.Visibility = Visibility.Visible;
    }

    private void UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_updateReleaseUrl))
        {
            Process.Start(new ProcessStartInfo(_updateReleaseUrl) { UseShellExecute = true });
        }
        UpdateNotification.Visibility = Visibility.Collapsed;
        _updateDismissed = true;
    }

    private void UpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        UpdateNotification.Visibility = Visibility.Collapsed;
        _updateDismissed = true;
    }
}
