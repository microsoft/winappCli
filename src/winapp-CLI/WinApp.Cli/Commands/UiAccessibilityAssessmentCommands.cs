// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

internal sealed class UiAccessibilityAssessmentTemplateCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilityAssessmentTemplateCommand()
        : base("assessment-template", "Create a supplementary assessment template for an existing scan. " +
            "Fill evidence-based entries before attaching; no assessment or automatic verdict is inferred.")
    {
        Input("scan", "Original scan.json file.");
        Text("--output", "New assessment JSON file; existing files are never overwritten.", required: true);
        var author = Text("--author", "Explicit assessment author name (maximum 256 characters).", required: true);
        var source = Text("--source", "Assessment author kind: human or ai.", defaultValue: "human");
        source.HelpName = "human|ai";
        source.AcceptOnlyFromAmong("human", "ai");
        var model = Text("--model", "Model name, required for --source ai (maximum 256 characters).");
        ValidateOptions(result =>
        {
            if (result.GetValue(source) == "ai" && result.GetValue(model) is null)
            {
                result.AddError("--source ai requires --model.");
            }
            if (result.GetValue(author)?.Length > 256 || result.GetValue(model)?.Length > 256)
            {
                result.AddError("--author and --model must be at most 256 characters.");
            }
        });
    }
}

internal sealed class UiAccessibilityAssessCommand : UiAccessibilityOperationCommand
{
    public UiAccessibilityAssessCommand()
        : base("assess", "Attach an evidence-based supplementary assessment and write a combined report. " +
            "The original automatic scan results and artifacts remain unchanged.")
    {
        Input("scan", "Original scan.json file.");
        Text("--assessment", "Reviewed assessment JSON file.", required: true);
        Output(required: true);
    }
}
