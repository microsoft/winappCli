// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Diagnostics.Tracing;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Integration tests for the Program-level parse-error → JSON bridge.
/// All tests invoke <see cref="Program.Main"/> directly so the real bridge logic is
/// exercised — not a reimplementation in the test helper.
/// </summary>
/// <remarks>
/// The class is marked <c>[DoNotParallelize]</c> because <see cref="BaseCommandTests.InvokeProgramAsync"/>
/// redirects the process-wide <see cref="Console.Out"/> and <see cref="Console.Error"/> streams.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class ProgramJsonBridgeTests : BaseCommandTests
{
    [TestMethod]
    public async Task Pen_PressureInfinity_Rejected_NoInjection()
    {
        // SCL in the full parse chain parses "Infinity" as float.PositiveInfinity (parse succeeds).
        // The handler's !float.IsFinite guard then rejects it with a JSON invalid_arguments error.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--pressure", "Infinity", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-finite Infinity pressure must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for Infinity pressure");
    }

    // -------------------------------------------------------------------------
    // M4 — parse-time typed-option failures (previously tested via the now-deleted
    //       duplicate bridge in ParseAndInvokeWithCaptureAsync)
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_PressureTypo_Json_EmitsStructuredInvalidArgumentsError()
    {
        // --pressure nope is rejected at SCL parse time (Option<float> cannot parse "nope").
        // The bridge must intercept the error and emit an invalid_arguments JSON envelope.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--pressure", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Bad --pressure must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for bad --pressure");
    }

    [TestMethod]
    public async Task Pen_PressureTypo_NoApp_Json_EmitsInvalidArguments_NotMissingApp()
    {
        // Parse errors are intercepted at Program level before the missing-app check, so a bad
        // --pressure value without -a must return invalid_arguments, not missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--pressure", "nope", "--json"]); // no -a

        Assert.AreEqual(1, exitCode, "Bad --pressure must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) when --pressure is unparseable");
    }

    [TestMethod]
    public async Task Pen_TiltXTypo_Json_EmitsStructuredInvalidArgumentsError()
    {
        // --tilt-x nope is rejected at SCL parse time (Option<int>).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--tilt-x", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Bad --tilt-x must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for bad --tilt-x");
    }

    [TestMethod]
    public async Task Pen_TiltYTypo_Json_EmitsStructuredInvalidArgumentsError()
    {
        // --tilt-y nope is rejected at SCL parse time (Option<int>).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--tilt-y", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Bad --tilt-y must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for bad --tilt-y");
    }

    // -------------------------------------------------------------------------
    // M1 — effective JSON derived from PARSED option, not raw token scan
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_JsonFalse_NoEnvelope_PlainTextError()
    {
        // M1: `--json false` must suppress the bridge — the pre-scan sees --json as present,
        // but the parsed boolean value is false, so effectiveJson = false.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--pressure", "nope", "--json", "false"]);

        Assert.AreEqual(1, exitCode, "Parse error must still exit 1");
        // Stderr must NOT contain a JSON error envelope.
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"--json false must NOT emit a JSON bridge envelope; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_CommandWithoutJsonOption_NoEnvelope()
    {
        // M1: `complete` has no --json option.  Even though the pre-scan sees --json in the
        // raw args, effectiveJson must be false and the bridge must not fire.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["complete", "wat", "--json"]);

        // No JSON bridge envelope on stderr — the bridge must not have fired.
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"Bridge must not fire for a command without --json; got stderr: {stderr}");
        Assert.IsFalse(stdout.Contains("\"error\":"),
            $"Bridge must not fire for a command without --json; got stdout: {stdout}");
    }

    // -------------------------------------------------------------------------
    // M2 — single-dash typo path routes through the JSON bridge in JSON mode
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_SingleDashTypo_WithJson_EmitsJsonEnvelope()
    {
        // M2: `-pressure` is a single-dash long-option typo. Previously it bypassed the bridge
        // because the typo-check returned before the bridge. Now it must emit a JSON envelope
        // when --json is active on the selected command.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-pressure", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Single-dash typo must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"JSON bridge must emit an envelope for single-dash typo in --json mode; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for single-dash typo");
    }

    // -------------------------------------------------------------------------
    // No false-positive: a valid invocation with --json must NOT emit invalid_arguments
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_ValidJsonInvocation_NoFalseInvalidArguments()
    {
        // Regression guard: a successfully parsed --json command with a runtime error (no app)
        // must NOT be caught by the parse-error bridge. It should surface as missing_app, not
        // invalid_arguments, because the parse succeeded (no SCL parse errors).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "1,1", "--json"]); // valid parse; missing -a → missing_app at runtime

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        // Bridge must NOT have fired (no parse errors → no invalid_arguments).
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"Bridge must not fire for a successfully parsed command; got stderr: {stderr}");
        // Runtime error (missing_app) should be in stderr.
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Expected missing_app error; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // #719 — find-ui parse errors must stay machine-readable under --json.
    // find-ui emits its JSON (results and errors) as a flat {"error":"..."} object
    // on STDOUT, so parser-level failures must land there too — not as SCL help text.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task FindUi_MaxNotAnInteger_Json_EmitsFlatJsonErrorOnStdout()
    {
        // The reported repro: `--max abc` fails at SCL parse time (Option<int> can't parse "abc"),
        // before the handler runs. Without the bridge, SCL prints the command description + usage.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["find-ui", "grid", "--max", "abc", "--json"]);

        Assert.AreEqual(1, exitCode, "A bad --max value must exit 1");

        // stdout must be exactly one valid JSON object with a non-empty error string.
        int jsonStart = stdout.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stdout must contain a JSON error object; got stdout: {stdout} / stderr: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stdout.AsSpan(jsonStart).TrimEnd());
        var message = error.GetProperty("error").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(message), "the JSON error payload must carry a message");
        StringAssert.Contains(message, "--max", "the error should name the offending option");

        // The human help text (description/usage) must NOT leak onto stdout.
        Assert.IsFalse(stdout.Contains("Usage:", StringComparison.Ordinal),
            $"stdout must be machine-readable JSON only, not usage help; got: {stdout}");
    }

    [TestMethod]
    public async Task FindUi_SingleDashTypo_Json_EmitsFlatJsonErrorOnStdout()
    {
        // A single-dash long-option typo (`-max`) is caught by the typo validator, which also
        // returns before the handler. Under --json it must emit the flat find-ui error, not text.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["find-ui", "grid", "-max", "3", "--json"]);

        Assert.AreEqual(1, exitCode, "A single-dash option typo must exit 1");
        int jsonStart = stdout.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stdout must contain a JSON error object; got stdout: {stdout} / stderr: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stdout.AsSpan(jsonStart).TrimEnd());
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()),
            "the JSON error payload must carry a message");
    }

    [TestMethod]
    public async Task FindUi_MaxNotAnInteger_NoJson_KeepsHumanText()
    {
        // Guard: without --json the behavior is unchanged — SCL still surfaces its human
        // diagnostic and no JSON object is emitted.
        var (stdout, _, exitCode) = await InvokeProgramAsync(
            ["find-ui", "grid", "--max", "abc"]);

        Assert.AreEqual(1, exitCode, "A bad --max value must exit 1");
        Assert.IsFalse(stdout.TrimStart().StartsWith('{'),
            $"without --json, stdout must not be a JSON error object; got: {stdout}");
    }

    // -------------------------------------------------------------------------
    // M1 — value-aware pre-scan: --json=true / --json=false spellings
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_JsonEqualsTrue_ParseError_EmitsJsonEnvelope()
    {
        // M1: `--json=true` uses the equals-sign spelling. The pre-scan must detect it as
        // json=true so the logger is suppressed and the bridge fires on parse error.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=true", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty (logger suppressed)");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"--json=true must fire the bridge on parse error; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for --json=true parse error");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsTrue_MissingApp_JsonOnlyOnStderr_NoLoggerLine()
    {
        // M1: `--json=true` must suppress the logger (LogLevel.None) so that only the
        // structured JSON error appears on stderr and stdout is clean.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=true", "--at", "100,100", "--app", "__no_such_app__"]);

        Assert.AreEqual(1, exitCode, "Missing/invalid app must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(),
            $"Stdout must be empty when --json=true — no logger line should contaminate stdout; got: {stdout}");
        // A JSON error envelope must appear on stderr (runtime error, not parse error).
        Assert.IsTrue(stderr.Contains('{'),
            $"stderr must contain a JSON error; got: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonFalse_Verbose_NoConflict()
    {
        // M1: `--json false --verbose` must NOT trigger the json/verbose conflict check.
        // The pre-scan must correctly resolve --json as false (not true) when followed by "false".
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json", "false", "--verbose", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        // Must NOT emit the conflict error.
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json false must not trigger the --json/--verbose conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json false must not trigger the --json/--verbose conflict; got stderr: {stderr}");
        // Must NOT emit a JSON bridge envelope (json is false).
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"--json false must not emit a bridge envelope; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // M1 (round 12) — reject a non-boolean '='-attached value on a global bool flag
    // (e.g. --json=bogus) with a single clean error instead of emitting both the
    // human ❌ log line and the machine-readable JSON envelope.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_JsonEqualsBogus_MissingApp_EmitsInvalidArguments_NotMissingApp()
    {
        // System.CommandLine coerces --json=bogus to true (no parse error) while the value-aware
        // pre-scan reads it as false. Previously that mismatch printed BOTH the human ❌ logger line
        // and the missing_app JSON envelope on stderr, corrupting --json output. The invalid attached
        // value must now be rejected up front, so the command never runs and neither the ❌ line nor
        // a missing_app envelope is produced — only a single invalid_arguments error.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--json=bogus"]);

        Assert.AreEqual(1, exitCode, "Invalid --json value must exit 1");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"Expected a JSON error envelope; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            $"--json=bogus must be rejected as invalid_arguments; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Command must not run, so no missing_app envelope should appear; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains("No running app found"),
            $"The human ❌ logger line must not leak alongside the JSON error; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_VerboseEqualsBogus_RejectedAsInvalidArguments_PlainText()
    {
        // The same invalid-attached-boolean guard applies to the other global bool flags. With no
        // --json in play, --verbose=bogus must be rejected with a plain-text invalid-argument message
        // rather than silently ignoring the bad value and running the command.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--verbose=bogus"]);

        Assert.AreEqual(1, exitCode, "Invalid --verbose value must exit 1");
        Assert.IsTrue(stderr.Contains("for option '--verbose'"),
            $"Expected a plain-text invalid-argument message for --verbose; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"A non-json flag must not emit a JSON envelope; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains("No running app found"),
            $"Command must not run; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsFalse_NotRejectedAsInvalid()
    {
        // Guard: the invalid-attached-boolean rejection must NOT fire for the valid --json=false
        // spelling. The command should proceed to its normal missing_app runtime path, not an
        // invalid_arguments error.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--json=false"]);

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"--json=false is valid and must not be rejected as invalid_arguments; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // M3 — bridge is scoped to ui descendants only
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_NewCommand_ParseError_EmitsNewCommandResultJson()
    {
        // `--template` is free-form (validated at runtime), so a bad value no longer fails at parse
        // time. A trailing `--template` with no value is a genuine System.CommandLine parse error.
        // Without the bridge, System.CommandLine prints human help/error text and JSON callers (agents)
        // get no machine-readable result. The bridge must emit a flat NewCommandResult on stdout (where
        // `new`'s success JSON also goes).
        var (stdout, _, exitCode) = await InvokeProgramAsync(
            ["new", "--json", "--template"]);

        Assert.AreEqual(NewCommand.ExitInvalidArgs, exitCode,
            "A `new` parse error must return the same ExitInvalidArgs (2) the handler uses for invalid names/versions.");

        int jsonStart = stdout.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"new parse error with --json must emit a NewCommandResult on stdout; got stdout: {stdout}");
        var result = JsonSerializer.Deserialize<JsonElement>(stdout.AsSpan(jsonStart).TrimEnd());
        Assert.IsFalse(result.GetProperty("Created").GetBoolean(),
            "A parse failure must report Created=false.");
        var error = result.GetProperty("Error").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(error),
            $"The parse error must be surfaced in the JSON Error field; got: {stdout}");
    }

    [TestMethod]
    public async Task JsonBridge_NewCommand_InvalidBooleanValue_EmitsNewCommandResultJson()
    {
        // `--force=bogus` is rejected by the early invalid-boolean handler in Main, before the main
        // parse-error bridge. That early exit must still route `new --json` through the structured
        // envelope (and ExitInvalidArgs) instead of emitting human text.
        var (stdout, _, exitCode) = await InvokeProgramAsync(
            ["new", "--json", "--force=bogus"]);

        Assert.AreEqual(NewCommand.ExitInvalidArgs, exitCode,
            "An invalid boolean value on `new --json` must return ExitInvalidArgs.");
        int jsonStart = stdout.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"new invalid-boolean with --json must emit a NewCommandResult on stdout; got stdout: {stdout}");
        var result = JsonSerializer.Deserialize<JsonElement>(stdout.AsSpan(jsonStart).TrimEnd());
        Assert.IsFalse(result.GetProperty("Created").GetBoolean());
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.GetProperty("Error").GetString()));
    }

    [TestMethod]
    public async Task JsonBridge_NewCommand_SingleDashTypo_EmitsNewCommandResultJson()
    {
        // `-template blank` (single dash) is caught by the typo handler in Main, before the main
        // parse-error bridge. It must also route `new --json` through the structured envelope.
        var (stdout, _, exitCode) = await InvokeProgramAsync(
            ["new", "--json", "-template", "blank"]);

        Assert.AreEqual(NewCommand.ExitInvalidArgs, exitCode,
            "A single-dash typo on `new --json` must return ExitInvalidArgs.");
        int jsonStart = stdout.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"new typo with --json must emit a NewCommandResult on stdout; got stdout: {stdout}");
        var result = JsonSerializer.Deserialize<JsonElement>(stdout.AsSpan(jsonStart).TrimEnd());
        Assert.IsFalse(result.GetProperty("Created").GetBoolean());
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.GetProperty("Error").GetString()));
    }

    [TestMethod]
    public async Task JsonBridge_NonUiCommand_ParseError_NoNestedUiSchema()
    {
        // M3: `cert info --bogus nope --json` has a parse error. The bridge must NOT impose the
        // UI nested schema {"error":{"code":"...","message":"..."}} on non-ui commands. The cert
        // command uses a flat schema or default SCL error output.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["cert", "info", "--bogus", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");

        // Deserialize structurally rather than string-matching so the assertion is robust against
        // JSON formatting differences (e.g. "error": { vs "error":{) — M4 regression fix.
        static bool HasNestedUiErrorSchema(string text)
        {
            var idx = text.IndexOf('{');
            if (idx < 0) { return false; }
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(text.AsSpan(idx).TrimEnd());
                return el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("error", out var errProp)
                    && errProp.ValueKind == JsonValueKind.Object
                    && errProp.TryGetProperty("code", out _);
            }
            catch { return false; }
        }

        Assert.IsFalse(HasNestedUiErrorSchema(stdout),
            $"Non-ui command must NOT get the nested UI error schema on stdout; got: {stdout}");
        Assert.IsFalse(HasNestedUiErrorSchema(stderr),
            $"Non-ui command must NOT get the nested UI error schema on stderr; got: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_UiPen_ParseError_StillGetsNestedSchema()
    {
        // M3 regression guard: ui pen must still get the nested UI schema after the bridge is scoped.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--pressure", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"ui pen must still get the nested UI error schema; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "ui pen error.code must be 'invalid_arguments'");
    }

    // -------------------------------------------------------------------------
    // M4 — UiTouch: app-independent arg validation before missing-app check
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Touch_UnknownGesture_NoApp_Json_EmitsInvalidArguments()
    {
        // M4: --gesture nope is invalid regardless of --app. Must return invalid_arguments, not missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--gesture", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid gesture must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for unknown gesture");
    }

    [TestMethod]
    public async Task Touch_BadAtCoord_NoApp_Json_EmitsInvalidArguments()
    {
        // M4: --at nope is an invalid coordinate regardless of --app. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid --at must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for bad --at coordinate");
    }

    [TestMethod]
    public async Task Touch_TooManyFingers_NoApp_Json_EmitsInvalidArguments()
    {
        // M4: --fingers 99 exceeds the touch-injection contact limit. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "100,100", "--fingers", "99", "--json"]);

        Assert.AreEqual(1, exitCode, "--fingers 99 must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for --fingers out of range");
    }

    [TestMethod]
    public async Task Touch_ValidArgs_NoApp_Json_EmitsMissingApp()
    {
        // M4 regression guard: valid args with no app must still return missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Valid args + no app must return missing_app; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"Valid args + no app must NOT return invalid_arguments; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // M5 — UiPen: --path and --at parsing before missing-app check
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_BadPath_NoApp_Json_EmitsInvalidArguments()
    {
        // M5: --path nope is an invalid path regardless of --app. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--path", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid --path must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for bad --path");
    }

    [TestMethod]
    public async Task Pen_BadAt_NoApp_Json_EmitsInvalidArguments()
    {
        // M5: --at nope is an invalid coordinate regardless of --app. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid --at must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for bad --at coordinate");
    }

    [TestMethod]
    public async Task Pen_ValidArgs_NoApp_Json_EmitsMissingApp()
    {
        // M5 regression guard: valid args with no app must still return missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Valid args + no app must return missing_app; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"Valid args + no app must NOT return invalid_arguments; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // M1 (round-10) — pen/touch: --app <non-existent> must return missing_app,
    // not internal_error.  Both commands must agree (parity regression).
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_AppNotFound_WithAt_ReturnsMissingApp()
    {
        // When --app names a process that is not running, the session resolver throws
        // InvalidOperationException. The command must catch it as missing_app, not bubble
        // it to the generic handler (which emits internal_error).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "10,10", "--app", "__no_such_app_9b3a__", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-existent app must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Non-existent --app with valid --at must return missing_app, not internal_error");
    }

    [TestMethod]
    public async Task Pen_AppNotFound_WithPath_ReturnsMissingApp()
    {
        // Same as above but with --path instead of --at (covers the path-from-option code branch).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--path", "10,10 20,20", "--app", "__no_such_app_9b3a__", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-existent app must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Non-existent --app with valid --path must return missing_app, not internal_error");
    }

    [TestMethod]
    public async Task Touch_AppNotFound_WithAt_ReturnsMissingApp()
    {
        // Parity test: touch must agree with pen — both must return missing_app for a non-existent app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "10,10", "--app", "__no_such_app_9b3a__", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-existent app must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Non-existent --app with valid --at must return missing_app for touch (parity with pen)");
    }

    [TestMethod]
    public async Task PenAndTouch_NoTarget_AppNotFound_BothReturnInvalidArguments()
    {
        // When both the target and the app are missing/invalid, the target-required check fires
        // first (before session resolution), so both pen and touch return invalid_arguments,
        // not missing_app. This verifies the ordering is consistent across the two commands.
        var (_, penStderr, penExit) = await InvokeProgramAsync(
            ["ui", "pen", "--app", "__no_such_app_9b3a__", "--json"]); // no --at/--path/selector
        var (_, touchStderr, touchExit) = await InvokeProgramAsync(
            ["ui", "touch", "--gesture", "tap", "--app", "__no_such_app_9b3a__", "--json"]); // no --at/selector

        Assert.AreEqual(1, penExit, "Pen must exit 1 with no target");
        Assert.AreEqual(1, touchExit, "Touch must exit 1 with no target");

        int penJsonStart = penStderr.IndexOf('{');
        Assert.IsTrue(penJsonStart >= 0, $"pen stderr must have JSON; got: {penStderr}");
        var penError = JsonSerializer.Deserialize<JsonElement>(penStderr.AsSpan(penJsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            penError.GetProperty("error").GetProperty("code").GetString(),
            "Pen with no target must return invalid_arguments (target check before session resolution)");

        int touchJsonStart = touchStderr.IndexOf('{');
        Assert.IsTrue(touchJsonStart >= 0, $"touch stderr must have JSON; got: {touchStderr}");
        var touchError = JsonSerializer.Deserialize<JsonElement>(touchStderr.AsSpan(touchJsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            touchError.GetProperty("error").GetProperty("code").GetString(),
            "Touch with no target must return invalid_arguments (parity with pen)");
    }

    // -------------------------------------------------------------------------
    // M2 (round-10) — single-dash typo bridge is gated by IsUiDescendant
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_SingleDashTypo_NonUiCommand_NoNestedUiSchema()
    {
        // M2 regression: a single-dash typo on a non-UI command with --json must NOT receive
        // the nested UI {"error":{"code":"...","message":"..."}} schema. Before the fix the
        // IsUiDescendant gate was absent from the typo path, so cert info got the wrong schema.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["cert", "info", "file.pfx", "-password", "x", "--json"]);

        Assert.AreEqual(1, exitCode, "Single-dash typo must exit 1");

        // Structural check (robust against formatted JSON whitespace differences — M4 fix applied).
        static bool HasNestedUiErrorSchema(string text)
        {
            var idx = text.IndexOf('{');
            if (idx < 0) { return false; }
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(text.AsSpan(idx).TrimEnd());
                return el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("error", out var errProp)
                    && errProp.ValueKind == JsonValueKind.Object
                    && errProp.TryGetProperty("code", out _);
            }
            catch { return false; }
        }

        Assert.IsFalse(HasNestedUiErrorSchema(stdout),
            $"Non-ui single-dash typo must NOT emit nested UI schema on stdout; got: {stdout}");
        Assert.IsFalse(HasNestedUiErrorSchema(stderr),
            $"Non-ui single-dash typo must NOT emit nested UI schema on stderr; got: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_SingleDashTypo_UiCommand_StillGetsNestedSchema()
    {
        // M2 regression guard: a single-dash typo on a ui command must still get the nested schema.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-pressure", "0.5", "--json"]);

        Assert.AreEqual(1, exitCode, "Single-dash typo must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"ui command single-dash typo must still get nested UI schema; got stderr: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "ui pen single-dash typo must return invalid_arguments in nested schema");
    }

    [TestMethod]
    public async Task JsonBridge_SingleDashTypo_UiCommand_LogsCommandTelemetryContext()
    {
        using var telemetry = new TelemetryCaptureListener();

        var (_, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-pressure", "0.5", "--json"]);

        Assert.AreEqual(1, exitCode, "Single-dash typo must exit 1");
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"single-dash typo must still emit the JSON invalid_arguments envelope; got stderr: {stderr}");

        Assert.IsTrue(telemetry.ContainsCommandEvent(
                "CommandInvoked_Event", "WinApp.Cli.Commands.UiPenCommand"),
            "Single-dash parse errors must log the same invoked command context as the normal parse-error bridge.");
        Assert.IsTrue(telemetry.ContainsCommandEvent(
                "CommandCompleted_Event", "WinApp.Cli.Commands.UiPenCommand", expectedExitCode: 1),
            "Single-dash parse errors must log command completion telemetry with exit code 1.");
    }

    // -------------------------------------------------------------------------
    // M2 (round-11) — boolean pre-scan: --json=<invalid> must NOT be coerced
    // to true and must NOT trigger the --json/--verbose conflict check.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_JsonEqualsBogus_Verbose_NoConflict_InvalidValueError()
    {
        // M2 root-cause fix: --json=bogus must NOT trigger the spurious --json/--verbose conflict
        // that the old pre-scan produced (it treated any non-"false" attached value as true and
        // then fired the conflict check). With bool.TryParse the pre-scan returns false for
        // "bogus", so no conflict fires and the command proceeds normally.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "10,10", "--app", "__no_such__", "--json=bogus", "--verbose"]);

        Assert.AreEqual(1, exitCode, "Invalid --json value must exit 1");
        // The critical invariant: no conflict error.
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the --json/--verbose conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the --json/--verbose conflict; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsBogus_WithBadArg_InvalidValueError_NoConflict()
    {
        // Combining --json=bogus with another invalid arg: must produce a parse/error result
        // but NOT a --json/--verbose conflict.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=bogus", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Invalid args must exit 1");
        // No conflict error (the key invariant).
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the conflict; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsFalse_Verbose_Regression_NoConflict()
    {
        // Regression: --json=false (valid bool false) combined with --verbose must still produce
        // no conflict (this was already fixed in round 9; keep it locked).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=false", "--verbose", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json=false must not trigger the conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json=false must not trigger the conflict; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"--json=false must not emit a bridge envelope; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // Round 13 — the invalid-attached-boolean rejection is generalized from the three global
    // flags to EVERY bool option reachable by the selected command (H1: e.g. --eraser on
    // `ui pen`), scans ALL occurrences so a later bad value is caught (M2), fires BEFORE the
    // first-run notice so a cold cache does not leak a banner to stdout (M1), and the
    // verbose/quiet conflict checks are value-aware (M3). InvokeProgramAsync now isolates the
    // real first-run/update cache from Program.Main (M5).
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_EraserEqualsBogus_Json_RejectedAsInvalidArguments_NoErase()
    {
        // H1: --eraser is a COMMAND-level Option<bool>. System.CommandLine silently coerces
        // --eraser=bogus to true, which would switch the pen to its eraser end. The generalized
        // guard must reject ANY invalid '='-attached bool value — not just the global flags — so
        // the command never runs and no eraser stroke is injected.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--eraser=bogus", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid --eraser value must exit 1");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"Expected a JSON error envelope; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            $"--eraser=bogus must be rejected as invalid_arguments; got stderr: {stderr}");
        Assert.IsTrue(error.GetProperty("error").GetProperty("message").GetString()!.Contains("--eraser"),
            $"The error message must name the offending --eraser option; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Command must not run, so no missing_app envelope should appear; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task Pen_EraserEqualsBogus_NoJson_RejectedAsInvalidArguments_PlainText()
    {
        // H1 without --json: a command-level bool with a bad attached value is rejected with a
        // plain-text message naming --eraser, and no JSON envelope is emitted.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--eraser=bogus"]);

        Assert.AreEqual(1, exitCode, "Invalid --eraser value must exit 1");
        Assert.IsTrue(stderr.Contains("for option '--eraser'"),
            $"Expected a plain-text invalid-argument message for --eraser; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"No --json in play, so no JSON envelope should be emitted; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonValidThenBogus_Rejected_NotAcceptedAsTrue()
    {
        // M2: a repeated option where an EARLIER occurrence is valid and a LATER one is invalid
        // (--json=true --json=bogus) must still be rejected. The scan must not early-return on the
        // first valid occurrence.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--json=true", "--json=bogus"]);

        Assert.AreEqual(1, exitCode, "Duplicate --json with a later invalid value must exit 1");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"Expected a JSON error envelope; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            $"--json=true --json=bogus must be rejected as invalid_arguments; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Command must not run; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsBogus_ColdCache_NoFirstRunBannerOnStdout()
    {
        // M1: on a COLD cache (no .first-run-complete marker), the invalid-bool rejection must fire
        // BEFORE the first-run notice, so the first-run banner never contaminates stdout. Only the
        // JSON invalid_arguments error is emitted, on stderr; stdout stays empty. (This test also
        // exercises the M5 isolation: coldCache:true only reproduces a true first run because the
        // harness redirects the global winapp dir to a fresh, markerless temp cache.)
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--json=bogus"],
            coldCache: true);

        Assert.AreEqual(1, exitCode, "Invalid --json value must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(),
            $"On a cold cache the first-run banner must not print to stdout; got stdout: {stdout}");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"Expected a JSON error envelope on stderr; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            $"--json=bogus must be rejected as invalid_arguments; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task VerboseFalse_Json_NoFalseConflict()
    {
        // M3: `--verbose false --json` must NOT trigger the verbose/json conflict. The value-aware
        // scan resolves --verbose to false, so the command proceeds to its normal missing_app path.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--verbose", "false", "--json", "--at", "100,100", "--app", "__no_such_app__"]);

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--verbose false must not trigger the conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--verbose false must not trigger the conflict; got stderr: {stderr}");
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeMissingApp),
            $"With no real conflict the command must reach the missing_app path; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task VerboseEqualsTrue_Json_TriggersConflict()
    {
        // M3: `--verbose=true --json` DOES conflict — the value-aware scan resolves --verbose to
        // true, so the mutually-exclusive verbose/json conflict must fire and short-circuit.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--verbose=true", "--json", "--at", "100,100", "--app", "__no_such_app__"]);

        Assert.AreEqual(1, exitCode, "verbose/json conflict must exit 1");
        Assert.IsTrue(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--verbose=true --json must trigger the conflict; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // `target ...` — a mistyped option must come back as the same error envelope
    // every other `target` failure uses, so a caller parsing JSON never has to
    // parse help text instead.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Target_OptionTypo_Json_EmitsTheTargetErrorEnvelopeOnStderr()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["target", "record", "sandbox", "-o", "out.mp4", "--duration-sec", "nope", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout carries results only, never diagnostics.");

        var jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must carry a JSON error envelope; got: {stderr}");

        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd())
            .GetProperty("error");

        Assert.AreEqual(
            ExecutionTargetErrorCodes.TargetInvalidArguments,
            error.GetProperty("code").GetString());
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()),
            "The message must say what was wrong with the command line.");
    }

    [TestMethod]
    public async Task Target_UnknownOption_Json_EmitsJsonRatherThanHelp()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["target", "snapshot", "sandbox", "--nonsense", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, stdout.Trim());
        Assert.IsFalse(stderr.Contains("Usage:", StringComparison.Ordinal), $"Help is not JSON; got: {stderr}");
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.TargetInvalidArguments);
    }

    [TestMethod]
    public async Task Target_OptionTypo_WithoutJson_StillExplainsItselfInPlainText()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["target", "record", "sandbox", "-o", "out.mp4", "--duration-sec", "nope"]);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsFalse(
            (stdout + stderr).Contains(ExecutionTargetErrorCodes.TargetInvalidArguments, StringComparison.Ordinal),
            "Without --json a human gets the ordinary command-line diagnostics, not a machine envelope.");
    }

    [TestMethod]
    public async Task InvokeProgramAsync_RestoresEnvironment_AfterIsolatedRun()
    {
        // M5: the isolation harness sets WINAPP_CLI_CACHE_DIRECTORY / WINAPP_CLI_UPDATE_CHECK for
        // the duration of the Program.Main call and must restore them afterwards, so invoking
        // Program.Main from a test never leaks an override (or mutates the real cache) into later
        // tests.
        var beforeCache = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        var beforeUpdate = Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK");

        _ = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--app", "__no_such_app__", "--json=bogus"]);

        Assert.AreEqual(beforeCache, Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY"),
            "WINAPP_CLI_CACHE_DIRECTORY must be restored after InvokeProgramAsync");
        Assert.AreEqual(beforeUpdate, Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK"),
            "WINAPP_CLI_UPDATE_CHECK must be restored after InvokeProgramAsync");
    }

    private sealed class TelemetryCaptureListener : EventListener
    {
        private const string ProviderName = "Microsoft.Windows.WinAppDevCLI";
        private readonly List<(string EventName, Dictionary<string, object?> Payload)> events = [];
        private readonly Lock gate = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == ProviderName)
            {
                EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < eventData.PayloadNames?.Count; i++)
            {
                payload[eventData.PayloadNames[i]] = eventData.Payload is null || i >= eventData.Payload.Count
                    ? null
                    : eventData.Payload[i];
            }

            lock (gate)
            {
                events.Add((eventData.EventName ?? string.Empty, payload));
            }
        }

        public bool ContainsCommandEvent(string eventName, string commandName, int? expectedExitCode = null)
        {
            lock (gate)
            {
                return events.Any(e =>
                    e.EventName == eventName
                    && e.Payload.TryGetValue("CommandName", out var actualCommand)
                    && string.Equals(actualCommand?.ToString(), commandName, StringComparison.Ordinal)
                    && (expectedExitCode is null
                        || (e.Payload.TryGetValue("ExitCode", out var actualExitCode)
                            && string.Equals(actualExitCode?.ToString(), expectedExitCode.Value.ToString(), StringComparison.Ordinal))));
            }
        }
    }
}
