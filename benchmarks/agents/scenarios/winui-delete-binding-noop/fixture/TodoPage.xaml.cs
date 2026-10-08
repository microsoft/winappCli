using Microsoft.UI.Xaml.Controls;

namespace Brightwater;

public sealed partial class TodoPage : Page
{
    public TodoViewModel ViewModel { get; } = new();
    public TodoPage() => InitializeComponent();
}
