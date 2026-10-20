using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AccessLoop.JourneyDemo;

public sealed class AutomationStackPanel : StackPanel
{
    // A panel needs a peer for its automation ID to appear in the UIA control tree.
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new AutomationStackPanelPeer(this);

    private sealed class AutomationStackPanelPeer(AutomationStackPanel owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(AutomationStackPanel);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Group;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
