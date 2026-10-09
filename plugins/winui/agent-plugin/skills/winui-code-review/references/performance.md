# Performance rules

## Performance

### x:Bind vs {Binding}

Always prefer `x:Bind` (compiled bindings) over `{Binding}` (runtime reflection). `x:Bind` resolves at compile time, generates strongly typed code, and avoids the reflection overhead of `{Binding}`.

| Feature | `x:Bind` | `{Binding}` |
|---|---|---|
| Resolution | Compile-time | Runtime (reflection) |
| Type safety | ✅ Yes | ❌ No |
| Default mode | OneTime | OneWay |
| Performance | Faster | Slower |

Reserve `{Binding}` only where `x:Bind` cannot be used (e.g., `Style` setters).

### Deferred Loading with x:Load

Use `x:Load` to defer creation of UI subtrees that aren't immediately visible (e.g., dialogs, secondary tabs, collapsed panels). The element is created only when `x:Load` evaluates to `true`.

```xml
<StackPanel x:Name="SettingsPanel" x:Load="{x:Bind ViewModel.IsSettingsOpen, Mode=OneWay}">
    <TextBlock Text="Settings content here" />
</StackPanel>
```

### Incremental Rendering with x:Phase

Use `x:Phase` inside `DataTemplate` to prioritize which parts of each list item render first. Phase 0 (default) renders immediately; higher phases render in subsequent passes.

```xml
<DataTemplate x:DataType="vm:ItemViewModel">
    <StackPanel>
        <TextBlock Text="{x:Bind Title}" />
        <TextBlock Text="{x:Bind Description}" x:Phase="1" />
        <Image Source="{x:Bind ThumbnailUrl}" x:Phase="2" />
    </StackPanel>
</DataTemplate>
```

### Collection Virtualization

Use `ListView`, `GridView`, or `ItemsRepeater` for any list that may exceed ~20 items. These controls create UI elements only for visible items and recycle them on scroll.

```xml
<ScrollViewer>
    <ItemsRepeater ItemsSource="{x:Bind ViewModel.Items}">
        <ItemsRepeater.Layout>
            <StackLayout Spacing="4" />
        </ItemsRepeater.Layout>
    </ItemsRepeater>
</ScrollViewer>
```

For large datasets, implement `ISupportIncrementalLoading` so the `ListView` fetches pages of data as the user scrolls.

### DispatcherQueue for UI-Thread Management

```csharp
public async Task LoadDataAsync()
{
    var data = await Task.Run(() => _service.GetExpensiveData());

    DispatcherQueue.TryEnqueue(() =>
    {
        ViewModel.Items.Clear();
        foreach (var item in data)
            ViewModel.Items.Add(item);
    });
}
```

**Do not flood the queue.** Batch updates into a single `TryEnqueue` call rather than enqueuing per item.

### Async Patterns

- Use `async/await` for I/O-bound work (file access, HTTP calls, database queries).
- Use `Task.Run` for CPU-bound work (parsing, compression, image processing).
- Never block the UI thread with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.

### Layout and Visual Tree

- Minimize XAML visual tree depth — deep nesting compounds layout-pass cost.
- Prefer `Grid` over nested `StackPanel` layouts when you need rows and columns.
- Cache expensive computations and HTTP responses when appropriate.

---
