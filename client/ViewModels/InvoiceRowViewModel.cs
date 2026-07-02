using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Hotix.InvoiceClient.ViewModels;

public sealed class InvoiceRowViewModel : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private string? _numeroFacture;
    private string? _date;
    private string? _fournisseur;
    private string? _client;
    private string? _montantHt;
    private string? _montantTva;
    private string? _montantTaxe;
    private string? _montantTtc;
    private double _confidence;
    private string? _rawText;
    private bool _hasError;
    private string? _errorMessage;
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

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
        set => SetField(ref _fournisseur, value);
    }

    public string? Client
    {
        get => _client;
        set => SetField(ref _client, value);
    }

    public string? MontantHt
    {
        get => _montantHt;
        set => SetField(ref _montantHt, value);
    }

    public string? MontantTva
    {
        get => _montantTva;
        set => SetField(ref _montantTva, value);
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
        HasError      = false,
    };

    public static InvoiceRowViewModel FromError(string filePath, string message) => new()
    {
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
