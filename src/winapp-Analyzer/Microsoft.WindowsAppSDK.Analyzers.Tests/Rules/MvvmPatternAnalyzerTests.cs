// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class MvvmPatternAnalyzerTests
{
    [Fact]
    public async Task Wui3001FlagsFieldBackedObservableProperty()
    {
        await new AnalyzerTest<MvvmPatternAnalyzer>()
            .WithSource(@"
using System;
class ObservablePropertyAttribute : Attribute {}
partial class VM { [ObservableProperty] private string _name = """"; }")
            .ExpectDiagnostic(DiagnosticIds.OldMvvmSyntax)
            .RunAsync();
    }

    [Fact]
    public async Task Wui3001DoesNotFlagPlainPrivateField()
    {
        await new AnalyzerTest<MvvmPatternAnalyzer>()
            .WithSource(@"
class VM { private string _name = """"; }")
            .RunAsync();
    }

    [Fact]
    public async Task Wui3001DoesNotFlagFieldWithUnrelatedAttribute()
    {
        await new AnalyzerTest<MvvmPatternAnalyzer>()
            .WithSource(@"
using System;
class JsonIgnoreAttribute : Attribute {}
class VM { [JsonIgnore] private string _name = """"; }")
            .RunAsync();
    }
}
