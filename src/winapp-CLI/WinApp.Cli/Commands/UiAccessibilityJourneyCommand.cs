// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

internal sealed class UiAccessibilityJourneyCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilityJourneyCommand()
        : base("journey", "Build a source-owned demo, navigate approved pages and run declared keyboard checks. " +
            "--accept-navigation approves launch and those interactions; --apply separately approves supported XAML Name edits and frozen-route regression. " +
            "Without --copilot-plan or --plan, the declared visit order is used. This is bounded planning over declared navigation, not an autonomous observed-AI plan.")
    {
        Input("journey", "Reviewed journey JSON declaring the source project, pages and keyboard exercises.");
        var accept = Flag("--accept-navigation", "Explicitly approve building, launching, navigating and performing the declared keyboard exercises.");
        Output();
        var copilotPlan = Flag("--copilot-plan", "Ask Copilot to order approved navigation candidates; otherwise use the declared order or --plan.");
        var plan = Text("--plan", "Reviewed plan JSON to use instead of --copilot-plan.");
        var apply = Flag("--apply", "Approve supported Name-only source repairs and full frozen-route regression after each edit.");
        Text("--copilot", "Copilot executable for requested planning or repairs (default: copilot).");
        var proposals = Text("--proposals", "Reviewed <TargetAutomationId>.json proposal directory instead of Copilot repair requests; requires --apply.");
        Flag("--no-screenshot", "Do not capture screenshots.");
        Json();

        ValidateOptions(result =>
        {
            if (!result.GetValue(accept))
            {
                result.AddError("--accept-navigation is required to build, launch, navigate and perform declared keyboard exercises.");
            }
            if (result.GetValue(copilotPlan) && result.GetValue(plan) is not null)
            {
                result.AddError("Choose either --copilot-plan or --plan, not both.");
            }
            if (result.GetValue(proposals) is not null && !result.GetValue(apply))
            {
                result.AddError("--proposals requires --apply.");
            }
        });
    }
}

internal sealed class UiAccessibilityLoopCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilityLoopCommand()
        : base("loop", "Copy a source demo into an owned workspace, execute a reviewed preset route, and reproduce it in a new owned process. " +
            "--accept-actions approves preset test-state changes and real keyboard input; --apply separately approves bounded Name repairs. " +
            "The original source is never edited. --replay uses an archived source round (baseline by default). " +
            "Route planning is preset, not autonomous AI; capture completeness and repair verification are separate.")
    {
        var preset = Input("preset", "Reviewed loop preset JSON; omit with --replay.", required: false);
        var replay = Text("--replay", "Previous run directory containing archived source rounds and frozen evidence.");
        var round = Text("--round", "Replay round ID (default: baseline); only valid with --replay.");
        var accept = Flag("--accept-actions", "Explicitly approve building and launching the owned copy, preset state changes and real input.");
        Output(required: true);
        var apply = Flag("--apply", "Approve bounded reviewed-name repairs in the owned source copy, followed by same-route rebuild and retest.");
        var copilot = Text("--copilot", "Copilot executable for repair proposals; requires --apply (default: copilot).");
        var proposals = Text("--proposals", "Reviewed proposal directory instead of Copilot; requires --apply.");
        var noScreenshot = Flag("--no-screenshot", "Do not capture screenshots in a new loop; replay inherits the saved capture policy.");
        Json();

        ValidateOptions(result =>
        {
            if (!result.GetValue(accept))
            {
                result.AddError("--accept-actions is required to build, launch and perform reviewed actions in an owned demo.");
            }
            if ((result.GetValue(preset) is null) == (result.GetValue(replay) is null))
            {
                result.AddError("Supply a preset OR --replay, not both.");
            }
            if (result.GetValue(replay) is not null && (result.GetValue(apply) || result.GetValue(noScreenshot)
                || result.GetValue(copilot) is not null || result.GetValue(proposals) is not null))
            {
                result.AddError("Replay inherits the saved capture policy and cannot request source repairs.");
            }
            if (result.GetValue(round) is { } roundId)
            {
                if (result.GetValue(replay) is null)
                {
                    result.AddError("--round is only valid with --replay.");
                }
                if (!IsIdentifier(roundId))
                {
                    result.AddError("--round must be a literal ASCII identifier (maximum 256 characters), not a path.");
                }
            }
            if ((result.GetValue(copilot) is not null || result.GetValue(proposals) is not null) && !result.GetValue(apply))
            {
                result.AddError("Repair proposal options require --apply.");
            }
            if (result.GetValue(copilot) is not null && result.GetValue(proposals) is not null)
            {
                result.AddError("Choose reviewed --proposals or --copilot, not both.");
            }
        });
    }
}

internal sealed class UiAccessibilityCheckCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilityCheckCommand()
        : base("check", "Build and run a declared source-owned suite or scenario and retain accessibility evidence. " +
            "This is an active workflow requiring --accept-actions, not a passive scan. Exit codes: 0 passed, 1 failed, 2 inconclusive or error.")
    {
        Input("input", "Reviewed suite.json or scenario.json declaring the source app and checks.");
        RequireActions();
        Output();
    }
}

internal sealed class UiAccessibilityRepairCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilityRepairCommand()
        : base("repair", "Run a source-owned suite and propose one constrained XAML Name repair by default. " +
            "Multiple findings require --finding. --apply authorizes editing the source and rerunning every scenario; new regressions reject the patch. " +
            "A fixed finding does not mean the whole suite is clean. Exit codes: 0 fixed, 1 failed or regression, 2 inconclusive, 3 approval, selection or review required.")
    {
        Input("input", "Reviewed suite.json or scenario.json declaring the source app and frozen acceptance checks.");
        RequireActions();
        Text("--finding", "Finding ID to repair; required when several findings are available.");
        Flag("--apply", "Explicitly approve one constrained XAML Name source edit and rerunning all scenarios.");
        Text("--proposal", "Reviewed proposal JSON instead of asking Copilot for a proposal.");
        Text("--copilot", "Copilot executable used when a proposal is needed (default: copilot).");
        Output();
    }
}
