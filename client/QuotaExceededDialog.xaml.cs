using System.Windows;

namespace Hotix.InvoiceClient;

/// <summary>What the user decided in the quota dialog.</summary>
public enum QuotaDialogChoice
{
    /// <summary>User wants to enter a new API key in the setup window.</summary>
    EnterNewKey,

    /// <summary>User wants to keep processing with local OCR.</summary>
    ContinueWithOcr,
}

/// <summary>
/// Modal dialog shown on the first API-quota (429) detection of a batch.
/// Offers two clear choices — enter a new key or continue with local OCR —
/// plus an optional "remember for this session" checkbox so the dialog doesn't
/// reappear on every subsequent batch.
/// </summary>
public partial class QuotaExceededDialog : Window
{
    private readonly bool _isGemini;

    /// <summary>The user's decision (defaults to ContinueWithOcr).</summary>
    public QuotaDialogChoice Choice { get; private set; } = QuotaDialogChoice.ContinueWithOcr;

    /// <summary>True when the user asked to remember the choice for the whole session.</summary>
    public bool RememberForSession { get; private set; }

    public QuotaExceededDialog(bool isGemini)
    {
        InitializeComponent();
        _isGemini = isGemini;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        string provider = _isGemini
            ? TranslationSource.Get("ProviderGemini")
            : TranslationSource.Get("ProviderGrok");

        TitleText.Text = TranslationSource.Fmt("QuotaDialogTitleText", provider);
        BodyText.Text = TranslationSource.Fmt("QuotaDialogBody", provider);
    }

    private void EnterKey_Click(object sender, RoutedEventArgs e)
    {
        Choice = QuotaDialogChoice.EnterNewKey;
        RememberForSession = RememberCheck.IsChecked == true;
        DialogResult = true;
    }

    private void ContinueOcr_Click(object sender, RoutedEventArgs e)
    {
        Choice = QuotaDialogChoice.ContinueWithOcr;
        RememberForSession = RememberCheck.IsChecked == true;
        DialogResult = true;
    }
}
