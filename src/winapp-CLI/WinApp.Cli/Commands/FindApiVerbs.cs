// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Commands;

/// <summary><c>winapp find-api members &lt;Type&gt;</c> — list a type's properties, events, and methods.</summary>
internal sealed class FindApiMembersCommand : Command, IShortDescription
{
    public string ShortDescription => "List a type's properties, events, and methods";

    public static Argument<string[]> TypeArgument { get; } = new("type")
    {
        Description = "One or more types to inspect. Accepts short names (NavigationView) or fully-qualified names (Microsoft.UI.Xaml.Controls.NavigationView). Pass several to resolve them in a single call.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public static Option<string?> FilterOption { get; } = new("--filter")
    {
        Description = "Only list members whose name contains this text (case-insensitive), e.g. --filter background. Totals for the unfiltered type are still reported. Applies to every type in the call.",
    };

    public static Option<bool> AllOption { get; } = new("--all")
    {
        Description = "List the complete member surface: include dependency-property identifier statics (BackgroundProperty) and per-member descriptions, both of which an unfiltered listing omits to save context. Implied by --verbose, and usable together with --json (--verbose is not).",
    };

    public FindApiMembersCommand()
        : base("members", "List the properties, events, and methods of one or more types (with XML-doc descriptions and inherited members), resolved from the project's indexed API metadata. Pass several type names to inspect them all in one call.")
    {
        Arguments.Add(TypeArgument);
        Options.Add(FilterOption);
        Options.Add(AllOption);
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            List<string> types = FindApiShared.ReadSubjects(parseResult.GetValue(TypeArgument));
            if (types.Count == 0)
            {
                return FindApiShared.Fail(console, json, "At least one type name is required, e.g. winapp find-api members NavigationView (or several: members NavigationView InfoBar).");
            }

            var scope = FindApiShared.ReadScope(parseResult);
            string? filter = parseResult.GetValue(FilterOption);

            // --verbose cannot be combined with --json, so --all is the escape hatch that
            // works on both surfaces; --verbose implies it for interactive text callers.
            bool all = parseResult.GetValue(AllOption) || parseResult.GetValue(WinAppRootCommand.VerboseOption);

            if (types.Count == 1)
            {
                var single = service.Members(types[0], scope, filter, all);
                return FindApiShared.Emit(
                    console, json, "members", single, WinAppJsonContext.Default.ApiMembersOutput,
                    data => FindApiShared.RenderMembers(console, data),
                    data => (0, data.Properties.Count + data.Events.Count + data.Methods.Count, true));
            }

            var results = service.MembersBatch(types, scope, filter, all);
            return FindApiShared.EmitBatch(
                console, json, "members", results,
                (ok, errors) => new ApiMembersBatchOutput
                {
                    Count = ok.Count,
                    Results = ok,
                    Errors = errors.Count > 0 ? errors : null,
                },
                WinAppJsonContext.Default.ApiMembersBatchOutput,
                data => FindApiShared.RenderMembers(console, data),
                data => (data.Properties.Count + data.Events.Count + data.Methods.Count, true));
        }
    }
}

/// <summary><c>winapp find-api check-property &lt;Type&gt; &lt;Property&gt;</c> — validate a property exists on a type.</summary>
internal sealed class FindApiCheckPropertyCommand : Command, IShortDescription
{
    public string ShortDescription => "Validate that a property exists on a type";

    public static Argument<string?> TypeArgument { get; } = new("type")
    {
        Description = "The type to check.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string[]> PropertyArgument { get; } = new("property")
    {
        Description = "One or more property names to validate on the type. Pass several to check them all in a single call.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public FindApiCheckPropertyCommand()
        : base("check-property", "Validate that one or more properties exist on a type before you write XAML/code against it. Pass several property names to check them in one call. On a miss, suggests similar properties on the type, attached-property forms, and other types that declare the property. Exits non-zero when any property does not exist.")
    {
        Arguments.Add(TypeArgument);
        Arguments.Add(PropertyArgument);
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? type = parseResult.GetValue(TypeArgument);
            List<string> properties = FindApiShared.ReadSubjects(parseResult.GetValue(PropertyArgument));
            if (string.IsNullOrWhiteSpace(type) || properties.Count == 0)
            {
                return FindApiShared.Fail(console, json, "Usage: winapp find-api check-property <Type> <Property> [<Property>...].");
            }

            var scope = FindApiShared.ReadScope(parseResult);

            if (properties.Count == 1)
            {
                var single = service.CheckProperty(type, properties[0], scope);
                return FindApiShared.Emit(
                    console, json, "check-property", single, WinAppJsonContext.Default.ApiCheckPropertyOutput,
                    data => FindApiShared.RenderCheckProperty(console, data),
                    data => (data.Found ? 0 : 1, 0, data.Found));
            }

            var results = service.CheckProperties(type, properties, scope);
            return FindApiShared.EmitBatch(
                console, json, "check-property", results,
                (ok, errors) => new ApiCheckPropertyBatchOutput
                {
                    Count = ok.Count,
                    MissingCount = ok.Count(r => !r.Found) + errors.Count,
                    Results = ok,
                    Errors = errors.Count > 0 ? errors : null,
                },
                WinAppJsonContext.Default.ApiCheckPropertyBatchOutput,
                data => FindApiShared.RenderCheckPropertyCompact(console, data),
                data => (0, data.Found),
                separateItems: false);
        }
    }
}

/// <summary><c>winapp find-api types &lt;Namespace&gt;</c> — list the types in a namespace.</summary>
internal sealed class FindApiTypesCommand : Command, IShortDescription
{
    public string ShortDescription => "List the types declared in a namespace";

