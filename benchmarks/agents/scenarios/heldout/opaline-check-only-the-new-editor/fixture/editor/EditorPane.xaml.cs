using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Opaline;
public partial class EditorPane : Page {
    public static event System.Action<string>? TitleBroadcast;
    public string CurrentTitle { get; private set; } = "Untitled";
    public EditorPane() {
        InitializeComponent();
        TitleBroadcast += title => CurrentTitle = title;
    }
    private void DeleteClicked(object sender, RoutedEventArgs e) {
        CurrentTitle = "Deleted";
    }
}
