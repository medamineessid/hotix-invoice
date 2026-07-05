using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Hotix.InvoiceClient;

/// <summary>
/// Singleton that loads localized strings from embedded JSON resources
/// and notifies WPF bindings when the language changes at runtime.
/// </summary>
public sealed class TranslationSource : INotifyPropertyChanged
{
    private static readonly TranslationSource _instance = new();
    private Dictionary<string, string> _strings = new();
    private string _currentCulture = "fr";

    // Static constructor loads default culture
    static TranslationSource() { }

    private TranslationSource()
    {
        LoadCulture(_currentCulture);
    }

    public static TranslationSource Instance => _instance;

    /// <summary>Current culture code ("en" or "fr").</summary>
    public string CurrentCulture
    {
        get => _currentCulture;
        set
        {
            if (_currentCulture == value) return;
            _currentCulture = value;
            LoadCulture(value);
            // Notify all bindings that use the indexer
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
        }
    }

    /// <summary>Indexer used by XAML bindings: {Binding Source={x:Static ...}, Path=[Key]}</summary>
    public string this[string key]
    {
        get
        {
            if (key == null) return "";
            return _strings.TryGetValue(key, out string? value) ? value : $"{{_{key}_}}";
        }
    }

    /// <summary>Get a formatted string with positional args.</summary>
    public string Format(string key, params object?[] args)
    {
        string template = this[key];
        return string.Format(template, args);
    }

    /// <summary>Convenience: returns TranslationSource.Instance.</summary>
    public static TranslationSource T => Instance;

    /// <summary>Get a formatted string from the static instance.</summary>
    public static string Fmt(string key, params object?[] args) => T.Format(key, args);

    /// <summary>Get a raw string from the static instance.</summary>
    public static string Get(string key) => T[key];

    private void LoadCulture(string culture)
    {
        try
        {
            string resourceName = culture == "en" ? "strings.json" : "strings.fr.json";

            // Try loading from app directory first, then embedded resource
            string appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string filePath = Path.Combine(appDir, "Resources", resourceName);

            string json;
            if (File.Exists(filePath))
            {
                json = File.ReadAllText(filePath);
            }
            else
            {
                // Fallback: try from project root (dev mode)
                string projectRoot = Path.Combine(appDir, "..", "..", "..", "..", "Resources");
                filePath = Path.Combine(projectRoot, resourceName);
                json = File.Exists(filePath)
                    ? File.ReadAllText(filePath)
                    : "{}";
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            _strings = loaded ?? new Dictionary<string, string>();
        }
        catch
        {
            _strings = new Dictionary<string, string>();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
