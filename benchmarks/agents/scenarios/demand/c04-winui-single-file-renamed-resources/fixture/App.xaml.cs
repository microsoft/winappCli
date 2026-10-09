using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace NorthstarShell;

public partial class App : Application
{
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "startup.log"), ex.ToString());
            throw;
        }
    }
}
