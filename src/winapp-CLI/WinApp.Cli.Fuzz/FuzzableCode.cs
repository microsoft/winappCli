// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Fuzz;

/// <summary>
/// libFuzzer entry points for winapp's hand-written parsers over untrusted bytes:
/// <see cref="ZipRangeExtractor"/> and <see cref="GuestFrameCodec"/>.
/// </summary>
/// <remarks>
/// <c>WinDbgJsProviderAcquirer</c> range-downloads the WinDbg <c>.msixbundle</c> and parses its
/// ZIP64 central directory, and then a nested inner <c>.msix</c>, in order to locate
/// <c>JsProvider.dll</c>. That parsing necessarily happens <i>before</i> the extracted DLL's
/// Authenticode signature can be verified, so the archive bytes are untrusted at this point.
/// <para>
/// Each entry point must stay <c>public static void</c> taking <c>ReadOnlySpan&lt;byte&gt;</c> —
/// that is the signature libFuzzer binds to via <c>LibFuzzerDotnetLoader</c>.
/// </para>
/// </remarks>
public static class FuzzableCode
{
    // A malformed central directory can declare far more entries than the input could hold. Production
    // looks up one entry by name and stops; walking an unbounded list here would tank iteration rate
    // without covering new code.
    private const int MaxEntriesToVisit = 16;

    /// <summary>
    /// Targets <see cref="ZipRangeExtractor.ParseCentralDirectory"/> in isolation. This is a pure
    /// function over a byte array, so it delivers the highest iteration rate of the two targets.
    /// </summary>
    /// <remarks>
    /// Filtering here is deliberately strict. <c>ParseCentralDirectory</c> reads attacker-controlled
    /// 16-bit name/extra/comment lengths and slices on them, so a bounds exception escaping this
    /// method means the parser is missing a length check rather than rejecting bad input — exactly
    /// what this target exists to surface. <see cref="InvalidDataException"/> is tolerated so the
    /// target keeps working if those checks are later added as explicit rejections.
    /// </remarks>
    public static void FuzzParseCentralDirectory(ReadOnlySpan<byte> input)
    {
        try
        {
            ZipRangeExtractor.ParseCentralDirectory(input.ToArray(), archiveBase: 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // Deliberate rejection of a malformed archive.
        }
    }

    /// <summary>
    /// Targets the full acquisition sequence — outer central directory, nested inner archive, entry
    /// extraction — mirroring <c>WinDbgJsProviderAcquirer.ExtractJsProviderAsync</c>.
    /// </summary>
    public static void FuzzArchive(ReadOnlySpan<byte> input)
    {
        if (input.Length < 22)
        {
            // Smaller than a minimal end-of-central-directory record; cannot reach the parser.
            return;
        }

        var bytes = input.ToArray();

        try
        {
            var reader = new BoundedRangeReader(new MemoryRangeReader(bytes));
            var total = Await(reader.GetLengthAsync(CancellationToken.None));

            var (cdOffset, cdSize) = Await(
                ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, total, CancellationToken.None));
            var outerEntries = ZipRangeExtractor.ParseCentralDirectory(
                Await(reader.ReadAsync(cdOffset, checked((int)cdSize), CancellationToken.None)), 0);

            // Production selects the inner .msix by exact file name. A fuzzer will never synthesise
            // that name, so index into the entries instead to keep the nested path reachable.
            foreach (var inner in outerEntries.Take(MaxEntriesToVisit))
            {
                // Per entry, not around the loop: most entries are not archives, and rejecting one
                // must not stop the walk before it reaches the nested .msix.
                try
                {
                    VisitAsInnerArchive(reader, inner);
                }
                catch (Exception ex) when (IsExpectedArchiveRejection(ex))
                {
                    // This entry is not a usable nested archive; keep going.
                }
            }
        }
        catch (Exception ex) when (IsExpectedArchiveRejection(ex))
        {
            // Malformed archive. WinDbgJsProviderAcquirer.TryAcquireCoreAsync swallows these and
            // degrades gracefully, so they are not defects.
        }
    }

