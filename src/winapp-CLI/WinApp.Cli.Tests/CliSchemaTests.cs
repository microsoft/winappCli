// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
public class CliSchemaTests : BaseCommandTests
{
    [TestMethod]
    public async Task CliSchemaShouldOutputValidJson()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");
        Assert.IsFalse(string.IsNullOrWhiteSpace((string?)TestAnsiConsole.Output), "Output should not be empty");

        // Verify it's valid JSON
        JsonDocument? jsonDoc = null;
        try
        {
            jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
            Assert.IsNotNull(jsonDoc, "Output should be valid JSON");
        }
        catch (JsonException ex)
        {
            Assert.Fail($"Output is not valid JSON: {ex.Message}");
        }
        finally
        {
            jsonDoc?.Dispose();
        }
    }

    [TestMethod]
    public async Task CliSchemaShouldContainRootCommandDetails()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");

        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;

        // Verify root properties exist
        Assert.IsTrue(root.TryGetProperty("name", out _), "Schema should contain 'name' property");
        Assert.IsTrue(root.TryGetProperty("version", out _), "Schema should contain 'version' property");
        Assert.IsTrue(root.TryGetProperty("description", out _), "Schema should contain 'description' property");
    }

    [TestMethod]
    public async Task CliSchemaShouldContainSubcommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");

        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;

        // Verify subcommands exist
        Assert.IsTrue(root.TryGetProperty("subcommands", out var subcommands), "Schema should contain 'subcommands' property");
        Assert.AreEqual(JsonValueKind.Object, subcommands.ValueKind, "Subcommands should be an object");

        // Verify some known commands exist
        var expectedCommands = new[] { "init", "restore", "package", "manifest", "cert", "sign" };
        foreach (var commandName in expectedCommands)
        {
            Assert.IsTrue(subcommands.TryGetProperty(commandName, out _), 
                $"Schema should contain '{commandName}' subcommand");
        }
    }

    [TestMethod]
    public async Task CliSchemaShouldContainOptionsForCommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");

        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;

        // Navigate to a specific command with options (e.g., package command)
        Assert.IsTrue(root.TryGetProperty("subcommands", out var subcommands), "Schema should contain subcommands");
        Assert.IsTrue(subcommands.TryGetProperty("package", out var packageCommand), "Schema should contain package command");
        Assert.IsTrue(packageCommand.TryGetProperty("options", out var options), "Package command should have options");
        Assert.AreEqual(JsonValueKind.Object, options.ValueKind, "Options should be an object");

        // Verify some known options exist
        Assert.IsTrue(options.TryGetProperty("--output", out _), "Package command should have 'output' option");
        Assert.IsTrue(options.TryGetProperty("--name", out _), "Package command should have 'name' option");
    }

    [TestMethod]
    public async Task CliSchemaShouldContainArgumentsForCommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");

        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;

        // Navigate to a specific command with arguments (e.g., package command)
        Assert.IsTrue(root.TryGetProperty("subcommands", out var subcommands), "Schema should contain subcommands");
        Assert.IsTrue(subcommands.TryGetProperty("package", out var packageCommand), "Schema should contain package command");
        Assert.IsTrue(packageCommand.TryGetProperty("arguments", out var arguments), "Package command should have arguments");
        Assert.AreEqual(JsonValueKind.Object, arguments.ValueKind, "Arguments should be an object");

        // Verify the input-folder argument exists
        Assert.IsTrue(arguments.TryGetProperty("input-folder", out var inputFolder), 
            "Package command should have 'input-folder' argument");
        
        // Verify argument has expected properties
        Assert.IsTrue(inputFolder.TryGetProperty("order", out _), "Argument should have 'order' property");
        Assert.IsTrue(inputFolder.TryGetProperty("valueType", out _), "Argument should have 'valueType' property");
    }

    [TestMethod]
    public async Task CliSchemaOptionDetailsContainRequiredFields()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");

        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;

        // Get a specific option to verify its structure
        Assert.IsTrue(root.TryGetProperty("subcommands", out var subcommands), "Schema should contain subcommands");
        Assert.IsTrue(subcommands.TryGetProperty("package", out var packageCommand), "Schema should contain package command");
        Assert.IsTrue(packageCommand.TryGetProperty("options", out var options), "Package command should have options");
        Assert.IsTrue(options.TryGetProperty("--output", out var outputOption), "Should have output option");

        // Verify option has required fields
        Assert.IsTrue(outputOption.TryGetProperty("description", out _), "Option should have 'description' property");
        Assert.IsTrue(outputOption.TryGetProperty("hidden", out _), "Option should have 'hidden' property");
        Assert.IsTrue(outputOption.TryGetProperty("valueType", out _), "Option should have 'valueType' property");
        Assert.IsTrue(outputOption.TryGetProperty("arity", out var arity), "Option should have 'arity' property");
        
        // Verify arity structure
        Assert.AreEqual(JsonValueKind.Object, arity.ValueKind, "Arity should be an object");
        Assert.IsTrue(arity.TryGetProperty("minimum", out _), "Arity should have 'minimum' property");
    }

    [TestMethod]
    public async Task CliSchemaShouldHandleNestedSubcommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");

        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;

        // Navigate to manifest command which has subcommands
        Assert.IsTrue(root.TryGetProperty("subcommands", out var subcommands), "Schema should contain subcommands");
        Assert.IsTrue(subcommands.TryGetProperty("manifest", out var manifestCommand), "Schema should contain manifest command");
        Assert.IsTrue(manifestCommand.TryGetProperty("subcommands", out var manifestSubcommands), 
            "Manifest command should have subcommands");
        
        // Verify the generate subcommand exists
        Assert.IsTrue(manifestSubcommands.TryGetProperty("generate", out var generateCommand), 
            "Manifest should have 'generate' subcommand");
        
        // Verify generate command has its own options
        Assert.IsTrue(generateCommand.TryGetProperty("options", out var generateOptions), 
            "Generate subcommand should have options");
        Assert.IsTrue(generateOptions.TryGetProperty("--package-name", out _), 
            "Generate should have 'package-name' option");
    }

    [TestMethod]
    public async Task CliSchemaWithSubcommandShouldStillOutputSchema()
    {
        // Arrange - Test that --cli-schema works even when used with a subcommand
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "cert", "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");
        Assert.IsFalse(string.IsNullOrWhiteSpace((string?)TestAnsiConsole.Output), "Output should not be empty");

        // Verify it's valid JSON
        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.IsNotNull(jsonDoc, "Output should be valid JSON");
        
        // The schema should still show the entire CLI structure, not just the subcommand
        var root = jsonDoc.RootElement;
        Assert.IsTrue(root.TryGetProperty("subcommands", out _), "Schema should contain all subcommands");
    }

    [TestMethod]
    public async Task CliSchemaSerializationUsesSourceGenerator()
    {
        // This test verifies that the JSON source generator is being used
        // by ensuring the serialization is successful with the custom context
        
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "CLI schema command should complete successfully");
        
        // Verify the output is properly formatted JSON with correct line endings
        Assert.Contains("{\n", TestAnsiConsole.Output, "JSON should use \\n line endings (from source generator options)");
        Assert.DoesNotContain("\r\n", TestAnsiConsole.Output, "JSON should not use \\r\\n line endings");
        Assert.IsGreaterThan(100, TestAnsiConsole.Output.Length, "Indented JSON should be multi-line and reasonably long");
    }
}
