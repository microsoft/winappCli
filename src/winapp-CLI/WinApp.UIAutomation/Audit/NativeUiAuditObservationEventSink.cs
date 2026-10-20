// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Marshalling;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

[GeneratedComClass]
internal sealed partial class NativeUiAuditObservationEventSink(NativeUiAuditObservation observer) :
    IUIAutomationFocusChangedEventHandler,
    IUIAutomationPropertyChangedEventHandler,
    IUIAutomationStructureChangedEventHandler,
    IUIAutomationEventHandler,
    IUIAutomationNotificationEventHandler
{
    public unsafe void HandleFocusChangedEvent(IUIAutomationElement sender)
        => observer.OnEvent(sender, "focus");

    public unsafe void HandlePropertyChangedEvent(IUIAutomationElement sender, UIA_PROPERTY_ID propertyId, ComVariant newValue)
        // The borrowed VARIANT is intentionally never inspected or persisted.
        => observer.OnEvent(sender, "property", propertyId: (int)propertyId);

    public unsafe void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, SAFEARRAY* runtimeId)
        => observer.OnEvent(sender, "structure", structureChangeType: (int)changeType, changedRuntimeId: runtimeId);

    public unsafe void HandleAutomationEvent(IUIAutomationElement sender, UIA_EVENT_ID eventId)
    {
        if (eventId == UIA_EVENT_ID.UIA_LiveRegionChangedEventId)
        {
            observer.OnEvent(sender, "live-region");
        }
    }

    public unsafe void HandleNotificationEvent(IUIAutomationElement sender, NotificationKind notificationKind,
        NotificationProcessing notificationProcessing, BSTR displayString, BSTR activityId)
        // Both BSTRs are borrowed and may contain input values, even when the source is not a password.
        => observer.OnEvent(sender, "notification", notificationKind: (int)notificationKind,
            notificationProcessing: (int)notificationProcessing);
}
