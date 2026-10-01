// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;

namespace WinApp.DevTools.Managed;

internal static class BindingDiagnosis
{
    private static readonly BindingCaptureStore Captured = new();

    private const int UiTimeoutMs = 10000;

    public static string Run(BindingTarget target, string op, string prop, string authored)
    {
        bool handedOff = false;
        try
        {
            // Live XAML access is marshaled to the app UI thread; native refuses UI-thread callers to avoid deadlock.
            object? elementObj = target.Project();
            if (elementObj is not FrameworkElement element)
            {
                return Unavailable("the selected object is not a FrameworkElement");
            }

            handedOff = true;
            return RunOnUiThread(action => element.DispatcherQueue.TryEnqueue(() => action()),
                () => target.Project() is FrameworkElement current
                    ? Dispatch(current, op, prop, authored)
                    : Unavailable("the selected object is not a FrameworkElement"), target);
        }
        catch (Exception ex)
        {
            return Unavailable(Describe(ex));
        }
        finally { if (!handedOff) target.Dispose(); }
    }

    internal static string RunOnUiThread(Func<Action, bool> enqueue, Func<string> work, IDisposable? owner = null)
    {
        var operation = new BindingOperation(work, owner);
        try
        {
            if (!enqueue(operation.Invoke))
            {
                operation.Cancel();
                return Unavailable("the app UI thread is not accepting work");
            }
            // The pipe thread owns the wait; timeout cancels ownership so a late UI callback cannot use disposed state.
            if (!operation.Done.Task.Wait(UiTimeoutMs))
            {
                operation.Cancel();
                return Unavailable("the app UI thread did not respond");
            }
            return operation.Done.Task.Result;
        }
        catch (Exception ex)
        {
            operation.Cancel();
            return Unavailable(Describe(ex));
        }
    }

