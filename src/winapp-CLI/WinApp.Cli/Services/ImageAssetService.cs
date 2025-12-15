// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using SkiaSharp;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class ImageAssetService(ILogger<ImageAssetService> logger) : IImageAssetService
{
    // Define the required asset specifications for MSIX packages
    private static readonly (string FileName, int Width, int Height)[] AssetSpecifications =
    [
        ("Square44x44Logo.png", 44, 44),
        ("Square44x44Logo.scale-200.png", 88, 88),
        ("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24),
        ("Square150x150Logo.png", 150, 150),
        ("Square150x150Logo.scale-200.png", 300, 300),
        ("Wide310x150Logo.png", 310, 150),
        ("Wide310x150Logo.scale-200.png", 620, 300),
        ("SplashScreen.png", 620, 300),
        ("SplashScreen.scale-200.png", 1240, 600),
        ("StoreLogo.png", 50, 50),
        ("LockScreenLogo.png", 24, 24),
        ("LockScreenLogo.scale-200.png", 48, 48),
    ];

    public async Task GenerateAssetsAsync(FileInfo sourceImagePath, DirectoryInfo outputDirectory, CancellationToken cancellationToken = default)
    {
        if (!sourceImagePath.Exists)
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath.FullName}");
        }

        logger.LogInformation("{UISymbol} Generating MSIX image assets from: {SourceImage}", UiSymbols.Info, sourceImagePath.Name);

        // Load the source image
        using var sourceImage = SKBitmap.Decode(sourceImagePath.FullName);
        if (sourceImage == null)
        {
            throw new InvalidOperationException($"Failed to decode image: {sourceImagePath.FullName}. Please ensure the file is a valid image format.");
        }

        logger.LogDebug("Source image size: {Width}x{Height}", sourceImage.Width, sourceImage.Height);

        // Ensure output directory exists
        if (!outputDirectory.Exists)
        {
            outputDirectory.Create();
        }

        // Generate each required asset
        var successCount = 0;
        foreach (var (fileName, width, height) in AssetSpecifications)
        {
            try
            {
                var outputPath = Path.Combine(outputDirectory.FullName, fileName);
                await GenerateAssetAsync(sourceImage, outputPath, width, height, cancellationToken);
                successCount++;
                logger.LogDebug("  {UISymbol} Generated: {FileName} ({Width}x{Height})", UiSymbols.Check, fileName, width, height);
            }
            catch (Exception ex)
            {
                logger.LogWarning("{UISymbol} Failed to generate {FileName}: {ErrorMessage}", UiSymbols.Warning, fileName, ex.Message);
            }
        }

        logger.LogInformation("{UISymbol} Successfully generated {Count} of {Total} image assets", 
            UiSymbols.Party, successCount, AssetSpecifications.Length);
    }

    private static async Task GenerateAssetAsync(SKBitmap sourceImage, string outputPath, int targetWidth, int targetHeight, CancellationToken cancellationToken)
    {
        // Calculate scaling to fit target dimensions while maintaining aspect ratio
        var sourceAspect = (float)sourceImage.Width / sourceImage.Height;
        var targetAspect = (float)targetWidth / targetHeight;

        int scaledWidth, scaledHeight;
        if (sourceAspect > targetAspect)
        {
            // Source is wider - fit to width
            scaledWidth = targetWidth;
            scaledHeight = (int)(targetWidth / sourceAspect);
        }
        else
        {
            // Source is taller - fit to height
            scaledHeight = targetHeight;
            scaledWidth = (int)(targetHeight * sourceAspect);
        }

        // Create the target bitmap with the required dimensions
        using var targetBitmap = new SKBitmap(targetWidth, targetHeight);
        using var canvas = new SKCanvas(targetBitmap);

        // Fill with transparent background
        canvas.Clear(SKColors.Transparent);

        // Calculate position to center the scaled image
        var destRect = new SKRect(
            (targetWidth - scaledWidth) / 2f,
            (targetHeight - scaledHeight) / 2f,
            (targetWidth + scaledWidth) / 2f,
            (targetHeight + scaledHeight) / 2f
        );

        // Draw the scaled image
        var paint = new SKPaint
        {
            IsAntialias = true
        };
        canvas.DrawBitmap(sourceImage, destRect, paint);

        // Encode and save as PNG
        using var image = SKImage.FromBitmap(targetBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        
        // Write to file asynchronously
        await using var stream = File.Create(outputPath);
        data.SaveTo(stream);
        await stream.FlushAsync(cancellationToken);
    }
}
