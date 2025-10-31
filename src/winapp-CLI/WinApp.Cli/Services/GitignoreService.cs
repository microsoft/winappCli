// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class GitignoreService(ILogger<GitignoreService> logger) : IGitignoreService
{
    /// <summary>
    /// Update .gitignore to exclude .winapp folder
    /// </summary>
    /// <param name="projectDirectory">Directory containing the project</param>
    /// <returns>True if gitignore was updated, false if entry already existed</returns>
    public bool UpdateGitignore(DirectoryInfo projectDirectory)
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

                logger.LogDebug("{UISymbol} Added .winapp to .gitignore", UiSymbols.Check);
                logger.LogDebug("{UISymbol} Note: winapp.yaml should be committed to track SDK versions", UiSymbols.Note);

                return true;
            }
            else
            {
                logger.LogDebug("{UISymbol} .winapp already exists in .gitignore", UiSymbols.Skip);
            }

            logger.LogDebug("{UISymbol} Note: winapp.yaml should be committed to track SDK versions", UiSymbols.Note);

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug("{UISymbol} Could not update .gitignore: {Message}", UiSymbols.Warning, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Add a certificate file to .gitignore
    /// </summary>
    /// <param name="projectDirectory">Directory containing the project</param>
    /// <param name="certificateFileName">Name of the certificate file to add</param>
    /// <returns>True if gitignore was updated, false if entry already existed</returns>
    public bool AddCertificateToGitignore(DirectoryInfo projectDirectory, string certificateFileName)
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

                logger.LogDebug("{UISymbol} Added {CertificateFileName} to .gitignore", UiSymbols.Check, certificateFileName);

                return true;
            }
            else
            {
                logger.LogDebug("{UISymbol} {CertificateFileName} already exists in .gitignore", UiSymbols.Skip, certificateFileName);
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug("{UISymbol} Could not update .gitignore: {Message}", UiSymbols.Warning, ex.Message);
            return false;
        }
    }
}
