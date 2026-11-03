# Imaging Migration

Use this reference when the WPF application uses `System.Windows.Media.Imaging`, WPF bitmap transforms/encoders, `Image.Source`, or persisted imaging settings.

## Separate Display from Processing

WPF often uses `BitmapSource` for both UI display and pixel processing. WinUI migrations should make that boundary explicit:

| Need | Typical WinUI/Windows type |
|---|---|
| Display an image in XAML | `Microsoft.UI.Xaml.Media.Imaging.BitmapImage` or `SoftwareBitmapSource` |
| Decode, encode, transform, or inspect metadata | `Windows.Graphics.Imaging.BitmapDecoder`, `BitmapEncoder`, `BitmapTransform`, `SoftwareBitmap` |
| Existing cross-platform/native image pipeline | Keep or adapt the existing library if it meets deployment, licensing, performance, and format requirements |

`BitmapImage` is a UI source, not a replacement for WPF's processing pipeline. `System.Drawing` is also a separate technology; do not mechanically replace it with `BitmapImage`.

If the migration goal is to remove WPF, contain or replace `PresentationCore`/`System.Windows.Media.Imaging` dependencies rather than enabling WPF in the WinUI application as a compiler workaround. A deliberate hybrid library is possible, but it must be treated as an architectural and deployment choice.

## Inventory Imaging Behavior

Before changing types, record:

- Supported input and output formats, including multi-frame images.
- Resize/crop/rotate/orientation behavior and interpolation quality.
- Alpha and pixel format assumptions.
- Color management and ICC profile behavior.
- Metadata preservation/removal policy.
- JPEG quality, TIFF compression, PNG interlace, and other encoder options.
- Memory/stream ownership and maximum image size.
- Concurrency, cancellation, progress, and error reporting.
- Persisted codec GUIDs, enum numeric values, or settings names.
- Tests that expect byte-identical output versus semantically equivalent output.

Choose the processing API only after this inventory. Windows.Graphics.Imaging is a strong Windows-native option, but it does not reproduce every WPF codec feature.

## Core Type Map

| WPF | Windows.Graphics.Imaging / WinUI | Notes |
|---|---|---|
| `BitmapDecoder.Create(...)` and format-specific decoders | `BitmapDecoder.CreateAsync(...)` | Unified async decoder; format is detected from the stream. |
| `decoder.Frames[0]` | `await decoder.GetFrameAsync(0)` | Frame access is async. |
| `decoder.Frames.Count` | `decoder.FrameCount` | WinRT decoder counts/dimensions commonly use unsigned values; check each projected API. |
| `BitmapSource` pixel data | `SoftwareBitmap` or an app-owned image abstraction | Do not expose a UI source type from processing interfaces. |
| `BitmapImage` for display | `Microsoft.UI.Xaml.Media.Imaging.BitmapImage` | Similar name, different framework namespace/lifetime. |
| `FormatConvertedBitmap` | `SoftwareBitmap.Convert(...)` or decoder pixel-format request | Validate alpha and color behavior. |
| `TransformedBitmap` / `ScaleTransform` | `BitmapTransform` configured on decoder/encoder operations | WinRT often expresses transforms declaratively. |
| `CroppedBitmap` | `BitmapTransform.Bounds` | Bounds use pixel coordinates and unsigned sizes. |
| Format-specific encoders | `BitmapEncoder.CreateAsync(codecId, stream)` | Unified async encoder factory. |
| `encoder.Save(stream)` | `await encoder.FlushAsync()` | Observe failures/cancellation and stream position. |
| `BitmapMetadata` | `BitmapProperties` / image property queries | Query names and supported write behavior differ. |

## Streams

Windows.Graphics.Imaging consumes WinRT stream interfaces such as `IRandomAccessStream`:

```csharp
await using FileStream input = File.OpenRead(path);
using IRandomAccessStream randomAccess = input.AsRandomAccessStream();

BitmapDecoder decoder = await BitmapDecoder.CreateAsync(randomAccess);
```

