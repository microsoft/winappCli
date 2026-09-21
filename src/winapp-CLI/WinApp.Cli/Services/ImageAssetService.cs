// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using SkiaSharp;
using Svg;
using Svg.Skia;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Wraps either a raster Bitmap or an SVG SKPicture so that SVG sources can be
/// rendered directly at each target size without an intermediate rasterization step.
/// </summary>
internal sealed class ImageSource : IDisposable
{
    private readonly Bitmap? bitmap;
    private readonly SKSvg? svg;
    private readonly SKPicture? svgPicture;
    private readonly SKRect svgBounds;

    private ImageSource(Bitmap bitmap)
    {
        this.bitmap = bitmap;
        AspectRatio = (float)bitmap.Width / bitmap.Height;
    }

    private ImageSource(SKSvg svg, SKPicture picture, SKRect bounds)
    {
        this.svg = svg;
        svgPicture = picture;
        svgBounds = bounds;

        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        AspectRatio = (float)width / height;
    }

    public float AspectRatio { get; }

    public bool IsSvg => svgPicture != null;

    public string DimensionsLabel => bitmap != null
        ? $"{bitmap.Width}x{bitmap.Height}"
        : $"{(int)Math.Ceiling(svgBounds.Width)}x{(int)Math.Ceiling(svgBounds.Height)} (SVG)";

    /// <summary>
    /// Renders the source at the exact target size and returns PNG bytes.
    /// SVG sources are rasterized via SkiaSharp at the target resolution.
    /// Raster sources are scaled via GDI+ high-quality bicubic.
    /// </summary>
    public byte[] RenderToPng(int targetWidth, int targetHeight)
    {
        if (svgPicture != null)
        {
            return RenderSvgToPng(targetWidth, targetHeight);
        }

        return RenderBitmapToPng(bitmap!, targetWidth, targetHeight);
    }

    private byte[] RenderSvgToPng(int targetWidth, int targetHeight)
    {
        using var skBitmap = new SKBitmap(targetWidth, targetHeight);
        using (var canvas = new SKCanvas(skBitmap))
        {
            canvas.Clear(SKColors.Transparent);

            // Fit SVG into target maintaining aspect ratio, centered
            var svgWidth = svgBounds.Width;
            var svgHeight = svgBounds.Height;
            var svgAspect = svgWidth / svgHeight;
            var targetAspect = (float)targetWidth / targetHeight;

            float scale;
            float offsetX, offsetY;
            if (svgAspect > targetAspect)
            {
                scale = targetWidth / svgWidth;
                offsetX = 0;
                offsetY = (targetHeight - svgHeight * scale) / 2f;
            }
            else
            {
                scale = targetHeight / svgHeight;
                offsetX = (targetWidth - svgWidth * scale) / 2f;
                offsetY = 0;
            }

            canvas.Translate(offsetX - svgBounds.Left * scale, offsetY - svgBounds.Top * scale);
            canvas.Scale(scale);
            canvas.DrawPicture(svgPicture);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] RenderBitmapToPng(Bitmap source, int targetWidth, int targetHeight)
    {
        var sourceAspect = (float)source.Width / source.Height;
        var targetAspect = (float)targetWidth / targetHeight;

        int scaledWidth;
        int scaledHeight;
        if (sourceAspect > targetAspect)
        {
            scaledWidth = targetWidth;
            scaledHeight = (int)(targetWidth / sourceAspect);
        }
        else
        {
            scaledHeight = targetHeight;
            scaledWidth = (int)(targetHeight * sourceAspect);
        }

        using var targetBitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(targetBitmap);

        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.Clear(Color.Transparent);

        var x = (targetWidth - scaledWidth) / 2f;
        var y = (targetHeight - scaledHeight) / 2f;
        graphics.DrawImage(source, new RectangleF(x, y, scaledWidth, scaledHeight));

        using var ms = new MemoryStream();
        targetBitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public static ImageSource FromFile(FileInfo path)
    {
        if (path.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return FromSvgFile(path);
        }

        if (path.Extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var icon = new Icon(path.FullName);
            return new ImageSource(icon.ToBitmap());
        }

        return new ImageSource(new Bitmap(path.FullName));
    }

    private static ImageSource FromSvgFile(FileInfo path)
    {
        var svg = new SKSvg();
        try
        {
            using var stream = File.OpenRead(path.FullName);
            svg.Load(stream);

            var picture = svg.Picture;
            if (picture == null)
            {
                throw new InvalidOperationException(
                    $"Failed to render SVG image: {path.FullName}. The file may be corrupted or contain unsupported SVG features.");
            }

            if (!DeclaresUsableSize(svg.SourceDocument))
            {
                throw new InvalidOperationException(
                    $"SVG image has no usable dimensions: {path.FullName}. Ensure the SVG has a valid viewBox or width/height attributes.");
            }

            var bounds = picture.CullRect;
            var width = (int)Math.Ceiling(bounds.Width);
            var height = (int)Math.Ceiling(bounds.Height);

            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException(
                    $"SVG image has invalid dimensions ({width}x{height}): {path.FullName}. Ensure the SVG has a valid viewBox or width/height attributes.");
            }

            return new ImageSource(svg, picture, bounds);
        }
        catch
        {
            svg.Dispose();
            throw;
        }
    }

    /// <summary>Reports whether an SVG declares a size the renderer can scale assets from.</summary>
    /// <remarks>
    /// This asks the document rather than the rendered picture on purpose. Svg.Skia clamps a
    /// sizeless SVG to a 1x1 picture instead of reporting 0x0, which makes the rasterized bounds
    /// unable to tell "declares nothing" from "is genuinely one pixel" — so relying on them would
    /// silently produce a full set of blank assets. A viewBox alone is enough to scale from;
    /// failing that, width and height must both be positive and absolute, because a percentage is
    /// relative to a viewport that generating a fixed-size icon has no equivalent of.
    /// </remarks>
    private static bool DeclaresUsableSize(SvgDocument? document)
    {
        // Nothing to inspect: leave the decision to the bounds check that follows.
        if (document is null)
        {
            return true;
        }

        var viewBox = document.ViewBox;
        if (viewBox.Width > 0 && viewBox.Height > 0)
        {
            return true;
        }

        return IsPositiveAbsolute(document.Width) && IsPositiveAbsolute(document.Height);

        static bool IsPositiveAbsolute(SvgUnit unit)
            => unit.Value > 0 && unit.Type != SvgUnitType.Percentage;
    }

    public void Dispose()
    {
        bitmap?.Dispose();
        svg?.Dispose(); // Deterministically frees the SKPicture it owns
    }
}

internal class ImageAssetService : IImageAssetService
{
    private static readonly ManifestAssetReference[] DefaultAssetReferences =
    [
        new("AppList.png", 44, 44),
        new("MedTile.png", 150, 150),
        new("WideTile.png", 310, 150),
        new("StoreLogo.png", 50, 50),
    ];

