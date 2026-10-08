using System.Windows;
using Microsoft.Windows.AppLifecycle;

namespace Larkspur.Ledger;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.ShareTarget)
        {
            ShareImport.Handle(activation);
        }
        base.OnStartup(e);
    }
}
