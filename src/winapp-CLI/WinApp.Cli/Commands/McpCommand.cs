// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal class McpCommand : Command
{
    public McpCommand() : base("mcp", "Host an MCP (Model Context Protocol) server for AI agent integration")
    {
    }

    public class Handler(WinAppRootCommand rootCommand) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var builder = Host.CreateApplicationBuilder();

            // Store the root command in services for the MCP tools to access
            builder.Services.AddSingleton(rootCommand);

            builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "winapp",
                Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
            })
            .WithStdioServerTransport()
            .WithTools<McpTools>();

            // Suppress console logging to avoid interfering with MCP stdio transport
            builder.Logging.ClearProviders();

            var app = builder.Build();

            await app.RunAsync(cancellationToken);
            return 0;
        }
    }

    [McpServerToolType]
    internal class McpTools(WinAppRootCommand rootCommand)
    {
        [McpServerTool(Name = "get_cli_schema"), Description("Get the CLI schema describing all available winapp commands, their arguments, and options. " +
            "The WinAppCLI is a tool designed to help developers set up Windows SDK and Windows App SDK for use in their apps, create MSIX packages, generate " +
            "app manifests and certificates, and run build tools." +
            "Use this schema to understand what commands are available and how to invoke them via shell commands.")]
        public string GetCliSchema()
        {
            var parseResult = rootCommand.Parse([]);

            using var textWriter = new StringWriter();

            CliSchema.PrintCliSchema(parseResult.CommandResult, textWriter);

            return textWriter.ToString();
        }
    }
}
