// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

internal sealed class MigrationReport
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.1";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "mechanical-migration-complete";

    [JsonPropertyName("source")]
    public required MigrationProject Source { get; set; }

    [JsonPropertyName("target")]
    public required MigrationProject Target { get; set; }

    [JsonPropertyName("summary")]
    public MigrationSummary Summary { get; set; } = new();

    [JsonPropertyName("transforms")]
    public List<MigrationTransform> Transforms { get; set; } = [];

    [JsonPropertyName("todos")]
    public List<MigrationTodo> Todos { get; set; } = [];

    [JsonPropertyName("mechanicalVerification")]
    public MigrationMechanicalVerification MechanicalVerification { get; set; } = new();

    [JsonPropertyName("validation")]
    public MigrationValidation Validation { get; set; } = new();
}

internal sealed class MigrationMechanicalVerification
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not-run";

    [JsonPropertyName("inventory")]
    public MigrationFileInventory Inventory { get; set; } = new();

    [JsonPropertyName("legacyNamespaceResiduals")]
    public List<MigrationLocation> LegacyNamespaceResiduals { get; set; } = [];

    [JsonPropertyName("uninspectedFiles")]
    public int UninspectedFiles { get; set; }

    [JsonPropertyName("projectItems")]
    public MigrationProjectItemVerification ProjectItems { get; set; } = new();
}

internal sealed class MigrationFileInventory
{
    [JsonPropertyName("sourceFiles")]
    public int SourceFiles { get; set; }

    [JsonPropertyName("classifiedFiles")]
    public int ClassifiedFiles { get; set; }

    [JsonPropertyName("copiedFiles")]
    public int CopiedFiles { get; set; }

    [JsonPropertyName("preservedReferenceFiles")]
    public int PreservedReferenceFiles { get; set; }

    [JsonPropertyName("intentionallyExcludedFiles")]
    public int IntentionallyExcludedFiles { get; set; }

    [JsonPropertyName("unclassifiedFiles")]
    public List<string> UnclassifiedFiles { get; set; } = [];
}

internal sealed class MigrationProjectItemVerification
{
    [JsonPropertyName("sourceItems")]
    public int SourceItems { get; set; }

    [JsonPropertyName("migratedItems")]
    public int MigratedItems { get; set; }

    [JsonPropertyName("accountedItems")]
    public List<MigrationProjectItem> AccountedItems { get; set; } = [];

    [JsonPropertyName("unresolvedItems")]
    public List<MigrationLocation> UnresolvedItems { get; set; } = [];

    [JsonPropertyName("missingTargetItems")]
    public List<MigrationLocation> MissingTargetItems { get; set; } = [];
}

internal sealed class MigrationProjectItem
{
    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("requiresProjectEntry")]
    public bool RequiresProjectEntry { get; set; }
}

internal sealed class MigrationValidation
{
    [JsonPropertyName("statePlan")]
    public string StatePlan { get; set; } = ".migration-evidence/state-plan.json";

    [JsonPropertyName("sourceBaseline")]
    public MigrationValidationPhase SourceBaseline { get; set; } = new()
    {
        EvidenceRoot = ".migration-evidence/source"
    };

    [JsonPropertyName("targetReplay")]
    public MigrationValidationPhase TargetReplay { get; set; } = new()
    {
        EvidenceRoot = ".migration-evidence/target"
    };

    [JsonPropertyName("parityStatus")]
    public string ParityStatus { get; set; } = "unverified";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "Behavioral validation has not been completed.";
}

internal sealed class MigrationValidationPhase
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not-run";

    [JsonPropertyName("evidenceRoot")]
    public required string EvidenceRoot { get; set; }

    [JsonPropertyName("states")]
    public List<string> States { get; set; } = [];

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}

internal sealed class MigrationProject
{
    [JsonPropertyName("root")]
    public required string Root { get; set; }

    [JsonPropertyName("projectFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectFile { get; set; }
}

internal sealed class MigrationSummary
{
    [JsonPropertyName("copiedFiles")]
    public int CopiedFiles { get; set; }

    [JsonPropertyName("transformOperations")]
    public int TransformOperations { get; set; }

    [JsonPropertyName("todoCategories")]
    public int TodoCategories { get; set; }
}

internal sealed class MigrationTransform
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("changedFiles")]
    public int ChangedFiles { get; set; }
}

internal sealed class MigrationTodo
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("category")]
    public required string Category { get; set; }

    [JsonPropertyName("priority")]
    public required string Priority { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("locations")]
    public List<MigrationLocation> Locations { get; set; } = [];
}

internal sealed class MigrationLocation
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; set; }
}
