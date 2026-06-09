using System.Windows;

namespace Hotix.InvoiceClient;

public partial class SplashScreen : Window
{
    public static readonly DependencyProperty StatusMessageProperty =
        DependencyProperty.Register(nameof(StatusMessage), typeof(string), typeof(SplashScreen), new PropertyMetadata("Initialisation..."));

    public string StatusMessage
    {
        get => (string)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    public SplashScreen()
    {
        InitializeComponent();
    }
}