Review ownership carefully:

- Which wrapper disposes the underlying stream.
- Whether an operation changes stream position.
- Whether the same input stream is reused for transcoding or metadata reads.
- Whether the source supports random access.

`BitmapEncoder` expects an empty output stream. If a destination may already contain data, set its `Size` to `0` and seek to the beginning before creating the encoder. Do not truncate the decoder's source stream before the source-dependent encoding operation has completed.

For same-file replacement, follow the official SimpleImaging pattern: encode to a separate empty `InMemoryRandomAccessStream`; after `FlushAsync`, seek the temporary stream and destination to zero, set the destination's `Size` to `0`, and copy the encoded bytes. If the operation changes only a limited amount of metadata, evaluate `CreateForInPlacePropertyEncodingAsync` and its documented restrictions.

Do not rely on a sample's seek behavior without testing the exact decode/encode flow.

## Decode, Transform, and Encode

Same-format resize/transcode shape, where `outputStream` is separate from `inputStream`:

```csharp
BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream);

outputStream.Size = 0;
outputStream.Seek(0);
BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(
    outputStream,
    decoder);

encoder.BitmapTransform.ScaledWidth = targetWidth;
encoder.BitmapTransform.ScaledHeight = targetHeight;
encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;

await encoder.FlushAsync();
```

`CreateForTranscodingAsync` can only create an encoder in the decoder's image format and does not support codec-specific encoding options. Use it for same-format edits where unspecified source data should be preserved. For a format conversion, such as PNG to JPEG, or for options such as JPEG quality, decode the pixels and create a fresh encoder with `CreateAsync(targetEncoderId, outputStream, encodingOptions)`.

This is not a universal WPF pipeline replacement. Decide whether to:

- Transcode from the source decoder when the output remains in the same format.
- Decode to `SoftwareBitmap`, modify pixels, then call `SetSoftwareBitmap`.
- Process each frame independently.
- Create a fresh encoder for format conversion, codec options, or to avoid carrying source metadata.
- Use another imaging library for an unsupported codec or transform.

Before calling `SetSoftwareBitmap`, request or convert to a pixel format accepted by the encoder path (commonly `Bgra8`, `Rgba8`, or `Rgba16` as supported) and dispose intermediate frame/bitmap objects. Do not pass an arbitrary decoded gray/indexed format with a mismatched alpha mode.

## Encoder Options

WPF exposes format-specific encoder classes and strongly typed properties. Windows.Graphics.Imaging uses codec IDs and bitmap property sets for many options.

For example, JPEG quality commonly changes from a WPF integer percentage to a WinRT floating-point property:

```csharp
var properties = new BitmapPropertySet
{
    ["ImageQuality"] = new BitmapTypedValue(
        0.85f,
        PropertyType.Single),
};

outputStream.Size = 0;
outputStream.Seek(0);
BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
    BitmapEncoder.JpegEncoderId,
    outputStream,
    properties);
```

Verify the property name, type, range, and codec support against the target SDK and actual files. Do not assume WPF values can be cast directly.

Codec-specific mappings and compatibility gaps to test include:

- PNG interlace options. Windows.Graphics.Imaging exposes an interlace encoding option; verify its property type and output with the target SDK/codec.
- TIFF compression variants and their numeric/property mapping.
- WMP/HDP/JPEG XR. Windows.Graphics.Imaging exposes JPEG XR decoder/encoder IDs; verify deployed codec behavior, metadata, and output compatibility rather than declaring the format unsupported.
- Selective metadata removal.
- Indexed and uncommon pixel formats.
- Bit-exact scaler output.

When an option is unsupported, define the product behavior: preserve via another library, degrade with an explicit warning, or reject the operation.

## Display a SoftwareBitmap

`SoftwareBitmapSource` requires a display-compatible pixel/alpha format:

