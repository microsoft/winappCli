using Microsoft.UI.Xaml;

namespace AccessLoop.StateDemo;

public sealed partial class ScopeProbeWindow : Window
{
    public ScopeProbeWindow()
    {
        InitializeComponent();
    }

    private void ScopeRoot_Loaded(object sender, RoutedEventArgs e)
    {
        ScopeRoot.Loaded -= ScopeRoot_Loaded;
        WindowSizing.ResizeAndCenter(this, ScopeRoot, 500, 300);
    }

    private void CloseScopeProbe_Click(object sender, RoutedEventArgs e) => Close();
}
