// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>The outcome of a <c>find-api</c> query, used to pick the right exit code and message.</summary>
internal enum ApiQueryOutcome
{
    Ok,
    NoProject,
    InvalidInput,
    NotFound,
    NotAnEnum,
}

/// <summary>
/// Wraps a query payload with an <see cref="ApiQueryOutcome"/> and optional message
/// so command handlers can render text/JSON success or a clean error envelope.
/// </summary>
internal readonly record struct ApiQueryResult<T>(ApiQueryOutcome Outcome, string? Message, T? Data)
    where T : class
{
    public bool IsOk => Outcome == ApiQueryOutcome.Ok;

    public static ApiQueryResult<T> Ok(T data) => new(ApiQueryOutcome.Ok, null, data);
    public static ApiQueryResult<T> NoProject(string message) => new(ApiQueryOutcome.NoProject, message, null);
    public static ApiQueryResult<T> InvalidInput(string message) => new(ApiQueryOutcome.InvalidInput, message, null);
    public static ApiQueryResult<T> NotFound(string message) => new(ApiQueryOutcome.NotFound, message, null);
    public static ApiQueryResult<T> NotAnEnum(string message) => new(ApiQueryOutcome.NotAnEnum, message, null);
}

/// <summary>
/// Implemented by every query payload that is answered from a resolved scope, so
/// <c>find-api</c> can state — in text and in <c>--json</c> — whether results came
/// from an indexed project or from the machine-wide Windows SDK. Without this a
/// caller cannot tell a project-scoped answer from an SDK-only one.
/// </summary>
internal interface IApiScopedOutput
{
    /// <summary><c>project</c> when answered from an indexed project, <c>sdk</c> for the machine-wide SDK scope.</summary>
    string? Scope { get; set; }

    /// <summary>
    /// The indexed project that answered, or the SDK scope name. Reported on every
    /// payload — not just <c>packages</c>/<c>stats</c> — so a caller can always tell
    /// which index a result came from.
    /// </summary>
    string? ProjectName { get; set; }

    /// <summary>
    /// Absolute directory of the project that answered (<see langword="null"/> for the
    /// SDK scope). Project names are not unique across directories, so this is the only
    /// reliable identity of the answering index — without it, tooling auditing which
    /// project served a query has to infer it from cache file timestamps.
    /// </summary>
    string? ProjectDir { get; set; }
}

/// <summary>Well-known <see cref="IApiScopedOutput.Scope"/> values.</summary>
internal static class ApiScopeNames
{
    public const string Project = "project";
    public const string Sdk = "sdk";
}

// ---- batching ----
//
// Every verb accepts several subjects in one invocation (`members Button TextBox`,
// `check-property Button Background Foreground`). This exists for cost, not
// convenience: benchmark analysis showed the marginal token cost of a find-api call
// is dominated by the conversation context re-sent on the turn that issues it, not
// by the size of the payload returned. Verifying eight APIs in eight calls therefore
// costs roughly eight times a single verification regardless of how small each
// answer is, and shrinking individual payloads (the `--filter` approach) cannot fix
// it. Collapsing N lookups into one turn is the only lever that does.
//
// A single subject keeps the original single-object payload so existing callers and
// scripts are unaffected; the batch envelope appears only when more than one subject
// is supplied.

/// <summary>A subject in a batch that could not be answered, paired with why.</summary>
internal sealed class ApiBatchError
{
    /// <summary>The subject as the caller supplied it (type name, property, or query).</summary>
    public required string Subject { get; init; }

    public required string Message { get; init; }
}

/// <summary>Envelope for a multi-subject <c>find-api "&lt;query&gt;" "&lt;query&gt;"</c>.</summary>
internal sealed class ApiSearchBatchOutput
{
    public required int Count { get; init; }

    public required List<ApiSearchOutput> Results { get; init; }

    public List<ApiBatchError>? Errors { get; init; }
}

/// <summary>Envelope for a multi-type <c>find-api members</c>.</summary>
internal sealed class ApiMembersBatchOutput
{
    public required int Count { get; init; }

    public required List<ApiMembersOutput> Results { get; init; }

    public List<ApiBatchError>? Errors { get; init; }
}

/// <summary>Envelope for a multi-type <c>find-api enums</c>.</summary>
internal sealed class ApiEnumsBatchOutput
{
    public required int Count { get; init; }

