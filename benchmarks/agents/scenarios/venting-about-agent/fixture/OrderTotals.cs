namespace Juniper.Orders;

public static class OrderTotals
{
    public static decimal Sum(Order order) => order.Lines.Sum(l => l.Price * l.Quantity) - order.Discount!.Amount;
}

public record Order(List<OrderLine> Lines, Discount? Discount);
public record OrderLine(decimal Price, int Quantity);
public record Discount(decimal Amount);
