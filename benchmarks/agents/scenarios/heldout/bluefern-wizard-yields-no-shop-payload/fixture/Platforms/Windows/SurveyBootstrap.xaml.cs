using Microsoft.UI.Xaml;
namespace BluefernSurvey.WinUI;
public partial class SurveyBootstrap : MauiWinUIApplication {
    public SurveyBootstrap() { InitializeComponent(); }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
