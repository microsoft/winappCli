using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AccessLoop.StateDemo;

public sealed class AutomationStackPanel : StackPanel
{
    // Panels need a peer for their identifiers to appear in the UIA control tree.
    protected override AutomationPeer OnCreateAutomationPeer() => new PanelPeer(this);

    private sealed class PanelPeer(AutomationStackPanel owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(AutomationStackPanel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsPasswordCore() => false;
    }
}

// Keep native control patterns, while making the ordinary controls' non-password status explicit.
public sealed class AutomationButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ButtonPeer(this);

    private sealed class ButtonPeer(AutomationButton owner) : ButtonAutomationPeer(owner)
    {
        protected override bool IsPasswordCore() => false;
    }
}

public sealed class AutomationCheckBox : CheckBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new CheckBoxPeer(this);

    private sealed class CheckBoxPeer(AutomationCheckBox owner) : CheckBoxAutomationPeer(owner)
    {
        protected override bool IsPasswordCore() => false;
    }
}

public sealed class AutomationComboBox : ComboBox
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("Popup") is Popup popup)
        {
            // ComboBox opts out of these bounds while applying its native template.
            popup.ShouldConstrainToRootBounds = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ComboPeer(this);

    private sealed class ComboPeer(AutomationComboBox owner) : ComboBoxAutomationPeer(owner)
    {
        protected override bool IsPasswordCore() => false;
    }
}

public sealed class AutomationComboBoxItem : ComboBoxItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ItemPeer(this);

    private sealed class ItemPeer(AutomationComboBoxItem owner) : ComboBoxItemAutomationPeer(owner)
    {
        protected override bool IsPasswordCore() => false;
    }
}
