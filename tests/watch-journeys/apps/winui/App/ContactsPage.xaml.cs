using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TodoBench;

public sealed partial class ContactsPage : Page
{
    private int _clicks;

    public ContactsVm Vm { get; } = new();

    public ContactsPage()
    {
        InitializeComponent();
    }

    private static string Greeting()
    {
        return "hello-v1";
    }

    private void OnHello(object sender, RoutedEventArgs e)
    {
        StatusText.Text = Greeting();
    }

    private void OnCount(object sender, RoutedEventArgs e)
    {
        _clicks++;
        CountText.Text = $"clicks {_clicks}";
    }

    private void OnLib(object sender, RoutedEventArgs e)
    {
        Header.Text = Core.Greeter.Hello();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Vm.Selected = ContactList.SelectedItem as Contact;
    }
}
