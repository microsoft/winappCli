// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using Windows.Win32;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class NativeUiAuditSnapshotReader
{
    private const int MaximumRectanglesPerRun = 32;
    private const TextPatternRangeEndpoint Start = TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start;
    private const TextPatternRangeEndpoint End = TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;

    private UiAuditNodeTextRun[] ReadTextRuns(IUIAutomationElement element, string nodeId,
        List<UiAuditSnapshotIssue> issues, NativeUiAuditSnapshotComScope owned)
    {
        var runs = new List<UiAuditNodeTextRun>();
        try
        {
            _state.Budget.Check();
            if (owned.Add(element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId)) is not IUIAutomationTextPattern pattern)
            {
                _state.Issue(issues, "text_pattern_error", "The provider did not return its advertised TextPattern.");
                return [];
            }
            _state.Budget.Check();
            var visibleRanges = owned.Add(pattern.GetVisibleRanges());
            if (visibleRanges is null)
            {
                _state.Issue(issues, "visible_ranges_unavailable", "The TextPattern provider did not return visible ranges.");
                return [];
            }
            _state.Budget.Check();
            var count = visibleRanges.get_Length();
            if (count < 0)
            {
                _state.Issue(issues, "visible_ranges_error", "The provider returned an invalid visible-range count.");
                return [];
            }

            var maximum = _state.Options.MaxTextRunsPerNode;
            var visibleLimit = Math.Min(count, maximum);
            for (var index = 0; index < visibleLimit; index++)
            {
                _state.Budget.Check();
                if (runs.Count >= maximum)
                {
                    TextLimit(issues);
                    break;
                }
                var visible = owned.Add(visibleRanges.GetElement(index));
                if (visible is null)
                {
                    _state.Issue(issues, "visible_range_error", "The provider returned a null visible range.");
                    continue;
                }
                var extent = Compare(visible, Start, visible, End);
                if (extent == 0)
                {
                    continue;
                }
                if (extent > 0)
                {
                    _state.Issue(issues, "visible_range_error", "The provider returned reversed text-range endpoints.");
                    continue;
                }
                _state.Budget.Check();
                var remaining = owned.Add(visible.Clone());
                if (remaining is null)
                {
                    _state.Issue(issues, "text_format_error", "The provider could not clone a visible range.");
                    continue;
                }
                if (Compare(remaining, Start, visible, Start) != 0 || Compare(remaining, End, visible, End) != 0)
                {
                    _state.Issue(issues, "text_format_error", "The provider's visible-range clone changed endpoints; its formatting was omitted.");
                    continue;
                }
                while (Compare(remaining, Start, visible, End) < 0)
                {
                    if (runs.Count >= maximum)
                    {
                        TextLimit(issues);
                        return runs.ToArray();
                    }

                    IUIAutomationTextRange run;
                    var aggregateRemainder = false;
                    try
                    {
                        run = FormatRun(remaining, visible, owned);
                    }
                    catch (Exception ex) when (IsProviderFailure(ex))
                    {
                        // Query the remaining *visible* range, not DocumentRange. Its actual
                        // MixedAttributeValue sentinels are retained instead of inventing a style.
                        _state.Issue(issues, "text_format_fallback", FailureMessage("Formatting-range split; remaining visible range used", ex));
                        run = remaining;
                        aggregateRemainder = true;
                    }

                    var id = nodeId + ".t" + (runs.Count + 1).ToString(CultureInfo.InvariantCulture);
                    runs.Add(RangePrivacyKnown(run, element, issues)
                        ? ReadTextRun(run, id, issues) : WithheldRun(id));
                    if (aggregateRemainder || Compare(run, End, visible, End) == 0)
                    {
                        break;
                    }
                    _state.Budget.Check();
                    var oldStart = owned.Add(remaining.Clone())
                        ?? throw new InvalidOperationException("The provider could not preserve a text endpoint.");
                    if (Compare(oldStart, Start, remaining, Start) != 0)
                    {
                        _state.Issue(issues, "text_format_error", "The provider could not preserve the previous formatting endpoint; further runs were omitted.");
                        break;
                    }
                    MoveEndpoint(remaining, Start, run, End);
                    if (Compare(remaining, Start, oldStart, Start) <= 0)
                    {
                        _state.Issue(issues, "text_format_no_progress", "The provider's formatting range did not advance; further text ranges were omitted.");
                        break;
                    }
                }
            }
            if (count > visibleLimit)
            {
                TextLimit(issues);
            }
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "text_ranges_error", FailureMessage("Visible text ranges", ex));
        }
        return runs.ToArray();
    }

    private IUIAutomationTextRange FormatRun(IUIAutomationTextRange remaining, IUIAutomationTextRange visible, NativeUiAuditSnapshotComScope owned)
    {
        _state.Budget.Check();
        var run = owned.Add(remaining.Clone())
            ?? throw new InvalidOperationException("The provider could not clone a formatting range.");
        MoveEndpoint(run, End, run, Start);
        _state.Budget.Check();
        run.ExpandToEnclosingUnit(TextUnit.TextUnit_Format);
        if (Compare(run, Start, remaining, Start) < 0)
        {
            MoveEndpoint(run, Start, remaining, Start);
        }
        if (Compare(run, End, visible, End) > 0)
        {
            MoveEndpoint(run, End, visible, End);
        }
        if (Compare(run, Start, remaining, Start) != 0 || Compare(run, End, run, Start) <= 0
            || Compare(run, End, visible, End) > 0)
        {
            throw new InvalidOperationException("The provider did not supply a progressing, bounded formatting range.");
        }
        return run;
    }

    private int Compare(IUIAutomationTextRange source, TextPatternRangeEndpoint sourceEndpoint,
        IUIAutomationTextRange target, TextPatternRangeEndpoint targetEndpoint)
    {
        _state.Budget.Check();
        return source.CompareEndpoints(sourceEndpoint, target, targetEndpoint);
    }

    private void MoveEndpoint(IUIAutomationTextRange source, TextPatternRangeEndpoint sourceEndpoint,
        IUIAutomationTextRange target, TextPatternRangeEndpoint targetEndpoint)
    {
        _state.Budget.Check();
        source.MoveEndpointByRange(sourceEndpoint, target, targetEndpoint);
    }

    private bool RangePrivacyKnown(IUIAutomationTextRange range, IUIAutomationElement owner, List<UiAuditSnapshotIssue> issues)
    {
        using var owned = new NativeUiAuditSnapshotComScope(Root, _walker, _automation, owner, range);
        try
        {
            _state.Budget.Check();
            var current = owned.Add(range.GetEnclosingElement());
            for (var depth = 0; current is not null && depth <= _state.Options.MaxDepth; depth++)
            {
                _state.Budget.Check();
                using var process = current.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId, true);
                if (process.VarType != System.Runtime.InteropServices.VarEnum.VT_I4 || process.As<int>() != _scope.ProcessId
                    || !VerifyNativeWindowScope(current)
                    || ReadPrivacy(current, issues) != false)
                {
                    break;
                }
                _state.Budget.Check();
                if (_automation.CompareElements(current, owner))
                {
                    return true;
                }
                _state.Budget.Check();
                current = owned.Add(_walker.GetParentElement(current));
            }
            _state.Issue(issues, "text_privacy_withheld", "A text range's non-password ancestry to its exact TextPattern owner could not be verified; its bounds and attributes were withheld.");
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "text_privacy_withheld", FailureMessage("Text-range privacy", ex));
        }
        return false;
    }

    private UiAuditNodeTextRun ReadTextRun(IUIAutomationTextRange range, string id, List<UiAuditSnapshotIssue> issues)
    {
        var run = new UiAuditNodeTextRun
        {
            Id = id,
            Bounds = ReadTextBounds(range, issues),
            ForegroundColor = ReadAttribute(range, UIA_TEXTATTRIBUTE_ID.UIA_ForegroundColorAttributeId, UiAuditFieldKind.Color, id + " foregroundColor", issues),
            BackgroundColor = ReadAttribute(range, UIA_TEXTATTRIBUTE_ID.UIA_BackgroundColorAttributeId, UiAuditFieldKind.Color, id + " backgroundColor", issues),
            FontSize = ReadAttribute(range, UIA_TEXTATTRIBUTE_ID.UIA_FontSizeAttributeId, UiAuditFieldKind.FontSize, id + " fontSize", issues),
            FontWeight = ReadAttribute(range, UIA_TEXTATTRIBUTE_ID.UIA_FontWeightAttributeId, UiAuditFieldKind.FontWeight, id + " fontWeight", issues),
            IsHidden = ReadAttribute(range, UIA_TEXTATTRIBUTE_ID.UIA_IsHiddenAttributeId, UiAuditFieldKind.Boolean, id + " isHidden", issues),
        };
        return run;
    }

    private UiAuditField ReadAttribute(IUIAutomationTextRange range, UIA_TEXTATTRIBUTE_ID attribute,
        UiAuditFieldKind kind, string label, List<UiAuditSnapshotIssue> issues)
    {
        UiAuditField field;
        try
        {
            _state.Budget.Check();
            using var value = range.GetAttributeValue(attribute);
            field = UiAuditFieldReader.Read(value, kind, ClassifySentinel(value));
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            field = UiAuditFieldReader.Failure(FailureMessage(label, ex));
        }
        RecordFieldIssue(field, label, issues);
        return field;
    }

    private unsafe UiAuditNodeBounds[] ReadTextBounds(IUIAutomationTextRange range, List<UiAuditSnapshotIssue> issues)
    {
        try
        {
            _state.Budget.Check();
            var array = range.GetBoundingRectangles();
            try
            {
                if (array is null)
                {
                    _state.Issue(issues, "text_bounds_unavailable", "The provider returned no text-range rectangle array.", partial: false);
                    return [];
                }
                var numbers = NativeUiAuditSnapshotArrays.ReadDoubles(array, MaximumRectanglesPerRun * 4, out var truncated);
                if (array->rgsabound[0].cElements % 4 != 0)
                {
                    _state.Issue(issues, "text_bounds_error", "The text rectangle array did not contain complete left/top/width/height groups.");
                    return [];
                }
                if (truncated)
                {
                    _state.Issue(issues, "text_bounds_limit", "Text-range rectangles were limited to 32 per run.");
                }
                var rectangles = new List<UiAuditNodeBounds>();
                for (var index = 0; index < numbers.Length; index += 4)
                {
                    var bounds = UiAuditSnapshotTraversal.Bounds(numbers[index], numbers[index + 1], numbers[index + 2], numbers[index + 3]);
                    if (bounds is not null)
                    {
                        rectangles.Add(bounds);
                    }
                    else
                    {
                        _state.Issue(issues, "text_bounds_error", "A negative-sized or nonfinite text rectangle was omitted.");
                    }
                }
                return rectangles.ToArray();
            }
            finally
            {
                if (array is not null)
                {
                    PInvoke.SafeArrayDestroy(array).ThrowOnFailure();
                }
            }
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "text_bounds_error", FailureMessage("Text-range rectangles", ex));
            return [];
        }
    }

    private void TextLimit(List<UiAuditSnapshotIssue> issues)
    {
        if (!issues.Any(issue => issue.Code == "text_run_limit"))
        {
            _state.Issue(issues, "text_run_limit", "Visible ranges or formatting runs were omitted because the per-node text-run limit was reached.");
        }
    }
}