    public required List<ApiEnumsOutput> Results { get; init; }

    public List<ApiBatchError>? Errors { get; init; }
}

/// <summary>Envelope for a multi-property <c>find-api check-property</c>.</summary>
internal sealed class ApiCheckPropertyBatchOutput
{
    public required int Count { get; init; }

    /// <summary>Number of subjects that did NOT resolve — the count worth acting on.</summary>
    public required int MissingCount { get; init; }

    public required List<ApiCheckPropertyOutput> Results { get; init; }

    public List<ApiBatchError>? Errors { get; init; }
}

// ---- search ----

/// <summary>A single ranked type/member hit within a namespace group.</summary>
internal sealed class ApiTypeHit
{
    public required string Display { get; init; }

    public required int Score { get; init; }
}

/// <summary>Ranked matches grouped by the namespace they live in.</summary>
internal sealed class ApiNamespaceHit
{
    public required string Namespace { get; init; }

    public required int Score { get; init; }

    /// <summary>
    /// On-disk cache files backing this namespace group. Diagnostics, not an answer:
    /// long, absolute, and user-specific paths that measured ~21% of a search payload.
    /// Populated only under <c>--verbose</c>; <see langword="null"/> otherwise, which
    /// omits the field from JSON entirely.
    /// </summary>
    public List<string>? Files { get; set; }

    public required List<ApiTypeHit> Matches { get; init; }
}

/// <summary>One candidate for an ambiguous (multi-namespace) type name.</summary>
internal sealed class ApiAmbiguityCandidate
{
    public required string FullName { get; init; }

    public required string Kind { get; init; }

    public required int Score { get; init; }

    public string? Description { get; init; }

    public List<string>? EnumValues { get; init; }
}

/// <summary>A short type name that resolves to multiple namespaces (would cause CS0104).</summary>
internal sealed class ApiAmbiguityGroup
{
    public required string Name { get; init; }

    public required List<ApiAmbiguityCandidate> Candidates { get; init; }
}

/// <summary>The result of a <c>find-api "&lt;query&gt;"</c> search.</summary>
internal sealed class ApiSearchOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required string Query { get; init; }

    public List<ApiAmbiguityGroup>? Ambiguous { get; init; }

    /// <summary>
    /// Set when the answer may be a false negative because part of the index could not
    /// be read. Search returns an empty result set rather than an error, so without this
    /// a caller cannot tell "this API does not exist" from "the index is broken".
    /// </summary>
    public string? Note { get; init; }

    public required List<ApiNamespaceHit> Results { get; init; }
}

// ---- members / check-property ----

/// <summary>A single property/event/method entry in a members listing.</summary>
internal sealed class ApiMemberOutput
{
    public required string Name { get; init; }

    /// <summary>
    /// Omitted in an unfiltered <c>members</c> listing, where the array the entry sits
    /// in (<c>properties</c>/<c>events</c>/<c>methods</c>) already states it. Set on
    /// <c>check-property</c> results, which are not grouped by kind.
    /// </summary>
    public string? Kind { get; init; }

    public required string Signature { get; init; }

    /// <summary>
    /// Omitted in an unfiltered <c>members</c> listing, where it is already the leading
    /// token of <see cref="Signature"/>.
    /// </summary>
    public string? ReturnType { get; init; }

    public string? Description { get; init; }

    public string? Deprecated { get; init; }

    public string? DeclaringType { get; init; }

    /// <summary>
    /// <see langword="true"/> for a member declared on a base type, <see langword="null"/>
    /// otherwise — which is exactly when <see cref="DeclaringType"/> is set, so the two
    /// never disagree and the flag costs nothing on the common case.
    /// </summary>
    public bool? Inherited { get; init; }

    /// <summary>
    /// Whether a property can be assigned. <c>null</c> for non-properties and for
    /// properties whose accessors could not be determined.
    /// </summary>
    public bool? Writable { get; init; }
}

/// <summary>
/// The inherited members of a type, by the base type that declares them. An unfiltered
/// <c>members</c> listing summarizes inheritance this way rather than inlining every
/// member: a WinUI control inherits far more than it declares (Button declares 8 members
/// and inherits 280), and the inherited detail dominates the payload while answering a
/// question — "what does UIElement give me" — that the caller did not ask.
/// </summary>
internal sealed class ApiInheritedMemberGroup
{
    public required string DeclaringType { get; init; }

