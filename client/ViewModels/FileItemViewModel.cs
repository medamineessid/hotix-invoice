using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Hotix.InvoiceClient.ViewModels;

public sealed class FileItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    public string FileSizeDisplay
    {
        get
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists) return "?";
                long bytes = info.Length;
                return bytes switch
                {
                    < 1024 => $"{bytes} B",
                    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
                    _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
                };
            }
            catch
            {
                return "?";
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>
    /// Icon character differentiating PDF files from images.
    /// </summary>
    public string FileIcon
    {
        get
        {
            string ext = Path.GetExtension(FilePath).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "📋",
                ".jpg" or ".jpeg" => "🖼",
                ".png" => "🖼",
                ".bmp" => "🖼",
                ".tif" or ".tiff" => "🖼",
                _ => "📄",
            };
        }
    }

    public FileItemViewModel(string filePath) => FilePath = filePath;
}
