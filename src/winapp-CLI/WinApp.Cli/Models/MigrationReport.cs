// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

internal sealed class MigrationReport
{
    internal const string CurrentSchemaVersion = "1.3";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

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

    [JsonPropertyName("dependencyAnalysis")]
    public MigrationDependencyAnalysis DependencyAnalysis { get; set; } = new();

    [JsonPropertyName("activationAnalysis")]
    public MigrationActivationAnalysis ActivationAnalysis { get; set; } = new();

    [JsonPropertyName("projectItemDecisions")]
    public List<MigrationProjectItemDecision> ProjectItemDecisions { get; set; } = [];

    [JsonPropertyName("mechanicalVerification")]
    public MigrationMechanicalVerification MechanicalVerification { get; set; } = new();

    [JsonPropertyName("validation")]
    public MigrationValidation Validation { get; set; } = new();
}

internal sealed class MigrationActivationAnalysis
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not-run";

    [JsonPropertyName("sourceManifest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceManifest { get; set; }

    [JsonPropertyName("targetManifest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetManifest { get; set; }

    [JsonPropertyName("contracts")]
    public List<MigrationActivationContract> Contracts { get; set; } = [];

    [JsonPropertyName("issues")]
    public List<MigrationActivationIssue> Issues { get; set; } = [];
}

internal sealed class MigrationActivationContract
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("category")]
    public required string Category { get; set; }

    [JsonPropertyName("sourceLocation")]
    public required MigrationLocation SourceLocation { get; set; }

    [JsonPropertyName("sourceSchema")]
    public required string SourceSchema { get; set; }

    [JsonPropertyName("protocolName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolName { get; set; }

    [JsonPropertyName("associationName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssociationName { get; set; }

    [JsonPropertyName("supportedFileTypes")]
    public List<MigrationActivationFileType> SupportedFileTypes { get; set; } = [];

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("logo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logo { get; set; }

    [JsonPropertyName("desiredView")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DesiredView { get; set; }

    [JsonPropertyName("returnResults")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnResults { get; set; }

    [JsonPropertyName("multiSelectModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MultiSelectModel { get; set; }

    [JsonPropertyName("migrationStatus")]
    public string MigrationStatus { get; set; } = "review-required";

    [JsonPropertyName("targetSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetSchema { get; set; }

    [JsonPropertyName("targetLocation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MigrationLocation? TargetLocation { get; set; }

    [JsonPropertyName("verificationStatus")]
    public string VerificationStatus { get; set; } = "not-run";

    [JsonPropertyName("verificationReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerificationReason { get; set; }
}

internal sealed class MigrationActivationFileType
{
    [JsonPropertyName("extension")]
    public required string Extension { get; set; }

    [JsonPropertyName("contentType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; set; }
}

internal sealed class MigrationActivationIssue
{
    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "review-required";

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MigrationLocation? Location { get; set; }

    [JsonPropertyName("contractId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractId { get; set; }
}

internal sealed class MigrationDependencyAnalysis
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not-run";

    [JsonPropertyName("projects")]
    public List<MigrationDependencyProject> Projects { get; set; } = [];

    [JsonPropertyName("packageReferences")]
    public List<MigrationPackageReference> PackageReferences { get; set; } = [];

    [JsonPropertyName("projectReferences")]
    public List<MigrationProjectReference> ProjectReferences { get; set; } = [];

    [JsonPropertyName("issues")]
    public List<MigrationDependencyIssue> Issues { get; set; } = [];
}

internal sealed class MigrationDependencyIssue
{
    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("sourceProject")]
    public required string SourceProject { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }
}

internal sealed class MigrationDependencyProject
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("targetFrameworks")]
    public List<string> TargetFrameworks { get; set; } = [];

    [JsonPropertyName("targetPlatformIdentifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetPlatformIdentifier { get; set; }

    [JsonPropertyName("targetPlatformVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetPlatformVersion { get; set; }

    [JsonPropertyName("targetPlatformMinVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetPlatformMinVersion { get; set; }
}

internal sealed class MigrationPackageReference
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("sourceProject")]
    public required string SourceProject { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonPropertyName("resolutionStatus")]
    public string ResolutionStatus { get; set; } = "declared";

    [JsonPropertyName("compatibilityStatus")]
    public string CompatibilityStatus { get; set; } = "review-required";
}

internal sealed class MigrationProjectReference
{
    [JsonPropertyName("include")]
    public required string Include { get; set; }

    [JsonPropertyName("resolvedPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedPath { get; set; }

    [JsonPropertyName("sourceProject")]
    public required string SourceProject { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonPropertyName("resolutionStatus")]
    public string ResolutionStatus { get; set; } = "unresolved";

    [JsonPropertyName("outsideSourceRoot")]
    public bool OutsideSourceRoot { get; set; }

    [JsonPropertyName("compatibilityStatus")]
    public string CompatibilityStatus { get; set; } = "review-required";
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

    [JsonPropertyName("activationContracts")]
    public MigrationActivationVerification ActivationContracts { get; set; } = new();
}

internal sealed class MigrationActivationVerification
{
    [JsonPropertyName("sourceContracts")]
    public int SourceContracts { get; set; }

    [JsonPropertyName("migratedContracts")]
    public int MigratedContracts { get; set; }

    [JsonPropertyName("verifiedContracts")]
    public int VerifiedContracts { get; set; }

    [JsonPropertyName("reviewRequiredContracts")]
    public int ReviewRequiredContracts { get; set; }

    [JsonPropertyName("missingContracts")]
    public int MissingContracts { get; set; }

    [JsonPropertyName("driftedContracts")]
    public int DriftedContracts { get; set; }

    [JsonPropertyName("issues")]
    public int Issues { get; set; }
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

    [JsonPropertyName("reviewRequiredItems")]
    public List<MigrationReviewRequiredProjectItem> ReviewRequiredItems { get; set; } = [];

    [JsonPropertyName("verifiedDecisionItems")]
    public int VerifiedDecisionItems { get; set; }
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

internal sealed class MigrationReviewRequiredProjectItem
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("sourceProject")]
    public required string SourceProject { get; set; }

    [JsonPropertyName("sourceLocation")]
    public required MigrationLocation SourceLocation { get; set; }

    [JsonPropertyName("itemType")]
    public required string ItemType { get; set; }

    [JsonPropertyName("include")]
    public required string Include { get; set; }

    [JsonPropertyName("link")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Link { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonPropertyName("parentCondition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentCondition { get; set; }

    [JsonPropertyName("metadata")]
    public List<MigrationProjectItemMetadata> Metadata { get; set; } = [];

    [JsonPropertyName("reviewReason")]
    public required string ReviewReason { get; set; }
}

internal sealed class MigrationProjectItemMetadata
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("value")]
    public required string Value { get; set; }
}

internal sealed class MigrationProjectItemDecision
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; set; }

    [JsonPropertyName("strategy")]
    public required string Strategy { get; set; }

    [JsonPropertyName("targetPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetPath { get; set; }

    [JsonPropertyName("targetItemType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetItemType { get; set; }

    [JsonPropertyName("evidenceFiles")]
    public List<string> EvidenceFiles { get; set; } = [];

    [JsonPropertyName("rationale")]
    public required string Rationale { get; set; }

    [JsonPropertyName("verification")]
    public MigrationProjectItemDecisionVerification Verification { get; set; } = new();
}

internal sealed class MigrationProjectItemDecisionVerification
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not-run";

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("projectEvidence")]
    public List<MigrationLocation> ProjectEvidence { get; set; } = [];

    [JsonPropertyName("sourceSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSha256 { get; set; }

    [JsonPropertyName("targetSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetSha256 { get; set; }

    [JsonPropertyName("contentMatches")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ContentMatches { get; set; }
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