    public List<string>? Properties { get; init; }

    public List<string>? Events { get; init; }

    public List<string>? Methods { get; init; }

    public List<string>? Fields { get; init; }
}

/// <summary>The result of <c>find-api members &lt;Type&gt;</c>.</summary>
internal sealed class ApiMembersOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required string FullName { get; init; }

    public required string Kind { get; init; }

    public string? Description { get; init; }

    public string? BaseType { get; init; }

    public string? Deprecated { get; init; }

    /// <summary>The <c>--filter</c> substring applied, or <see langword="null"/> when unfiltered.</summary>
    public string? Filter { get; init; }

    /// <summary>Total property count before filtering; <see langword="null"/> when unfiltered.</summary>
    public int? TotalProperties { get; init; }

    /// <summary>Total event count before filtering; <see langword="null"/> when unfiltered.</summary>
    public int? TotalEvents { get; init; }

    /// <summary>Total method count before filtering; <see langword="null"/> when unfiltered.</summary>
    public int? TotalMethods { get; init; }

    /// <summary>Total field count before filtering; <see langword="null"/> when unfiltered.</summary>
    public int? TotalFields { get; init; }

    /// <summary>
    /// Number of dependency-property identifier statics omitted from an unfiltered
    /// listing. <see langword="null"/> when none were hidden. Reported so a trimmed
    /// listing is never mistaken for the type's whole property surface.
    /// </summary>
    public int? HiddenDependencyProperties { get; init; }

    /// <summary>
    /// <see langword="true"/> when per-member XML-doc descriptions were omitted because
    /// this is an unfiltered bulk listing; <see langword="null"/> otherwise. Use
    /// <c>--filter</c> or <c>--all</c> to get them.
    /// </summary>
    public bool? DescriptionsOmitted { get; init; }

    /// <summary>
    /// How to recover what an unfiltered listing omitted. Present only when something
    /// was actually omitted. JSON callers cannot use <c>--verbose</c> (it conflicts with
    /// <c>--json</c>), so the hint names <c>--all</c>.
    /// </summary>
    public string? Hint { get; init; }

    public required List<ApiMemberOutput> Properties { get; init; }

    public required List<ApiMemberOutput> Events { get; init; }

    public required List<ApiMemberOutput> Methods { get; init; }

    /// <summary>
    /// Public fields. A struct such as <c>CoreWebView2PhysicalKeyStatus</c> exposes its
    /// whole surface this way, so omitting fields reports it as having no members at all.
    /// <see langword="null"/> when the type declares none, which is the common case.
    /// </summary>
    public List<ApiMemberOutput>? Fields { get; init; }

    /// <summary>
    /// Inherited members, by declaring type, in an unfiltered listing. Names only —
    /// <c>--filter</c> or <c>--all</c> gets their signatures. <see langword="null"/>
    /// when the listing already shows every member inline.
    /// </summary>
    public List<ApiInheritedMemberGroup>? Inherited { get; init; }

    public bool GetForCurrentViewWarning { get; init; }

    /// <inheritdoc cref="ApiCheckPropertyOutput.AlsoMatched" />
    public List<string>? AlsoMatched { get; init; }
}

/// <summary>A property found on a different type, used for check-property suggestions.</summary>
internal sealed class ApiCrossTypeMember
{
    public required string TypeName { get; init; }

    public required string Signature { get; init; }

    public string? Description { get; init; }
}

