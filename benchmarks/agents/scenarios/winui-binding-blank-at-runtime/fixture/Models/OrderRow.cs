namespace Tallyport.Models;

// Lightweight projection used by the orders list (loaded from the cache).
public record OrderRow(string Number, CustomerRef Customer, decimal Total);

public record CustomerRef(string Id, string Name);
