// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

internal sealed class UiAccessibilitySurveyCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilitySurveyCommand()
        : base("survey", "Audit profiled navigation pages in an existing app without source, build, restart or settings-value changes. " +
            "A reviewed navigation profile and --accept-navigation are required. Resume checks the frozen target and environment; " +
            "--confirm-unchanged declares that settings and test data have not changed. " +
            "Exit codes: 0 finished without failures, 1 finished with failures, 2 partial or blocked, 3 paused.")
    {
        var profile = Input("profile", "Reviewed navigation profile JSON for a new survey; omit with --resume.", required: false);
        var resume = Text("--resume", "Existing survey run directory to resume.");
        var pid = Pid();
        var window = Window();
        var output = Output();
        var accept = Flag("--accept-navigation", "Explicitly approve profiled navigation and keyboard focus changes in the selected app.");
        var confirm = Flag("--confirm-unchanged", "Required for resume: confirm app settings and test data have not changed while paused.");
        Number("--stop-after", "Pause after this many pages (1-100).", 1, 100);
        var retry = Identifiers("--retry-page", "Resume-only page ID to retry; repeat for each ID. Previous evidence is retained.");
        var noScreenshot = Flag("--no-screenshot", "Do not capture screenshots in a new survey; resume inherits the saved capture policy.");
        Json();

        ValidateOptions(result =>
        {
            if (!result.GetValue(accept))
            {
                result.AddError("--accept-navigation is required before changing navigation or keyboard focus.");
            }
            if ((result.GetValue(profile) is null) == (result.GetValue(resume) is null))
            {
                result.AddError("Supply either a profile for a new survey or --resume, not both.");
            }
            if (result.GetValue(resume) is not null)
            {
                if (!result.GetValue(confirm))
                {
                    result.AddError("Resume requires --confirm-unchanged: confirm app settings and test data have not changed.");
                }
                if (result.GetValue(pid) is not null || result.GetValue(window) is not null
                    || result.GetValue(output) is not null || result.GetValue(noScreenshot))
                {
                    result.AddError("Resume uses the frozen target, output and capture settings; start a new survey to change them.");
                }
            }
            else
            {
                RequireTarget(result, pid, window);
                if (result.GetValue(output) is null)
                {
                    result.AddError("A new survey requires a new --output directory.");
                }
                if (result.GetValue(confirm) || result.GetValue(retry) is { Length: > 0 })
                {
                    result.AddError("--retry-page and --confirm-unchanged are resume-only options.");
                }
            }
        });
    }
}

internal sealed class UiAccessibilityExploreCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilityExploreCommand()
        : base("explore", "Audit declared pages, finite scroll positions, dropdown disclosure and known dialogs from a reviewed profile. " +
            "--accept-interactions does not approve option selection, feature-setting changes or source repairs. " +
            "--launch-owned additionally builds, launches and closes the profile's local source demo; attaching never owns an existing app. " +
            "Exit codes: 0 finished without failures, 1 complete with failures, 2 partial, 3 paused. Start a fresh run after interruption.")
    {
        Input("profile", "Reviewed exploration profile JSON; required even when attaching to an existing app.");
        var pid = Pid();
        var window = Window();
        var owned = Flag("--launch-owned", "Build, launch and close the local source demo declared in the profile instead of attaching.");
        var accept = Flag("--accept-interactions", "Explicitly approve declared navigation, scrolling, disclosure and safe dialog dismissal.");
        Output(required: true);
        Flag("--no-screenshot", "Do not capture screenshots.");
        var probe = Flag("--probe-scope-guard", "Owned-demo-only safety exercise: open the auxiliary test window declared in the profile.");
        Json();

        ValidateOptions(result =>
        {
            RequireTarget(result, pid, window, owned);
            if (!result.GetValue(accept))
            {
                result.AddError("--accept-interactions is required for declared navigation, scrolling, disclosure and safe dialog dismissal.");
            }
            if (result.GetValue(probe) && !result.GetValue(owned))
            {
                result.AddError("--probe-scope-guard requires --launch-owned and a declared owned-demo test window.");
            }
        });
    }
}
