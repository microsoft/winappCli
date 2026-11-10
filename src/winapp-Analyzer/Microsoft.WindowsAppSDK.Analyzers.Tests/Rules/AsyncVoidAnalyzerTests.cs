// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class AsyncVoidAnalyzerTests
{
    [Fact]
    public async Task Wui2004FlagsPrivateParameterlessAsyncVoidMethod()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System.Threading.Tasks;
class Page {
    private async void InitializeDataAsync() { await Task.Delay(1); }
}")
            .ExpectDiagnostic(DiagnosticIds.ParameterlessAsyncVoid)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004FlagsImplicitlyPrivateMethodWithoutAsyncSuffix()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System.Threading.Tasks;
class Page {
    async void initdata() { await Task.Delay(1); }
}")
            .ExpectDiagnostic(DiagnosticIds.ParameterlessAsyncVoid)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004DoesNotFlagTaskReturningMethod()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System.Threading.Tasks;
class Page {
    private async Task InitializeDataAsync() { await Task.Delay(1); }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004DoesNotFlagParameterizedCallback()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System.Threading.Tasks;
class Page {
    private async void ProcessAsync(object sender) { await Task.Delay(1); }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004DoesNotFlagPublicContractMethod()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System.Threading.Tasks;
class Page {
    public async void InitializeDataAsync() { await Task.Delay(1); }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004DoesNotFlagZeroArgumentDelegateHandler()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System;
using System.Threading.Tasks;
class Page {
    private event Action? Loaded;
    private Page() { Loaded += InitializeDataAsync; }
    private async void InitializeDataAsync() { await Task.Delay(1); }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004DoesNotFlagQualifiedDelegateHandler()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System;
using System.Threading.Tasks;
class Page {
    private Page() { Action callback = this.InitializeDataAsync; }
    private async void InitializeDataAsync() { await Task.Delay(1); }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004DoesNotFlagExplicitInterfaceImplementation()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System.Threading.Tasks;
interface IInitializer { void InitializeDataAsync(); }
class Page : IInitializer {
    async void IInitializer.InitializeDataAsync() { await Task.Delay(1); }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2004DoesNotFlagGenericDelegateHandler()
    {
        await new AnalyzerTest<AsyncVoidAnalyzer>()
            .WithSource(@"
using System;
using System.Threading.Tasks;
class Page {
    private Page() { Action callback = InitializeDataAsync<int>; }
    private async void InitializeDataAsync<T>() { await Task.Delay(1); }
}")
            .RunAsync();
    }
}
