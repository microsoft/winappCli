using Microsoft.Windows.AI.Text;

namespace Mossgrove.Notes;

internal static class Summarizer
{
    public static async Task<string> SummarizeAsync(string text)
    {
        if (LanguageModel.GetReadyState() is not AIFeatureReadyState.Ready) await LanguageModel.EnsureReadyAsync();
        using var model = await LanguageModel.CreateAsync();
        return (await model.GenerateResponseAsync($"Summarize: {text}")).Text;
    }
}
