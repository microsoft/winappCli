// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class GenAiApiAnalyzerTests
{
    [Fact]
    public async Task Wui4101FlagsSetInputSequences()
    {
        await new AnalyzerTest<GenAiApiAnalyzer>()
            .WithSource(@"
class GeneratorParams { public void SetInputSequences(object s) {} }
class C { void M() { var p = new GeneratorParams(); p.SetInputSequences(null!); } }")
            .ExpectDiagnostic(DiagnosticIds.GenAiSetInputSequences)
            .RunAsync();
    }

    [Fact]
    public async Task Wui4102FlagsComputeLogits()
    {
        await new AnalyzerTest<GenAiApiAnalyzer>()
            .WithSource(@"
class Generator { public void ComputeLogits() {} }
class C { void M() { var g = new Generator(); g.ComputeLogits(); } }")
            .ExpectDiagnostic(DiagnosticIds.GenAiComputeLogits)
            .RunAsync();
    }

    [Fact]
    public async Task Wui4103FlagsTokenizerStreamCtor()
    {
        await new AnalyzerTest<GenAiApiAnalyzer>()
            .WithSource(@"
class Tokenizer {}
class TokenizerStream { public TokenizerStream(Tokenizer t) {} }
class C { void M() { var t = new Tokenizer(); var s = new TokenizerStream(t); } }")
            .ExpectDiagnostic(DiagnosticIds.GenAiTokenizerStreamCtor)
            .RunAsync();
    }

    [Fact]
    public async Task GenAiDoesNotFlagUnrelatedClean()
    {
        await new AnalyzerTest<GenAiApiAnalyzer>()
            .WithSource(@"
class C { void M() { var s = ""hello""; } }")
            .RunAsync();
    }
}
