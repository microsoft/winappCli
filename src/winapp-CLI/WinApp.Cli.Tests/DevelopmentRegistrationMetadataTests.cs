// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevelopmentRegistrationMetadataTests
{
    [TestMethod]
    [DataRow("schema")]
    [DataRow("algorithm")]
    [DataRow("owner")]
    [DataRow("relative-owner")]
    [DataRow("missing-schema")]
    public void UnknownOrInconsistentMetadataIsRejected(string mismatch)
    {
        var root = Directory.CreateTempSubdirectory("winapp-receipt-");
        try
        {
            var layout = root.CreateSubdirectory("AppX");
            var owner = Path.Combine(root.FullName, "owner.csproj");
            var other = Path.Combine(root.FullName, "other.csproj");
            File.WriteAllText(owner, "<Project />");
            File.WriteAllText(other, "<Project />");
            var document = AppxManifestDocument.Parse(RunCommandTests.TestManifestContent);
            document.IdentityProcessorArchitecture = "x64";
            var identity = DevelopmentIdentityHelper.Create(document, owner, layout.FullName, uniqueIdentity: true);
            document.ApplyDevelopmentIdentity(identity);
            document.Save(Path.Combine(layout.FullName, "appxmanifest.xml"));
            var receipt = new DevelopmentRegistration
            {
                Identity = identity with { PackageFullName = DevelopmentIdentityHelper.ComputeFullName(document), Revision = 1 },
                ManifestHash = DevelopmentRegistrationStore.HashManifest(layout),
            };
            DevelopmentRegistrationStore.Commit(root, layout, receipt);
            var observed = DevelopmentRegistrationStore.Read(layout)!;
            Assert.IsNotNull(observed.RegisteredAtUtc);
            Assert.AreNotEqual(default, observed.UpdatedAtUtc);

            var changed = mismatch switch
            {
                "schema" => observed with { SchemaVersion = 99 },
                "algorithm" => observed with { AlgorithmVersion = "winapp-unique-future" },
                "owner" => observed with { Identity = observed.Identity with { OwnerPath = DevelopmentIdentityHelper.CanonicalizePath(other) } },
                "relative-owner" => observed with { Identity = observed.Identity with { OwnerPath = "owner.csproj" } },
                _ => observed,
            };
            var json = JsonSerializer.Serialize(changed, DevelopmentRegistrationJsonContext.Default.DevelopmentRegistration);
            if (mismatch == "missing-schema")
            {
                var node = JsonNode.Parse(json)!.AsObject();
                node.Remove("SchemaVersion");
                json = node.ToJsonString();
            }
            File.WriteAllText(DevelopmentRegistrationStore.ReceiptPath(layout), json);

            Assert.Throws<InvalidOperationException>(() => DevelopmentRegistrationStore.Read(layout));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
