// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class GitignoreService : IGitignoreService
{
    /// <summary>
    /// Update .gitignore to exclude .winapp folder
    /// </summary>
    /// <param name="projectDirectory">Directory containing the project</param>
    /// <returns>True if gitignore was updated, false if entry already existed</returns>
    public async Task<bool> UpdateGitignoreAsync(DirectoryInfo projectDirectory, TaskContext taskContext)
    {
        var result = await taskContext.AddSubTaskAsync("Updating .gitignore", async (subTaskContext) =>
        {
            try
            {
                var gitignorePath = Path.Combine(projectDirectory.FullName, ".gitignore");
                var gitignoreContent = "";
                var gitignoreExists = File.Exists(gitignorePath);

                // Read existing .gitignore if it exists
                if (gitignoreExists)
                {
                    gitignoreContent = File.ReadAllText(gitignorePath);
                }

                // Check if .winapp is already in .gitignore
                var winappEntry = ".winapp";
                var lines = gitignoreContent.Split('\n');
                var hasWinappEntry = lines.Any(line => line.Trim() == winappEntry.Trim());

                if (!hasWinappEntry)
                {
                    // Add entries to .gitignore
                    var newContent = gitignoreContent;

                    // Ensure we have a newline before our section if file exists and doesn't end with newline
                    if (gitignoreExists && !gitignoreContent.EndsWith('\n') && !string.IsNullOrEmpty(gitignoreContent))
                    {
                        newContent += '\n';
                    }

                    // Add our section
                    newContent += '\n';
                    newContent += "# Windows SDK packages and generated files\n";
                    newContent += winappEntry + '\n';

                    File.WriteAllText(gitignorePath, newContent);

                    taskContext.AddDebugMessage($"{UiSymbols.Check} Added .winapp to .gitignore");
                    taskContext.AddDebugMessage($"{UiSymbols.Note} Note: winapp.yaml should be committed to track SDK versions");

                    return (true, "Added .winapp to .gitignore");
                }
                else
                {
                    taskContext.AddDebugMessage($"{UiSymbols.Skip} .winapp already exists in .gitignore");
                }

                taskContext.AddDebugMessage($"{UiSymbols.Note} Note: winapp.yaml should be committed to track SDK versions");

                return (false, ".winapp already exists in .gitignore");
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not update .gitignore: {ex.Message}");
                return (false, "Failed to update .gitignore");
            }
        });
        return result.Item1;
    }

    /// <summary>
    /// Add a certificate file to .gitignore
    /// </summary>
    /// <param name="projectDirectory">Directory containing the project</param>
    /// <param name="certificateFileName">Name of the certificate file to add</param>
    /// <returns>True if gitignore was updated, false if entry already existed</returns>
    public bool AddCertificateToGitignore(DirectoryInfo projectDirectory, string certificateFileName, TaskContext taskContext)
    {
        try
        {
            var gitignorePath = Path.Combine(projectDirectory.FullName, ".gitignore");
            var gitignoreContent = "";
            var gitignoreExists = File.Exists(gitignorePath);

            // Read existing .gitignore if it exists
            if (gitignoreExists)
            {
                gitignoreContent = File.ReadAllText(gitignorePath);
            }

            // Check if certificate file is already in .gitignore
            var lines = gitignoreContent.Split('\n');
            var hasCertEntry = lines.Any(line => line.Trim() == certificateFileName);

            if (!hasCertEntry)
            {
                // Add certificate entry to .gitignore
                var newContent = gitignoreContent;

                // Ensure we have a newline before our entry if file exists and doesn't end with newline
                if (gitignoreExists && !gitignoreContent.EndsWith('\n') && !string.IsNullOrEmpty(gitignoreContent))
                {
                    newContent += '\n';
                }

                // Add certificate entry with comment
                newContent += '\n';
                newContent += "# Development certificate\n";
                newContent += certificateFileName + '\n';

                File.WriteAllText(gitignorePath, newContent);

                taskContext.AddDebugMessage($"{UiSymbols.Check} Added {certificateFileName} to .gitignore");

                return true;
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Skip} {certificateFileName} already exists in .gitignore");
            }

            return false;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not update .gitignore: {ex.Message}");
            return false;
        }
    }
}
