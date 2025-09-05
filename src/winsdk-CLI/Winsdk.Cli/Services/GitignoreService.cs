using System;
using System.IO;
using System.Linq;

namespace Winsdk.Cli.Services;

internal static class GitignoreService
{
    /// <summary>
    /// Update .gitignore to exclude .winsdk folder
    /// </summary>
    /// <param name="projectDirectory">Directory containing the project</param>
    /// <param name="verbose">Whether to log progress messages</param>
    /// <returns>True if gitignore was updated, false if entry already existed</returns>
    public static bool UpdateGitignore(string projectDirectory, bool verbose = true)
    {
        try
        {
            var gitignorePath = Path.Combine(projectDirectory, ".gitignore");
            var gitignoreContent = "";
            var gitignoreExists = File.Exists(gitignorePath);

            // Read existing .gitignore if it exists
            if (gitignoreExists)
            {
                gitignoreContent = File.ReadAllText(gitignorePath);
            }

            // Check if .winsdk is already in .gitignore
            var winsdkEntry = ".winsdk/";
            var lines = gitignoreContent.Split('\n');
            var hasWinsdkEntry = lines.Any(line => line.Trim() == winsdkEntry.Trim());

            if (!hasWinsdkEntry)
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
                newContent += winsdkEntry + '\n';

                File.WriteAllText(gitignorePath, newContent);

                if (verbose)
                {
                    Console.WriteLine($"{UiSymbols.Check} Added .winsdk/ to .gitignore");
                    Console.WriteLine($"{UiSymbols.Note} Note: winsdk.yaml should be committed to track SDK versions");
                }

                return true;
            }
            else if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Skip} .winsdk/ already exists in .gitignore");
            }

            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Note} Note: winsdk.yaml should be committed to track SDK versions");
            }

            return false;
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"⚠️  Could not update .gitignore: {ex.Message}");
            }
            return false;
        }
    }
}
