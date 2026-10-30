// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class CommentsAppRootTests
{
    private DirectoryInfo _root = null!;
    private string _app = string.Empty;
    private string _cwd = string.Empty;
    private readonly CommentStore _store = new();

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateTempSubdirectory("winapp-comment-roots-");
        _app = _root.CreateSubdirectory("AppA").FullName;
        _cwd = _root.CreateSubdirectory("AppB").FullName;
        foreach (var project in new[] { _app, _cwd })
        {
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            _store.Add(StorePath(project), new Comment
            {
                Id = "shared-id", Text = project == _app ? "App A comment" : "App B comment",
                ProjectRoot = project, Anchor = new() { Weak = true },
            });
        }
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    private string StorePath(string project) => _store.GetStorePath(new DirectoryInfo(project));

    private (int Exit, string Output, RecordingPusher Pusher) Run(string verb,
        Func<uint, CancellationToken, string?> reader, bool json = true, string? explicitRoot = null, bool withApp = true, int pid = 4321)
    {
        using var console = new TestConsole();
        console.Profile.Width = 4096;
        var target = new CommentTestTargetResolver(pid);
        var resolver = new CommentAnchorResolver();
        var cwd = new FixedDirectory(_cwd);
        var pusher = new RecordingPusher();
        (Command Command, AsynchronousCommandLineAction Handler) operation = verb switch
        {
            "list" => (new DevToolsCommentsListCommand(), new DevToolsCommentsListCommand.Handler(
                _store, resolver, cwd, console, NullLogger<DevToolsCommentsListCommand>.Instance, target) { ReadSourceRoot = reader }),
            "get" => (new DevToolsCommentsGetCommand(), new DevToolsCommentsGetCommand.Handler(
                _store, resolver, cwd, console, target) { ReadSourceRoot = reader }),
            "update" => (new DevToolsCommentsUpdateCommand(), new DevToolsCommentsUpdateCommand.Handler(
                _store, resolver, pusher, target, cwd, console, NullLogger<DevToolsCommentsUpdateCommand>.Instance) { ReadSourceRoot = reader }),
            "delete" => (new DevToolsCommentsDeleteCommand(), new DevToolsCommentsDeleteCommand.Handler(
                _store, resolver, pusher, target, cwd, console, NullLogger<DevToolsCommentsDeleteCommand>.Instance) { ReadSourceRoot = reader }),
            _ => throw new ArgumentException("Unknown test command.", nameof(verb)),
        };
        var args = new List<string>();
        if (verb != "list") { args.Add("shared-id"); }
        if (verb == "update") { args.AddRange(["--status", "resolved"]); }
        if (withApp) { args.AddRange(["--app", "4321"]); }
        if (json) { args.Add("--json"); }
        if (explicitRoot is not null) { args.AddRange(["--source-root", explicitRoot]); }
        var parsed = operation.Command.Parse(args.ToArray());
        Assert.IsEmpty(parsed.Errors);
        return (operation.Handler.InvokeAsync(parsed).GetAwaiter().GetResult(), console.Output, pusher);
    }

    [TestMethod]
    [DataRow("list")]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("delete")]
    public void SelectedApp_UsesProjectA_WhenCurrentDirectoryIsProjectB(string verb)
    {
        var before = File.ReadAllBytes(StorePath(_cwd));
        var (exit, output, pusher) = Run(verb, (pid, _) =>
        {
            Assert.AreEqual(4321u, pid);
            return _app;
        });

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "App A comment");
        Assert.IsFalse(output.Contains("App B comment", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(StorePath(_cwd)));
        AssertMutation(verb, _app);
        if (verb is "update" or "delete")
        {
            Assert.AreEqual(_app, pusher.SourceRoot);
            Assert.AreEqual(StorePath(_app), pusher.StorePath);
        }
    }

    [TestMethod]
    [DataRow("list")]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("delete")]
    public void ExplicitSourceRoot_DoesNotReadTheAppRoot(string verb)
    {
        var (exit, output, _) = Run(verb, (_, _) => throw new AssertFailedException("Explicit root must win."),
            explicitRoot: _app);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "App A comment");
        AssertMutation(verb, _app);
        Assert.AreEqual(CommentStatus.Open, _store.Get(StorePath(_cwd), "shared-id")!.Status);
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("delete")]
    public void AppWithoutAStore_DoesNotMutateTheCurrentDirectoryStore(string verb)
    {
        var emptyProject = _root.CreateSubdirectory("AppWithoutComments").FullName;
        var before = File.ReadAllBytes(StorePath(_cwd));

        var (exit, output, _) = Run(verb, (_, _) => emptyProject);

        Assert.AreEqual(1, exit, output);
        var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentResultPayload)!;
        StringAssert.Contains(payload.Error!, "No comment store found");
        StringAssert.Contains(payload.Error!, emptyProject);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(StorePath(_cwd)));
        Assert.IsFalse(File.Exists(StorePath(emptyProject)));
    }

    [TestMethod]
    [DataRow("list")]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("delete")]
    public void WithoutApp_UsesCurrentDirectoryWithoutAConnection(string verb)
    {
        var before = File.ReadAllBytes(StorePath(_app));
        var (exit, output, _) = Run(verb, (_, _) => throw new AssertFailedException("Offline command connected."), withApp: false);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "App B comment");
        AssertMutation(verb, _cwd);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(StorePath(_app)));
    }

    [TestMethod]
    [DataRow("list", false)]
    [DataRow("list", true)]
    [DataRow("get", false)]
    [DataRow("get", true)]
    [DataRow("update", false)]
    [DataRow("update", true)]
    [DataRow("delete", false)]
    [DataRow("delete", true)]
    public void UnavailableApp_FailsWithoutReadingOrMutatingTheCurrentStore(string verb, bool json)
    {
        var before = File.ReadAllBytes(StorePath(_cwd));
        foreach (var failure in new[] { "protocol", "io", "json", "missing" })
        {
            var (exit, output, pusher) = Run(verb, (_, _) => failure switch
            {
                "protocol" => throw new DevToolsProtocolException(DevToolsProtocolError.NoResponse("The app did not answer.")),
                "io" => throw new IOException("Connection closed."),
                "json" => throw new JsonException("Invalid root response."),
                _ => null,
            }, json);

            Assert.AreEqual(1, exit, output);
            StringAssert.Contains(output, "--devtools on");
            StringAssert.Contains(output, "--source-root");
            Assert.IsFalse(output.Contains("App B comment", StringComparison.Ordinal));
            Assert.IsFalse(output.Contains("DevToolsProtocolException", StringComparison.Ordinal));
            if (json)
            {
                var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentResultPayload)!;
                Assert.IsFalse(payload.Ok);
                Assert.IsNull(payload.Comment);
                Assert.IsNotNull(payload.Error);
            }
            else
            {
                StringAssert.Contains(output, "Error:");
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(StorePath(_cwd)));
            Assert.IsNull(pusher.StorePath, "A failed root lookup must not refresh another store.");
        }
    }

    [TestMethod]
    public void RootResolution_DoesNotSwallowCancellation()
    {
        Assert.ThrowsExactly<OperationCanceledException>(() => CommentsSharedOptions.TryReadAppRoot(
            new CommentTestTargetResolver(), "4321", (_, _) => throw new OperationCanceledException(),
            default, out _, out _));
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("list")]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("delete")]
    public void ProtocolRootFailure_IsAnActionableCommandError(string verb)
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Error("Internal.sourceRoot", -32000, "no-response", "DevTools did not answer.");
        var before = File.ReadAllBytes(StorePath(_cwd));

        var (exit, output, _) = Run(verb, CommentsSharedOptions.ReadTapSourceRoot, pid: agent.Pid);

        Assert.AreEqual(1, exit, output);
        var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentResultPayload)!;
        Assert.IsFalse(payload.Ok);
        StringAssert.Contains(payload.Error!, "DevTools is unavailable");
        StringAssert.Contains(payload.Error!, "--source-root");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(StorePath(_cwd)));
    }

    private void AssertMutation(string verb, string project)
    {
        var saved = _store.Get(StorePath(project), "shared-id");
        if (verb == "delete")
        {
            Assert.IsNull(saved);
        }
        else
        {
            Assert.AreEqual(verb == "update" ? CommentStatus.Resolved : CommentStatus.Open, saved!.Status);
        }
    }

    private sealed class FixedDirectory(string path) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => path;
        public DirectoryInfo GetCurrentDirectoryInfo() => new(path);
    }

    private sealed class RecordingPusher : ICommentPusher
    {
        public string? SourceRoot { get; private set; }
        public string? StorePath { get; private set; }

        public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
            CancellationToken cancellationToken = default)
        {
            SourceRoot = fallbackRoot;
            return (0, 0);
        }

        public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default)
        {
            StorePath = storePath;
            return (0, 0);
        }
    }
}
