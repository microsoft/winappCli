// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins the discovery and handshake contract: parsing a <c>winapp-devtools-&lt;pid&gt;</c> pipe leaf to its target
/// pid, enumerating injected apps from an injectable pipe lister (dedup + sort + junk-rejection), and parsing
/// the <c>hello</c> capability reply defensively. All pure — no running app or real pipe needed.
/// </summary>
[TestClass]
public class DevToolsPipeDiscoveryTests
{
    [TestMethod]
    [DataRow("winapp-devtools-12345", 12345)]
    [DataRow(@"\\.\pipe\winapp-devtools-12345", 12345)]        // full pipe path -> last segment
    [DataRow("winapp-devtools-1", 1)]
    public void TryParseTapPid_ValidNames_ReturnsPid(string name, int expected)
    {
        Assert.AreEqual(expected, DevToolsPipeDiscovery.TryParseTapPid(name));
    }

    [TestMethod]
    [DataRow(null)]                    // null
    [DataRow("")]                      // empty
    [DataRow("winapp-devtools-")]              // prefix only, no pid
    [DataRow("winapp-devtools-abc")]          // non-numeric
    [DataRow("winapp-devtools-12345x")]       // trailing junk after number
    [DataRow("winapp-devtools--5")]           // negative sign rejected by NumberStyles.None
    [DataRow("winapp-devtools-0")]            // zero is not a valid pid
    [DataRow("winapp-devtools- 5")]           // leading whitespace rejected
    [DataRow("other-pipe-12345")]     // wrong prefix
    [DataRow("CoreFxPipe_something")] // unrelated system pipe
    public void TryParseTapPid_InvalidNames_ReturnsNull(string? name)
    {
        Assert.IsNull(DevToolsPipeDiscovery.TryParseTapPid(name));
    }

    [TestMethod]
    public void PipeNameFor_BuildsPrefixedName()
    {
        Assert.AreEqual("winapp-devtools-4242", DevToolsPipeDiscovery.PipeNameFor(4242));
    }

    [TestMethod]
    public void EnumerateInjectedPids_FiltersSortsAndDedups()
    {
        IEnumerable<string> Fake() =>
        [
            @"\\.\pipe\winapp-devtools-300",
            @"\\.\pipe\winapp-devtools-100",
            @"\\.\pipe\winapp-devtools-100",   // duplicate leaf -> deduped
            @"\\.\pipe\winapp-devtools-200",
            @"\\.\pipe\CoreFxPipe_abc", // unrelated pipe -> filtered
            @"\\.\pipe\winapp-devtools-oops",   // malformed -> filtered
        ];

        var pids = DevToolsPipeDiscovery.EnumerateInjectedPids(Fake);

        int[] expected = [100, 200, 300];
        CollectionAssert.AreEqual(expected, pids.ToArray());
    }

    [TestMethod]
    public void EnumerateInjectedPids_EmptyListing_ReturnsEmpty()
    {
        var pids = DevToolsPipeDiscovery.EnumerateInjectedPids(() => []);
        Assert.AreEqual(0, pids.Count);
    }

    [TestMethod]
    public void EnumerateInjectedPids_AccessFailure_IsNotAnEmptyAppList()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            DevToolsPipeDiscovery.EnumerateInjectedPids(() => throw new UnauthorizedAccessException("controlled denial")));
    }

    [TestMethod]
    public void TapHello_TryParse_ValidReply_PopulatesFields()
    {
        const string line = """
            {"protocol":"winapp-devtools","protocolVersion":"1","experimental":true,"pid":12345,"mutation":true,"posture":"mutation"}
            """;

        var hello = TapHello.TryParse(line);

        Assert.IsNotNull(hello);
        Assert.AreEqual("winapp-devtools", hello.Protocol);
        Assert.AreEqual("1", hello.ProtocolVersion);
        Assert.IsTrue(hello.Experimental);
        Assert.AreEqual(12345, hello.Pid);
        Assert.IsTrue(hello.Mutation);
    }

    [TestMethod]
    public void TapHello_TryParse_MutationDenied_ReportsReadOnly()
    {
        const string line = """{"protocol":"winapp-devtools","protocolVersion":"1","experimental":true,"pid":7,"mutation":false,"posture":"ui"}""";

        var hello = TapHello.TryParse(line);

        Assert.IsNotNull(hello);
        Assert.IsFalse(hello.Mutation);
        Assert.AreEqual("ui", hello.Posture);
    }

    [TestMethod]
    [DataRow(null)]                                                   // null line
    [DataRow("")]                                                     // empty
    [DataRow("   ")]                                                  // whitespace
    [DataRow("ERR unknown")]                                          // older tap: no hello verb
    [DataRow("pong 27")]                                              // wrong verb reply
    [DataRow("not json at all")]                                      // malformed
    [DataRow("[1,2,3]")]                                              // JSON but not an object
    [DataRow("""{"protocol":"devtools","protocolVersion":"1"}""")]        // wrong protocol id
    [DataRow("""{"protocolVersion":"0"}""")]                          // missing protocol field
    [DataRow("""{"protocol":"winapp-devtools","protocolVersion":"1","pid":7,"mutation":false}""")] // posture required
    [DataRow("""{"protocol":"winapp-devtools","protocolVersion":"1","pid":7,"mutation":false,"posture":"admin"}""")]
    [DataRow("""{"protocol":"winapp-devtools","protocolVersion":"1","pid":7,"mutation":true,"posture":"read"}""")] // contradictory
    [DataRow("""{"protocol":"winapp-devtools","protocolVersion":"1","pid":"7","mutation":true,"posture":"mutation"}""")]
    [DataRow("""{"protocol":"winapp-devtools","protocolVersion":"1","pid":null,"mutation":true,"posture":"mutation"}""")]
    public void TapHello_TryParse_InvalidReplies_ReturnNull(string? line)
    {
        Assert.IsNull(TapHello.TryParse(line));
    }
}
