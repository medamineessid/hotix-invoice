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

    public FileItemViewModel(string filePath) => FilePath = filePath;
}
