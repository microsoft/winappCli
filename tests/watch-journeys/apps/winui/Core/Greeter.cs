namespace Core;

public static class Greeter
{
    public static string Hello()
    {
        return "core-v1|" + Data.Store.Greeting();
    }
}
