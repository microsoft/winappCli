// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzureAuthServiceTests
{
    [TestMethod]
    [DataRow("72f988bf-86f1-41af-91ab-2d7cd011db47")] // GUID tenant
    [DataRow("contoso.onmicrosoft.com")]              // domain tenant
    [DataRow("my-tenant.example.co.uk")]
    public void IsValidTenantId_AcceptsGuidsAndDomains(string tenantId)
    {
        Assert.IsTrue(AzureAuthService.IsValidTenantId(tenantId));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not a tenant")]                       // whitespace
    [DataRow("tenant && rm -rf /")]                 // shell metacharacters
    [DataRow("--query \"[].id\"")]                  // extra CLI argument injection
    [DataRow("tenant\"; calc.exe; \"")]
    [DataRow("tenant`whoami`")]
    [DataRow("contoso.onmicrosoft.com --debug")]
    public void IsValidTenantId_RejectsInvalidOrUnsafeInput(string tenantId)
    {
        Assert.IsFalse(AzureAuthService.IsValidTenantId(tenantId));
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_RejectsFileOutsideInstallRoots()
    {
        var installRoot = Directory.CreateTempSubdirectory("winapp-aztrust-root").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("winapp-aztrust-cwd").FullName;
        try
        {
            var azPath = Path.Join(elsewhere, "az.cmd");
            File.WriteAllText(azPath, "@echo off");

            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath(azPath, [installRoot]));
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_RejectsRepoControlledSiblingOutsideWorkingSubtree()
    {
        // A repo-controlled az.cmd (e.g. node_modules\.bin) is outside the working subtree but is
        // still attacker-controlled, so it must not be trusted just for being "outside cwd".
        var installRoot = Directory.CreateTempSubdirectory("winapp-aztrust-root2").FullName;
        var repo = Directory.CreateTempSubdirectory("winapp-aztrust-repo").FullName;
        try
        {
            var subDir = Path.Join(repo, "node_modules", ".bin");
            Directory.CreateDirectory(subDir);
            var azPath = Path.Join(subDir, "az.cmd");
            File.WriteAllText(azPath, "@echo off");

            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath(azPath, [installRoot]));
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_AcceptsFileUnderKnownInstallRoot()
    {
        var installRoot = Directory.CreateTempSubdirectory("winapp-aztrust-install").FullName;
        try
        {
            var wbin = Path.Join(installRoot, "wbin");
            Directory.CreateDirectory(wbin);
            var azPath = Path.Join(wbin, "az.cmd");
            File.WriteAllText(azPath, "@echo off");

            Assert.IsTrue(AzureAuthService.IsTrustedAzureCliPath(azPath, [installRoot]));
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
        }
    }

    [TestMethod]
    public void IsTrustedAzureCliPath_RejectsNonExistentOrRelativePath()
    {
        var installRoot = Directory.CreateTempSubdirectory("winapp-aztrust-none").FullName;
        try
        {
            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath(Path.Join(installRoot, "missing.cmd"), [installRoot]));
            Assert.IsFalse(AzureAuthService.IsTrustedAzureCliPath("az.cmd", [installRoot]));
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
        }
    }

    [TestMethod]
    public void SelectFirstTrustedAzureCliPath_SkipsUntrustedHitAndReturnsLaterTrustedPath()
    {
        var elsewhere = Directory.CreateTempSubdirectory("winapp-azwhere-cwd").FullName;
        var install = Directory.CreateTempSubdirectory("winapp-azwhere-install").FullName;
        try
        {
            // where.exe lists a hijacked copy outside any install root first, then a legitimate one.
            var hijack = Path.Join(elsewhere, "az.cmd");
            File.WriteAllText(hijack, "@echo off");
            var trusted = Path.Join(install, "az.cmd");
            File.WriteAllText(trusted, "@echo off");

            var whereOutput = $"{hijack}\r\n{trusted}\r\n";

            var result = AzureAuthService.SelectFirstTrustedAzureCliPath(whereOutput, [install]);

            Assert.AreEqual(trusted, result, "The untrusted hit must be skipped in favor of the trusted install path");
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
            Directory.Delete(install, recursive: true);
        }
    }

    [TestMethod]
    public void SelectFirstTrustedAzureCliPath_WhenAllCandidatesUntrusted_ReturnsNull()
    {
        var installRoot = Directory.CreateTempSubdirectory("winapp-azwhere-root").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("winapp-azwhere-none").FullName;
        try
        {
            var hijack = Path.Join(elsewhere, "az.cmd");
            File.WriteAllText(hijack, "@echo off");
            var subHijack = Path.Join(elsewhere, "sub");
            Directory.CreateDirectory(subHijack);
            var subHijackCmd = Path.Join(subHijack, "az.cmd");
            File.WriteAllText(subHijackCmd, "@echo off");

            var whereOutput = $"{hijack}\n{subHijackCmd}\n";

            Assert.IsNull(AzureAuthService.SelectFirstTrustedAzureCliPath(whereOutput, [installRoot]));
            Assert.IsNull(AzureAuthService.SelectFirstTrustedAzureCliPath(string.Empty, [installRoot]));
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    private const string ArmScope = "https://management.azure.com/.default";

    [TestMethod]
    public async Task GetAccessTokenAsync_WhenPrimaryCredentialSucceeds_ReturnsToken()
    {
        using var service = new TestableAzureAuthService
        {
            PrimaryCredential = new StubCredential("primary-token"),
        };

        var token = await service.GetAccessTokenAsync(ArmScope);

        Assert.AreEqual("primary-token", token);
        Assert.AreEqual(0, service.RunAzLoginCallCount, "Should not fall back to az login when the primary credential works");
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_NonInteractiveAndCredentialFails_ThrowsCiGuidance()
    {
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = false,
            PrimaryCredential = new ThrowingCredential(),
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        Assert.Contains("AZURE_CLIENT_SECRET", ex.Message);
        Assert.AreEqual(0, service.RunAzLoginCallCount, "Non-interactive sessions must never launch az login");
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InteractiveButNoAzureCli_ThrowsInstallGuidance()
    {
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = null,
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        Assert.Contains("Install the Azure CLI", ex.Message);
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_ExistingCliSession_ReturnsTokenWithoutLogin()
    {
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            HasExistingSession = true,
            SeedTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        };

        var token = await service.GetAccessTokenAsync(ArmScope);

        Assert.AreEqual("cli-token", token);
        Assert.AreEqual(0, service.RunAzLoginCallCount, "A valid cached session must not trigger an interactive login");

        // The token must be minted through the validated absolute az path, never an ambient lookup.
        var tokenRequest = service.ProcessRunner.Requests.Single();
        Assert.AreEqual(@"C:\fake\az.cmd", tokenRequest.FileName);
        string[] expectedTokenArgs = ["account", "get-access-token", "--scope", ArmScope, "--output", "json", "--tenant", "72f988bf-86f1-41af-91ab-2d7cd011db47"];
        Assert.AreSequenceEqual(expectedTokenArgs, tokenRequest.Arguments.ToArray());
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_NonInteractiveExistingCliSession_ReturnsTokenWithoutLogin()
    {
        // Regression: DefaultAzureCredential excludes AzureCliCredential, so a CI job authenticated via
        // 'az login' (e.g. the azure/login GitHub Action) is only reachable through the explicit CLI
        // token path. That path must run even when non-interactive, rather than throwing CI guidance.
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = false,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            HasExistingSession = true,
            SeedTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        };

        var token = await service.GetAccessTokenAsync(ArmScope);

        Assert.AreEqual("cli-token", token);
        Assert.AreEqual(0, service.RunAzLoginCallCount, "Non-interactive sessions must never launch az login");

        // The token must still be minted through the validated absolute az path, never an ambient lookup.
        var tokenRequest = service.ProcessRunner.Requests.Single();
        Assert.AreEqual(@"C:\fake\az.cmd", tokenRequest.FileName);
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_NonInteractiveCliInstalledButNoSession_ThrowsCiGuidanceWithoutLogin()
    {
        // The CLI is present but has no cached session; a non-interactive environment cannot launch an
        // interactive login, so it must surface CI guidance rather than prompting.
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = false,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            HasExistingSession = false,
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        Assert.Contains("AZURE_CLIENT_SECRET", ex.Message);
        Assert.AreEqual(0, service.RunAzLoginCallCount, "Non-interactive sessions must never launch az login");
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InteractiveLoginSucceeds_RetriesViaValidatedAzPath()
    {
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            LoginResult = true,
            SeedTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        };

        var token = await service.GetAccessTokenAsync(ArmScope);

        Assert.AreEqual("cli-token", token);
        Assert.AreEqual(1, service.RunAzLoginCallCount);
        Assert.AreEqual("72f988bf-86f1-41af-91ab-2d7cd011db47", service.LastLoginTenantId);

        // The login itself must run against the validated absolute path with a list-form argument
        // for the (validated) tenant — never a concatenated command string.
        var loginRequest = service.ProcessRunner.Requests.Single(r => r.Arguments.Count > 0 && r.Arguments[0] == "login");
        Assert.AreEqual(@"C:\fake\az.cmd", loginRequest.FileName);
        string[] expectedLoginArgs = ["login", "--tenant", "72f988bf-86f1-41af-91ab-2d7cd011db47"];
        Assert.AreSequenceEqual(expectedLoginArgs, loginRequest.Arguments.ToArray());
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InteractiveLoginFails_Throws()
    {
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            LoginResult = false,
            SeedTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        Assert.Contains("Azure CLI login failed", ex.Message);
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InteractiveLoginSucceedsButRetryTokenFails_Throws()
    {
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            LoginResult = true,
            TokenAlwaysFails = true,
            SeedTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        Assert.Contains("retrieving an access token failed", ex.Message);
        Assert.AreEqual(1, service.RunAzLoginCallCount);
    }

    [TestMethod]
    public async Task GetAccessTokenAsync_InvalidPresetTenantId_Throws()
    {
        using var service = new TestableAzureAuthService
        {
            InteractiveOverride = true,
            PrimaryCredential = new ThrowingCredential(),
            AzCliPath = @"C:\fake\az.cmd",
            LoginResult = true,
            SeedTenantId = "not a valid tenant",
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ArmScope));

        Assert.Contains("Invalid AZURE_TENANT_ID", ex.Message);
        Assert.AreEqual(0, service.RunAzLoginCallCount, "Login must not run with an invalid tenant id");
        Assert.AreEqual(0, service.ProcessRunner.Requests.Count, "An invalid tenant must never reach the Azure CLI");
    }

    private sealed class TestableAzureAuthService : AzureAuthService, IDisposable
    {
        private readonly TestConsole _console;

        public TestableAzureAuthService() : this(new TestConsole(), new FakeProcessRunner()) { }

        private TestableAzureAuthService(TestConsole console, FakeProcessRunner processRunner)
            : base(NullLogger<AzureAuthService>.Instance, console, processRunner)
        {
            _console = console;
            ProcessRunner = processRunner;
        }

        public FakeProcessRunner ProcessRunner { get; }

        public void Dispose()
        {
            _console.Dispose();
        }

        public bool? InteractiveOverride { get; init; }
        public TokenCredential PrimaryCredential { get; init; } = new ThrowingCredential();
        public string? AzCliPath { get; init; }

        // Convenience wrappers so tests read/configure through the injected fake runner.
        public bool HasExistingSession { init => ProcessRunner.HasSession = value; }
        public bool LoginResult { init => ProcessRunner.LoginSucceeds = value; }
        public bool TokenAlwaysFails { init => ProcessRunner.TokenAlwaysFails = value; }
        public int RunAzLoginCallCount => ProcessRunner.LoginCallCount;
        public string? LastLoginTenantId => ProcessRunner.LastLoginTenantId;

        // Pre-seed the tenant so the interactive paths don't block on a prompt.
        public string? SeedTenantId { init => TenantId = value; }

        public override bool IsInteractive => InteractiveOverride ?? base.IsInteractive;

        protected override TokenCredential CreateCredential() => PrimaryCredential;

        protected override string? FindAzureCli() => AzCliPath;
    }

    /// <summary>
    /// A fake <see cref="IProcessRunner"/> that emulates the Azure CLI: it services the login and
    /// <c>account get-access-token</c> invocations the real service constructs, so the argument
    /// building, tenant forwarding, and token parsing run for real without launching a process.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public bool HasSession { get; set; }
        public bool LoginSucceeds { get; set; } = true;
        public bool TokenAlwaysFails { get; set; }
        public string TokenValue { get; set; } = "cli-token";
        public int LoginCallCount { get; private set; }
        public string? LastLoginTenantId { get; private set; }
        public List<ProcessRunRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var args = request.Arguments;

            if (args.Count > 0 && args[0] == "login")
            {
                LoginCallCount++;
                LastLoginTenantId = ArgValue(args, "--tenant");
                if (LoginSucceeds)
                {
                    HasSession = true;
                }

                return Task.FromResult(new ProcessRunResult(LoginSucceeds ? 0 : 1, string.Empty, LoginSucceeds ? string.Empty : "login failed"));
            }

            // account get-access-token
            if (TokenAlwaysFails || !HasSession)
            {
                return Task.FromResult(new ProcessRunResult(1, string.Empty, "Please run 'az login'."));
            }

            var json = $$"""{"accessToken":"{{TokenValue}}","expiresOn":"2999-01-01 00:00:00.000000","tokenType":"Bearer"}""";
            return Task.FromResult(new ProcessRunResult(0, json, string.Empty));
        }

        private static string? ArgValue(IReadOnlyList<string> args, string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }

    private sealed class StubCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class ThrowingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new AuthenticationFailedException("no credentials");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new AuthenticationFailedException("no credentials");
    }
}
