// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public class UiActionNativeTests
{
    private static UiActionTarget Button => new() { AutomationId = "Save", ControlType = "Button" };
    private static UiActionElement Element => new(Button, new PointerRect(10, 20, 110, 220), false, false, true, false);

    [TestMethod]
    public void Locators_AreExactBoundedAndHaveOneIdentifier()
    {
        Assert.AreEqual(50000, NativeUiActionRules.ValidateLocator(Button));
        Assert.AreEqual(50025, NativeUiActionRules.ValidateLocator(new UiActionTarget { AutomationId = "CustomInput", ControlType = "Custom" }));
        Assert.AreEqual(50000, NativeUiActionRules.ValidateLocator(new UiActionTarget { Name = "Save", ControlType = "Button" }));
        Assert.AreEqual(50000, NativeUiActionRules.ValidateLocator(Button with { AutomationId = new string('x', 512) }));
        Assert.AreEqual(50000, NativeUiActionRules.ValidateLocator(Button with { AutomationId = null, Name = new string('x', 512) }));

        foreach (var target in new[]
        {
            Button with { AutomationId = "" },
            Button with { AutomationId = " " },
            Button with { Name = "Save" },
            Button with { AutomationId = null },
            Button with { AutomationId = new string('x', 513) },
            Button with { AutomationId = "private\nvalue" },
            Button with { AutomationId = null, Name = new string('x', 513) },
            Button with { AutomationId = null, Name = "private\tvalue" },
            Button with { ControlType = "button" },
            Button with { ControlType = "Unknown(50000)" },
        })
        {
            var failure = Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.ValidateLocator(target));
            Assert.AreEqual("unstable_target", failure.Code);
            Assert.IsFalse(failure.Message.Contains("private", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void CanonicalLocator_PrefersIdWithoutReadingAnyName()
    {
        var fromNamedLookup = NativeUiActionRules.CreateLocator("Save", "Button", false, false,
            () => throw new AssertFailedException("A named lookup must not override a stable AutomationId."));
        Assert.AreEqual(Button, fromNamedLookup);
        Assert.IsNull(fromNamedLookup.Name);

        var protectedId = NativeUiActionRules.CreateLocator("Password", "Edit", true, true,
            () => throw new AssertFailedException("Password names must never be read."));
        Assert.AreEqual("Password", protectedId.AutomationId);
        Assert.IsNull(protectedId.Name);
    }

    [TestMethod]
    public void CanonicalLocator_NameFallbackIsLazyAndRejectsTextContent()
    {
        var calls = 0;
        string ReadName()
        {
            calls++;
            return "Save";
        }

        Assert.AreEqual(new UiActionTarget { Name = "Save", ControlType = "Button" },
            NativeUiActionRules.CreateLocator("", "Button", false, false, ReadName));
        Assert.AreEqual(1, calls);
        Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.CreateLocator("", "Custom", false, true, ReadName));
        Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.CreateLocator("", "Button", true, false, ReadName));
        Assert.AreEqual(1, calls);

        var readOnlyText = NativeUiActionRules.CreateLocator("ReadOnlyDocument", "Custom", false, true, ReadName);
        Assert.AreEqual("ReadOnlyDocument", readOnlyText.AutomationId);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void UniqueIdResolution_CannotReuseProofForAChangedIdentifier()
    {
        NativeUiActionRules.RequireResolvedId(Button, Button);
        NativeUiActionRules.RequireResolvedId(new UiActionTarget { Name = "Save document", ControlType = "Button" }, Button);
        Assert.AreEqual("unstable_target", Assert.ThrowsExactly<UiActionException>(
            () => NativeUiActionRules.RequireResolvedId(Button, Button with { AutomationId = "Changed" })).Code);
        Assert.AreEqual("unstable_target", Assert.ThrowsExactly<UiActionException>(
            () => NativeUiActionRules.RequireResolvedId(Button, Button with { ControlType = "Edit" })).Code);
    }

    [TestMethod]
    public void ControlTypes_HaveCanonicalRoundTrips()
    {
        for (var id = 50000; id <= 50040; id++)
        {
            var target = new UiActionTarget { AutomationId = "Control", ControlType = NativeUiActionRules.ControlTypeName(id) };
            Assert.AreEqual(id, NativeUiActionRules.ValidateLocator(target));
        }

        Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.ControlTypeName(49999));
        Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.ControlTypeName(50041));
    }

    [TestMethod]
    public void NameFallback_RejectsPasswordsAndAllWritableControls()
    {
        NativeUiActionRules.RequireNameFallback(false, false, "Button");
        Assert.AreEqual("password_target", Assert.ThrowsExactly<UiActionException>(
            () => NativeUiActionRules.RequireNameFallback(true, false, "Button")).Code);
        Assert.AreEqual("unstable_target", Assert.ThrowsExactly<UiActionException>(
            () => NativeUiActionRules.RequireNameFallback(false, true, "Custom")).Code);
        foreach (var type in new[] { "Edit", "TextBox", "Document", "ComboBox" })
        {
            Assert.AreEqual("unstable_target", Assert.ThrowsExactly<UiActionException>(
                () => NativeUiActionRules.RequireNameFallback(false, false, type)).Code);
            Assert.AreEqual("unstable_target", Assert.ThrowsExactly<UiActionException>(
                () => NativeUiActionRules.ValidateLocator(new UiActionTarget { Name = "private", ControlType = type })).Code);
        }
    }

    [TestMethod]
    public void Privacy_RequiresAnExplicitBooleanRatherThanAProviderDefault()
    {
        NativeUiActionRules.RequireKnownPrivacy(VarEnum.VT_BOOL);
        foreach (var unknown in new[] { VarEnum.VT_EMPTY, VarEnum.VT_UNKNOWN, VarEnum.VT_NULL, VarEnum.VT_I4, VarEnum.VT_BSTR })
        {
            Assert.AreEqual("privacy_unavailable", Assert.ThrowsExactly<UiActionException>(
                () => NativeUiActionRules.RequireKnownPrivacy(unknown)).Code);
        }
    }

    [TestMethod]
    public void NativeImeStatus_RejectsUnverifiableCompositionCapableInput()
    {
        NativeUiActionRules.RequireCommittedNativeInput(0x04090409);
        foreach (var layout in new nuint[] { 0x04070407, 0x08090809, 0x08040804, 0xe0200804, 0x04040404, 0x04110411, 0x04120412 })
        {
            Assert.AreEqual("unsupported_input", Assert.ThrowsExactly<UiActionException>(
                () => NativeUiActionRules.RequireCommittedNativeInput(layout)).Code);
        }

        Assert.AreEqual("unsupported_input", Assert.ThrowsExactly<UiActionException>(
            () => NativeUiActionRules.RequireCommittedNativeInput(0)).Code);
    }

    [TestMethod]
    public void InputReadiness_RejectsPasswordsBeforeOtherErrors()
    {
        NativeUiActionRules.RequireUsable(Element);
        Assert.AreEqual("password_target", Assert.ThrowsExactly<UiActionException>(
            () => NativeUiActionRules.RequireUsable(Element with { IsPassword = true, IsEnabled = false, Bounds = default })).Code);
        foreach (var element in new[]
        {
            Element with { IsEnabled = false },
            Element with { IsOffscreen = true },
            Element with { Bounds = new PointerRect(10, 20, 10, 30) },
            Element with { Bounds = new PointerRect(10, 20, 20, 19) },
        })
        {
            Assert.AreEqual("target_not_ready", Assert.ThrowsExactly<UiActionException>(
                () => NativeUiActionRules.RequireUsable(element)).Code);
        }
    }

    [TestMethod]
    public void Fractions_StayInsideExclusiveBounds()
    {
        var bounds = new PointerRect(-100, 20, 0, 220);
        Assert.AreEqual(new PointerPoint(-100, 20), NativeUiActionRules.Point(bounds, 0, 0));
        Assert.AreEqual(new PointerPoint(-50, 120), NativeUiActionRules.Point(bounds, 0.5, 0.5));
        Assert.AreEqual(new PointerPoint(-1, 219), NativeUiActionRules.Point(bounds, 1, 1));
        Assert.AreEqual(new PointerPoint(4, 5), NativeUiActionRules.Point(new PointerRect(4, 5, 5, 6), 1, 1));

        var large = new PointerRect(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
        Assert.IsTrue(large.Contains(NativeUiActionRules.Point(large, 0.5, 0.5)));
        Assert.IsTrue(large.Contains(NativeUiActionRules.Point(large, 1, 1)));
    }

    [TestMethod]
    public void Fractions_RejectNonfiniteOutOfRangeAndEmptyRectangles()
    {
        foreach (var fraction in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.01, 1.01 })
        {
            Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.Point(Element.Bounds, fraction, 0.5));
            Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.Point(Element.Bounds, 0.5, fraction));
        }

        Assert.ThrowsExactly<UiActionException>(() => NativeUiActionRules.Point(default, 0.5, 0.5));
    }

    [TestMethod]
    public void WheelHit_SelectsNearestScrollContainerInsteadOfChangingListItems()
    {
        string[] path = ["Item16", "ItemsList", "OuterScrollPane", "Window"];
        var index = NativeUiActionRules.FindPointerAncestor(path, true,
            candidate => candidate is "ItemsList" or "OuterScrollPane",
            _ => throw new AssertFailedException("Wheel targeting must not select ordinary interactive leaves."));
        Assert.AreEqual(1, index);
        path[0] = "Item28";
        Assert.AreEqual(index, NativeUiActionRules.FindPointerAncestor(path, true,
            candidate => candidate is "ItemsList" or "OuterScrollPane", _ => true));
    }

    [TestMethod]
    public void ClickHit_KeepsItsInteractiveLeafWithoutLookingForScrollContainers()
    {
        string[] path = ["Item16", "ItemsList", "Window"];
        Assert.AreEqual(0, NativeUiActionRules.FindPointerAncestor(path, false,
            _ => throw new AssertFailedException("Click targeting must keep its existing semantics."),
            candidate => candidate is "Item16" or "ItemsList"));
    }

    [TestMethod]
    public void WheelHit_DoesNotFallBackToALeafWhenNoScrollPatternExists()
    {
        string[] path = ["Item16", "ItemsList", "Window"];
        Assert.AreEqual(-1, NativeUiActionRules.FindPointerAncestor(path, true, _ => false, _ => true));
        Assert.AreEqual(0, NativeUiActionRules.FindPointerAncestor(path, true, candidate => candidate == "Item16", _ => false),
            "an actual scrolling control under the pointer is itself the nearest supported container");
    }

    [TestMethod]
    public void PointerConfirmation_DoesNotGuessAfterMovementOrResize()
    {
        var bounds = Element.Bounds;
        NativeUiActionRules.RequireSameBounds(bounds, bounds);
        foreach (var changed in new[]
        {
            bounds with { Left = bounds.Left + 1 },
            bounds with { Right = bounds.Right + 1 },
            bounds with { Top = bounds.Top + 1 },
            bounds with { Bottom = bounds.Bottom + 1 },
        })
        {
            Assert.AreEqual("target_moved", Assert.ThrowsExactly<UiActionException>(
                () => NativeUiActionRules.RequireSameBounds(bounds, changed)).Code);
        }
    }

    [TestMethod]
    public void TextChunks_KeepSurrogatePairsAndNewlinePairsTogether()
    {
        foreach (var pair in new[] { "\U0001f642", "\r\n" })
        {
            var text = new string('x', KeyboardInput.DefaultTextChunkChars - 1) + pair + "tail";
            var first = NativeUiActionRules.TextChunkLength(text, 0);
            Assert.AreEqual(KeyboardInput.DefaultTextChunkChars - 1, first);
            var second = NativeUiActionRules.TextChunkLength(text, first);
            Assert.AreEqual(pair + "tail", text.Substring(first, second));
        }

        Assert.AreEqual(0, NativeUiActionRules.TextChunkLength("", 0));
        Assert.AreEqual(3, NativeUiActionRules.TextChunkLength("abc", 0));
    }

    [TestMethod]
    public void Scope_RequiresTheOriginalLiveRootAndInteractiveDesktop()
    {
        Assert.AreEqual(NativeUiActionInputScope.Inside, NativeUiActionInputRules.ClassifyScope(true, true, true));
        Assert.AreEqual(NativeUiActionInputScope.Outside, NativeUiActionInputRules.ClassifyScope(true, true, false));
        Assert.AreEqual(NativeUiActionInputScope.NoInteractiveDesktop, NativeUiActionInputRules.ClassifyScope(true, false, true));
        Assert.AreEqual(NativeUiActionInputScope.TargetClosed, NativeUiActionInputRules.ClassifyScope(false, true, true));
    }

    [TestMethod]
    public void PointerOutside_DoesNotChangeForegroundScopeOrKeyboardModifiers()
    {
        var modifiers = new NativeUiActionModifierState();
        modifiers.Update(0xa0, true, 0);
        foreach (var insidePointer in new[] { false, false, true, false, true, false })
        {
            Assert.AreEqual(insidePointer ? NativeUiActionPointerScope.Inside : NativeUiActionPointerScope.Outside,
                NativeUiActionInputRules.ClassifyPointer(insidePointer, false));
            Assert.AreEqual(NativeUiActionInputScope.Inside, NativeUiActionInputRules.ClassifyScope(true, true, true));
            Assert.IsTrue(modifiers.TryGetModifiers(1, out var current));
            Assert.AreEqual(UiActionModifiers.Shift, current);
        }
    }

    [TestMethod]
    public void PointerOutside_DuringAnOwnedPressRequiresAnIncompleteRecording()
    {
        foreach (var right in new[] { false, true })
        {
            var buttons = new NativeUiActionMouseState();
            Assert.AreEqual(NativeUiActionPointerScope.Outside, NativeUiActionInputRules.ClassifyPointer(false, buttons.HasActivePress));
            buttons.Observe(UiActionInputKind.MouseDown, right, new PointerPoint(20, 30));
            Assert.AreEqual(NativeUiActionPointerScope.Inside, NativeUiActionInputRules.ClassifyPointer(true, buttons.HasActivePress));
            Assert.AreEqual(NativeUiActionPointerScope.InterruptedPress, NativeUiActionInputRules.ClassifyPointer(false, buttons.HasActivePress),
                "an out-of-window move or release cannot silently lose its scoped press");
            buttons.Reset();
            Assert.IsFalse(buttons.ActivationBoundary(UiActionInputKind.MouseUp, right, foreground: true, pointBelongs: false));
            Assert.AreEqual(NativeUiActionPointerScope.Outside, NativeUiActionInputRules.ClassifyPointer(false, buttons.HasActivePress));
        }
    }

    [TestMethod]
    public void InjectedInput_RequiresAnExplicitOptIn()
    {
        Assert.IsTrue(NativeUiActionInputRules.AcceptKeyboard(0, false));
        Assert.IsTrue(NativeUiActionInputRules.AcceptKeyboard(0x01 | 0x20 | 0x80, false));
        foreach (var flags in new uint[] { 0x10, 0x02, 0x12 })
        {
            Assert.IsFalse(NativeUiActionInputRules.AcceptKeyboard(flags, false));
            Assert.IsTrue(NativeUiActionInputRules.AcceptKeyboard(flags, true));
        }

        Assert.IsTrue(NativeUiActionInputRules.AcceptMouse(0, false));
        foreach (var flags in new uint[] { 1, 2, 3 })
        {
            Assert.IsFalse(NativeUiActionInputRules.AcceptMouse(flags, false));
            Assert.IsTrue(NativeUiActionInputRules.AcceptMouse(flags, true));
        }
    }

    [TestMethod]
    public void InjectionFlags_RejectUnknownBitsRatherThanAssumingPhysicalInput()
    {
        Assert.IsTrue(NativeUiActionInputRules.KnownKeyboardFlags(0));
        Assert.IsTrue(NativeUiActionInputRules.KnownKeyboardFlags(0xb3));
        Assert.IsTrue(NativeUiActionInputRules.KnownMouseFlags(0));
        Assert.IsTrue(NativeUiActionInputRules.KnownMouseFlags(3));
        foreach (var flags in new uint[] { 4, 8, 0x40, 0x100, 0xffffffff })
        {
            Assert.IsFalse(NativeUiActionInputRules.KnownKeyboardFlags(flags));
            Assert.IsFalse(NativeUiActionInputRules.KnownMouseFlags(flags));
        }
    }

    [TestMethod]
    public void NativeMessages_OnlyTranslateSupportedMouseButtonsAndVerticalWheel()
    {
        Assert.AreEqual(UiActionInputKind.MouseDown, NativeUiActionInputRules.MouseKind(0x0201));
        Assert.AreEqual(UiActionInputKind.MouseDown, NativeUiActionInputRules.MouseKind(0x0204));
        Assert.AreEqual(UiActionInputKind.MouseUp, NativeUiActionInputRules.MouseKind(0x0202));
        Assert.AreEqual(UiActionInputKind.MouseUp, NativeUiActionInputRules.MouseKind(0x0205));
        Assert.AreEqual(UiActionInputKind.Wheel, NativeUiActionInputRules.MouseKind(0x020a));
        foreach (var unsupported in new uint[] { 0x0207, 0x0208, 0x020b, 0x020c, 0x020e, 0 })
        {
            Assert.IsNull(NativeUiActionInputRules.MouseKind(unsupported));
        }

        Assert.AreEqual(120, NativeUiActionInputRules.WheelDelta(0x00780000));
        Assert.AreEqual(-120, NativeUiActionInputRules.WheelDelta(0xff880000));
        Assert.AreEqual(0, NativeUiActionInputRules.WheelDelta(0x0000ffff));
    }

    [TestMethod]
    public void KeyboardMessages_NormalizeModifierSidesWithoutDecodingText()
    {
        Assert.AreEqual((ushort)0xa0, NativeUiActionInputRules.NormalizeKey(0x10, 0x2a, 0));
        Assert.AreEqual((ushort)0xa1, NativeUiActionInputRules.NormalizeKey(0x10, 0x36, 0));
        Assert.AreEqual((ushort)0xa2, NativeUiActionInputRules.NormalizeKey(0x11, 0, 0));
        Assert.AreEqual((ushort)0xa3, NativeUiActionInputRules.NormalizeKey(0x11, 0, 1));
        Assert.AreEqual((ushort)0xa4, NativeUiActionInputRules.NormalizeKey(0x12, 0, 0));
        Assert.AreEqual((ushort)0xa5, NativeUiActionInputRules.NormalizeKey(0x12, 0, 1));
        Assert.AreEqual((ushort)0x41, NativeUiActionInputRules.NormalizeKey(0x41, 0, 0));
        Assert.AreEqual((ushort)0, NativeUiActionInputRules.NormalizeKey(0xffffffff, 0, 0));
    }

    [TestMethod]
    public void ModifierState_AccountsForTheEventBeforeAsyncStateUpdates()
    {
        var state = new NativeUiActionModifierState();
        state.Update(0xa0, true, 0);
        Assert.IsTrue(state.TryGetModifiers(1, out var shift));
        Assert.AreEqual(UiActionModifiers.Shift, shift);
        state.Update(0xa2, true, 1);
        Assert.IsTrue(state.TryGetModifiers(5, out var chord));
        Assert.AreEqual(UiActionModifiers.Shift | UiActionModifiers.Control, chord);
        state.Update(0xa0, false, 5);
        Assert.IsTrue(state.TryGetModifiers(4, out var control));
        Assert.AreEqual(UiActionModifiers.Control, control);
        state.Update(0xa2, false, 4);
        Assert.IsTrue(state.TryGetModifiers(0, out var none));
        Assert.AreEqual(UiActionModifiers.None, none);
    }

    [TestMethod]
    public void ModifierState_DoesNotImportModifiersHeldInAnotherApplication()
    {
        var state = new NativeUiActionModifierState();
        state.Reset(4);
        state.Update(0xa2, true, 4);
        Assert.IsFalse(state.TryGetModifiers(4, out _), "a repeated down event must not adopt an out-of-scope modifier");
        state.Update(0xa2, false, 4);
        Assert.IsTrue(state.TryGetModifiers(0, out _));
        state.Update(0xa2, true, 0);
        Assert.IsTrue(state.TryGetModifiers(4, out var control));
        Assert.AreEqual(UiActionModifiers.Control, control);
        state.Reset(0);
        Assert.IsFalse(state.TryGetModifiers(4, out _), "an unobserved modifier must fail closed");
    }

    [TestMethod]
    public void ModifierState_PreservesOtherSideAndIgnoresPrintableKeyState()
    {
        var state = new NativeUiActionModifierState();
        state.Update(0xa0, true, 0);
        state.Update(0xa1, true, 1);
        state.Update(0xa0, false, 3);
        state.Update(0x41, true, 2);
        Assert.IsTrue(state.TryGetModifiers(2, out var modifiers));
        Assert.AreEqual(UiActionModifiers.Shift, modifiers);
        Assert.AreEqual((byte)0, NativeUiActionModifierState.ModifierBit(0x41));
    }

    [TestMethod]
    public void MouseState_SuppressesOnlyAProvenActivationRelease()
    {
        foreach (var right in new[] { false, true })
        {
            var state = new NativeUiActionMouseState();
            Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, right, true, true),
                "an arbitrary unpaired release must reach core");
            Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseDown, right, false, true));
            Assert.IsTrue(state.HasPendingActivation);
            Assert.IsFalse(state.HasActivePress);
            state.Reset(preserveActivation: true);
            Assert.IsTrue(state.ActivationBoundary(UiActionInputKind.MouseUp, right, true, true));
            Assert.IsFalse(state.HasPendingActivation);
            Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, right, true, true),
                "activation proof is one-use, not blanket release suppression");
        }
    }

    [TestMethod]
    public void MouseState_OutsideClicksAndMismatchedButtonsCannotInventActivationProof()
    {
        var state = new NativeUiActionMouseState();
        state.ActivationBoundary(UiActionInputKind.MouseDown, false, false, false);
        state.Reset(preserveActivation: true);
        Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, false, true, true));
        state.ActivationBoundary(UiActionInputKind.MouseDown, false, false, true);
        Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, true, true, true));
        Assert.IsTrue(state.ActivationBoundary(UiActionInputKind.MouseUp, false, true, true));
    }

    [TestMethod]
    public void MouseState_ClearsActivationProofOnOutsideReleaseOrNewPress()
    {
        var state = new NativeUiActionMouseState();
        state.ActivationBoundary(UiActionInputKind.MouseDown, false, false, true);
        Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, false, false, false));
        Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, false, true, true));
        state.ActivationBoundary(UiActionInputKind.MouseDown, false, false, true);
        state.ActivationBoundary(UiActionInputKind.MouseDown, false, true, true);
        state.Observe(UiActionInputKind.MouseDown, false);
        Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, false, true, true));
        state.Observe(UiActionInputKind.MouseUp, false);
        Assert.IsFalse(state.HasActivePress);
        state.ActivationBoundary(UiActionInputKind.MouseDown, true, false, true);
        state.Reset();
        Assert.IsFalse(state.ActivationBoundary(UiActionInputKind.MouseUp, true, true, true));
    }

    [TestMethod]
    public void MouseState_LatchesAnIntermediateDragEvenWhenThePointerReturns()
    {
        foreach (var right in new[] { false, true })
        {
            var state = new NativeUiActionMouseState();
            var origin = new PointerPoint(100, 200);
            Assert.IsFalse(state.HasDragExcursion(new PointerPoint(1000, 2000)));
            state.Observe(UiActionInputKind.MouseDown, right, origin);
            Assert.IsTrue(state.HasActivePress);
            Assert.IsFalse(state.HasDragExcursion(new PointerPoint(104, 196)));
            Assert.IsTrue(state.HasDragExcursion(new PointerPoint(105, 200)));
            Assert.IsTrue(state.HasDragExcursion(origin), "returning to the press point must not turn a drag into a click");
            state.Reset();
            Assert.IsFalse(state.HasActivePress);
            Assert.IsFalse(state.HasDragExcursion(origin));
        }
    }

    [TestMethod]
    public void MouseState_UsesThePressOriginAndAvoidsCoordinateOverflow()
    {
        var state = new NativeUiActionMouseState();
        state.Observe(UiActionInputKind.MouseDown, false, new PointerPoint(10, 10));
        Assert.IsFalse(state.HasDragExcursion(new PointerPoint(12, 12)));
        Assert.IsFalse(state.HasDragExcursion(new PointerPoint(14, 14)));
        Assert.IsTrue(state.HasDragExcursion(new PointerPoint(15, 15)), "small consecutive movements must be measured against the original press");
        state.Reset();
        state.Observe(UiActionInputKind.MouseDown, false, new PointerPoint(int.MinValue, int.MinValue));
        Assert.IsTrue(state.HasDragExcursion(new PointerPoint(int.MaxValue, int.MaxValue)));
    }

    [TestMethod]
    public async Task BufferOverflow_CompletesWithFailureInsteadOfDroppingIntoSuccess()
    {
        var channel = Channel.CreateBounded<UiActionInput>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        var first = new UiActionInput(UiActionInputKind.ScopeEntered, 0, ObservedTimestamp: 1000);
        Assert.IsTrue(NativeUiActionInputRules.TryPublish(channel.Writer, first));
        Assert.IsFalse(NativeUiActionInputRules.TryPublish(channel.Writer,
            new UiActionInput(UiActionInputKind.KeyDown, 1, VirtualKey: 0x41, ObservedTimestamp: 1001)));
        Assert.AreSame(first, await channel.Reader.ReadAsync());
        var error = await Assert.ThrowsExactlyAsync<UiActionException>(async () => await channel.Reader.Completion);
        Assert.AreEqual("input_overflow", error.Code);
    }

    [TestMethod]
    public async Task InputBuffer_PreservesObservationTimeIndependentlyOfDiagnosticElapsedTime()
    {
        var channel = Channel.CreateBounded<UiActionInput>(4);
        var events = new[]
        {
            new UiActionInput(UiActionInputKind.ScopeEntered, 9000, ObservedTimestamp: 100),
            new UiActionInput(UiActionInputKind.KeyDown, 9005, VirtualKey: 0x41, ObservedTimestamp: 100),
            new UiActionInput(UiActionInputKind.Wheel, 9010, WheelDelta: 120, ObservedTimestamp: 105),
            new UiActionInput(UiActionInputKind.Error, 9015, ErrorCode: "unsupported_input", ObservedTimestamp: 110),
        };
        foreach (var input in events)
        {
            Assert.IsTrue(NativeUiActionInputRules.TryPublish(channel.Writer, input));
        }

        foreach (var expected in events)
        {
            var actual = await channel.Reader.ReadAsync();
            Assert.AreSame(expected, actual, "enqueueing must not replace callback-entry time with a later timestamp");
            Assert.AreEqual(expected.ObservedTimestamp, actual.ObservedTimestamp);
            Assert.AreEqual(expected.ElapsedMs, actual.ElapsedMs);
        }
    }

    [TestMethod]
    public void HookPayloads_HaveBlittableNativeLayouts()
    {
        Assert.AreEqual(nint.Size == 8 ? 24 : 20, Unsafe.SizeOf<KBDLLHOOKSTRUCT>());
        Assert.AreEqual(nint.Size == 8 ? 32 : 24, Unsafe.SizeOf<MSLLHOOKSTRUCT>());
        Assert.IsFalse(RuntimeHelpers.IsReferenceOrContainsReferences<KBDLLHOOKSTRUCT>());
        Assert.IsFalse(RuntimeHelpers.IsReferenceOrContainsReferences<MSLLHOOKSTRUCT>());
    }
}
