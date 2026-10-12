using System.Windows;
using System.Windows.Controls;
namespace Kestrel.Older;
public partial class SearchWindow : Window {
    private readonly QueryService service = new();
    public SearchWindow() { InitializeComponent(); }
    private async void QueryChanged(object sender, TextChangedEventArgs e) {
        var result = await service.SearchAsync(QueryBox.Text);
        Dispatcher.Invoke(() => ResultSummary.Text = result);
    }
}
