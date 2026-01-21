using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Windows.ApplicationModel;

namespace wpf_app;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

         try
        {
            var package = Package.Current;
            var familyName = package.Id.FamilyName;
            StatusTextBlock.Text = $"Package Family Name: {familyName}";
            
            // Get Windows App Runtime version using the API
            var runtimeVersion = Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString;
            StatusTextBlock.Text += $"\nWindows App Runtime Version: {runtimeVersion}";
        }
        catch (InvalidOperationException)
        {
            // Thrown when app doesn't have package identity
            StatusTextBlock.Text = "Not packaged";
        }
    }
}