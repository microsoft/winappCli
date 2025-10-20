// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;

namespace Winsdk.Cli.Telemetry.Events;

[EventData]
internal class EmptyEvent(PartA_PrivTags tags) : EventBase
{
    public override PartA_PrivTags PartA_PrivTags { get; } = tags;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        // No sensitive string
    }
}