    public static Argument<string?> NamespaceArgument { get; } = new("namespace")
    {
        Description = "The namespace to list, e.g. Microsoft.UI.Xaml.Controls.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public FindApiTypesCommand()
        : base("types", "List the types declared in a namespace (class/struct/enum/interface/delegate) with their base types.")
    {
        // Hidden, not removed. Across two full benchmark iterations (48 trial-runs) this
        // verb was never invoked once, while sitting in help and in the generated schema
        // as one more near-duplicate for an agent to choose wrongly between `search`,
        // `members`, and `types`. It still works for anyone who knows it exists.
        Hidden = true;
        Arguments.Add(NamespaceArgument);
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? ns = parseResult.GetValue(NamespaceArgument);
            if (string.IsNullOrWhiteSpace(ns))
            {
                return FindApiShared.Fail(console, json, "A namespace is required, e.g. winapp find-api types Microsoft.UI.Xaml.Controls.");
            }

            var scope = FindApiShared.ReadScope(parseResult);
            var result = service.Types(ns, scope);
            return FindApiShared.Emit(
                console, json, "types", result, WinAppJsonContext.Default.ApiTypesOutput,
                data => FindApiShared.RenderTypes(console, data),
                data => (0, data.Types.Count, true));
        }
    }
}

/// <summary><c>winapp find-api enums &lt;Type&gt;</c> — list an enum's values.</summary>
internal sealed class FindApiEnumsCommand : Command, IShortDescription
{
    public string ShortDescription => "List the values of an enum type";

    public static Argument<string[]> TypeArgument { get; } = new("type")
    {
        Description = "One or more enum types to list, e.g. Symbol or Microsoft.UI.Xaml.Controls.Symbol. Pass several to list them in a single call.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public static Option<string?> FilterOption { get; } = new("--filter")
    {
        Description = "Only list values whose name contains this text (case-insensitive), e.g. --filter folder. The unfiltered total is still reported. Prefer listing the whole enum once over repeated filtered calls \u2014 most enums are small enough that the full list is cheaper than several narrowed lookups.",
    };

    public FindApiEnumsCommand()
        : base("enums", "List the values of one or more enum types. Pass several type names to list them in one call. Exits non-zero when a type exists but is not an enum.")
    {
        Arguments.Add(TypeArgument);
        Options.Add(FilterOption);
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            List<string> types = FindApiShared.ReadSubjects(parseResult.GetValue(TypeArgument));
            if (types.Count == 0)
            {
                return FindApiShared.Fail(console, json, "At least one enum type name is required, e.g. winapp find-api enums Symbol (or several: enums Symbol IconSource).");
            }

            var scope = FindApiShared.ReadScope(parseResult);
            string? filter = parseResult.GetValue(FilterOption);

            if (types.Count == 1)
            {
                var single = service.Enums(types[0], scope, filter);
                return FindApiShared.Emit(
                    console, json, "enums", single, WinAppJsonContext.Default.ApiEnumsOutput,
                    data => FindApiShared.RenderEnums(console, data),
                    data => (0, data.Values.Count, true));
            }

            var results = service.EnumsBatch(types, scope, filter);
            return FindApiShared.EmitBatch(
                console, json, "enums", results,
                (ok, errors) => new ApiEnumsBatchOutput
                {
                    Count = ok.Count,
                    Results = ok,
                    Errors = errors.Count > 0 ? errors : null,
                },
                WinAppJsonContext.Default.ApiEnumsBatchOutput,
                data => FindApiShared.RenderEnums(console, data),
                data => (data.Values.Count, true));
        }
    }
}

/// <summary><c>winapp find-api namespaces [--filter &lt;prefix&gt;]</c> — list indexed namespaces.</summary>
internal sealed class FindApiNamespacesCommand : Command, IShortDescription
{
    public string ShortDescription => "List the namespaces available to the project";

