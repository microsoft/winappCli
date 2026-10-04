// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevToolsTrustPolicyTests
{
    [TestMethod]
    public void ApplyMutationFloor_DenyCapsMutationToUi()
    {
        Assert.AreEqual(
            DevToolsAccess.Ui,
            DevToolsService.ApplyMutationFloor(DevToolsAccess.Mutation, "deny"));
        Assert.AreEqual(
            DevToolsAccess.Ui,
            DevToolsService.ApplyMutationFloor(DevToolsAccess.Mutation, "DENY"));
    }

    [TestMethod]
    public void BuildInitializationData_DenyEncodesUiPosture()
    {
        using var document = JsonDocument.Parse(
            DevToolsService.BuildInitializationData(DevToolsAccess.Mutation, "deny"));

        Assert.AreEqual(
            "ui",
            document.RootElement.GetProperty("posture").GetString());
    }

    [TestMethod]
    public void ApplyMutationFloor_OtherRequestsAreUnchanged()
    {
        Assert.AreEqual(
            DevToolsAccess.Read,
            DevToolsService.ApplyMutationFloor(DevToolsAccess.Read, "deny"));
        Assert.AreEqual(
            DevToolsAccess.Ui,
            DevToolsService.ApplyMutationFloor(DevToolsAccess.Ui, "deny"));
        Assert.AreEqual(
            DevToolsAccess.Mutation,
            DevToolsService.ApplyMutationFloor(DevToolsAccess.Mutation, null));
        Assert.AreEqual(
            DevToolsAccess.Mutation,
            DevToolsService.ApplyMutationFloor(DevToolsAccess.Mutation, "allow"));
    }
}
