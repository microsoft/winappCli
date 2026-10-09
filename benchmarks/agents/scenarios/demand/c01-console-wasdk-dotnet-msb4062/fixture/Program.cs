using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

WinRT.ComWrappersSupport.InitializeComWrappers();

Application.Start(_ =>
{
    var app = new App();
    app.Launched += (_, _) =>
    {
        var window = new Window
        {
            Title = "Aster Console Host",
            Content = new TextBlock { Text = "Hello from the console host" }
        };
        window.Activate();
    };
});

public sealed class App : Application
{
    public event EventHandler<LaunchActivatedEventArgs>? Launched;

    protected override void OnLaunched(LaunchActivatedEventArgs args) => Launched?.Invoke(this, args);
}
