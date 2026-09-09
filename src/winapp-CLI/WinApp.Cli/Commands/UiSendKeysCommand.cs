// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiSendKeysCommand : Command, IShortDescription
{
    public string ShortDescription => "Send synthetic keyboard input (keys, combos, and text) to a window";

    public static Argument<string?> KeysArgument { get; } = new("keys")
    {
        Description = "Keys to send. Whitespace-separated tokens: named keys (down, enter, tab, esc, f5), " +
                      "modifier combos (ctrl+shift+t, alt+f4), raw virtual keys (vk=0x42), or literal text (hello). " +
                      "Use text=<literal> to type a single value verbatim when it would otherwise be read as a key " +
                      "name or combo (text=enter types \"enter\"; text=ctrl+a types \"ctrl+a\"); backslash escapes \\s \\t " +
                      "\\n \\r \\\\ are supported (text=a\\s\\sb types \"a  b\"). To type the whole argument literally " +
                      "without escaping each token, pass --verbatim instead. " +
                      "Quote multi-token strings, e.g. \"ctrl+a delete\".",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Option<string?> TargetOption { get; } = new("--target")
    {
        Description = "Optional selector (slug or text) to focus before sending keys."
    };

    public static Option<string> ViaOption { get; } = new("--via")
    {
        Description = "Transport: post-message (default, HWND-targeted, bypasses UIPI; typed text raises TextChanged " +
                      "but not a per-character KeyDown) or send-input (OS-wide; typed text raises a real per-character " +
                      "KeyDown + TextChanged). Named keys and combos raise KeyDown on both, but keyboard " +
                      "accelerators/shortcuts (KeyboardAccelerator, e.g. ctrl+t) only fire via send-input. " +
                      "post-message targets the focused child control and works for classic Win32/WinForms controls, " +
                      "but WinUI 3 / UWP / XAML controls are windowless and ignore posted messages — use send-input " +
                      "for those (a warning is emitted when the target looks like a XAML app).",
        DefaultValueFactory = _ => "post-message"
    };

    public static Option<bool> VerbatimOption { get; } = new("--verbatim")
    {
        Description = "Type the entire keys argument as literal text — no named-key, combo, or vk= interpretation, " +
                      "and exact whitespace preserved. The whole-argument form of the per-token text= escape: " +
                      "--verbatim \"down down enter\" types the words instead of pressing Down, Down, Enter."
    };

    public static Option<bool> AllowSystemKeysOption { get; } = new("--allow-system-keys")
    {
        Description = "Allow synthesizing system-/shell-reserved combos (win+<key>, alt+f4, alt+tab, ctrl+esc, …) via " +
                      "--via send-input, which are refused by default because they act on the OS/shell beyond the " +
                      "target app. Opt in to drive global hotkeys (e.g. PowerToys' win+shift+v, win+r). " +
                      "No effect on --via post-message (already window-scoped; a warning is emitted if set without send-input). " +
                      "Note: win+l and ctrl+alt+del stay blocked even with this flag — win+l locks the workstation " +
                      "(LockWorkStation() via the shell hook), which is unrecoverable from automation, and ctrl+alt+del " +
                      "is a Secure Attention Sequence (SAS) that Windows drops from injected input regardless of this flag, " +
                      "so it can never take effect."
    };

    public UiSendKeysCommand()
        : base("send-keys", "Send synthetic keyboard input to a window. Supports named keys (down, enter, tab), " +
               "modifier combos (ctrl+shift+t), raw virtual keys (vk=0xNN), and literal text. " +
               "Use --verbatim to type the whole argument literally, or --target to focus an element first. " +
               "Two transports via --via: post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide). " +
               "For per-keystroke KeyDown on typed text (e.g. a WinUI 3/WPF TextBox), use --via send-input.")
    {
        Arguments.Add(KeysArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(TargetOption);
        Options.Add(ViaOption);
        Options.Add(VerbatimOption);
        Options.Add(AllowSystemKeysOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IKeyboardInput keyboardInput,
        IForegroundGuard foregroundGuard,
        IDesktopForegroundService desktopForeground,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiSendKeysCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui send-keys";

        /// <remarks>
        /// Both transports are desktop-exclusive: send-input is OS-wide and post-message still foregrounds
        /// and focuses the target to route keys.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var keysStr = parseResult.GetValue(KeysArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var viaStr = parseResult.GetValue(ViaOption) ?? "post-message";
            var verbatim = parseResult.GetValue(VerbatimOption);
            var allowSystemKeys = parseResult.GetValue(AllowSystemKeysOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            // For --verbatim, whitespace is legitimate content to type (the help promises exact
            // whitespace preservation), so only reject a genuinely empty argument. Without --verbatim,
            // a whitespace-only argument has no tokens to interpret, so it stays an error. (The IsNullOrEmpty
            // operand is also what lets the compiler treat keysStr as non-null on the fall-through path.)
            if (string.IsNullOrEmpty(keysStr) || (!verbatim && string.IsNullOrWhiteSpace(keysStr)))
            {
                logger.LogError("{Symbol} Keys are required. Usage: winapp ui send-keys <keys> -a <app>", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "Keys are required. Usage: winapp ui send-keys <keys> -a <app>");
                return 1;
            }

            if (!TryParseTransport(viaStr, out var transport))
            {
                logger.LogError("{Symbol} Invalid --via value '{Via}'. Use post-message or send-input.", UiSymbols.Error, viaStr);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"Invalid --via value '{viaStr}'. Use post-message or send-input.");
                return 1;
            }

            IReadOnlyList<KeyAction> actions;
            try
            {
                // --verbatim types the whole argument literally (no key/combo/vk=/text= parsing, no
                // whitespace collapsing); otherwise interpret the friendly key grammar token by token.
                // keysStr is non-null here: the guard above rejected null/empty.
                actions = verbatim ? KeyStringParser.ParseVerbatim(keysStr) : KeyStringParser.Parse(keysStr);
            }
            catch (FormatException ex)
            {
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, ex.Message);
                return 1;
            }

            if (transport == KeyTransport.SendInput)
            {
                var neverBypassable = SystemKeyGuard.FindNeverBypassableCombos(actions);
                if (neverBypassable.Count > 0)
                {
                    var names = string.Join(", ", neverBypassable.Select(c => c.Name));
                    var reasons = string.Join(" ", neverBypassable.Select(c => $"{c.Name} {c.Reason}."));
                    logger.LogError(
                        "{Symbol} Refusing to synthesize {Combos} via --via send-input. {Reasons} " +
                        "This stays blocked even with --allow-system-keys, which is for app-registered " +
                        "global hotkeys (e.g. win+r, win+shift+v), not combos that can't be driven from automation.",
                        UiSymbols.Error, names, reasons);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                        $"Refusing to synthesize {names} via --via send-input. {reasons} " +
                        "This stays blocked even with --allow-system-keys, which is for app-registered " +
                        "global hotkeys (e.g. win+r, win+shift+v), not combos that can't be driven from automation.",
                        errorOut: parseResult.InvocationConfiguration.Error);
                    return 1;
                }

                var systemCombos = SystemKeyGuard.FindSystemCombos(actions);
                if (systemCombos.Count > 0 && !allowSystemKeys)
                {
                    logger.LogError(
                        "{Symbol} Refusing to synthesize system-reserved key(s) via --via send-input: {Combos}. " +
                        "These act on the OS/shell (e.g. win+l locks the session, alt+f4 closes the window, ctrl+alt+del is intercepted by Windows), not just the target app. " +
                        "Pass --allow-system-keys to opt in (e.g. to drive a global hotkey).",
                        UiSymbols.Error, string.Join(", ", systemCombos));
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                        $"Refusing to synthesize system-reserved key(s) via --via send-input: {string.Join(", ", systemCombos)}. " +
                        "These act on the OS/shell rather than just the target app. Pass --allow-system-keys to opt in.");
                    return 1;
                }
            }

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight rejected an empty keys argument, so this is non-null by construction.
            var keysStr = parseResult.GetValue(KeysArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var target = parseResult.GetValue(TargetOption);
            var viaStr = parseResult.GetValue(ViaOption) ?? "post-message";
            var verbatim = parseResult.GetValue(VerbatimOption);
            var allowSystemKeys = parseResult.GetValue(AllowSystemKeysOption);

            // Preflight validated the transport, so this cannot fail here.
            _ = TryParseTransport(viaStr, out var transport);

            var warnings = new List<string>();
            if (allowSystemKeys && transport != KeyTransport.SendInput)
            {
                logger.LogWarning(
                    "{Symbol} --allow-system-keys only applies to --via send-input and has no effect with " +
                    "--via post-message (post-message is already window-scoped and never blocks system combos).",
                    UiSymbols.Warning);
                warnings.Add(
                    "--allow-system-keys only applies to --via send-input and has no effect with " +
                    "--via post-message (post-message is already window-scoped and never blocks system combos).");
            }

            // Preflight already proved this parses.
            IReadOnlyList<KeyAction> actions =
                verbatim ? KeyStringParser.ParseVerbatim(keysStr) : KeyStringParser.Parse(keysStr);

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var targetHwnd = uiTarget.WindowHandle;

                UiElement? targetElement = null;
                UiSelector? targetSelector = null;
                if (!string.IsNullOrWhiteSpace(target))
                {
                    targetSelector = selectorParser.Parse(target);
                    targetElement = await uiAutomation.FindSingleElementAsync(uiTarget, targetSelector, cancellationToken);

                    if (targetElement is null)
                    {
                        UiErrors.ElementNotFound(logger, target, json);
                        return 1;
                    }

                    targetHwnd = targetElement.WindowHandle ?? uiTarget.WindowHandle;
                }

                var effectiveHwnd = targetHwnd;
                bool targetLooksXaml;

                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (targetSelector is not null)
                    {
                        targetElement = await uiAutomation.FindSingleElementAsync(uiTarget, targetSelector, cancellationToken);
                        if (targetElement is null)
                        {
                            UiErrors.ElementNotFound(logger, target!, json);
                            return 1;
                        }

                        targetHwnd = targetElement.WindowHandle ?? uiTarget.WindowHandle;
                        effectiveHwnd = targetHwnd;
                    }

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, "send-keys",
                            parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    if (targetHwnd != 0)
                    {
                        var topLevel = systemQuery.GetRootWindow(targetHwnd);
                        desktopForeground.RequestForeground(topLevel != 0 ? topLevel : targetHwnd);
                        await Task.Delay(100, cancellationToken);
                    }

                    if (transport == KeyTransport.SendInput)
                    {
                        if (targetHwnd == 0)
                        {
                            logger.LogError(
                                "{Symbol} --via send-input needs a resolvable target window, but none was found. Pass --window <hwnd>, ensure -a/--app resolves a window, or use --target to focus an element first.",
                                UiSymbols.Error);
                            UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget,
                                "send-input needs a resolvable target window, but none was found — refusing OS-wide keyboard injection without a known target. Pass --window/--app or --target.");
                            return 1;
                        }

                        if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "--via send-input"))
                        {
                            return 1;
                        }
                    }

                    var focusWasApplied = false;
                    if (targetElement is not null)
                    {
                        await uiAutomation.FocusAsync(uiTarget, targetElement, cancellationToken);
                        focusWasApplied = true;
                    }

                    if (focusWasApplied
                        && transport == KeyTransport.SendInput
                        && !foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "--via send-input"))
                    {
                        return 1;
                    }

                    // PostMessage posts to a specific HWND's message queue; a top-level window does NOT
                    // forward keyboard messages to its focused child control, so posting there silently
                    // drops the input for classic Win32 child controls (e.g. an edit box).
                    if (transport == KeyTransport.PostMessage && targetHwnd != 0)
                    {
                        var focused = systemQuery.GetFocusedWindow(targetHwnd);
                        if (focused != 0 && focused != targetHwnd)
                        {
                            var targetRoot = systemQuery.GetRootWindow(targetHwnd);
                            if (targetRoot != 0 && systemQuery.GetRootWindow(focused) == targetRoot)
                            {
                                logger.LogDebug(
                                    "post-message: retargeting from HWND {Target} to focused child HWND {Focused}",
                                    targetHwnd, focused);
                                effectiveHwnd = focused;
                            }
                            else
                            {
                                logger.LogDebug(
                                    "post-message: focused HWND {Focused} is not within target {Target}'s top-level window; keeping target",
                                    focused, targetHwnd);
                            }
                        }
                    }

                    targetLooksXaml =
                        (targetHwnd != 0 && FrameworkHint.IsXamlClassName(systemQuery.GetWindowClassName(targetHwnd)))
                        || (effectiveHwnd != 0 && effectiveHwnd != targetHwnd
                            && FrameworkHint.IsXamlClassName(systemQuery.GetWindowClassName(effectiveHwnd)));

                    keyboardInput.Send(effectiveHwnd, actions, transport);
                }

                if (ShouldWarnPostMessageMayNotDeliver(transport == KeyTransport.PostMessage, targetLooksXaml))
                {
                    const string postMessageXamlWarning =
                        "Input via --via post-message may not reach WinUI 3 / UWP / XAML controls — they are " +
                        "windowless and ignore posted WM_CHAR/WM_KEYDOWN, so keys can be silently dropped even " +
                        "though this command reports success. Use --via send-input if the input does not take effect.";
                    logger.LogWarning("{Symbol} {Message}", UiSymbols.Warning, postMessageXamlWarning);
                    warnings.Add(postMessageXamlWarning);
                }

                if (transport == KeyTransport.SendInput)
                {
                    var systemCombos = SystemKeyGuard.FindSystemCombos(actions);
                    if (systemCombos.Count > 0)
                    {
                        var systemCombosStr = string.Join(", ", systemCombos);
                        logger.LogWarning(
                            "{Symbol} Injecting system-reserved key(s) via --via send-input because --allow-system-keys was set: {Combos}. " +
                            "These act on the OS/shell beyond the target app.",
                            UiSymbols.Warning, systemCombosStr);
                        warnings.Add(
                            $"Injecting system-reserved key(s) via --via send-input because --allow-system-keys was set: {systemCombosStr}. " +
                            "These act on the OS/shell beyond the target app.");
                   }

                   int textChars = actions.OfType<TextInput>().Sum(t => t.Text.Length);
                   if (textChars > KeyboardInput.DefaultTextChunkChars)
                   {
                       logger.LogWarning(
                           "{Symbol} {Count} characters via --via send-input are auto-throttled into paced chunks for reliable delivery, so this may take a moment. For bulk text, 'ui set-value' is faster and more reliable.",
                           UiSymbols.Warning, textChars);
                       warnings.Add(
                           $"{textChars} characters via send-input are auto-throttled into paced chunks for reliable delivery, so this may take a moment. For bulk text, 'ui set-value' is faster and more reliable.");
                   }
                }

               if (json)
                {
                    var result = new UiSendKeysResult
                    {
                        Keys = keysStr,
                        Via = transport == KeyTransport.PostMessage ? "post-message" : "send-input",
                        ActionCount = actions.Count,
                        Target = target,
                        Hwnd = effectiveHwnd,
                        Warnings = warnings
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiSendKeysResult));
                }
                else
                {
                    // Don't echo the raw keystrokes to the human log — in CI those logs are persisted and
                    // shared, and key sequences may carry passwords / tokens being typed into a field. The
                    // structured --json result still includes `keys` for callers that opt in (and it's
                    // already on the command line); the plain log reports only the action count.
                    // PostMessage is fire-and-forget — it only queues the message and can't confirm the
                    // target consumed it — so report it as "Posted" rather than overstating with "Sent";
                    // send-input is real synthesized input and stays "Sent".
                    logger.LogInformation("{Symbol} {Verb} {ActionCount} key action(s) via {Via}",
                        UiSymbols.Check,
                        transport == KeyTransport.PostMessage ? "Posted" : "Sent",
                        actions.Count,
                        transport == KeyTransport.PostMessage ? "post-message" : "send-input");
                }

                return 0;
            }
            catch (ForegroundLostException)
            {
                // Focus left the target partway through a throttled --via send-input injection; the rest of
                // the keystrokes were withheld rather than sprayed into whatever window grabbed focus (issue
                // #657 follow-up H1). Surface the same foreground_not_target contract as the pre-send check.
                logger.LogError(
                    "{Symbol} Target window lost the foreground partway through --via send-input — aborted to avoid typing the rest into the wrong window. Keep the target focused (avoid clicking away or focus-stealing popups) and retry; for bulk text prefer 'ui set-value'.",
                    UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget,
                    "Target window lost the foreground partway through --via send-input — aborted to avoid injecting the rest into the wrong window. Keep the target focused and retry, or use 'ui set-value' for bulk text.");
                return 1;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        /// <summary>
        /// Whether to warn that post-message input may be silently dropped: only when posting
        /// (<paramref name="isPostMessage"/>) AND the target looks like a XAML window (WinUI 3 / UWP),
        /// whose windowless controls ignore posted WM_CHAR/WM_KEYDOWN — so typed text AND named
        /// keys/combos can no-op there. Pure so the gate is unit-testable without a live XAML window;
        /// the command computes <paramref name="targetLooksXaml"/> via FrameworkHint.IsXamlClassName
        /// over the seam-read class name(s) and routes the warning through here.
        /// </summary>
        internal static bool ShouldWarnPostMessageMayNotDeliver(bool isPostMessage, bool targetLooksXaml)
            => isPostMessage && targetLooksXaml;

        private static bool TryParseTransport(string via, out KeyTransport transport)
        {
            switch (via.ToLowerInvariant().Replace("_", "-"))
            {
                case "post-message":
                case "postmessage":
                    transport = KeyTransport.PostMessage;
                    return true;
                case "send-input":
                case "sendinput":
                    transport = KeyTransport.SendInput;
                    return true;
                default:
                    transport = KeyTransport.PostMessage;
                    return false;
            }
        }
    }
}