    private static readonly (string Suffix, float Scale)[] ScaleVariants =
    [
        ("", 1.0f),
        (".scale-125", 1.25f),
        (".scale-150", 1.5f),
        (".scale-200", 2.0f),
        (".scale-400", 4.0f),
    ];

    private static readonly int[] TargetSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];

    private static readonly int[] IcoSizes = [16, 24, 32, 48, 256];

    public Task GenerateAssetsAsync(
        FileInfo sourceImagePath,
        DirectoryInfo outputDirectory,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default)
    {
        return GenerateAssetsFromManifestAsync(
            sourceImagePath,
            outputDirectory,
            DefaultAssetReferences,
            taskContext,
            lightImagePath,
            cancellationToken);
    }

    public async Task GenerateAssetsFromManifestAsync(
        FileInfo sourceImagePath,
        DirectoryInfo manifestDirectory,
        IReadOnlyList<ManifestAssetReference> assetReferences,
        TaskContext taskContext,
        FileInfo? lightImagePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!sourceImagePath.Exists)
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath.FullName}");
        }

        if (lightImagePath is { Exists: false })
        {
            throw new FileNotFoundException($"Light theme source image not found: {lightImagePath.FullName}");
        }

        if (assetReferences.Count == 0)
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} No asset references found in manifest. No assets generated.");
            return;
        }

        taskContext.AddDebugMessage($"Generating MSIX image assets from: {sourceImagePath.FullName}");

        ImageSource source;
        try
        {
            source = ImageSource.FromFile(sourceImagePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to decode image: {sourceImagePath.FullName}. Please ensure the file is a valid image format.", ex);
        }

        ImageSource? lightSource = null;
        try
        {
            if (lightImagePath != null)
            {
                lightSource = ImageSource.FromFile(lightImagePath);
            }
        }
        catch (Exception ex)
        {
            source.Dispose();
            throw new InvalidOperationException($"Failed to decode image: {lightImagePath!.FullName}. Please ensure the file is a valid image format.", ex);
        }

        using (source)
        using (lightSource)
        {
            taskContext.AddDebugMessage($"Source image: {source.DimensionsLabel}");
            if (lightSource != null)
            {
                taskContext.AddDebugMessage($"Light image: {lightSource.DimensionsLabel}");
            }

            var (successCount, totalCount) = await Task.Run(() =>
            {
                var success = 0;
                var total = 0;

                foreach (var assetReference in assetReferences)
            {
                var assetFullPath = Path.Combine(manifestDirectory.FullName, assetReference.RelativePath);
                var assetDirectory = Path.GetDirectoryName(assetFullPath) ?? manifestDirectory.FullName;
                var assetFileName = Path.GetFileNameWithoutExtension(assetReference.RelativePath);
                var assetExtension = Path.GetExtension(assetReference.RelativePath);

                if (!Directory.Exists(assetDirectory))
                {
                    Directory.CreateDirectory(assetDirectory);
                }

                foreach (var (suffix, scale) in ScaleVariants)
                {
                    var scaledWidth = (int)Math.Round(assetReference.BaseWidth * scale, MidpointRounding.AwayFromZero);
                    var scaledHeight = (int)Math.Round(assetReference.BaseHeight * scale, MidpointRounding.AwayFromZero);
                    var scaledFileName = $"{assetFileName}{suffix}{assetExtension}";
                    var scaledPath = Path.Combine(assetDirectory, scaledFileName);

                    total++;
                    if (TryGenerateAsset(source, scaledPath, scaledFileName, scaledWidth, scaledHeight, taskContext))
                    {
                        success++;
                    }

                    if (lightSource != null)
                    {
                        var lightScaleFileName = $"{assetFileName}.scale-{GetScalePercentage(scale)}_altform-colorful_theme-light{assetExtension}";
                        var lightScalePath = Path.Combine(assetDirectory, lightScaleFileName);

                        total++;
                        if (TryGenerateAsset(lightSource, lightScalePath, lightScaleFileName, scaledWidth, scaledHeight, taskContext))
                        {
                            success++;
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (IsTargetSizeAsset(assetReference))
                {
                    foreach (var targetSize in TargetSizes)
                    {
                        var platedFileName = $"{assetFileName}.targetsize-{targetSize}{assetExtension}";
                        var platedPath = Path.Combine(assetDirectory, platedFileName);

                        total++;
                        if (TryGenerateAsset(source, platedPath, platedFileName, targetSize, targetSize, taskContext))
                        {
                            success++;
                        }

                        var unplatedFileName = $"{assetFileName}.targetsize-{targetSize}_altform-unplated{assetExtension}";
                        var unplatedPath = Path.Combine(assetDirectory, unplatedFileName);

                        total++;
                        if (TryGenerateAsset(source, unplatedPath, unplatedFileName, targetSize, targetSize, taskContext))
                        {
                            success++;
                        }

                        if (lightSource != null)
                        {
                            var lightTargetFileName = $"{assetFileName}.targetsize-{targetSize}_altform-lightunplated{assetExtension}";
                            var lightTargetPath = Path.Combine(assetDirectory, lightTargetFileName);

                            total++;
                            if (TryGenerateAsset(lightSource, lightTargetPath, lightTargetFileName, targetSize, targetSize, taskContext))
                            {
                                success++;
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

                return (success, total);
            },
            cancellationToken);

            if (successCount == totalCount)
            {
                taskContext.AddDebugMessage($"Successfully generated {totalCount} image assets");
            }
            else
            {
                taskContext.AddStatusMessage($"{UiSymbols.Info} Successfully generated {successCount} of {totalCount} image assets");
            }
        }
    }

    public async Task GenerateIcoAsync(FileInfo sourceImagePath, string outputPath, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        if (!sourceImagePath.Exists)
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath.FullName}");
        }

        taskContext.AddDebugMessage($"Generating ICO file: {outputPath}");

        ImageSource source;
        try
        {
            source = ImageSource.FromFile(sourceImagePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to decode image: {sourceImagePath.FullName}. Please ensure the file is a valid image format.", ex);
        }

        using (source)
        {
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await Task.Run(() =>
            {
                var pngFrames = new List<byte[]>(IcoSizes.Length);

                foreach (var size in IcoSizes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pngFrames.Add(source.RenderToPng(size, size));
                }

                WriteIcoFile(outputPath, IcoSizes, pngFrames);
            }, cancellationToken);
        }

        taskContext.AddDebugMessage($"Generated ICO file with {IcoSizes.Length} sizes");
    }

    private static int GetScalePercentage(float scale)
    {
        return (int)Math.Round(scale * 100, MidpointRounding.AwayFromZero);
    }

    private static bool IsTargetSizeAsset(ManifestAssetReference assetReference)
    {
        // App icon assets (44x44) get targetsize variants regardless of naming convention
        // Supports both old naming (Square44x44Logo) and new naming (AppList)
        return assetReference.BaseWidth == 44
            && assetReference.BaseHeight == 44;
    }

    private static bool TryGenerateAsset(
        ImageSource source,
        string outputPath,
        string fileName,
        int targetWidth,
        int targetHeight,
        TaskContext taskContext)
    {
        try
        {
            GenerateAsset(source, outputPath, targetWidth, targetHeight);
            taskContext.AddDebugMessage($"  {UiSymbols.Check} Generated: {fileName} ({targetWidth}x{targetHeight})");
            return true;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"  {UiSymbols.Warning} Failed to generate {fileName}: {ex.Message}");
            return false;
        }
    }

    private static void WriteIcoFile(string outputPath, int[] sizes, List<byte[]> pngFrames)
    {
        if (sizes.Length != pngFrames.Count)
        {
            throw new InvalidOperationException("ICO size and frame counts must match.");
        }

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fileStream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);

        var dataOffset = 6 + (16 * sizes.Length);
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var pngData = pngFrames[i];

            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)pngData.Length);
            writer.Write((uint)dataOffset);

            dataOffset += pngData.Length;
        }

        foreach (var pngData in pngFrames)
        {
            writer.Write(pngData);
        }
    }

    private static void GenerateAsset(ImageSource source, string outputPath, int targetWidth, int targetHeight)
    {
        var pngData = source.RenderToPng(targetWidth, targetHeight);
        File.WriteAllBytes(outputPath, pngData);
    }
}
