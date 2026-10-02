using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ContosoTasks;

public sealed partial class MainPage : Page
{
    public ObservableCollection<string> Tasks { get; } = new();

    public MainPage() => InitializeComponent();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewItemBox.Text)) return;
        Tasks.Add(NewItemBox.Text.Trim());
        NewItemBox.Text = string.Empty;
        CountText.Text = $"{Tasks.Count} tasks";
    }
}