    public static Option<string?> FilterOption { get; } = new("--filter")
    {
        Description = "Only list namespaces starting with this prefix, e.g. --filter Microsoft.UI.",
    };

    public FindApiNamespacesCommand()
        : base("namespaces", "List the namespaces available to the project across its indexed API metadata, optionally filtered by prefix.")
    {
        // Hidden for the same reason as `types`: never invoked in 48 trial-runs.
        Hidden = true;
        Options.Add(FilterOption);
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            string? filter = parseResult.GetValue(FilterOption);
            var scope = FindApiShared.ReadScope(parseResult);
            var result = service.Namespaces(filter, scope);
            return FindApiShared.Emit(
                console, json, "namespaces", result, WinAppJsonContext.Default.ApiNamespacesOutput,
                data => FindApiShared.RenderNamespaces(console, data),
                data => (0, data.Namespaces.Count, true));
        }
    }
}

/// <summary><c>winapp find-api packages</c> — list the indexed packages for a project.</summary>
internal sealed class FindApiPackagesCommand : Command, IShortDescription
{
    public string ShortDescription => "List the indexed metadata packages for a project";

    public FindApiPackagesCommand()
        : base("packages", "List the NuGet/SDK packages whose API metadata is indexed for a project, with per-package type and member counts.")
    {
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var scope = FindApiShared.ReadScope(parseResult);
            var result = service.Packages(scope);
            return FindApiShared.Emit(
                console, json, "packages", result, WinAppJsonContext.Default.ApiPackagesOutput,
                data => FindApiShared.RenderPackages(console, data),
                data => (0, data.Packages.Count, true));
        }
    }
}

/// <summary><c>winapp find-api stats</c> — show aggregate index statistics for a project.</summary>
internal sealed class FindApiStatsCommand : Command, IShortDescription
{
    public string ShortDescription => "Show aggregate API-index statistics for a project";

    public FindApiStatsCommand()
        : base("stats", "Show aggregate statistics for a project's API index: package, namespace, type, member, and .winmd file counts.")
    {
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var scope = FindApiShared.ReadScope(parseResult);
            var result = service.Stats(scope);
            return FindApiShared.Emit(
                console, json, "stats", result, WinAppJsonContext.Default.ApiStatsOutput,
                data => FindApiShared.RenderStats(console, data),
                data => (0, data.Types, true));
        }
    }
}

/// <summary><c>winapp find-api projects</c> — list all indexed projects.</summary>
internal sealed class FindApiProjectsCommand : Command, IShortDescription
{
    public string ShortDescription => "List all indexed projects";

    public FindApiProjectsCommand()
        : base("projects", "List every project that currently has an API index in the shared cache, with the number of packages indexed for each.")
    {
        // Hidden for the same reason as `types`: never invoked in 48 trial-runs. This one
        // is also cache introspection rather than API discovery, so it answers a question
        // an agent writing code never has.
        Hidden = true;
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var data = service.Projects();
            return FindApiShared.EmitRaw(
                console, json, "projects", data, WinAppJsonContext.Default.ApiProjectsOutput,
                d => FindApiShared.RenderProjects(console, d),
                d => (0, d.Projects.Count, d.Projects.Count > 0));
        }
    }
}

/// <summary><c>winapp find-api refresh</c> — rebuild the API index for a project.</summary>
internal sealed class FindApiRefreshCommand : Command, IShortDescription
{
    public string ShortDescription => "Rebuild the API index for a project";

    public static Option<bool> ScanOption { get; } = new("--scan")
    {
        Description = "Recursively discover and index every project under the directory instead of just the top-level project(s).",
    };

    public FindApiRefreshCommand()
        : base("refresh", "Rebuild the API metadata index for a project from its restored packages. Runs automatically when a project is restored; run it manually to force a re-index or to index a project for the first time.")
    {
        Options.Add(ScanOption);
        FindApiShared.AddScopeOptions(this);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            bool quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            bool scan = parseResult.GetValue(ScanOption);
            var scope = FindApiShared.ReadScope(parseResult);

            // Progress prints in text mode only; --json and --quiet suppress it so
            // scripted/quiet callers get a clean payload on stdout.
            Action<string>? onProgress = (json || quiet) ? null : msg => console.MarkupLineInterpolated($"[grey]{msg}[/]");
            // An explicit refresh forces a full rebuild (ignoring reused caches).
            var data = service.Refresh(scope, scan, onProgress, force: true);
            return FindApiShared.Emit(
                console, json, "refresh", data, WinAppJsonContext.Default.ApiRefreshOutput,
                d => FindApiShared.RenderRefresh(console, d),
                d => (d.ProjectsProcessed > 0 ? 0 : 1, d.ProjectsProcessed, d.ProjectsProcessed > 0));
        }
    }
}