    private static void VisitAsInnerArchive(BoundedRangeReader reader, ZipEntry inner)
    {
        // Extracting the entry directly exercises the stored/deflate paths...
        _ = Await(ZipRangeExtractor.ExtractEntryAsync(reader, inner, CancellationToken.None));

        // ...and treating it as a nested archive exercises the bundle -> msix descent, where a
        // non-zero archiveBase makes every offset calculation relative.
        var innerStart = Await(ZipRangeExtractor.GetDataStartAsync(reader, inner, CancellationToken.None));
        var (innerCdOffset, innerCdSize) = Await(ZipRangeExtractor.FindCentralDirectoryAsync(
            reader, innerStart, inner.CompressedSize, CancellationToken.None));
        var innerEntries = ZipRangeExtractor.ParseCentralDirectory(
            Await(reader.ReadAsync(innerCdOffset, checked((int)innerCdSize), CancellationToken.None)), innerStart);

        foreach (var entry in innerEntries.Take(MaxEntriesToVisit))
        {
            _ = Await(ZipRangeExtractor.ExtractEntryAsync(reader, entry, CancellationToken.None));
        }
    }

    /// <summary>
    /// Exceptions that mean "this archive is not usable" rather than "the parser has a gap".
    /// <para>
    /// <see cref="ArgumentOutOfRangeException"/> is deliberately absent. A range read that runs off
    /// the end of the archive is a legitimate rejection, but <see cref="BoundedRangeReader"/> reports
    /// that as <see cref="InvalidDataException"/>, so anything still surfacing as out-of-range came
    /// from the parser indexing a buffer it already holds — the bug class this target exists to find.
    /// </para>
    /// <para>
    /// <see cref="OutOfMemoryException"/> is absent for the same reason: entry sizes are
    /// attacker-controlled independently of input length, so an allocation blow-up is a finding.
    /// </para>
    /// </summary>
    private static bool IsExpectedArchiveRejection(Exception ex) => ex
        is InvalidDataException          // the parser's own deliberate rejections, and short reads
        or NotSupportedException         // unsupported compression method
        or OverflowException;            // checked cast of an oversized central-directory size

    // The parser is async; libFuzzer targets must be void. These calls never touch I/O because the
    // reader is memory-backed, so blocking cannot deadlock.
    private static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();

    /// <summary>
    /// Targets <see cref="GuestFrameCodec"/>'s decode path — the framing winapp uses for every byte
    /// that crosses the host/guest boundary.
    /// </summary>
    /// <remarks>
    /// The guest command channel treats the connection as untrusted, so this decoder is reached by
    /// arbitrary bytes from the network before anything is authenticated. It reads an
    /// attacker-controlled 32-bit length prefix and slices ciphertext and tag on it, so any
    /// exception escaping here means a missing bounds or length check rather than a rejection —
    /// which is precisely what this target exists to surface.
    /// <para>
    /// The key is fixed and the input is never expected to authenticate; the value is in the parsing
    /// path, and authentication failure must be reported through <c>GuestFrameError</c> rather than
    /// thrown. Two sequence numbers are exercised so a nonce-construction bug that only appears once
    /// the sequence exceeds a single byte cannot hide.
    /// </para>
    /// </remarks>
    public static void FuzzGuestFrame(ReadOnlySpan<byte> input)
    {
        Span<byte> key = stackalloc byte[GuestFrameCodec.KeySize];
        Span<byte> noncePrefix = stackalloc byte[GuestFrameCodec.NoncePrefixSize];

        using var codec = new GuestFrameCodec(key, noncePrefix);

        // The decoder must never write more plaintext than the frame could possibly carry.
        var destination = new byte[Math.Max(1, input.Length)];

        _ = GuestFrameCodec.TryReadBodyLength(input, out _, out _);
        _ = codec.TryDecode(input, sequence: 0, destination, out _, out _);
        _ = codec.TryDecode(input, sequence: ulong.MaxValue, destination, out _, out _);
    }
}

/// <summary>
/// Wraps <see cref="MemoryRangeReader"/> so that asking for bytes outside the archive reads as a
/// rejected archive instead of an out-of-range access.
/// </summary>
/// <remarks>
/// Without this the harness cannot tell "the parser asked for a range past EOF", which is a normal
/// consequence of malformed input, apart from "the parser indexed past a buffer it already holds",
/// which is a bounds bug. Collapsing both into <see cref="ArgumentOutOfRangeException"/> is what
/// previously made this target blind to the latter.
/// </remarks>
internal sealed class BoundedRangeReader(MemoryRangeReader inner) : IRangeReader
{
    public Task<long> GetLengthAsync(CancellationToken cancellationToken) => inner.GetLengthAsync(cancellationToken);

    public Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken)
    {
        try
        {
            return inner.ReadAsync(offset, length, cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException($"Range {offset}..{offset + length} is outside the archive.", ex);
        }
    }
}