```csharp
SoftwareBitmap displayBitmap = bitmap;

if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
    bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
{
    displayBitmap = SoftwareBitmap.Convert(
        bitmap,
        BitmapPixelFormat.Bgra8,
        BitmapAlphaMode.Premultiplied);
}

var source = new SoftwareBitmapSource();
await source.SetBitmapAsync(displayBitmap);
image.Source = source;
```

Decide who owns and disposes the original and converted bitmaps. Avoid leaking one `SoftwareBitmap` per preview refresh.

For URI/file display that needs no pixel processing, a `BitmapImage` may be simpler:

```csharp
var source = new BitmapImage();
await source.SetSourceAsync(randomAccessStream);
image.Source = source;
```

## Async and Concurrency

Many WinRT imaging operations are async. Propagate async through the processing boundary rather than blocking:

```csharp
public async Task<ResizeResult> ResizeAsync(
    Stream input,
    Stream output,
    CancellationToken cancellationToken)
{
    // Decode, transform, encode, and observe cancellation at owned boundaries.
}
```

- Prefer `Task.WhenAll` or `Parallel.ForEachAsync` only when memory and codec concurrency have safe bounds.
- Limit parallelism by both CPU and expected decoded image size; a few large images can exhaust memory.
- Observe all task exceptions and define partial-failure behavior.
- Do not use `.Result`/`.Wait()` on the UI thread.
- Dispatch only UI progress/preview updates, not the decode itself.

## Multi-Frame Images

Do not silently process only frame zero if WPF preserved animated GIFs or multi-page TIFFs.

For every frame, decide:

- Whether the output format supports multiple frames.
- Frame delay/disposal metadata behavior.
- Per-frame transform and pixel format.
- Metadata/orientation policy.
- What happens when the selected target codec cannot represent the source.

Use `FrameCount`, `GetFrameAsync`, and the encoder's frame APIs as supported by the codec, then test real multi-frame samples.

## Metadata and Orientation

`CreateForTranscodingAsync` starts from the source decoder and is useful when preserving source information; `CreateAsync` starts a new encode path. Neither choice replaces a product-specific metadata policy.

Validate at least:

- EXIF orientation and whether pixels are physically rotated.
- Date taken, camera, GPS, comment, rating, and custom metadata required by the app.
- ICC profile/color management.
- Whether “remove metadata” also removes orientation needed for correct display.
- Metadata behavior across resize, format conversion, and multi-frame output.

Use property queries supported by the selected codec. Treat unsupported or rejected metadata writes as expected error paths rather than assuming every WPF metadata field maps.

## Persisted Imaging Settings

WPF codec container GUIDs or enum values may be stored in settings or exposed through IPC/CLI contracts. Removing the WPF type must not corrupt those contracts.

- Keep an application-owned compatibility enum with the same serialized numeric/string values when required.
- Map legacy identifiers to the selected WinRT codec IDs at the boundary.
- Preserve unknown values for forward compatibility when the existing schema does so.
- Add upgrade/deserialization tests using settings produced by released versions.
- Do not copy another application's GUID table; derive mappings from the identifiers this application actually persisted.

## Testing

Use representative fixtures for:

- Every supported format and encoder option.
- Tiny, large, transparent, indexed, high-bit-depth, and malformed images.
- EXIF orientation and color profiles.
- Multi-frame input.
- Metadata keep/remove policies.
- Cancellation, locked files, non-seekable streams, and partial output.
- Peak memory and bounded parallelism.

Compare the correct invariant. Dimensions, visible pixels within tolerance, metadata, and user-visible behavior are often more meaningful than byte equality across different codec implementations.

## Official References

- [`BitmapEncoder.CreateAsync`](https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapencoder.createasync)
- [`BitmapEncoder.CreateForTranscodingAsync`](https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapencoder.createfortranscodingasync)
- [Create, edit, and save bitmap images](https://learn.microsoft.com/windows/apps/develop/media-authoring-processing/imaging)
- [`BitmapEncoder.CreateForInPlacePropertyEncodingAsync`](https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapencoder.createforinplacepropertyencodingasync)
