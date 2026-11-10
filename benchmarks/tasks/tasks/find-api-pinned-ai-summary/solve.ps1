# Reference solution: Microsoft.WindowsAppSDK 1.7.260224002 has no LanguageModel.GenerateResponseAsync,
# so summarize through TextSummarizer, which that version does have.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
@'
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;

namespace NoteKeeper;

public sealed record Note(string Title, string Body);

// Text shown for a note in the note list.
public static class NoteSummary
{
    public static string Preview(Note note) =>
        note.Body.Length <= 80 ? note.Body : note.Body[..80] + "...";

    // A short summary of the note's body from the on-device language model.
    public static async Task<string> SummarizeAsync(Note note)
    {
        if (LanguageModel.GetReadyState() == AIFeatureReadyState.NotReady)
        {
            await LanguageModel.EnsureReadyAsync();
        }
        using var model = await LanguageModel.CreateAsync();
        var result = await new TextSummarizer(model).SummarizeParagraphAsync(note.Body);
        return result.Text;
    }
}
'@ | Set-Content (Join-Path $Workspace 'NoteSummary.cs') -Encoding utf8NoBOM
dotnet build -nologo 2>&1 | Write-Host
