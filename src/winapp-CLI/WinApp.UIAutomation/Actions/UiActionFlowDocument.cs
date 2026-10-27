// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Strict, bounded action-file serialization and validation, shared by recording and replay.</summary>
public static class UiActionFlowDocument
{
    /// <summary>Maximum UTF-8 recording size.</summary>
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    /// <summary>Maximum actions in one file.</summary>
    public const int MaximumSteps = 10000;
    /// <summary>Maximum characters in a literal or supplied text parameter.</summary>
    public const int MaximumTextLength = 16384;

    /// <summary>Read and validate a JSON flow without executing or resolving any action.</summary>
    public static UiActionFlow Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumFileBytes)
        {
            throw Invalid("Action file exceeds the 4 MiB limit.");
        }

        var content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        return Parse(content);
    }

    /// <summary>Parse strict JSON; duplicate and unknown fields are rejected.</summary>
    public static UiActionFlow Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaximumFileBytes)
        {
            throw Invalid("Action file exceeds the 4 MiB limit.");
        }

        if (content.StartsWith("\uFEFF"u8))
        {
            content = content[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            var flow = JsonSerializer.Deserialize(content, UiActionDocumentJsonContext.Default.UiActionFlow)
                ?? throw Invalid("Action file must contain a JSON object.");
            Validate(flow);
            return flow;
        }
        catch (JsonException)
        {
            throw Invalid("Action file is not valid schema-1 JSON. Unknown fields, incorrect types, and excessive nesting are not accepted.");
        }
    }

    /// <summary>Save a validated flow to a new path, never overwriting an existing recording.</summary>
    public static void WriteNew(string path, UiActionFlow flow)
    {
        Validate(flow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(flow, UiActionDocumentJsonContext.Default.UiActionFlow);
        if (bytes.Length > MaximumFileBytes)
        {
            throw Invalid("Recorded actions exceed the 4 MiB output limit.");
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Validate a flow's supported actions without performing UI operations.</summary>
    public static void Validate(UiActionFlow flow, bool forReplay = false)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.SchemaVersion != 1)
        {
            throw Invalid("Only action-file schemaVersion 1 is supported.");
        }

        if (flow.Status is not ("recorded" or "incomplete"))
        {
            throw Invalid("Action status must be recorded or incomplete.");
        }

        if (forReplay && flow.Status != "recorded")
        {
            throw new UiActionException("incomplete_recording", "This recording is incomplete. Review the stop reason and record a fresh supported flow.");
        }

        ValidateLabel(flow.ProcessName, "processName", 260);
        if (flow.Steps is null || flow.Steps.Count > MaximumSteps || flow.Steps.Any(s => s is null))
        {
            throw Invalid("Action file must contain at most 10000 non-null steps.");
        }

        if (flow.Status == "recorded" && flow.Steps.Count == 0)
        {
            throw Invalid("A completed recording must contain at least one action.");
        }

        if (flow.Warnings is null || flow.Warnings.Count > 100)
        {
            throw Invalid("Action file may contain at most 100 warnings.");
        }

        foreach (var warning in flow.Warnings)
        {
            ValidateLabel(warning, "warning", 1024);
        }

        if (flow.InitialFocus is not null)
        {
            ValidateTarget(flow.InitialFocus);
        }

        long previous = 0;
        foreach (var step in flow.Steps)
        {
            ValidateTarget(step.Target);
            if (step.ElapsedMs < previous || step.ElapsedMs > 3600000)
            {
                throw Invalid("Action timestamps must be ordered and within one hour.");
            }

            previous = step.ElapsedMs;
            ValidateStep(step);
        }
        if (forReplay && flow.InitialFocus is null && flow.Steps[0].Kind is "key" or "text")
        {
            throw Invalid("A keyboard-first recording must declare its initial focus anchor.");
        }
    }

    /// <summary>Require explicit approval and resolve every text parameter before any input is injected.</summary>
    public static void ValidateReplay(UiActionFlow flow, UiActionReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(flow, forReplay: true);
        if (!options.AcceptRecording)
        {
            throw new UiActionException("review_required", "Review the action file and pass --accept-recording before replaying input.");
        }

        if (options.TimeoutMs is < 100 or > 120000)
        {
            throw Invalid("Replay timeout must be between 100 and 120000 milliseconds.");
        }

        if (options.Parameters is null)
        {
            throw Invalid("Replay parameters must not be null.");
        }

        var required = flow.Steps.Where(s => s.Parameter is not null).Select(s => s.Parameter!).ToHashSet(StringComparer.Ordinal);
        foreach (var parameter in options.Parameters)
        {
            ValidateParameterName(parameter.Key);
            if (!required.Contains(parameter.Key))
            {
                throw Invalid("An unused text parameter was supplied; check the reviewed recording's parameter names.");
            }

            ValidateText(parameter.Value);
        }
        if (required.Any(name => !options.Parameters.ContainsKey(name)))
        {
            throw new UiActionException("missing_parameter", "Provide a value for every text parameter declared in the reviewed recording.");
        }
    }

    internal static void ValidateTarget(UiActionTarget target)
    {
        if (target is null)
        {
            throw Invalid("Every action requires a target.");
        }

        ValidateLabel(target.ControlType, "target controlType", 80);
        if ((target.AutomationId is null) == (target.Name is null))
        {
            throw Invalid("A target requires exactly one of automationId or name, plus controlType.");
        }

        if (target.AutomationId is not null)
        {
            ValidateLabel(target.AutomationId, "target automationId", 512);
        }

        if (target.Name is not null)
        {
            ValidateLabel(target.Name, "target name", 512);
        }

        if (target.ControlType is "Edit" or "TextBox" or "ComboBox" && target.AutomationId is null)
        {
            throw Invalid("Editable targets require an AutomationId, not an accessible name derived from text.");
        }
        try
        {
            NativeUiActionRules.ValidateLocator(target);
        }
        catch (UiActionException)
        {
            throw Invalid("Target controlType or locator is not supported. Use an exact control type and a valid static locator.");
        }
    }

    private static void ValidateStep(UiActionStep step)
    {
        if (step.Kind is not ("click" or "key" or "text" or "wheel" or "assert"))
        {
            throw Invalid("Supported action kinds are click, key, text, wheel, and assert.");
        }

        if (!double.IsFinite(step.XFraction) || !double.IsFinite(step.YFraction)
            || step.XFraction is < 0 or > 1 || step.YFraction is < 0 or > 1)
        {
            throw Invalid("Mouse positions must be fractions between zero and one.");
        }

        if (step.Kind != "key" && step.Keys is not null
            || step.Kind != "text" && (step.Text is not null || step.Parameter is not null)
            || step.Kind != "click" && (step.Button is not null || step.ClickCount != 1)
            || step.Kind != "wheel" && step.WheelDelta != 0
            || step.Kind != "assert" && (step.Property is not null || step.Expected is not null)
            || step.Kind is not ("click" or "wheel") && (step.XFraction != 0.5 || step.YFraction != 0.5))
        {
            throw Invalid("An action contains fields belonging to a different action kind.");
        }

        switch (step.Kind)
        {
            case "click":
                if (step.Button is not ("left" or "right") || step.ClickCount is < 1 or > 2)
                {
                    throw Invalid("Click actions require left/right button and clickCount 1 or 2.");
                }

                break;
            case "key":
                ValidateKeys(step.Keys);
                break;
            case "text":
                if ((step.Text is null) == (step.Parameter is null))
                {
                    throw Invalid("Text actions require exactly one of text or parameter.");
                }

                if (step.Target.AutomationId is null)
                {
                    throw Invalid("Text actions require an AutomationId target.");
                }

                if (step.Text is not null)
                {
                    ValidateText(step.Text);
                }

                if (step.Parameter is not null)
                {
                    ValidateParameterName(step.Parameter);
                }

                break;
            case "wheel":
                if (step.WheelDelta == 0 || step.WheelDelta is < -12000 or > 12000)
                {
                    throw Invalid("Wheel delta must be nonzero and between -12000 and 12000.");
                }

                break;
            case "assert":
                if (step.Property is not ("Name" or "Value" or "IsEnabled" or "ToggleState") || step.Expected is null)
                {
                    throw Invalid("Assertions require a supported property and an explicit expected string.");
                }

                ValidateText(step.Expected);
                break;
        }
    }

    internal static void ValidateKeys(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys) || keys.Length > 80)
        {
            throw Invalid("Key actions require one supported key chord.");
        }

        IReadOnlyList<KeyAction> actions;
        try { actions = KeyStringParser.Parse(keys); }
        catch (FormatException) { throw Invalid("Key action is not a valid chord."); }
        if (actions.Count != 1 || actions[0] is not KeyChord chord
            || chord.Modifiers.Any(m => m is not (0x10 or 0x11 or 0x12))
            || chord.Modifiers.Distinct().Count() != chord.Modifiers.Count
            || SystemKeyGuard.FindSystemCombos(actions).Count != 0)
        {
            throw Invalid("Key action must be one non-system chord; literal typing belongs in a text action.");
        }

        var commandModifier = chord.Modifiers.Contains((ushort)0x11) || chord.Modifiers.Contains((ushort)0x12);
        var navigation = chord.Vk is 0x08 or 0x09 or 0x0d or 0x1b or >= 0x20 and <= 0x28
            or 0x2d or 0x2e or >= 0x70 and <= 0x87;
        var shortcut = commandModifier
            && chord.Vk is >= 0x30 and <= 0x5a or >= 0x60 and <= 0x6f or >= 0xba and <= 0xe2;
        if (!navigation && !shortcut)
        {
            throw Invalid("Only navigation keys, function keys, and app shortcuts are replayable key actions; use text steps for typing.");
        }

        if (chord.Modifiers.Contains((ushort)0x11) && chord.Vk is 0x43 or 0x56 or 0x58 or 0x2d
            || chord.Modifiers.Contains((ushort)0x10) && chord.Vk == 0x2d)
        {
            throw Invalid("Clipboard shortcuts are not replayed; provide reviewed replacement text instead.");
        }
    }

    private static void ValidateParameterName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !char.IsAsciiLetter(name[0])
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
        {
            throw Invalid("Parameter names must start with an ASCII letter and contain at most 64 letters, digits, underscores, or hyphens.");
        }
    }

    private static void ValidateText(string? text)
    {
        if (text is null || text.Length > MaximumTextLength
            || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
        {
            throw Invalid("Text exceeds 16384 characters or contains unsupported control characters.");
        }
    }

    private static void ValidateLabel(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw Invalid($"Invalid {field}; a bounded nonempty single-line value is required.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "steps" or "warnings" && property.Value.ValueKind == JsonValueKind.Null)
                {
                    throw Invalid("Action steps and warnings must be arrays, not null.");
                }
                if (!names.Add(property.Name))
                {
                    throw Invalid("Duplicate JSON properties are not accepted.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RejectDuplicateProperties(child);
            }
        }
    }

    private static UiActionException Invalid(string message) => new("invalid_flow", message);
}

[JsonSerializable(typeof(UiActionFlow))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    MaxDepth = 16)]
internal partial class UiActionDocumentJsonContext : JsonSerializerContext;
