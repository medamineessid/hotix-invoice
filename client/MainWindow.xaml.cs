using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hotix.InvoiceClient.ViewModels;

namespace Hotix.InvoiceClient;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();

        // Set initial language radio button state
        string currentLang = TranslationSource.Instance.CurrentCulture;
        LangFrenchRadio.IsChecked = currentLang == "fr";
        LangEnglishRadio.IsChecked = currentLang == "en";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
        => ViewModel.Dispose();

    // ── Title Bar ──────────────────────────────────────────

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

    // ── Language Selection ────────────────────────────────────

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
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Hotix", "settings.json");
            var settings = new Dictionary<string, string> { ["language"] = culture };

            // Preserve any existing engine preference when saving language
            try
            {
                var existing = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (existing.RootElement.TryGetProperty("engine", out var engine))
                    settings["engine"] = engine.GetString() ?? "auto";
            }
            catch { /* file may not exist yet */ }

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(settings));
        }
        catch { /* best-effort */ }
    }

    // ── Drag & Drop ────────────────────────────────────────

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

    // ── Add Button Context Menu ────────────────────────────

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    // ── Sidebar Navigation ─────────────────────────────────

    private void NavExtraction_Click(object sender, MouseButtonEventArgs e)
    {
        SetActiveNav(NavExtraction, NavExtractionIndicator, NavExtractionText, true);
        SetActiveNav(NavSettings, NavSettingsIndicator, NavSettingsText, false);
        SetActiveNav(NavAbout, NavAboutIndicator, NavAboutText, false);

        // Show extraction content
        PageTitle.Text = TranslationSource.Get("NavPageExtraction");
        // Could show/hide different panels here if we had About/Settings pages
    }

    private void NavSettings_Click(object sender, MouseButtonEventArgs e)
    {
        SetActiveNav(NavExtraction, NavExtractionIndicator, NavExtractionText, false);
        SetActiveNav(NavSettings, NavSettingsIndicator, NavSettingsText, true);
        SetActiveNav(NavAbout, NavAboutIndicator, NavAboutText, false);

        PageTitle.Text = TranslationSource.Get("NavPageSettings");
        // Open Gemini setup dialog
        ViewModel.ToggleSettingsCommand.Execute(null);
        // Return to extraction after settings
        NavExtraction_Click(sender, e);
    }

    private void NavAbout_Click(object sender, MouseButtonEventArgs e)
    {
        SetActiveNav(NavExtraction, NavExtractionIndicator, NavExtractionText, false);
        SetActiveNav(NavSettings, NavSettingsIndicator, NavSettingsText, false);
        SetActiveNav(NavAbout, NavAboutIndicator, NavAboutText, true);

        PageTitle.Text = TranslationSource.Get("NavPageAbout");
        // Show a brief about state
        MessageBox.Show(
            TranslationSource.Get("AboutMessage"),
            TranslationSource.Get("AboutTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        // Return to extraction
        NavExtraction_Click(sender, e);
    }

    private static void SetActiveNav(Border navItem, Border indicator, TextBlock text, bool active)
    {
        if (active)
        {
            // Smooth transition: background fade
            var bgAnim = new ColorAnimation
            {
                To = ((SolidColorBrush)Application.Current.FindResource("BrushSelected")).Color,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            var bgBrush = new SolidColorBrush();
            navItem.Background = bgBrush;
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);

            // Indicator: height + opacity glide
            var heightAnim = new DoubleAnimation
            {
                To = 20,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            indicator.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);

            var opacityAnim = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            indicator.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            text.Style = (Style)Application.Current.FindResource("NavTextSelectedStyle");
        }
        else
        {
            // Smooth transition away
            var bgAnim = new ColorAnimation
            {
                To = Colors.Transparent,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            var bgBrush = new SolidColorBrush(Colors.Transparent);
            navItem.Background = bgBrush;
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);

            var heightAnim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            indicator.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);

            var opacityAnim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(100),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            indicator.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            text.Style = (Style)Application.Current.FindResource("NavTextStyle");
        }
    }

    // ── Staggered Row Animation ────────────────────────────

    private void ResultsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        int rowIndex = e.Row.GetIndex();
        double delayMs = rowIndex * 40.0; // 40ms stagger per row

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

    // ── Tab Switching (Results / Incomplete) ───────────────

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
}
