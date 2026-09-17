// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests;

public sealed class AnalyzerCompatibilityTests
{
    [Theory]
    [InlineData("Microsoft.CodeAnalysis")]
    [InlineData("Microsoft.CodeAnalysis.CSharp")]
    public void ShippedAnalyzerReferencesOldestSupportedCompiler(string assemblyName)
    {
        // Inspect the shipped assembly's references, not the newer Roslyn loaded by the test host.
        var reference = Assert.Single(
            typeof(UwpApiAnalyzer).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == assemblyName);

        Assert.Equal(new Version(4, 8, 0, 0), reference.Version);
    }
}
