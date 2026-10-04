using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Kestrel.Photos;

public partial class App : Application
{
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.ShareTarget) { ShareHandler.Handle(activation); }
        new MainWindow().Activate();
    }
}
