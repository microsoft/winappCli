// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;

namespace WinApp.Cli.Telemetry.Events;

/// <summary>
/// Usage telemetry for <c>winapp find-api</c>. Emitted by the command handlers
/// rather than the generic <see cref="CommandInvokedEvent"/> path so a single,
/// bounded event covers the whole verb family with a consistent shape.
///
/// <para><b>Privacy — allow-list, not blanket redaction.</b> The only string
/// captured is <see cref="Verb"/>, drawn from a fixed, code-controlled vocabulary
/// (search/members/check-property/types/enums/namespaces/packages/stats/projects/refresh).
/// Every free-form argument — the search query, a type/namespace/property name, a
/// namespace filter — is intentionally NOT captured, because a user can type
/// anything into them. Only booleans (<see cref="Json"/>, <see cref="Found"/>) and
/// a bounded <see cref="ResultCount"/> accompany the verb.</para>
///
/// <para>Logged at <see cref="LogLevel.Measure"/> — new usage-measure telemetry
/// that has not been through Critical-event approval.</para>
/// </summary>
[EventData]
internal class FindApiUsageEvent : EventBase
{
    private FindApiUsageEvent(string verb, bool json, int resultCount, bool found)
    {
        Verb = verb;
        Json = json;
        ResultCount = resultCount;
        Found = found;
    }

    /// <summary>The invocation verb (search, members, check-property, types, enums,
    /// namespaces, packages, stats, projects, refresh). Always from a fixed
    /// vocabulary — never a free-form string.</summary>
    public string Verb { get; private set; }

    /// <summary>Whether <c>--json</c> output was requested.</summary>
    public bool Json { get; }

    /// <summary>A bounded result count whose meaning depends on the verb (matched
    /// namespaces for search, members listed, packages, etc.); 0 when not applicable.</summary>
    public int ResultCount { get; }

    /// <summary>Whether the query produced a hit (e.g. type found, property found).
    /// Always true for verbs where "found" has no meaning.</summary>
    public bool Found { get; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServiceUsage;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        // Verb is a fixed-vocabulary token with no PII, but run the sanitizer over
        // it anyway to honor the EventBase contract (defensive, cheap).
        Verb = replaceSensitiveStrings(Verb);
    }

    /// <summary>Emit a usage event for a <c>find-api</c> verb.</summary>
    public static void Log(string verb, bool json, int resultCount = 0, bool found = true) =>
        Emit(Create(verb, json, resultCount, found));

    // Internal factory so tests can assert the arg→field mapping directly without
    // reconstructing the payload from ETW (the constructor stays private).
    internal static FindApiUsageEvent Create(string verb, bool json, int resultCount = 0, bool found = true) =>
        new(verb, json, resultCount, found);

    private static void Emit(FindApiUsageEvent usageEvent) =>
        TelemetryFactory.Get<ITelemetry>().Log("FindApiUsage_Event", LogLevel.Measure, usageEvent);
}
