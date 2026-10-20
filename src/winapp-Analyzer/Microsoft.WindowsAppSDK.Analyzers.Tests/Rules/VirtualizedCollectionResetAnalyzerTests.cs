// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class VirtualizedCollectionResetAnalyzerTests
{
    private const string Types = @"
using System;
using System.Collections.Specialized;

namespace Microsoft.UI.Xaml.Data
{
    public interface IItemsRangeInfo
    {
        void RangesChanged(object ranges);
    }
}

public sealed class RangeCache
{
    public void UpdateRanges(object ranges) { }
}
";

    [Fact]
    public async Task Wui2005FlagsRebuiltRangeCacheWithoutReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private object trackedRanges = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsTrackedRangeReplayAfterReset()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private object trackedRanges = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        trackedRanges = ranges;
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(trackedRanges);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresOrdinaryObservableCollection()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresResetThatKeepsRangeCache()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresMutuallyExclusiveRebuildAndReset()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection(bool rebuild)
    {
        if (rebuild)
        {
            cache = new RangeCache();
        }
        else
        {
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsConditionalReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private object trackedRanges = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        trackedRanges = ranges;
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection(bool replay)
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        if (replay)
        {
            cache.UpdateRanges(trackedRanges);
        }
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresUnrelatedFieldReinitializedBeforeReset()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private object trackedRanges = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        trackedRanges = ranges;
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        trackedRanges = new object();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005DoesNotTreatNestedReplayAsExecuted()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private object trackedRanges = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        trackedRanges = ranges;
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Action replayLater = () => cache.UpdateRanges(trackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsNullReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(null!);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresConditionalInitialization()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache? cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        cache!.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache ??= new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresResetArgumentsThatAreNotRaised()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();

    public void RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        var unused = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsExplicitRangesChangedImplementation()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    void Microsoft.UI.Xaml.Data.IItemsRangeInfo.RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplaySkippedByEarlyReturn()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private object trackedRanges = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        trackedRanges = ranges;
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection(bool skipReplay)
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        if (skipReplay)
        {
            return;
        }

        cache.UpdateRanges(trackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsResetAfterGuardReturn()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection(bool skipReset)
    {
        cache = new RangeCache();
        if (skipReset)
        {
            return;
        }

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsRetainedRangePropertyReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private object TrackedRanges { get; set; } = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(object ranges)
    {
        TrackedRanges = ranges;
        cache.UpdateRanges(ranges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(TrackedRanges);
    }
}")
            .RunAsync();
    }
}
