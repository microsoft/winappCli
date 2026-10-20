using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;

namespace ScanDesk;

public sealed class ReceiptOcrService
{
    private TextRecognizer? _textRecognizer;

    public async Task EnsureReadyAsync()
    {
        if (TextRecognizer.GetReadyState() == AIFeatureReadyState.NotReady)
        {
            var loadResult = await TextRecognizer.EnsureReadyAsync();
            if (loadResult.Status == AIFeatureReadyResultState.Failure)
            {
                throw new InvalidOperationException(loadResult.Error.Message);
            }
        }

        _textRecognizer = await TextRecognizer.CreateAsync();
    }
}
