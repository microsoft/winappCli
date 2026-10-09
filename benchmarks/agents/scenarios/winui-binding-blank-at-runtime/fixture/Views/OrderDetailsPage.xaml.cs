using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Tallyport.Views;

public sealed partial class OrderDetailsPage : Page
{
    public OrderDetailsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Shell passes whatever the list item carries.
        DataContext = e.Parameter;
    }
}
