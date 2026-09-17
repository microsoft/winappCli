// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ImageAssetService"/> and its <see cref="ImageSource"/> helper.
/// Drives the real asset-generation workflow (scale + targetsize variants, light-theme
/// variants, ICO writing) from raster / SVG / ICO sources, plus the decode and
/// not-found error paths that surface actionable exceptions.
/// </summary>
// These tests render real bitmaps through GDI+ (System.Drawing). GDI+ encoder lookup
// (Image.Save with an ImageFormat) is not thread-safe and races under MSTest's
// method-level parallelism, intermittently throwing "Value cannot be null.
// (Parameter 'encoder')". Production renders assets sequentially, so serialize this
// class rather than altering product code.
[DoNotParallelize]
[TestClass]
public class ImageAssetServiceTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"ImageAssetTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    private static TaskContext CreateTaskContext()
    {
        var task = new GroupableTask("test", null);
        var console = new Spectre.Console.Testing.TestConsole();
        return new TaskContext(task, null, console, NullLogger.Instance, new Lock());
    }

    private string Path_(string name) => Path.Combine(_tempDir.FullName, name);

    private string CreateSvg(string name, int width, int height)
    {
        var path = Path_(name);
        File.WriteAllText(path,
            $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}"><rect width="{width}" height="{height}" fill="green"/></svg>""");
        return path;
    }

    #region ImageSource

    [TestMethod]
    public void ImageSource_FromRaster_ReportsBitmapMetadata()
    {
        var path = PngHelper.CreateRasterPng(Path_("wide.png"), 40, 20);

        using var source = ImageSource.FromFile(new FileInfo(path));

        Assert.IsFalse(source.IsSvg, "a PNG source is not an SVG");
        Assert.AreEqual(2f, source.AspectRatio, 0.01, "40x20 has a 2:1 aspect ratio");
        Assert.AreEqual("40x20", source.DimensionsLabel);
    }

    [TestMethod]
    public void ImageSource_FromSvg_ReportsSvgMetadata()
    {
        var path = CreateSvg("wide.svg", 200, 100);

        using var source = ImageSource.FromFile(new FileInfo(path));

        Assert.IsTrue(source.IsSvg, "an SVG source reports IsSvg");
        Assert.AreEqual(2f, source.AspectRatio, 0.01);
        StringAssert.Contains(source.DimensionsLabel, "(SVG)");
        StringAssert.Contains(source.DimensionsLabel, "200x100");
    }

    [TestMethod]
    public void ImageSource_RenderToPng_ProducesRequestedSize()
    {
        using var source = ImageSource.FromFile(new FileInfo(PngHelper.CreateRasterPng(Path_("src.png"), 40, 20)));

        var png = source.RenderToPng(64, 64);

        using var ms = new MemoryStream(png);
        using var decoded = new Bitmap(ms);
        Assert.AreEqual(64, decoded.Width);
        Assert.AreEqual(64, decoded.Height);
    }

    [TestMethod]
    public void ImageSource_FromInvalidSvg_Throws()
    {
        var path = Path_("broken.svg");
        File.WriteAllText(path, "this is not svg");

        var threw = false;
        try
        {
            using var _ = ImageSource.FromFile(new FileInfo(path));
        }
        catch
        {
            threw = true;
        }

        Assert.IsTrue(threw, "an unparseable SVG must surface an error rather than a null picture");
    }

    [TestMethod]
    public void ImageSource_FromZeroDimensionSvg_Throws()
    {
        var path = Path_("empty.svg");
        File.WriteAllText(path,
            """<svg xmlns="http://www.w3.org/2000/svg" width="0" height="0" viewBox="0 0 0 0"></svg>""");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            using var _ = ImageSource.FromFile(new FileInfo(path));
        });
    }

    [TestMethod]
    public void ImageSource_FromViewBoxOnlySvg_UsesViewBoxDimensions()
    {
        // No width/height attributes, so the document reports "100%" for both. The viewBox is the
        // only thing that declares a size, and it has to be enough on its own.
        var path = Path_("viewbox-only.svg");
        File.WriteAllText(path,
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 24"><rect width="48" height="24" fill="red"/></svg>""");

        using var source = ImageSource.FromFile(new FileInfo(path));

        Assert.IsTrue(source.IsSvg);
        Assert.AreEqual(2f, source.AspectRatio, 0.01);
        StringAssert.Contains(source.DimensionsLabel, "48x24");
    }

    #endregion

    #region GenerateAssetsFromManifestAsync - workflows

    [TestMethod]
    public async Task GenerateAssets_RasterSource_WritesScaleAndTargetSizeVariants()
    {
        // A 44x44 asset reference triggers both scale variants and the targetsize plated/unplated set.
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 40, 20));
        var refs = new[] { new ManifestAssetReference(@"Assets\AppList.png", 44, 44) };

        await new ImageAssetService().GenerateAssetsFromManifestAsync(
            source, _tempDir, refs, CreateTaskContext());

        var assetsDir = Path.Combine(_tempDir.FullName, "Assets");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "AppList.png")), "base scale asset written");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "AppList.scale-200.png")), "scale-200 variant written");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "AppList.targetsize-16.png")), "targetsize variant written");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "AppList.targetsize-256_altform-unplated.png")), "unplated targetsize variant written");
    }

    [TestMethod]
    public async Task GenerateAssets_NonIconReference_SkipsTargetSizeVariants()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 40, 20));
        var refs = new[] { new ManifestAssetReference(@"Assets\MedTile.png", 150, 150) };

        await new ImageAssetService().GenerateAssetsFromManifestAsync(
            source, _tempDir, refs, CreateTaskContext());

        var assetsDir = Path.Combine(_tempDir.FullName, "Assets");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "MedTile.png")));
        Assert.IsFalse(File.Exists(Path.Combine(assetsDir, "MedTile.targetsize-16.png")),
            "non 44x44 assets do not get targetsize variants");
    }

    [TestMethod]
    public async Task GenerateAssets_SvgSource_RendersVariants()
    {
        var source = new FileInfo(CreateSvg("app.svg", 200, 100));
        var refs = new[]
        {
            new ManifestAssetReference(@"Assets\AppList.png", 44, 44),
            new ManifestAssetReference(@"Assets\WideTile.png", 310, 150),
        };

        await new ImageAssetService().GenerateAssetsFromManifestAsync(
            source, _tempDir, refs, CreateTaskContext());

        var assetsDir = Path.Combine(_tempDir.FullName, "Assets");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "AppList.png")));
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "WideTile.png")));
    }

    [TestMethod]
    public async Task GenerateAssets_WithLightImage_WritesThemedVariants()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("dark.png"), 44, 44));
        var light = new FileInfo(PngHelper.CreateRasterPng(Path_("light.png"), 44, 44));
        var refs = new[] { new ManifestAssetReference(@"Assets\AppList.png", 44, 44) };

        await new ImageAssetService().GenerateAssetsFromManifestAsync(
            source, _tempDir, refs, CreateTaskContext(), light);

        var assetsDir = Path.Combine(_tempDir.FullName, "Assets");
        Assert.IsTrue(
            File.Exists(Path.Combine(assetsDir, "AppList.scale-100_altform-colorful_theme-light.png")),
            "light theme scale variant written");
        Assert.IsTrue(
            File.Exists(Path.Combine(assetsDir, "AppList.targetsize-16_altform-lightunplated.png")),
            "light theme targetsize variant written");
    }

    [TestMethod]
    public async Task GenerateAssetsAsync_ConvenienceOverload_UsesDefaultReferenceSet()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 40, 20));

        await new ImageAssetService().GenerateAssetsAsync(source, _tempDir, CreateTaskContext());

        // Default reference set includes StoreLogo (50x50) and the 44x44 app icon.
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "StoreLogo.png")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "AppList.png")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "AppList.targetsize-32.png")));
    }

    #endregion

    #region GenerateAssetsFromManifestAsync - guard rails

    [TestMethod]
    public async Task GenerateAssets_MissingSource_ThrowsFileNotFound()
    {
        var missing = new FileInfo(Path_("does-not-exist.png"));
        var refs = new[] { new ManifestAssetReference("StoreLogo.png", 50, 50) };

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            new ImageAssetService().GenerateAssetsFromManifestAsync(missing, _tempDir, refs, CreateTaskContext()));
    }

    [TestMethod]
    public async Task GenerateAssets_MissingLightImage_ThrowsFileNotFound()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 44, 44));
        var missingLight = new FileInfo(Path_("no-light.png"));
        var refs = new[] { new ManifestAssetReference("StoreLogo.png", 50, 50) };

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            new ImageAssetService().GenerateAssetsFromManifestAsync(source, _tempDir, refs, CreateTaskContext(), missingLight));
    }

    [TestMethod]
    public async Task GenerateAssets_EmptyReferenceList_WarnsAndReturns()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 44, 44));

        await new ImageAssetService().GenerateAssetsFromManifestAsync(
            source, _tempDir, Array.Empty<ManifestAssetReference>(), CreateTaskContext());

        // No assets should be produced, and no exception thrown — only the source file remains.
        Assert.AreEqual(1, _tempDir.GetFiles("*.png", SearchOption.AllDirectories).Length,
            "an empty reference list generates nothing; only the source png exists");
    }

    [TestMethod]
    public async Task GenerateAssets_UndecodableSource_ThrowsInvalidOperation()
    {
        var bad = new FileInfo(Path_("garbage.png"));
        File.WriteAllText(bad.FullName, "not really a png");
        var refs = new[] { new ManifestAssetReference("StoreLogo.png", 50, 50) };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new ImageAssetService().GenerateAssetsFromManifestAsync(bad, _tempDir, refs, CreateTaskContext()));
    }

    [TestMethod]
    public async Task GenerateAssets_UndecodableLightImage_ThrowsInvalidOperation()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 44, 44));
        var badLight = new FileInfo(Path_("garbage-light.png"));
        File.WriteAllText(badLight.FullName, "not really a png");
        var refs = new[] { new ManifestAssetReference("StoreLogo.png", 50, 50) };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new ImageAssetService().GenerateAssetsFromManifestAsync(source, _tempDir, refs, CreateTaskContext(), badLight));
    }

    [TestMethod]
    public async Task GenerateAssets_Cancelled_ThrowsOperationCanceled()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 44, 44));
        var refs = new[] { new ManifestAssetReference(@"Assets\AppList.png", 44, 44) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await new ImageAssetService().GenerateAssetsFromManifestAsync(
                source, _tempDir, refs, CreateTaskContext(), null, cts.Token);
            Assert.Fail("expected cancellation to propagate");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    #endregion

    #region GenerateIcoAsync

    [TestMethod]
    public async Task GenerateIco_RasterSource_WritesMultiSizeIcon()
    {
        var source = new FileInfo(PngHelper.CreateRasterPng(Path_("app.png"), 40, 20));
        var icoPath = Path.Combine(_tempDir.FullName, "icons", "app.ico");

        await new ImageAssetService().GenerateIcoAsync(source, icoPath, CreateTaskContext());

        Assert.IsTrue(File.Exists(icoPath), "nested output directory should be created and the ICO written");

        var bytes = File.ReadAllBytes(icoPath);
        Assert.AreEqual(0, BitConverter.ToUInt16(bytes, 0), "reserved field");
        Assert.AreEqual(1, BitConverter.ToUInt16(bytes, 2), "type = icon");
        Assert.AreEqual(5, BitConverter.ToUInt16(bytes, 4), "one directory entry per ICO size");
    }

    [TestMethod]
    public async Task GenerateIco_SvgSource_CanBeReloadedAsIcoImageSource()
    {
        // Generate an ICO from an SVG, then feed that ICO back in as a source to cover the .ico branch.
        var svg = new FileInfo(CreateSvg("app.svg", 200, 100));
        var icoPath = Path.Combine(_tempDir.FullName, "app.ico");
        await new ImageAssetService().GenerateIcoAsync(svg, icoPath, CreateTaskContext());

        using var fromIco = ImageSource.FromFile(new FileInfo(icoPath));
        Assert.IsFalse(fromIco.IsSvg, "an .ico is loaded as a raster bitmap source");
    }

    [TestMethod]
    public async Task GenerateIco_MissingSource_ThrowsFileNotFound()
    {
        var missing = new FileInfo(Path_("missing.png"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            new ImageAssetService().GenerateIcoAsync(missing, Path.Combine(_tempDir.FullName, "x.ico"), CreateTaskContext()));
    }

    [TestMethod]
    public async Task GenerateIco_UndecodableSource_ThrowsInvalidOperation()
    {
        var bad = new FileInfo(Path_("bad.svg"));
        File.WriteAllText(bad.FullName, "definitely not svg");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new ImageAssetService().GenerateIcoAsync(bad, Path.Combine(_tempDir.FullName, "x.ico"), CreateTaskContext()));
    }

    #endregion
}
