// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class AttachedPropertyAnalyzerTests
{
    [Fact]
    public async Task Wui2030FlagsNestedAutomationPropertiesInitializer()
    {
        await new AnalyzerTest<AttachedPropertyAnalyzer>()
            .WithSource(@"
class Button { public object? AutomationProperties { get; set; } }
class C { void M() {
    var b = new Button { AutomationProperties = { AutomationId = ""ok"" } };
} }")
            .ExpectDiagnostic(DiagnosticIds.AttachedPropertyInitializer)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2030DoesNotFlagSimpleInitializer()
    {
        await new AnalyzerTest<AttachedPropertyAnalyzer>()
            .WithSource(@"
class Button { public string? Content { get; set; } }
class C { void M() { var b = new Button { Content = ""hi"" }; } }")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2030DoesNotFlagUnrelatedNestedInitializer()
    {
        // FP guard: nested initializer on a non-attached-property type.
        await new AnalyzerTest<AttachedPropertyAnalyzer>()
            .WithSource(@"
class Inner { public int Value { get; set; } }
class Outer { public Inner Child { get; } = new(); }
class C { void M() { var o = new Outer { Child = { Value = 1 } }; } }")
            .RunAsync();
    }
}
