using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ContosoOrders;

public sealed partial class OrdersPage : Page
{
    public List<Order> Orders { get; set; } = new();

    public OrdersPage()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        new Thread(() =>
        {
            var json = new HttpClient().GetStringAsync("http://orders.contoso.com/api/orders").Result;
            Orders = Order.Parse(json);
            DataContext = null;
            DataContext = this;
        }).Start();
    }
}
