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
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI.Xaml.Data;

namespace Microsoft.UI.Xaml.Data
{
    public sealed class ItemIndexRange { }

    public interface IItemsRangeInfo
    {
        void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems);
    }
}

public sealed class RangeCache
{
    public void UpdateRanges(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems) { }
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
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
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
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(visibleRangeState, trackedRanges);
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

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
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

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
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

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
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
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(bool replay)
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        if (replay)
        {
            cache.UpdateRanges(visibleRangeState, trackedRanges);
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
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        trackedRanges = Array.Empty<ItemIndexRange>();
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
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Action replayLater = () => cache.UpdateRanges(visibleRangeState, trackedRanges);
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

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(null!, null!);
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

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache!.UpdateRanges(visibleRange, trackedItems);
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

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
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
    public async Task Wui2005FlagsResetLocalRaisedByEvent()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
        CollectionChanged?.Invoke(this, args);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsResetLocalRaisedByOnCollectionChanged()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source :
    System.Collections.ObjectModel.ObservableCollection<object>,
    Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        NotifyCollectionChangedEventArgs args;
        args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
        OnCollectionChanged(args);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresOverwrittenResetLocal()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
        args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new object());
        CollectionChanged?.Invoke(this, args);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresUnraisedResetLocalWhenDifferentArgsAreRaised()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        var resetArgs = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
        var addArgs = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new object());
        CollectionChanged?.Invoke(this, addArgs);
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

    void Microsoft.UI.Xaml.Data.IItemsRangeInfo.RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
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
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(bool skipReplay)
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        if (skipReplay)
        {
            return;
        }

        cache.UpdateRanges(visibleRangeState, trackedRanges);
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

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
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
    public async Task Wui2005FlagsReplaySkippedByBreak()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(bool skipReplay)
    {
        while (true)
        {
            cache = new RangeCache();
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            if (skipReplay)
            {
                break;
            }

            cache.UpdateRanges(visibleRangeState, trackedRanges);
            break;
        }
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplaySkippedByOuterLoopContinue()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(bool skipReplay)
    {
        for (var index = 0; index < 1; index++)
        {
            cache = new RangeCache();
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            if (skipReplay)
            {
                continue;
            }

            cache.UpdateRanges(visibleRangeState, trackedRanges);
        }
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsSwitchBreakBeforeReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(int mode)
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        switch (mode)
        {
            case 0:
                break;
            default:
                break;
        }

        cache.UpdateRanges(visibleRangeState, trackedRanges);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsInnerLoopBreakBeforeReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        while (true)
        {
            break;
        }

        cache.UpdateRanges(visibleRangeState, trackedRanges);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsInnerLoopContinueBeforeReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        for (var index = 0; index < 1; index++)
        {
            continue;
        }

        cache.UpdateRanges(visibleRangeState, trackedRanges);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplaySkippedByIteratorExit()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public IEnumerable<object> ResetCollection(bool skipReplay)
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        if (skipReplay)
        {
            yield break;
        }

        cache.UpdateRanges(visibleRangeState, trackedRanges);
        yield break;
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplayDeferredPastIteratorYield()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public IEnumerable<object> ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        yield return new object();
        cache.UpdateRanges(visibleRangeState, trackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsLoopExitAfterReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        while (true)
        {
            cache = new RangeCache();
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            cache.UpdateRanges(visibleRangeState, trackedRanges);
            break;
        }
    }
}")
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
    private ItemIndexRange VisibleRangeState { get; set; } = new();
    private IReadOnlyList<ItemIndexRange> TrackedRanges { get; set; } = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        VisibleRangeState = visibleRange;
        TrackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(VisibleRangeState, TrackedRanges);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplayFromOverwrittenRetainedField()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        visibleRangeState = new ItemIndexRange();
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(visibleRangeState, trackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplayFromConditionallyRetainedProperty()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange VisibleRangeState { get; set; } = new();
    private IReadOnlyList<ItemIndexRange> TrackedRanges { get; set; } = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        if (trackedItems.Count > 0)
        {
            VisibleRangeState = visibleRange;
        }

        TrackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(VisibleRangeState, TrackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplayFromRetainedFieldOverwrittenBeforeReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        visibleRangeState = new ItemIndexRange();
        cache.UpdateRanges(visibleRangeState, trackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsRebuiltPropertyBackedRangeCache()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache Cache { get; set; } = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        Cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        Cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsStaticallyImportedReset()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(
                "using static System.Collections.Specialized.NotifyCollectionChangedAction;" +
                Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(Reset));
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsObservableCollectionOnCollectionChangedReset()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source :
    System.Collections.ObjectModel.ObservableCollection<object>,
    Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresOtherInstanceOnCollectionChangedReset()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source :
    System.Collections.ObjectModel.ObservableCollection<object>,
    Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(Source other)
    {
        cache = new RangeCache();
        other.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresOtherInstanceEventReset()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(Source other)
    {
        cache = new RangeCache();
        other.CollectionChanged?.Invoke(
            other,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresResetPassedToUnrelatedMethod()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        Publish(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void Publish(NotifyCollectionChangedEventArgs args) { }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsReplayThatRetainsOnlyVisibleRange()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(visibleRangeState, Array.Empty<ItemIndexRange>());
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsIndirectAliasContractWithoutReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> TrackedRanges { get; set; } = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        TrackedRanges = trackedItems;
        cache.UpdateRanges(visibleRangeState, TrackedRanges);
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
    public async Task Wui2005AllowsIndirectAliasContractReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> TrackedRanges { get; set; } = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        TrackedRanges = trackedItems;
        cache.UpdateRanges(visibleRangeState, TrackedRanges);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(visibleRangeState, TrackedRanges);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsReorderedNamedArgumentReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(trackedItems: trackedItems, visibleRange: visibleRange);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(visibleRange: visibleRangeState, trackedItems: trackedRanges);
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005AllowsParenthesizedConvertedReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(((ItemIndexRange)visibleRangeState), ((IReadOnlyList<ItemIndexRange>)trackedRanges));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsConditionalReplayArgument()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private ItemIndexRange fallbackVisibleRange = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(bool useRetainedRange)
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(
            useRetainedRange ? visibleRangeState : fallbackVisibleRange,
            trackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsCoalescedReplayArgument()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache cache = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange>? trackedRanges;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        cache.UpdateRanges(
            visibleRangeState,
            trackedRanges ?? Array.Empty<ItemIndexRange>());
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresOtherInstancePropertyCacheContract()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private Source? other;
    private RangeCache Cache { get; set; } = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        other!.Cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection()
    {
        other!.Cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005IgnoresOtherInstancePropertyCacheRebuild()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache Cache { get; set; } = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        Cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(Source other)
    {
        other.Cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2005FlagsOtherInstancePropertyReplay()
    {
        await new AnalyzerTest<VirtualizedCollectionResetAnalyzer>()
            .WithSource(Types + @"
public sealed class Source : Microsoft.UI.Xaml.Data.IItemsRangeInfo
{
    private RangeCache Cache { get; set; } = new();
    private ItemIndexRange visibleRangeState = new();
    private IReadOnlyList<ItemIndexRange> trackedRanges = Array.Empty<ItemIndexRange>();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        visibleRangeState = visibleRange;
        trackedRanges = trackedItems;
        Cache.UpdateRanges(visibleRange, trackedItems);
    }

    public void ResetCollection(Source other)
    {
        Cache = new RangeCache();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        other.Cache.UpdateRanges(visibleRangeState, trackedRanges);
    }
}")
            .ExpectDiagnostic(DiagnosticIds.VirtualizedResetDropsCache)
            .RunAsync();
    }
}
