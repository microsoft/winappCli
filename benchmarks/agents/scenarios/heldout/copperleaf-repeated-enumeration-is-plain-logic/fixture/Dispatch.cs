var messages = new[] { "alpha", "beta" };
var sent = messages.Select(message => {
    Console.WriteLine($"SEND {message}");
    return message;
});
Console.WriteLine($"Count: {sent.Count()}");
Console.WriteLine(string.Join(", ", sent));