/// <summary>The result of <c>find-api check-property &lt;Type&gt; &lt;Property&gt;</c>.</summary>
internal sealed class ApiCheckPropertyOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required bool Found { get; init; }

    public required string Type { get; init; }

    public required string Property { get; init; }

    public ApiMemberOutput? Match { get; init; }

    /// <summary>
    /// Whether the found property can be assigned. A read-only property such as
    /// <c>FrameworkElement.ActualWidth</c> exists and is discoverable, but assigning it
    /// in XAML fails with WMC0050, so existence alone is not a safe green light.
    /// <c>null</c> when writability could not be determined.
    /// </summary>
    public bool? Writable { get; init; }

    public bool Attached { get; init; }

    public string? AttachedInfo { get; init; }

    /// <summary>
    /// Near-miss properties on the same type. <see langword="null"/> rather than empty
    /// when there are none, so the field is omitted from JSON instead of costing bytes
    /// to say "nothing". Populated only on a miss.
    /// </summary>
    public List<ApiMemberOutput>? SimilarOnType { get; init; }

    /// <summary>Other types that do have this property; <see langword="null"/> when none.</summary>
    public List<ApiCrossTypeMember>? TypesWithProperty { get; init; }

    /// <summary>Other types with a similarly-named property; <see langword="null"/> when none.</summary>
    public List<ApiCrossTypeMember>? TypesWithSimilar { get; init; }

    /// <summary>
    /// Set only on a miss, and only when the index behind it is known to be partial.
    /// Tells the caller the "does not have property" answer is not authoritative.
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>
    /// Other indexed types that share the short name asked for, when a
    /// <c>Microsoft.*</c>/<c>Windows.*</c> pair was resolved in favour of the
    /// <c>Microsoft.*</c> one. <c>DispatcherQueue</c> answers for
    /// <c>Microsoft.UI.Dispatching.DispatcherQueue</c> while
    /// <c>Windows.System.DispatcherQueue</c> is a different type with different members,
    /// so the answer is only safe if the caller is told which type it is about.
    /// <see langword="null"/> when the name resolved to exactly one type.
    /// </summary>
    public List<string>? AlsoMatched { get; init; }
}

// ---- types / enums / namespaces ----

/// <summary>A type summary line for a namespace listing.</summary>
internal sealed class ApiTypeSummary
{
    public required string FullName { get; init; }

    public required string Kind { get; init; }

    public string? BaseType { get; init; }
}

/// <summary>The result of <c>find-api types &lt;Namespace&gt;</c>.</summary>
internal sealed class ApiTypesOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required string Namespace { get; init; }

    public required List<ApiTypeSummary> Types { get; init; }
}

/// <summary>The result of <c>find-api enums &lt;Type&gt;</c>.</summary>
internal sealed class ApiEnumsOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required string FullName { get; init; }

    /// <summary>The <c>--filter</c> substring applied, or <see langword="null"/> when unfiltered.</summary>
    public string? Filter { get; init; }

    /// <summary>Total value count before filtering; <see langword="null"/> when unfiltered.</summary>
    public int? TotalValues { get; init; }

    public required List<string> Values { get; init; }

    /// <inheritdoc cref="ApiCheckPropertyOutput.AlsoMatched" />
    public List<string>? AlsoMatched { get; init; }
}

/// <summary>The result of <c>find-api namespaces</c>.</summary>
internal sealed class ApiNamespacesOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required List<string> Namespaces { get; init; }
}

// ---- packages / stats / projects ----

/// <summary>Per-package index stats for a project.</summary>
internal sealed class ApiPackageSummary
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public int? TotalTypes { get; init; }

    public int? TotalMembers { get; init; }

    /// <summary>One of <c>ok</c>, <c>incomplete</c>, <c>cache-missing</c>, or <c>meta-unreadable</c>.</summary>
    public required string Status { get; init; }
}

/// <summary>The result of <c>find-api packages</c>.</summary>
internal sealed class ApiPackagesOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required List<ApiPackageSummary> Packages { get; init; }
}

/// <summary>The result of <c>find-api stats</c>.</summary>
internal sealed class ApiStatsOutput : IApiScopedOutput
{
    /// <inheritdoc />
    public string? Scope { get; set; }

    /// <inheritdoc />
    public string? ProjectName { get; set; }

    /// <inheritdoc />
    public string? ProjectDir { get; set; }

    public required int Packages { get; init; }

    public required int Namespaces { get; init; }

    public required int Types { get; init; }

    public required int Members { get; init; }

    public required int WinMdFiles { get; init; }
}

/// <summary>A cached project summary.</summary>
internal sealed class ApiProjectSummary
{
    public required string Name { get; init; }

    public required int PackageCount { get; init; }
}

/// <summary>The result of <c>find-api projects</c>.</summary>
internal sealed class ApiProjectsOutput
{
    public required List<ApiProjectSummary> Projects { get; init; }
}

/// <summary>The result of <c>find-api refresh</c> (a re-scan of the project's metadata).</summary>
internal sealed class ApiRefreshOutput
{
    public required int ProjectsProcessed { get; init; }

    public required int PackagesParsed { get; init; }

    public required int PackagesReused { get; init; }

    public required List<string> ProjectNames { get; init; }
}
