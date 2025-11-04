using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Linq;

namespace winapp_GUI.Services;

public class CliService
{
    public string GetCliExecutable()
    {
        // Assume CLI is in user's PATH
        return "winapp.exe";
    }

    // Standardized CLI runner for progress cards
    public async Task<int> RunCliStandardizedAsync(string title, string arguments, string workingDir, ObservableCollection<ProgressCard>? progressCards, ProgressCard? currentCard, bool runAsAdmin = false)
    {
        if (progressCards == null)
        {
            progressCards = new ObservableCollection<ProgressCard>();
        }
        if (!arguments.Contains("--verbose"))
        {
            arguments += " --verbose";
        }
        string cliPath = GetCliExecutable();
        if (currentCard != null)
        {
            currentCard.Title = title;
        }
        if (currentCard == null || currentCard.Title != title)
        {
            ProgressCard.Start(title, progressCards);
        }
        ProgressCard card = progressCards.Count > 0 ? progressCards[progressCards.Count - 1] : ProgressCard.Start(title, progressCards);
        card.AddSubItem($"Running CLI: {arguments}");
        int exitCode = -1;
        string output = "";
        string error = "";
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            EnvironmentVariables =
            {
                ["WINAPP_CLI_CALLER"] = "winapp-GUI"
            }
        };
        if (runAsAdmin)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }
        else
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
        }
        try
        {
            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new InvalidOperationException($"Failed to start CLI: {cliPath}");
                if (!runAsAdmin)
                {
                    output = await process.StandardOutput.ReadToEndAsync();
                    error = await process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                }
                else
                {
                    process.WaitForExit();
                }
                exitCode = process.ExitCode;
            }
            card.AddSubItem($"Exit code: {exitCode}");
            if (!string.IsNullOrWhiteSpace(output))
                card.AddSubItem(output);
            if (!string.IsNullOrWhiteSpace(error))
                card.AddSubItem($"Error: {error}");
        }
        catch (Exception ex)
        {
            card.AddSubItem($"Error: {ex.Message}");
            card.MarkFailure();
            return -1;
        }
        if (exitCode == 0)
        {
            card.MarkSuccess();
        }
        else
        {
            card.MarkFailure();
        }
        return exitCode;
    }
}
