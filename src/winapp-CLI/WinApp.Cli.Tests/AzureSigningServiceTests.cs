// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzureSigningServiceTests
{
    private static AzureSigningService CreateService(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AzureSigningService(NullLogger<AzureSigningService>.Instance, http);
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_ParsesAndFiltersByEnabledState()
    {
        const string json = """
        {
            "value": [
                { "subscriptionId": "sub-1", "displayName": "Enabled Sub", "state": "Enabled" },
                { "subscriptionId": "sub-2", "displayName": "Disabled Sub", "state": "Disabled" }
            ]
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var subscriptions = await service.ListSubscriptionsAsync("token");

        // Only the enabled subscription is returned.
        Assert.AreEqual(1, subscriptions.Count);
        Assert.AreEqual("sub-1", subscriptions[0].SubscriptionId);
        Assert.AreEqual("Enabled Sub", subscriptions[0].DisplayName);

        // The bearer token is attached and the subscriptions endpoint is targeted.
        Assert.AreEqual("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.AreEqual("token", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("https://management.azure.com/subscriptions?api-version=", handler.LastRequestUri!);
    }

    [TestMethod]
    public async Task ListSigningAccountsAsync_WithResourceGroup_BuildsScopedUrlAndParsesAccount()
    {
        const string json = """
        {
            "value": [
                {
                    "name": "myaccount",
                    "location": "eastus",
                    "id": "/subscriptions/sub-1/resourceGroups/my-rg/providers/Microsoft.CodeSigning/codeSigningAccounts/myaccount",
                    "properties": { "accountUri": "https://eus.codesigning.azure.net" }
                }
            ]
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var accounts = await service.ListSigningAccountsAsync("token", "sub-1", "my-rg");

        Assert.AreEqual(1, accounts.Count);
        Assert.AreEqual("myaccount", accounts[0].Name);
        Assert.AreEqual("my-rg", accounts[0].ResourceGroup);
        Assert.AreEqual("eastus", accounts[0].Location);
        Assert.AreEqual("https://eus.codesigning.azure.net", accounts[0].AccountUri);

        // Resource-group-scoped URL is used when a group is supplied.
        Assert.Contains("/resourceGroups/my-rg/providers/Microsoft.CodeSigning/codeSigningAccounts", handler.LastRequestUri!);
    }

    [TestMethod]
    public async Task ListSigningAccountsAsync_WithoutResourceGroup_BuildsSubscriptionWideUrl()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "value": [] }""");

        var service = CreateService(handler);
        var accounts = await service.ListSigningAccountsAsync("token", "sub-1");

        Assert.AreEqual(0, accounts.Count);
        Assert.Contains("/subscriptions/sub-1/providers/Microsoft.CodeSigning/codeSigningAccounts", handler.LastRequestUri!);
        Assert.IsFalse(handler.LastRequestUri!.Contains("/resourceGroups/"), "Should not include a resource group segment");
    }

    [TestMethod]
    public async Task ListCertificateProfilesAsync_ParsesProfiles()
    {
        const string json = """
        {
            "value": [
                { "name": "profile-a", "properties": { "profileType": "PublicTrust", "status": "Active" } }
            ]
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var profiles = await service.ListCertificateProfilesAsync("token", "sub-1", "my-rg", "myaccount");

        Assert.AreEqual(1, profiles.Count);
        Assert.AreEqual("profile-a", profiles[0].Name);
        Assert.AreEqual("PublicTrust", profiles[0].ProfileType);
        Assert.AreEqual("Active", profiles[0].Status);
        Assert.Contains("/codeSigningAccounts/myaccount/certificateProfiles", handler.LastRequestUri!);
    }

    [TestMethod]
    public async Task GetArmResponse_OnErrorStatus_SurfacesParsedAzureError()
    {
        const string errorJson = """
        { "error": { "code": "AuthorizationFailed", "message": "The client does not have authorization." } }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.Forbidden, errorJson);

        var service = CreateService(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ListSubscriptionsAsync("token"));

        Assert.Contains("AuthorizationFailed", ex.Message);
        Assert.Contains("The client does not have authorization.", ex.Message);
    }

    [TestMethod]
    public async Task GetArmResponse_OnErrorStatusWithNonJsonBody_FallsBackToStatusCode()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "<html>boom</html>");

        var service = CreateService(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ListSubscriptionsAsync("token"));

        Assert.Contains("500", ex.Message);
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_FollowsNextLinkAcrossPages()
    {
        const string page1 = """
        {
            "value": [ { "subscriptionId": "sub-1", "displayName": "Page 1 Sub", "state": "Enabled" } ],
            "nextLink": "https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=page2"
        }
        """;
        const string page2 = """
        {
            "value": [ { "subscriptionId": "sub-2", "displayName": "Page 2 Sub", "state": "Enabled" } ]
        }
        """;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page1) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page2) });

        var service = new AzureSigningService(NullLogger<AzureSigningService>.Instance, new HttpClient(handler));
        var subscriptions = await service.ListSubscriptionsAsync("token");

        Assert.AreEqual(2, subscriptions.Count, "Both pages of results should be combined");
        Assert.AreEqual("sub-1", subscriptions[0].SubscriptionId);
        Assert.AreEqual("sub-2", subscriptions[1].SubscriptionId);
        Assert.AreEqual(2, handler.RequestUris.Count, "Both the initial URL and the nextLink should be requested");
        Assert.Contains("$skiptoken=page2", handler.RequestUris[1]);
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_RepeatedNextLink_ThrowsInsteadOfLoopingForever()
    {
        const string cyclic = """
        {
            "value": [ { "subscriptionId": "sub-1", "displayName": "Loop Sub", "state": "Enabled" } ],
            "nextLink": "https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=loop"
        }
        """;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cyclic) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cyclic) });

        var service = new AzureSigningService(NullLogger<AzureSigningService>.Instance, new HttpClient(handler));
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ListSubscriptionsAsync("token"));

        Assert.Contains("repeated nextLink", ex.Message);
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_NextLinkToUntrustedHost_ThrowsWithoutFollowingIt()
    {
        const string page1 = """
        {
            "value": [ { "subscriptionId": "sub-1", "displayName": "Page 1 Sub", "state": "Enabled" } ],
            "nextLink": "https://evil.example.com/subscriptions?api-version=2022-12-01&$skiptoken=page2"
        }
        """;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page1) },
            // This second response must never be requested — following it would leak the token.
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "value": [] }""") });

        var service = new AzureSigningService(NullLogger<AzureSigningService>.Instance, new HttpClient(handler));
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ListSubscriptionsAsync("token"));

        Assert.Contains("untrusted", ex.Message);
        Assert.AreEqual(1, handler.RequestUris.Count, "The untrusted nextLink must not be fetched");
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_WhenCancelledMidPagination_ThrowsAndStopsRequesting()
    {
        const string page1 = """
        {
            "value": [ { "subscriptionId": "sub-1", "displayName": "Page 1 Sub", "state": "Enabled" } ],
            "nextLink": "https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=page2"
        }
        """;
        using var cts = new CancellationTokenSource();
        var handler = new BlockingSecondPageHandler(page1, cts);

        var service = new AzureSigningService(NullLogger<AzureSigningService>.Instance, new HttpClient(handler));

        var threw = false;
        try
        {
            await service.ListSubscriptionsAsync("token", cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "A cancelled paginated request must surface an OperationCanceledException");
        // The initial page and the (blocked, then cancelled) nextLink request are attempted; no page
        // beyond the cancellation point may be requested — a regression dropping the token would
        // otherwise keep paging.
        Assert.AreEqual(2, handler.RequestUris.Count,
            "Only the first page and the cancelled nextLink request should be attempted");
    }

    [TestMethod]
    public async Task GetSigningAccountAsync_Success_ParsesAccountAndTargetsResourceScopedUrl()
    {
        const string json = """
        {
            "name": "myaccount",
            "location": "eastus",
            "id": "/subscriptions/sub-1/resourceGroups/my-rg/providers/Microsoft.CodeSigning/codeSigningAccounts/myaccount",
            "properties": { "accountUri": "https://eus.codesigning.azure.net" }
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var account = await service.GetSigningAccountAsync("token", "sub-1", "my-rg", "myaccount");

        Assert.IsNotNull(account);
        Assert.AreEqual("myaccount", account.Name);
        Assert.AreEqual("my-rg", account.ResourceGroup);
        Assert.AreEqual("eastus", account.Location);
        Assert.AreEqual("https://eus.codesigning.azure.net", account.AccountUri);

        // Direct GET on the named account — no list segment, so only reader on the resource is needed.
        Assert.Contains("/resourceGroups/my-rg/providers/Microsoft.CodeSigning/codeSigningAccounts/myaccount?api-version=", handler.LastRequestUri!);
        Assert.AreEqual("token", handler.LastRequest!.Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task GetSigningAccountAsync_NotFound_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, """{ "error": { "code": "NotFound", "message": "gone" } }""");

        var service = CreateService(handler);
        var account = await service.GetSigningAccountAsync("token", "sub-1", "my-rg", "missing");

        Assert.IsNull(account);
    }

    [TestMethod]
    public async Task GetSigningAccountAsync_OnErrorStatus_SurfacesParsedAzureError()
    {
        const string errorJson = """
        { "error": { "code": "AuthorizationFailed", "message": "The client does not have authorization." } }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.Forbidden, errorJson);

        var service = CreateService(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetSigningAccountAsync("token", "sub-1", "my-rg", "myaccount"));

        Assert.Contains("AuthorizationFailed", ex.Message);
    }

    [TestMethod]
    public async Task GetCertificateProfileAsync_Success_ParsesProfileAndTargetsProfileScopedUrl()
    {
        const string json = """
        {
            "name": "profile-a",
            "properties": { "profileType": "PublicTrust", "status": "Active" }
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var profile = await service.GetCertificateProfileAsync("token", "sub-1", "my-rg", "myaccount", "profile-a");

        Assert.IsNotNull(profile);
        Assert.AreEqual("profile-a", profile.Name);
        Assert.AreEqual("PublicTrust", profile.ProfileType);
        Assert.AreEqual("Active", profile.Status);

        // Direct GET on the named profile — no certificateProfiles list, so a profile-scoped
        // signer principal that cannot enumerate the collection still validates successfully.
        Assert.Contains("/codeSigningAccounts/myaccount/certificateProfiles/profile-a?api-version=", handler.LastRequestUri!);
    }

    [TestMethod]
    public async Task GetCertificateProfileAsync_NotFound_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, """{ "error": { "code": "NotFound", "message": "gone" } }""");

        var service = CreateService(handler);
        var profile = await service.GetCertificateProfileAsync("token", "sub-1", "my-rg", "myaccount", "missing");

        Assert.IsNull(profile);
    }

    [TestMethod]
    public async Task GetCertificateProfileAsync_OnErrorStatus_SurfacesParsedAzureError()
    {
        const string errorJson = """
        { "error": { "code": "AuthorizationFailed", "message": "The client does not have authorization." } }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.Forbidden, errorJson);

        var service = CreateService(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetCertificateProfileAsync("token", "sub-1", "my-rg", "myaccount", "profile-a"));

        Assert.Contains("AuthorizationFailed", ex.Message);
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        private HttpResponseMessage? _response;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestUri = request.RequestUri?.ToString();
            _response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            };
            return Task.FromResult(_response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _response?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("QueueHttpMessageHandler received more requests than queued responses.");
            }

            return Task.FromResult(_responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.Count > 0)
                {
                    _responses.Dequeue().Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Returns a first page (with a nextLink) on the initial request, then — on the nextLink
    /// request — cancels the shared token and blocks on it so the paginated call is cancelled
    /// mid-flight, exercising cancellation propagation through <c>GetArmResponseAsync</c>.
    /// </summary>
    private sealed class BlockingSecondPageHandler(string firstPageBody, CancellationTokenSource cts) : HttpMessageHandler
    {
        private int _requestCount;

        public List<string> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            _requestCount++;

            if (_requestCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(firstPageBody) };
            }

            // The nextLink request: cancel and then block on the token so the service observes
            // cancellation while awaiting the second page.
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable: the request should have been cancelled.");
        }
    }
}
