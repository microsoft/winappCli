using System.Collections.ObjectModel;
using System.Windows;

namespace ContosoInventory;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Item> _items = new();

    public MainWindow()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke(() => _items.Add(new Item { Name = "New item", Quantity = 1 }));
    }
}

public class Item
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
}
