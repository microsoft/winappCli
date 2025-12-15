// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;

namespace WinApp.Cli.Telemetry.Events;

public class PrivacyProduct
{
    public static readonly UInt16
#pragma warning disable CA1707 // Identifiers should not contain underscores
    WIN_APP_DEV_CLI = 9;
#pragma warning restore CA1707 // Identifiers should not contain underscores
};

/// <summary>
/// Base class for all telemetry events to ensure they are properly tagged.
/// </summary>
/// <remarks>
/// The public properties of each event are logged in the telemetry.
/// We should not change an event's properties, as that could break the processing of that event's data.
/// </remarks>
[EventData]
public abstract class EventBase
{
    /// <summary>
    /// Gets the privacy datatype tag for the telemetry event.
    /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
    public abstract PartA_PrivTags PartA_PrivTags
    {
        get;
    }

    /// <summary>
    /// PartA_PrivacyProduct must be set for the telemetry event to be logged when in the WinExt provider group.
    /// </summary>
    public UInt16 PartA_PrivacyProduct { get; } = PrivacyProduct.WIN_APP_DEV_CLI;

#pragma warning restore CA1707 // Identifiers should not contain underscores

    /// <summary>
    /// Gets the app version from the assembly.
    /// </summary>
    public string AppVersion { get; } = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";

    public bool CI { get; } = CIEnvironmentDetectorForTelemetry.IsCIEnvironment();

    public string? Caller { get; set; } = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");

    /// <summary>
    /// Replaces all the strings in this event that may contain PII using the provided function.
    /// </summary>
    /// <remarks>
    /// This is called by <see cref="ITelemetry"/> before logging the event.
    /// It is the responsibility of each event to ensure we replace all strings with possible PII;
    /// we ensure we at least consider this by forcing to implement this.
    /// </remarks>
    /// <param name="replaceSensitiveStrings">
    /// A function that replaces all the sensitive strings in a given string with tokens
    /// </param>
    public abstract void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings);
}
