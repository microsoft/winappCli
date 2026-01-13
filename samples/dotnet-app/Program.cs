using System;
using Windows.ApplicationModel;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            var package = Package.Current;
            var familyName = package.Id.FamilyName;
            Console.WriteLine($"Package Family Name: {familyName}");
            
            // Get Windows App Runtime version using the API
            var runtimeVersion = Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString;
            Console.WriteLine($"Windows App Runtime Version: {runtimeVersion}");
        }
        catch (InvalidOperationException)
        {
            // Thrown when app doesn't have package identity
            Console.WriteLine("Not packaged");
        }
    }
}