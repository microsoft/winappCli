using Microsoft.UI.Xaml.Controls;
using TidePlanner.ViewModels;

namespace TidePlanner;

public sealed partial class MainPage : Page
{
    public StatusViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
    }
}
