using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Pebblewick.Views;

public sealed partial class InvoicePage : Page
{
    public InvoicePage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var id = (string)e.Parameter;
        DataContext = await App.Invoices.LoadAsync(id);
    }
}