    private sealed class BindingOperation(Func<string> work, IDisposable? owner)
    {
        private Func<string>? work = work;
        private IDisposable? owner = owner;
        private int state; // Pending, Running, Cancelled, Completed.
        internal TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Cancel()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) != 0) return;
            Interlocked.Exchange(ref work, null);
            Interlocked.Exchange(ref owner, null)?.Dispose();
        }
        internal void Invoke()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            var action = Interlocked.Exchange(ref work, null)!;
            string result;
            try { result = action(); }
            catch (Exception ex) { result = Unavailable(Describe(ex)); }
            finally
            {
                Interlocked.Exchange(ref owner, null)?.Dispose();
                Volatile.Write(ref state, 3);
            }
            Done.SetResult(result);
        }
    }

    private static string Dispatch(FrameworkElement element, string op, string prop, string authored) => op switch
    {
        "diagnose" => Diagnose(element, prop, authored),
        // Every other op reports the property's before/after values.
        _ when IsSecret(prop) => Unavailable("'" + prop + "' holds a secret; DevTools does not read or write it.", nativeFallback: false),
        "capture" => Capture(element, prop, authored),
        "restore" => Restore(element, prop, authored, false),
        "restoreconfirmed" => Restore(element, prop, authored, true),
        "clear" => Clear(element, prop),
        // Native DevToolsBindingInstall owns grafting. For writesource, authored carries the value instead of markup.
        "writesource" => WriteSource(element, prop, authored),
        _ => Unavailable("unknown binding op: " + op),
    };


    private static string Diagnose(FrameworkElement element, string prop, string authored)
    {
        DependencyProperty? dp = FindDp(element, prop);
        if (dp is null) return Unavailable("'" + prop + "' is not a dependency property on " + ShortName(element.GetType()));

        object? local = element.ReadLocalValue(dp);
        Binding? parent = ParentBindingOf(local);

        if (parent is not null) return DiagnoseRuntimeBinding(element, dp, prop, parent);

        string? xbindPath = ParseXBindPath(authored);
        if (xbindPath is not null) return DiagnoseCompiledBinding(element, dp, prop, xbindPath, authored);

        // No runtime Binding does not disprove x:Bind: WinUI applies compiled bindings as plain local values.
        return DiagnoseWithoutRuntimeBinding(authored);
    }

    internal static string DiagnoseWithoutRuntimeBinding(string authored)
        => !string.IsNullOrWhiteSpace(authored) &&
           (!authored.TrimStart().StartsWith('{') || authored.TrimStart().StartsWith("{}", StringComparison.Ordinal))
            ? "{\"state\":\"none\"}"
            : Unavailable("no runtime binding was observed; authored binding information is unavailable or is not a known literal, so a compiled binding cannot be ruled out", false);

    private static string DiagnoseRuntimeBinding(FrameworkElement element, DependencyProperty dp, string prop, Binding parent)
    {
        string path = parent.Path?.Path ?? "";
        var sb = new StringBuilder();
        string sourceLabel;
        object? source;

        if (parent.RelativeSource is not null)
        {
            sourceLabel = "RelativeSource=" + parent.RelativeSource.Mode;
            source = ResolveRelativeSource(parent.RelativeSource.Mode, element,
                () => element.GetType().GetProperty("TemplatedParent")?.GetValue(element));
            if (source is null)
                return Unavailable(sourceLabel + " could not be resolved; DataContext is not used for an explicit RelativeSource", false);
        }
        else if (!string.IsNullOrEmpty(parent.ElementName))
        {
            source = element.FindName(parent.ElementName) ?? FindByName(element, parent.ElementName);
            sourceLabel = "ElementName=" + parent.ElementName;
            if (source is null)
            {
                return Fault("bad-segment", parent.ElementName,
                    "no element named '" + parent.ElementName + "' is in scope for this binding",
                    path, sourceLabel, "{Binding}", parent.Mode.ToString());
            }
        }
        else if (parent.Source is not null)
        {
            source = parent.Source;
            sourceLabel = "Source=" + ShortName(source.GetType());
        }
        else
        {
            source = element.DataContext;
            sourceLabel = source is null ? "DataContext" : "DataContext=" + ShortName(source.GetType());
            if (source is null)
            {
                return Fault("no-datacontext", "", "DataContext is null on this element and its ancestors",
                    path, "DataContext", "{Binding}", parent.Mode.ToString());
            }
        }

        _ = sb;
        return WalkAndJudge(element, dp, prop, path, source, sourceLabel, "{Binding}", parent.Mode.ToString(),
            parent.Converter, parent.ConverterParameter, parent.ConverterLanguage,
            EffectiveTrigger(parent.UpdateSourceTrigger.ToString(), element.GetType(), prop));
    }

    private static string DiagnoseCompiledBinding(FrameworkElement element, DependencyProperty dp, string prop, string path, string authored)
    {
        if (!IsSimplePath(path))
            return UnsupportedPath(path, "{x:Bind}");
        object? source;
        try { source = XBindSource(element); }
        catch (Exception ex) { return Unavailable("compiled binding owner inspection failed: " + Describe(ex), false); }
        if (source is null)
        {
            return Unavailable("the selected element's generated binding component/source could not be established; an enclosing page or unrelated Window is not a substitute", false);
        }
        string mode = CompiledOption(authored, "Mode", "Unknown", ["OneTime", "OneWay", "TwoWay"]);
        return WalkAndJudge(element, dp, prop, path, source, ShortName(source.GetType()), "{x:Bind}", mode, null, null, "",
            CompiledTrigger(authored, element.GetType(), prop));
    }

    private static string WalkAndJudge(FrameworkElement element, DependencyProperty dp, string prop, string path,
                                       object? source, string sourceLabel, string kind, string mode, IValueConverter? converter,
                                       object? converterParameter, string converterLanguage, string updateSourceTrigger = "Unknown")
        => Evaluate(path, source, sourceLabel, kind, mode, ClrTypeOf(element, prop), prop,
            () => element.GetValue(dp), converter, converterParameter, converterLanguage, updateSourceTrigger);

    internal static string Evaluate(string path, object? source, string sourceLabel, string kind, string mode,
        Type targetType, string prop, Func<object?> readTarget, IValueConverter? converter = null,
        object? converterParameter = null, string converterLanguage = "", string updateSourceTrigger = "Unknown")
    {
        if (!IsSimplePath(path)) return UnsupportedPath(path, kind);

        object? current = source;
        // A Nullable<T> member boxes to T or null, so its Value and HasValue are answered from the declared type.
        Type? declared = null;
        string[] segments = string.IsNullOrEmpty(path) || path == "." ? [] : path.Split('.');
        // x:Bind is compiled against its owner, so it can reach non-public members; {Binding} reflects public ones only.
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy |
            (kind == "{x:Bind}" ? BindingFlags.NonPublic : 0);
        string visibility = kind == "{x:Bind}" ? "" : "public ";
        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (declared is not null && Nullable.GetUnderlyingType(declared) is { } underlying && seg is "Value" or "HasValue")
            {
                declared = seg == "Value" ? underlying : typeof(bool);
                if (seg == "HasValue") { current = current is not null; continue; }
                if (current is not null) continue;
            }
            if (current is null)
            {
                string prior = i > 0 ? segments[i - 1] : sourceLabel;
                return Fault("null-link", prior, prior + " is null, so '" + seg + "' is never reached",
                    path, sourceLabel, kind, mode);
            }

            Type t = current.GetType();
            PropertyInfo? pi = t.GetProperty(seg, flags);
            if (pi is not null)
            {
                try { current = pi.GetValue(current); }
                catch (Exception ex)
                {
                    Exception real = ex.InnerException ?? ex;
                    return Fault("threw", seg, ShortName(t) + "." + seg + " threw " + Describe(real),
                        path, sourceLabel, kind, mode);
                }
                declared = pi.PropertyType;
                continue;
            }

            FieldInfo? fi = t.GetField(seg, flags);
            if (fi is null)
            {
                return Fault("bad-segment", seg, "no " + visibility + "property or field '" + seg + "' on " + ShortName(t),
                    path, sourceLabel, kind, mode);
            }
            try { current = fi.GetValue(current); }
            catch (Exception ex)
            {
                Exception real = ex.InnerException ?? ex;
                return Fault("threw", seg, ShortName(t) + "." + seg + " threw " + Describe(real), path, sourceLabel, kind, mode);
            }
            declared = fi.FieldType;
        }

        object? sourceValue = current;
        if (converter is not null)
        {
            try
            {
                current = converter.Convert(current!, targetType, converterParameter!, converterLanguage);
            }
            catch (Exception ex)
            {
                Exception real = ex.InnerException ?? ex;
                return Fault("threw", ShortName(converter.GetType()),
                    ShortName(converter.GetType()) + " threw " + Describe(real), path, sourceLabel, kind, mode);
            }
        }

        bool differentType = current is not null && targetType != typeof(object) && !targetType.IsInstanceOfType(current);
        var sb = new StringBuilder(differentType ? "{\"state\":\"silent\"" : "{\"state\":\"evaluated\"");
        // A secret's value never leaves the app, whichever side of the binding names it.
        bool secret = IsSecret(prop) || segments.Length > 0 && IsSecret(segments[^1]);
        string Value(object? value) => secret ? Redacted : Str(value);
        // This diagnosis is read-only evidence; it deliberately avoids claiming source freshness or ConvertBack success.
        Field(sb, "reason", "Forward path and CLR type evaluated only; target freshness, change notifications, ConvertBack and source setters were not checked. Prior reverse-write failures cannot be validated by this read.");
        Field(sb, "updateSourceTrigger", updateSourceTrigger);
        Field(sb, "sourceValue", Value(sourceValue));
        Field(sb, "resolvedValue", Value(current));
        Field(sb, "resolvedType", current is null ? "(null)" : ShortName(current.GetType()));
        Field(sb, "targetProperty", prop);
        Field(sb, "targetType", ShortName(targetType));
        try { Field(sb, "targetValue", Value(readTarget())); }
        catch (Exception ex) { Field(sb, "targetUnavailable", Describe(ex.InnerException ?? ex)); }
        if (secret) sb.Append(",\"redacted\":true");
        Field(sb, "converterEvaluation", converter is not null ? "executed" : kind == "{x:Bind}" ? "not inspected" : "none");
        Common(sb, path, sourceLabel, kind, mode);
        sb.Append('}');
        return sb.ToString();
    }

    private const string Redacted = "<redacted>";

    private static bool IsSecret(string name) => name.EndsWith("Password", StringComparison.OrdinalIgnoreCase);

    private static bool IsSimplePath(string path)
        => path.Length == 0 || path == "." || path.Split('.').All(segment =>
            segment.Length > 0 && (char.IsLetter(segment[0]) || segment[0] == '_') &&
            segment.All(c => char.IsLetterOrDigit(c) || c == '_'));

    private static string UnsupportedPath(string path, string kind)
    {
        var sb = new StringBuilder("{\"state\":\"unavailable\"");
        Field(sb, "reason", "this path syntax is not supported by the managed evaluator; only simple property/field paths can be inspected");
        Field(sb, "nativeFallback", "False");
        Common(sb, path, "", kind, "");
        sb.Append('}');
        return sb.ToString();
    }


    private static string Capture(FrameworkElement element, string prop, string authored)
    {
        DependencyProperty? dp = FindDp(element, prop);
        if (dp is null) return Unavailable("'" + prop + "' is not a dependency property");

        Binding? parent = ParentBindingOf(element.ReadLocalValue(dp));
        var sb = new StringBuilder("{\"state\":\"captured\"");
        if (parent is not null)
        {
            Captured.Set(element, prop, CloneBinding(parent));
            Field(sb, "restoreKind", "binding");
            Field(sb, "restoreScope", "element-property");
            Field(sb, "path", parent.Path?.Path ?? "");
            Field(sb, "mode", parent.Mode.ToString());
            Field(sb, "updateSourceTrigger", EffectiveTrigger(parent.UpdateSourceTrigger.ToString(), element.GetType(), prop));
            // A surviving TwoWay expression writes through to the source; disclose that outcome.
            Field(sb, "writesThrough", (parent.Mode == BindingMode.TwoWay).ToString());
            if (parent.Mode == BindingMode.TwoWay && !string.IsNullOrEmpty(parent.Path?.Path))
            {
                object? src = RuntimeSource(element, parent);
                if (src is not null) Field(sb, "writesThroughTo", ShortName(src.GetType()) + "." + parent.Path!.Path);
            }
            if (parent.Mode == BindingMode.TwoWay)
                Field(sb, "editWarning", "Editing a TwoWay target can change the source model. Restore binding does not undo source/model changes.");
        }
        else
        {
            // No live expression to capture. An {x:Bind} row still has a restore route — Bindings.Update()
            Captured.Remove(element, prop);
            CompiledOwner? owner;
            try { owner = ParseXBindPath(authored) is not null ? XBindOwner(element) : null; }
            catch (Exception ex) { return Unavailable("compiled binding owner inspection failed; no restore route was captured: " + Describe(ex), false); }
            CompiledCaptureMetadata(sb, owner);
            CompiledEditMetadata(sb, authored, element.GetType(), prop);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string Restore(FrameworkElement element, string prop, string authored, bool confirmOwnerRebind)
    {
        DependencyProperty? dp = FindDp(element, prop);
        if (dp is null) return Unavailable("'" + prop + "' is not a dependency property");

        var sb = new StringBuilder("{\"state\":\"restored\"");
        Field(sb, "before", Str(element.GetValue(dp)));

        if (Captured.TryGet(element, prop, out BindingBase? saved))
        {
            try
            {
                BindingOperations.SetBinding(element, dp, saved is Binding binding ? CloneBinding(binding) : saved);
                Field(sb, "restoreKind", "binding");
                Field(sb, "restoreScope", "element-property");
                Field(sb, "restoreDisclosure", "Re-attached the binding; source/model changes were not undone.");
            }
            catch (Exception ex) { return Unavailable("re-attaching the binding threw " + Describe(ex)); }
        }
        else if (ParseXBindPath(authored) is not null)
        {
            CompiledOwner? owner;
            try { owner = XBindOwner(element); }
            catch (Exception ex) { return Unavailable("compiled binding owner inspection failed; no owner was updated: " + Describe(ex), false); }
            return RestoreCompiled(owner, confirmOwnerRebind, () => element.GetValue(dp));
        }
        else
        {
            return Unavailable("nothing was captured for " + prop + " on this element");
        }

        object? after = element.GetValue(dp);
        Field(sb, "after", Str(after));
        Field(sb, "bound", (ParentBindingOf(element.ReadLocalValue(dp)) is not null).ToString());
        sb.Append('}');
        return sb.ToString();
    }

    private static string Clear(FrameworkElement element, string prop)
    {
        DependencyProperty? dp = FindDp(element, prop);
        if (dp is null) return Unavailable("'" + prop + "' is not a dependency property");

        var sb = new StringBuilder("{\"state\":\"cleared\"");
        Field(sb, "before", Str(element.GetValue(dp)));
        try { element.ClearValue(dp); }
        catch (Exception ex) { return Unavailable("clearing the value threw " + Describe(ex)); }
        Field(sb, "after", Str(element.GetValue(dp)));
        sb.Append('}');
        return sb.ToString();
    }


    internal static object? ResolveRelativeSource(RelativeSourceMode mode, object self, Func<object?> templatedParent)
    {
        if (mode == RelativeSourceMode.Self) return self;
        if (mode != RelativeSourceMode.TemplatedParent) return null;
        try { return templatedParent(); }
        catch { return null; }
    }

    private static object? RuntimeSource(FrameworkElement element, Binding binding)
        => binding.RelativeSource is not null
            ? ResolveRelativeSource(binding.RelativeSource.Mode, element,
                () => element.GetType().GetProperty("TemplatedParent")?.GetValue(element))
            : !string.IsNullOrEmpty(binding.ElementName)
                ? element.FindName(binding.ElementName) ?? FindByName(element, binding.ElementName)
                : binding.Source ?? element.DataContext;

    private static Binding CloneBinding(Binding original)
    {
        var copy = new Binding();
        CopyBindingProperties(original, copy);
        return copy;
    }

    internal static void CopyBindingProperties(object original, object copy)
    {
        foreach (var property in typeof(Binding).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite))
        {
            object? value = original.GetType().GetProperty(property.Name)!.GetValue(original);
            // Setting even an empty source selector can conflict with the active selector.
            if (property.Name is "Source" or "RelativeSource" && value is null) continue;
            if (property.Name == "ElementName" && string.IsNullOrEmpty(value as string)) continue;
            copy.GetType().GetProperty(property.Name)!.SetValue(copy, value);
        }
    }

    private static string CompiledOption(string authored, string name, string defaultValue, string[] known)
    {
        if (ParseXBindPath(authored) is null || !authored.TrimEnd().EndsWith('}')) return "Unknown";
        var options = SplitTopLevel(authored.Trim().Substring("{x:Bind".Length).TrimEnd('}'))
            .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0] == name).Select(p => p[1]).ToArray();
        return options.Length == 0 ? defaultValue
            : options.Length == 1 && known.Contains(options[0]) ? options[0] : "Unknown";
    }

    internal static void CompiledEditMetadata(StringBuilder sb, string authored, Type targetType, string prop)
    {
        string mode = CompiledOption(authored, "Mode", "Unknown", ["OneTime", "OneWay", "TwoWay"]);
        string trigger = CompiledTrigger(authored, targetType, prop);
        Field(sb, "mode", mode);
        Field(sb, "updateSourceTrigger", trigger);
        Field(sb, "writesThrough", mode == "TwoWay" ? "True" : mode == "Unknown" ? "Unknown" : "False");
        if (mode is "TwoWay" or "Unknown")
            Field(sb, "editWarning", "Editing this compiled binding can change the source model"
                + (trigger == "PropertyChanged" ? " immediately on property change" : " (update trigger: " + trigger + ")")
                + ". Restore re-reads the current source; it does not undo source/model changes.");
    }

    internal static string EffectiveTrigger(string trigger, Type targetType, string prop)
        => trigger == "Default"
            ? typeof(TextBox).IsAssignableFrom(targetType) && prop == "Text" ? "LostFocus" : "Unknown"
            : trigger;

    private static string CompiledTrigger(string authored, Type targetType, string prop)
        => EffectiveTrigger(CompiledOption(authored, "UpdateSourceTrigger", "Default",
            ["Default", "PropertyChanged", "LostFocus", "Explicit"]), targetType, prop);

    internal sealed record CompiledOwner(object Owner, object Source, object Bindings);

    private static CompiledOwner? XBindOwner(FrameworkElement element)
        => ResolveCompiledOwner(element,
            node => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent((DependencyObject)node),
            node => XamlBindingHelper.GetDataTemplateComponent((DependencyObject)node),
            () => WindowOwner(element));

    internal static object? XBindSource(FrameworkElement element, Func<DependencyObject, DependencyObject?>? parent = null,
        Func<DependencyObject, object?>? component = null)
    {
        if (parent is null && component is null) return XBindOwner(element)?.Source;
        parent ??= Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent;
        component ??= node => XamlBindingHelper.GetDataTemplateComponent(node);
        return ResolveCompiledOwner(element, node => parent((DependencyObject)node),
            node => component((DependencyObject)node))?.Source;
    }

    // The nearest generated scope that references the exact target owns it. A nearer scope that does not (for example a
    // UserControl whose content was supplied by the enclosing page) is skipped rather than ending the search.
    internal static CompiledOwner? ResolveCompiledOwner(object selected, Func<object, object?> parent,
        Func<object, object?> component, Func<CompiledOwner?>? windowOwner = null)
    {
        for (object? node = selected; node is not null; node = parent(node))
        {
            object? bindings = component(node);
            FieldInfo? field = FindInstanceField(node.GetType(), "Bindings");
            if (bindings is null && field is null) continue;
            bindings ??= field?.GetValue(node);
            if (GeneratedOwner(node, bindings, selected) is { } owner) return owner;
        }
        return windowOwner?.Invoke();
    }

    private static CompiledOwner? GeneratedOwner(object owner, object? bindings, object selected)
    {
        if (bindings is null ||
            bindings.GetType().GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>()?.Tool
                != "Microsoft.UI.Xaml.Markup.Compiler") return null;
        object? source = FindInstanceField(bindings.GetType(), "dataRoot")?.GetValue(bindings);
        bool ownsTarget = bindings.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.Name.StartsWith("obj", StringComparison.Ordinal) && f.Name.Length > 3 &&
                f.Name.Skip(3).All(char.IsDigit))
            .Select(f => f.GetValue(bindings))
            .Any(value => ReferenceEquals(value is WeakReference weak ? weak.Target : value, selected));
        return source is not null && ownsTarget ? new(owner, source, bindings) : null;
    }

    private static CompiledOwner? WindowOwner(FrameworkElement element)
    {
        if (!element.DispatcherQueue.HasThreadAccess || element.XamlRoot is not { } root) return null;
        var content = root.Content;
        if (content is null) return null;
        return ResolveWindowOwner(element, Application.Current, window =>
        {
            if (!window.DispatcherQueue.HasThreadAccess) return false;
            var windowContent = window.Content;
            return ReferenceEquals(element.XamlRoot, root) &&
                ReferenceEquals(root.Content, content) &&
                ReferenceEquals(windowContent, content) &&
                ReferenceEquals(windowContent.XamlRoot, root);
        });
    }

    internal static CompiledOwner? ResolveWindowOwner(
        object selected, object application, Func<Window, bool> matchesCurrentRoot)
    {
        const int limit = 256;
        var windows = new HashSet<Window>(ReferenceEqualityComparer.Instance);
        var fieldCount = 0;
        // Apps commonly keep the window in a static property (App.MainWindow); fields are read, getters never run.
        for (Type? type = application.GetType(); type is not null && type != typeof(Application) && type != typeof(object);
            type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (++fieldCount > limit) throw new InvalidOperationException("Window owner discovery exceeded its field limit.");
                var value = field.GetValue(field.IsStatic ? null : application);
                if (value is Window window) Add(window);
                else if (value?.GetType() == typeof(Window[]))
                {
                    var array = (Window[])value;
                    if (array.Length > limit) throw new InvalidOperationException("Window owner discovery exceeded its collection limit.");
                    foreach (var item in array) Add(item);
                }
                else if (value?.GetType() == typeof(List<Window>))
                {
                    var list = (List<Window>)value;
                    if (list.Count > limit) throw new InvalidOperationException("Window owner discovery exceeded its collection limit.");
                    foreach (var item in list) Add(item);
                }
            }
        }
        CompiledOwner? found = null;
        foreach (var window in windows)
        {
            var owner = GeneratedOwner(window, FindInstanceField(window.GetType(), "Bindings")?.GetValue(window), selected);
            if (owner is null || !ReferenceEquals(owner.Source, window)) continue;
            if (!matchesCurrentRoot(window)) continue;
            if (found is not null) return null;
            found = owner;
        }
        return found is not null && matchesCurrentRoot((Window)found.Owner) ? found : null;

        void Add(Window? window)
        {
            if (window is not null && windows.Add(window) && windows.Count > limit)
                throw new InvalidOperationException("Window owner discovery exceeded its candidate limit.");
        }
    }

    private static FieldInfo? FindInstanceField(Type? type, string name)
    {
        for (; type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                is { } field) return field;
        return null;
    }

    private static (object? bindings, MethodInfo? update) FindBindingsUpdate(object owner)
    {
        object? bindings = owner is CompiledOwner compiled ? compiled.Bindings : FindInstanceField(owner.GetType(), "Bindings")?.GetValue(owner);
        MethodInfo? update = bindings?.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        return (bindings, update);
    }

    internal static void CompiledCaptureMetadata(StringBuilder sb, CompiledOwner? owner)
    {
        bool rebindable = owner is not null && FindBindingsUpdate(owner).update is not null;
        Field(sb, "restoreKind", rebindable ? "rebind" : "none");
        if (rebindable) RebindScope(sb, owner!);
        else
        {
            Field(sb, "reason", "The selected compiled binding component/source could not be established or has no Update method; no restore route was captured.");
            Field(sb, "nativeFallback", "False");
        }
    }

    internal static string RestoreCompiled(CompiledOwner? owner, bool confirmed, Func<object?> readTarget)
    {
        if (owner is null)
            return Unavailable("the selected compiled binding component/source could not be established; an enclosing page will not be updated", false);
        object? before;
        try { before = readTarget(); }
        catch (Exception ex) { return Unavailable("the selected target could not be read before updating: " + Describe(ex), false); }
        string? refusal = RebindOwner(owner, confirmed);
        if (refusal is not null) return refusal;
        var sb = new StringBuilder("{\"state\":\"unavailable\"");
        Field(sb, "reason", "The owner's compiled bindings were updated, but restoration of the selected property could not be verified. The observed values prove neither restoration nor failure.");
        Field(sb, "nativeFallback", "False");
        Field(sb, "ownerUpdated", "True");
        Field(sb, "restoreVerification", "unchecked");
        Field(sb, "restoreKind", "rebind");
        Field(sb, "before", Str(before));
        try { Field(sb, "after", Str(readTarget())); }
        catch (Exception ex) { Field(sb, "targetUnavailable", Describe(ex.InnerException ?? ex)); }
        RebindScope(sb, owner);
        sb.Append('}');
        return sb.ToString();
    }

    internal static string? RebindOwner(object? owner, bool confirmed)
    {
        if (owner is null) return Unavailable("the selected element's {x:Bind} owner could not be established", false);
        var (bindings, update) = FindBindingsUpdate(owner);
        if (update is null) return Unavailable("this owner has no compiled bindings to re-push", false);
        if (!confirmed)
        {
            var sb = new StringBuilder("{\"state\":\"confirmation-required\"");
            Field(sb, "restoreKind", "rebind");
            RebindScope(sb, owner);
            sb.Append('}');
            return sb.ToString();
        }
        try { update.Invoke(bindings, null); }
        catch (Exception ex) { return Unavailable("re-pushing the compiled bindings threw " + Describe(ex.InnerException ?? ex), false); }
        return null;
    }

    private static void RebindScope(StringBuilder sb, object owner)
    {
        Field(sb, "restoreScope", "owner");
        Field(sb, "restoreOwner", ShortName(owner is CompiledOwner compiled ? compiled.Bindings.GetType() : owner.GetType()));
        Field(sb, "warning", "Calls Bindings.Update() for this entire owner and can replace other live edits on its compiled binding targets. Confirm owner-wide restore before continuing.");
        Field(sb, "restoreDisclosure", "Compiled restore requests an owner-wide source refresh, not verified selected-property restoration; source/model changes are not undone.");
    }

    private static string WriteSource(FrameworkElement element, string prop, string valueText)
    {
        DependencyProperty? dp = FindDp(element, prop);
        if (dp is null) return Unavailable("'" + prop + "' is not a dependency property");

        Binding? binding = ParentBindingOf(element.ReadLocalValue(dp));
        if (binding is null) return Unavailable("'" + prop + "' is not bound to a runtime {Binding}");
        if (binding.Mode != BindingMode.TwoWay)
            return Unavailable("this binding is " + binding.Mode + ", so its source cannot be written");

        string path = binding.Path?.Path ?? "";
        if (string.IsNullOrEmpty(path)) return Unavailable("this binding has no path to write through");

        object? source = RuntimeSource(element, binding);
        if (source is null) return Unavailable("this binding's source could not be resolved");

        string[] parts = path.Split('.');
        object? owner = source;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            PropertyInfo? step = owner?.GetType().GetProperty(parts[i]);
            if (step is null) return Unavailable("'" + parts[i] + "' is not a property on " + ShortName(owner!.GetType()));
            owner = step.GetValue(owner);
            if (owner is null) return Unavailable("'" + parts[i] + "' is null, so nothing below it can be written");
        }
        PropertyInfo? leaf = owner?.GetType().GetProperty(parts[parts.Length - 1]);
        if (leaf is null) return Unavailable("'" + parts[parts.Length - 1] + "' is not a property on " + ShortName(owner!.GetType()));
        if (!leaf.CanWrite) return Unavailable("'" + leaf.Name + "' has no setter, so the source cannot be written");

        object? converted;
        try { converted = ConvertTo(valueText, leaf.PropertyType); }
        catch (Exception ex) { return Unavailable("'" + valueText + "' is not a " + ShortName(leaf.PropertyType) + " (" + Describe(ex) + ")"); }

        var sb = new StringBuilder("{\"state\":\"wroteSource\"");
        Field(sb, "before", Str(element.GetValue(dp)));
        Field(sb, "sourceTarget", ShortName(owner!.GetType()) + "." + leaf.Name);
        try { leaf.SetValue(owner, converted); }
        catch (Exception ex) { return Unavailable("writing the source threw " + Describe(ex)); }

        Field(sb, "after", Str(element.GetValue(dp)));
        Field(sb, "bound", (ParentBindingOf(element.ReadLocalValue(dp)) is not null).ToString());
        sb.Append('}');
        return sb.ToString();
    }

    private static object? ConvertTo(string text, Type target)
    {
        Type t = Nullable.GetUnderlyingType(target) ?? target;
        if (t == typeof(string)) return text;
        if (t.IsEnum) return Enum.Parse(t, text, ignoreCase: true);
        if (t == typeof(bool) || t == typeof(byte) || t == typeof(short) || t == typeof(int)
            || t == typeof(long) || t == typeof(float) || t == typeof(double) || t == typeof(decimal))
        {
            return Convert.ChangeType(text, t, System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new NotSupportedException(ShortName(t) + " is not a type this can write");
    }

    internal static string? ParseXBindPath(string authored)
    {
        if (string.IsNullOrEmpty(authored)) return null;
        string s = authored.Trim();
        if (!s.StartsWith("{x:Bind", StringComparison.Ordinal)) return null;
        s = s.Substring("{x:Bind".Length).TrimEnd('}').Trim();
        if (s.Length == 0) return "";                       // {x:Bind} with no path binds the source itself

        foreach (string rawPart in SplitTopLevel(s))
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;
            if (part.StartsWith("Path=", StringComparison.Ordinal)) return part.Substring(5).Trim();
            if (part.IndexOf('=') >= 0) continue;           // Mode=OneWay, Converter=..., FallbackValue=...
            return part;
        }
        return "";
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == ',' && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    private static Binding? ParentBindingOf(object? localValue)
    {
        if (localValue is null) return null;
        if (localValue is Binding direct) return direct;
        PropertyInfo? pi = localValue.GetType().GetProperty("ParentBinding",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pi is null) return null;
        try { return pi.GetValue(localValue) as Binding; }
        catch { return null; }
    }

    private static FrameworkElement? FindByName(FrameworkElement from, string name)
    {
        for (DependencyObject? node = from; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement fe && fe.FindName(name) is FrameworkElement hit) return hit;
        }
        return null;
    }


    private static DependencyProperty? FindDp(object element, string prop)
    {
        for (Type? t = element.GetType(); t is not null; t = t.BaseType)
        {
            if (t.GetField(prop + "Property", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetValue(null) is DependencyProperty dp) return dp;
            if (t.GetProperty(prop + "Property", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetValue(null) is DependencyProperty dp2) return dp2;
        }
        return null;
    }

    private static Type ClrTypeOf(FrameworkElement element, string prop)
        => element.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.PropertyType ?? typeof(object);

    private static string Fault(string state, string segment, string reason, string path, string sourceLabel, string kind, string mode)
    {
        var sb = new StringBuilder("{\"state\":\"" + state + "\"");
        Field(sb, "segment", segment);
        Field(sb, "reason", reason);
        Common(sb, path, sourceLabel, kind, mode);
        sb.Append('}');
        return sb.ToString();
    }

    private static void Common(StringBuilder sb, string path, string sourceLabel, string kind, string mode)
    {
        Field(sb, "evaluationScope", "forward-path-and-type");
        Field(sb, "reversePropagation", "unchecked");
        Field(sb, "path", path);
        Field(sb, "source", sourceLabel);
        Field(sb, "kind", kind);
        if (!string.IsNullOrEmpty(mode)) Field(sb, "mode", mode);
    }

    private static string Unavailable(string why, bool nativeFallback = true)
        => "{\"state\":\"unavailable\",\"reason\":\"" + Esc(why) + "\"" +
           (nativeFallback ? "" : ",\"nativeFallback\":\"False\"") + "}";

    private static string ShortName(Type t) => t.Name;

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static string Str(object? v)
    {
        if (v is null) return "(null)";
        if (ReferenceEquals(v, DependencyProperty.UnsetValue)) return "(unset)";
        try { return v.ToString() ?? "(null)"; }
        catch (Exception ex) { return "(ToString threw " + Describe(ex) + ")"; }
    }

    private static void Field(StringBuilder sb, string key, string? value)
        => sb.Append(",\"").Append(key).Append("\":\"").Append(Esc(value ?? "")).Append('"');

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': break;
                case '\n':
                case '\t': sb.Append(' '); break;
                default: sb.Append(c < 0x20 ? ' ' : c); break;
            }
        }
        return sb.ToString();
    }
}
