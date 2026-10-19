namespace Tallyport.Models;

public class Order
{
    public string Number { get; set; } = "";
    public Customer Customer { get; set; } = new();
    public decimal Total { get; set; }
}

public class Customer
{
    public string DisplayName { get; set; } = "";
}
