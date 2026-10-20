using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AccessLoop.StateDemo;

public sealed partial class MainWindow : Window
{
    private enum DemoPage { Basics, Scrolling, Dialogs }

    private DemoPage currentPage;
    private InfoDialog? infoDialog;
    private ScopeProbeWindow? scopeProbe;
    private bool isClosing;

    public MainWindow()
    {
        InitializeComponent();
        MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        PreviewGuidesToggle.Checked += PreviewGuidesToggle_Changed;
        PreviewGuidesToggle.Unchecked += PreviewGuidesToggle_Changed;
        DensityCombo.SelectionChanged += DensityCombo_SelectionChanged;
        Closed += MainWindow_Closed;
        ResetPage(DemoPage.Basics);
    }

    private void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        WindowRoot.Loaded -= WindowRoot_Loaded;
        WindowSizing.ResizeAndCenter(this, WindowRoot, 1100, 700);
        // Full tab labels are visible; redundant native tooltip windows are outside this fixture's scope.
        foreach (TabViewItem tab in MainTabs.TabItems)
        {
            ToolTipService.SetToolTip(tab, null);
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex is >= 0 and <= 2)
        {
            ResetPage((DemoPage)MainTabs.SelectedIndex);
        }
    }

    private void ResetPage(DemoPage page)
    {
        DensityCombo.IsDropDownOpen = false;
        currentPage = page;

        if (page == DemoPage.Basics)
        {
            PreviewGuidesToggle.IsChecked = false;
            UpdatePreviewGuides();
            DensityCombo.SelectedIndex = 0;
            UpdateDensityPreview();
            BasicsActionStatus.Text = "Preview idle";
        }
        else if (page == DemoPage.Scrolling)
        {
            ScrollActionStatus.Text = "Layout not saved";
        }
    }

    private void PreviewGuidesToggle_Changed(object sender, RoutedEventArgs e) => UpdatePreviewGuides();

    private void UpdatePreviewGuides()
    {
        var showGuides = PreviewGuidesToggle.IsChecked == true;
        PreviewGuidesStatus.Text = showGuides ? "Preview guides on" : "Preview guides off";
        PreviewGuidesOverlay.Visibility = showGuides ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DensityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDensityPreview();

    private void UpdateDensityPreview()
    {
        if (DensityCombo.SelectedItem is ComboBoxItem { Content: string density })
        {
            DensityStatus.Text = $"Current preview: {density}";
            PreviewRows.Spacing = density switch
            {
                "Compact" => 8,
                "Comfortable" => 18,
                _ => 12
            };
        }
    }

    private void BasicIconButton_Click(object sender, RoutedEventArgs e) =>
        BasicsActionStatus.Text = "Preview refreshed";

    private void ContentScroller_Loaded(object sender, RoutedEventArgs e) =>
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);

    private void ContentScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0)
        {
            ScrollContent.Height = Math.Max(720, e.NewSize.Height * 1.8);
        }
    }

    private void ScrollBottomButton_Click(object sender, RoutedEventArgs e) =>
        ScrollActionStatus.Text = "Layout saved";

    private async void OpenInfoDialog_Click(object sender, RoutedEventArgs e)
    {
        if (infoDialog is not null)
        {
            return;
        }

        infoDialog = new InfoDialog { XamlRoot = WindowRoot.XamlRoot };
        try
        {
            await infoDialog.ShowAsync(ContentDialogPlacement.Popup);
        }
        finally
        {
            infoDialog = null;
            if (!isClosing && currentPage == DemoPage.Dialogs)
            {
                OpenInfoDialogButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void OpenScopeProbe_Click(object sender, RoutedEventArgs e)
    {
        if (scopeProbe is null)
        {
            scopeProbe = new ScopeProbeWindow();
            scopeProbe.Closed += (_, _) => scopeProbe = null;
        }

        scopeProbe.Activate();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        isClosing = true;
        infoDialog?.Hide();
        scopeProbe?.Close();
        scopeProbe = null;
    }
}
