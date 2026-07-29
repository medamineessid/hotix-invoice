using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Hotix.InvoiceClient;

namespace Hotix.InvoiceClient.ViewModels;

public sealed class InvoiceRowViewModel : INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    private string _fileName = string.Empty;
    private string? _numeroFacture;
    private string? _date;
    private string? _fournisseur;
    private string? _client;
    private string? _montantHt;
    private string? _montantTva;
    private string? _montantTaxe;
    private string? _montantTtc;
    private string _engineUsed = "ocr";
    private double _confidence;
    private string? _rawText;
    private bool _hasError;
    private string? _errorMessage;
    private bool _isSelected;
    private string? _geminiFallbackReason;
    private HashSet<string> _computedFields = new();
    private bool _amountMismatch;
    private string _invoiceDirection = string.Empty;
    private int _itemsCount;
    private bool _areItemsExpanded;
    private List<InvoiceItem> _items = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Full absolute path to the file on disk. Set once at creation and used directly for retry operations.</summary>
    public string FilePath
    {
        get => _filePath;
        set => SetField(ref _filePath, value);
    }

    public string FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value);
    }

    public string? NumeroFacture
    {
        get => _numeroFacture;
        set => SetField(ref _numeroFacture, value);
    }

    public string? Date
    {
        get => _date;
        set => SetField(ref _date, value);
    }

    public string? Fournisseur
    {
        get => _fournisseur;
        set
        {
            if (SetField(ref _fournisseur, value))
                AutoDetectDirection();
        }
    }

    public string? Client
    {
        get => _client;
        set
        {
            if (SetField(ref _client, value))
                AutoDetectDirection();
        }
    }

    public string? MontantHt
    {
        get => _montantHt;
        set => SetField(ref _montantHt, value);
    }

    public string? MontantTva
    {
        get => _montantTva;
        set
        {
            if (SetField(ref _montantTva, value))
                CheckVatCrossCheck();
        }
    }

    public string? MontantTaxe
    {
        get => _montantTaxe;
        set => SetField(ref _montantTaxe, value);
    }

    public string? MontantTtc
    {
        get => _montantTtc;
        set => SetField(ref _montantTtc, value);
    }

    public string EngineUsed
    {
        get => _engineUsed;
        set
        {
            if (SetField(ref _engineUsed, value))
            {
                OnPropertyChanged(nameof(IsLocalOcr));
            }
        }
    }

    public bool IsLocalOcr => _engineUsed == "ocr";

    public double Confidence
    {
        get => _confidence;
        set
        {
            if (SetField(ref _confidence, value))
            {
                OnPropertyChanged(nameof(ConfidenceDisplay));
                OnPropertyChanged(nameof(ConfidenceTooltip));
            }
        }
    }

    public string? RawText
    {
        get => _rawText;
        set => SetField(ref _rawText, value);
    }

    public bool HasError
    {
        get => _hasError;
        set
        {
            if (SetField(ref _hasError, value))
            {
                OnPropertyChanged(nameof(FileDisplay));
                OnPropertyChanged(nameof(IsIncomplete));
                OnPropertyChanged(nameof(IsError));
            }
        }
    }

    public bool IsError => HasError;

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetField(ref _errorMessage, value))
                OnPropertyChanged(nameof(FileDisplay));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string? GeminiFallbackReason
    {
        get => _geminiFallbackReason;
        set
        {
            if (SetField(ref _geminiFallbackReason, value))
                OnPropertyChanged(nameof(HasGeminiFallback));
        }
    }

    public bool HasGeminiFallback => !string.IsNullOrEmpty(_geminiFallbackReason);

    public HashSet<string> ComputedFields
    {
        get => _computedFields;
        set
        {
            if (SetField(ref _computedFields, value))
            {
                OnPropertyChanged(nameof(IsComputed));
            }
        }
    }

    public bool AmountMismatch
    {
        get => _amountMismatch;
        set
        {
            if (SetField(ref _amountMismatch, value))
            {
                OnPropertyChanged(nameof(HasAmountMismatch));
            }
        }
    }

    public bool HasAmountMismatch => _amountMismatch;

    /// <summary>
    /// Invoice direction: "received" (Hotix received this from a supplier),
    /// "issued" (Hotix issued this to its own client), or "" (unset).
    /// User can cycle through values by clicking the badge in the list.
    /// </summary>
    public string InvoiceDirection
    {
        get => _invoiceDirection;
        set
        {
            if (SetField(ref _invoiceDirection, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DirectionDisplay));
                OnPropertyChanged(nameof(DirectionBadgeColor));
                OnPropertyChanged(nameof(DirectionBadgeBg));
                OnPropertyChanged(nameof(DirectionIcon));
                OnPropertyChanged(nameof(DirectionTooltip));
            }
        }
    }

    /// <summary>Display text for the direction badge (from translations).</summary>
    public string DirectionDisplay => _invoiceDirection switch
    {
        "received" => TranslationSource.Get("DirectionReceived"),
        "issued"   => TranslationSource.Get("DirectionIssued"),
        _          => "—",
    };

    /// <summary>Text color for the direction badge.</summary>
    public string DirectionBadgeColor => _invoiceDirection switch
    {
        "received" => "#2E7D32",
        "issued"   => "#1565C0",
        _          => "#8A8A8A",
    };

    /// <summary>Background color for the direction badge.</summary>
    public string DirectionBadgeBg => _invoiceDirection switch
    {
        "received" => "#E8F5E9",
        "issued"   => "#E3F2FD",
        _          => "#F0EFEA",
    };

    /// <summary>Icon for the direction badge.</summary>
    public string DirectionIcon => _invoiceDirection switch
    {
        "received" => "↓",
        "issued"   => "↑",
        _          => "○",
    };

    /// <summary>Tooltip for the direction badge (from translations).</summary>
    public string DirectionTooltip => _invoiceDirection switch
    {
        "received" => TranslationSource.Get("DirectionTooltipReceived"),
        "issued"   => TranslationSource.Get("DirectionTooltipIssued"),
        _          => TranslationSource.Get("DirectionTooltipUnset"),
    };

    /// <summary>Cycle direction: unset → received → issued → unset.</summary>
    public void CycleDirection()
    {
        InvoiceDirection = _invoiceDirection switch
        {
            ""        => "received",
            "received" => "issued",
            "issued"   => "",
            _         => "",
        };
    }

    /// <summary>
    /// Auto-detect invoice direction from Fournisseur/Client fields.
    /// Only applies when direction is currently unset (user hasn't manually set it).
    /// If "Hotix" (case-insensitive) appears in Fournisseur → "issued"
    /// If "Hotix" appears in Client → "received"
    /// If in neither or both (ambiguous) → leave unset.
    /// </summary>
    public void AutoDetectDirection()
    {
        // Never override a manually-set direction
        if (!string.IsNullOrEmpty(_invoiceDirection))
            return;

        // Don't auto-detect until BOTH Fournisseur and Client have been populated.
        // This prevents a premature decision when FromSuccess sets Fournisseur first
        // (triggering this setter side-effect) before Client has been assigned.
        if (string.IsNullOrEmpty(_fournisseur) || string.IsNullOrEmpty(_client))
            return;

        bool hotixInFournisseur = _fournisseur.Contains("Hotix", StringComparison.OrdinalIgnoreCase);
        bool hotixInClient = _client.Contains("Hotix", StringComparison.OrdinalIgnoreCase);

        if (hotixInFournisseur && !hotixInClient)
            InvoiceDirection = "issued";
        else if (hotixInClient && !hotixInFournisseur)
            InvoiceDirection = "received";
        // else: ambiguous (both or neither) — leave as unset
    }

    // ── Items / Line-Articles (UI placeholder for future item-level data) ──

    /// <summary>Number of line items detected. 0 means no item data available yet.</summary>
    public int ItemsCount
    {
        get => _itemsCount;
        set
        {
            if (SetField(ref _itemsCount, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ItemsCountDisplay));
                OnPropertyChanged(nameof(ItemsHeaderText));
            }
        }
    }

    /// <summary>True when item-level data exists (itemsCount > 0).</summary>
    public bool HasItems => _itemsCount > 0;

    /// <summary>Display string for item count: number or "—" when no data.</summary>
    public string ItemsCountDisplay => _itemsCount > 0 ? _itemsCount.ToString() : "—";

    /// <summary>Header text for the collapsible articles section.</summary>
    public string ItemsHeaderText => TranslationSource.Fmt("ArticlesHeader", ItemsCountDisplay);

    /// <summary>The actual item list from extraction.</summary>
    public List<InvoiceItem> Items
    {
        get => _items;
        set
        {
            if (SetField(ref _items, value ?? new List<InvoiceItem>()))
            {
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ItemsCount));
                OnPropertyChanged(nameof(ItemsCountDisplay));
                OnPropertyChanged(nameof(ItemsHeaderText));
                CheckVatCrossCheck();
            }
        }
    }

    /// <summary>Expand/collapse state for the articles sub-panel.</summary>
    public bool AreItemsExpanded
    {
        get => _areItemsExpanded;
        set => SetField(ref _areItemsExpanded, value);
    }

    /// <summary>Toggle the articles expand/collapse state.</summary>
    public void ToggleItemsExpanded() => AreItemsExpanded = !AreItemsExpanded;

    /// <summary>
    /// Cross-check item-level VAT against the invoice-level MontantTva field.
    /// Sets VatMismatch flag if they disagree beyond tolerance (~1% or 0.50).
    /// </summary>
    public bool VatMismatch { get; private set; }

    private void CheckVatCrossCheck()
    {
        VatMismatch = false;
        if (_items.Count == 0 || string.IsNullOrWhiteSpace(_montantTva))
            return;

        // Sum item-level VAT where both tva_rate and montant are present
        double sumItemVat = 0.0;
        bool hasItemVat = false;
        foreach (var item in _items)
        {
            if (item.TvaRate.HasValue && item.Montant.HasValue)
            {
                sumItemVat += item.Montant.Value * (item.TvaRate.Value / 100.0);
                hasItemVat = true;
            }
        }

        if (!hasItemVat)
            return;

        double invoiceVat;
        if (!double.TryParse(_montantTva.Replace(",", "."), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out invoiceVat))
            return;

        double diff = Math.Abs(sumItemVat - invoiceVat);
        double tolerance = Math.Max(0.50, invoiceVat * 0.01); // 1% or 0.50, whichever is larger
        VatMismatch = diff > tolerance;
        OnPropertyChanged(nameof(VatMismatch));
    }

    /// <summary>True when any amount field was computed (not OCR-read).</summary>
    public bool IsComputed => _computedFields.Count > 0;

    public bool MontantHtComputed => _computedFields.Contains("montant_ht");
    public bool MontantTvaComputed => _computedFields.Contains("montant_tva");
    public bool MontantTaxeComputed => _computedFields.Contains("montant_taxe");
    public bool MontantTtcComputed => _computedFields.Contains("montant_ttc");

    public string FileDisplay => HasError ? $"{FileName} — {ErrorMessage}" : FileName;

    public bool NumeroFactureMissing => string.IsNullOrWhiteSpace(NumeroFacture);
    public bool DateMissing          => string.IsNullOrWhiteSpace(Date);
    public bool FournisseurMissing   => string.IsNullOrWhiteSpace(Fournisseur);
    public bool ClientMissing        => string.IsNullOrWhiteSpace(Client);
    public bool MontantHtMissing     => string.IsNullOrWhiteSpace(MontantHt);
    public bool MontantTvaMissing    => string.IsNullOrWhiteSpace(MontantTva);
    public bool MontantTaxeMissing   => string.IsNullOrWhiteSpace(MontantTaxe);
    public bool MontantTtcMissing    => string.IsNullOrWhiteSpace(MontantTtc);

    public bool IsIncomplete => HasError
        || NumeroFactureMissing || DateMissing || FournisseurMissing || ClientMissing
        || MontantHtMissing || MontantTvaMissing || MontantTaxeMissing || MontantTtcMissing;

    public string ConfidenceDisplay => HasError ? "—" : $"{(int)Math.Round(Confidence * 100)}%";

    public string ConfidenceTooltip => HasError ? ErrorMessage ?? "Erreur" : $"Score brut : {Confidence:F4}";

    public string MissingFieldsSummary
    {
        get
        {
            var missing = new List<string>();
            if (NumeroFactureMissing) missing.Add("numero_facture");
            if (DateMissing)          missing.Add("date");
            if (FournisseurMissing)   missing.Add("fournisseur");
            if (ClientMissing)        missing.Add("client");
            if (MontantHtMissing)     missing.Add("montant_ht");
            if (MontantTvaMissing)    missing.Add("montant_tva");
            if (MontantTaxeMissing)   missing.Add("montant_taxe");
            if (MontantTtcMissing)    missing.Add("montant_ttc");
            return string.Join(", ", missing);
        }
    }

    public static InvoiceRowViewModel FromSuccess(string filePath, InvoiceResult result) => new()
    {
        FilePath      = filePath,
        FileName      = Path.GetFileName(filePath),
        NumeroFacture = result.NumeroFacture,
        Date          = result.Date,
        Fournisseur   = result.Fournisseur,
        Client        = result.Client,
        MontantHt     = result.MontantHt,
        MontantTva    = result.MontantTva,
        MontantTaxe   = result.MontantTaxe,
        MontantTtc    = result.MontantTtc,
        Confidence    = result.Confidence,
        RawText       = result.RawText,
        EngineUsed    = result.EngineUsed,
        HasError      = false,
        GeminiFallbackReason = result.GeminiFallbackReason,
        ComputedFields = result.ComputedFields != null
            ? new HashSet<string>(result.ComputedFields)
            : new HashSet<string>(),
        AmountMismatch = result.AmountMismatch,
        ItemsCount = result.Items?.Count ?? 0,
        Items = result.Items ?? new List<InvoiceItem>(),
    };

    public static InvoiceRowViewModel FromError(string filePath, string message) => new()
    {
        FilePath     = filePath,
        FileName     = Path.GetFileName(filePath),
        HasError     = true,
        ErrorMessage = message,
        Confidence   = 0.0,
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        OnDerivedFieldChanges();
        return true;
    }

    private void OnDerivedFieldChanges()
    {
        OnPropertyChanged(nameof(FileDisplay));
        OnPropertyChanged(nameof(NumeroFactureMissing));
        OnPropertyChanged(nameof(DateMissing));
        OnPropertyChanged(nameof(FournisseurMissing));
        OnPropertyChanged(nameof(ClientMissing));
        OnPropertyChanged(nameof(MontantHtMissing));
        OnPropertyChanged(nameof(MontantTvaMissing));
        OnPropertyChanged(nameof(MontantTaxeMissing));
        OnPropertyChanged(nameof(MontantTtcMissing));
        OnPropertyChanged(nameof(IsIncomplete));
        OnPropertyChanged(nameof(MissingFieldsSummary));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
