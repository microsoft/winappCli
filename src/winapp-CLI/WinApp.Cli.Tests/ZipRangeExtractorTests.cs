// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ZipRangeExtractorTests
{
    private const string InnerMsixName = "windbg_win-arm64.msix";
    private const string JsProviderPath = "arm64/winext/JsProvider.dll";

    // A fake PE payload (starts with the MZ signature) that compresses well via deflate.
    private static byte[] FakeJsProvider()
    {
        var payload = new byte[4096];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        for (var i = 2; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 7);
        }

        return payload;
    }

    /// <summary>Builds a ZIP containing the supplied entries (forward-slash names).</summary>
    private static byte[] BuildZip(IEnumerable<(string Name, byte[] Data, CompressionLevel Level)> entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, level) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Builds an outer bundle ZIP that STORES an inner msix ZIP containing JsProvider.dll.</summary>
    private static (byte[] Bundle, byte[] ExpectedJsProvider) BuildNestedBundle(string jsProviderPath = JsProviderPath)
    {
        var js = FakeJsProvider();
        var innerMsix = BuildZip(
        [
            ("AppxManifest.xml", Encoding.UTF8.GetBytes("<manifest/>"), CompressionLevel.Optimal),
            (jsProviderPath, js, CompressionLevel.Optimal),
            ("arm64/winext/chakra/JsProvider.dll", Encoding.UTF8.GetBytes("MZchakra"), CompressionLevel.Optimal),
        ]);

        var bundle = BuildZip(
        [
            ("AppxMetadata/AppxBundleManifest.xml", Encoding.UTF8.GetBytes("<bundle/>"), CompressionLevel.Optimal),
            // Inner msix packages are STORED in the real bundle, so the inner archive is contiguous.
            (InnerMsixName, innerMsix, CompressionLevel.NoCompression),
            ("windbg_win-x64.msix", Encoding.UTF8.GetBytes("not a real zip"), CompressionLevel.NoCompression),
        ]);

        return (bundle, js);
    }

    [TestMethod]
    public async Task ExtractJsProvider_NestedBundle_ReturnsPeBytes()
    {
        var (bundle, expected) = BuildNestedBundle();
        var reader = new MemoryRangeReader(bundle);

        var bytes = await WinDbgJsProviderAcquirer.ExtractJsProviderAsync(reader, InnerMsixName, "arm64", CancellationToken.None);

        Assert.IsNotNull(bytes, "JsProvider.dll should be extracted from the nested bundle.");
        Assert.AreSequenceEqual(expected, bytes, "Extracted bytes must match the original (deflate round-trip).");
    }

    [TestMethod]
    public async Task ExtractJsProvider_MissingInnerMsix_ReturnsNull()
    {
        var (bundle, _) = BuildNestedBundle();
        var reader = new MemoryRangeReader(bundle);

        var bytes = await WinDbgJsProviderAcquirer.ExtractJsProviderAsync(reader, "windbg_win-does-not-exist.msix", "arm64", CancellationToken.None);

        Assert.IsNull(bytes, "A missing inner msix must yield null, not throw.");
    }

    [TestMethod]
    public async Task ExtractJsProvider_MissingFileInInner_ReturnsNull()
    {
        var (bundle, _) = BuildNestedBundle();
        var reader = new MemoryRangeReader(bundle);

        // The inner msix exists but contains no amd64/winext/JsProvider.dll.
        var bytes = await WinDbgJsProviderAcquirer.ExtractJsProviderAsync(reader, InnerMsixName, "amd64", CancellationToken.None);

        Assert.IsNull(bytes, "A missing JsProvider path must yield null, not throw.");
    }

    [TestMethod]
    public async Task ExtractEntry_StoredAndDeflate_RoundTrip()
    {
        var stored = Encoding.UTF8.GetBytes("stored-payload-exactly");
        var deflated = FakeJsProvider();
        var zip = BuildZip(
        [
            ("stored.bin", stored, CompressionLevel.NoCompression),
            ("deflated.bin", deflated, CompressionLevel.Optimal),
        ]);
        var reader = new MemoryRangeReader(zip);

        var (cdOffset, cdSize) = await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, zip.Length, CancellationToken.None);
        var entries = ZipRangeExtractor.ParseCentralDirectory(
            await reader.ReadAsync(cdOffset, (int)cdSize, CancellationToken.None), 0);

        var storedEntry = entries.Single(e => e.Name == "stored.bin");
        var deflatedEntry = entries.Single(e => e.Name == "deflated.bin");
        Assert.AreEqual(0, storedEntry.Method, "NoCompression must produce a STORED entry.");
        Assert.AreEqual(8, deflatedEntry.Method, "Optimal must produce a DEFLATE entry.");

        Assert.AreSequenceEqual(stored, await ZipRangeExtractor.ExtractEntryAsync(reader, storedEntry, CancellationToken.None));
        Assert.AreSequenceEqual(deflated, await ZipRangeExtractor.ExtractEntryAsync(reader, deflatedEntry, CancellationToken.None));
    }

    [TestMethod]
    public async Task FindCentralDirectory_Zip64Eocd_ResolvesOffsetAndSize()
    {
        // Hand-build a ZIP64 tail: [cd][zip64 eocd][zip64 locator][eocd], because
        // System.IO.Compression only emits ZIP64 for archives too large to construct in a unit test.
        const long cdOffset = 0;
        var directory = MinimalCentralDirectory();
        long cdSize = directory.Length;
        var buf = new List<byte>();

        buf.AddRange(directory);
        var recordRelative = buf.Count;                       // offset of the ZIP64 EOCD record

        var record = new byte[56];
        WriteU32(record, 0, 0x06064b50);                      // ZIP64 EOCD signature
        WriteU64(record, 40, unchecked((ulong)cdSize));       // size of the central directory
        WriteU64(record, 48, unchecked((ulong)cdOffset));     // offset of the central directory
        buf.AddRange(record);

        var locator = new byte[20];
        WriteU32(locator, 0, 0x07064b50);                     // ZIP64 locator signature
        WriteU64(locator, 8, (ulong)recordRelative);          // relative offset of the ZIP64 EOCD record
        buf.AddRange(locator);

        var eocd = new byte[22];
        WriteU32(eocd, 0, 0x06054b50);                        // EOCD signature
        WriteU16(eocd, 10, 0xFFFF);                           // entry count → ZIP64 marker
        WriteU32(eocd, 12, 0xFFFFFFFF);                       // cd size → ZIP64 marker
        WriteU32(eocd, 16, 0xFFFFFFFF);                       // cd offset → ZIP64 marker
        buf.AddRange(eocd);

        var data = buf.ToArray();
        var reader = new MemoryRangeReader(data);

        var (offset, size) = await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, data.Length, CancellationToken.None);

        Assert.AreEqual(cdOffset, offset, "ZIP64 EOCD central-directory offset must be honored.");
        Assert.AreEqual(cdSize, size, "ZIP64 EOCD central-directory size must be honored.");
    }

    [TestMethod]
    [DataRow(28, DisplayName = "oversized name length")]
    [DataRow(30, DisplayName = "oversized extra length")]
    [DataRow(32, DisplayName = "oversized comment length")]
    public void ParseCentralDirectory_LengthsPastEndOfDirectory_ThrowsInvalidData(int lengthFieldOffset)
    {
        // Found by the OneFuzz harness: these three 16-bit lengths are attacker-controlled and were
        // sliced on without validation, surfacing as ArgumentOutOfRangeException rather than a
        // rejected archive.
        var header = new byte[46];
        WriteU32(header, 0, 0x02014b50);
        WriteU16(header, lengthFieldOffset, 0xFFFF);

        var ex = Assert.ThrowsExactly<InvalidDataException>(
            () => ZipRangeExtractor.ParseCentralDirectory(header, archiveBase: 0));
        Assert.Contains("extends past", ex.Message);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "signature is the whole tail")]
    [DataRow(8, DisplayName = "signature 8 bytes from the end")]
    [DataRow(17, DisplayName = "one byte short of a full record")]
    public async Task FindCentralDirectory_TruncatedEocdRecord_ThrowsInvalidData(int trailingBytes)
    {
        // The EOCD signature can sit within the final 21 bytes, leaving the fixed fields past the end
        // of the buffer. Reading them unguarded is a bounds gap, not a rejection.
        var archive = new byte[64 + trailingBytes];
        WriteU32(archive, 64 - 4, 0x06054b50);

        var reader = new MemoryRangeReader(archive);

        var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, archive.Length, CancellationToken.None));
        Assert.Contains("not found", ex.Message);
    }

    [TestMethod]
    public async Task FindCentralDirectory_ArchiveCommentContainingEocdSignature_StillParses()
    {
        // A ZIP comment may legally contain PK\x05\x06. Taking the *last* signature match lands
        // inside the comment rather than on the real record.
        var archive = BuildZip([("a.bin", Encoding.UTF8.GetBytes("stored"), CompressionLevel.NoCompression)]);
        var comment = new byte[] { 0x50, 0x4b, 0x05, 0x06 };

        var withComment = new byte[archive.Length + comment.Length];
        Array.Copy(archive, withComment, archive.Length);
        Array.Copy(comment, 0, withComment, archive.Length, comment.Length);

        // The EOCD comment-length field is the last two bytes of the fixed record.
        WriteU16(withComment, archive.Length - 2, (ushort)comment.Length);

        var reader = new MemoryRangeReader(withComment);
        var (offset, size) = await ZipRangeExtractor.FindCentralDirectoryAsync(
            reader, 0, withComment.Length, CancellationToken.None);

        var entries = ZipRangeExtractor.ParseCentralDirectory(
            await reader.ReadAsync(offset, (int)size, CancellationToken.None), 0);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("a.bin", entries[0].Name);
    }

    [TestMethod]
    [DataRow((ushort)0, 0u, 0u, DisplayName = "zeroed fake record")]
    [DataRow((ushort)1, 1u, 0u, DisplayName = "fake record pointing at offset zero")]
    [DataRow((ushort)1, 64u, 8u, DisplayName = "fake record pointing into file data")]
    [DataRow((ushort)1, 4u, 0u, DisplayName = "fake record with a header-sized truncated directory")]
    [DataRow((ushort)0xFFFF, 0xFFFFFFFFu, 0xFFFFFFFFu, DisplayName = "fake record claiming ZIP64")]
    public async Task FindCentralDirectory_FakeEocdInComment_StillFindsTheRealDirectory(
        ushort fakeCount, uint fakeSize, uint fakeOffset)
    {
        // A whole EOCD hidden in the comment satisfies the comment-length rule, so a scan that trusts
        // the last match returns it and the archive parses as empty or from attacker-chosen offsets.
        var archive = BuildZip([("a.bin", Encoding.UTF8.GetBytes("stored"), CompressionLevel.NoCompression)]);

        var fake = new byte[22];
        WriteU32(fake, 0, 0x06054b50);
        WriteU16(fake, 10, fakeCount);
        WriteU32(fake, 12, fakeSize);
        WriteU32(fake, 16, fakeOffset);

        var withComment = new byte[archive.Length + fake.Length];
        Array.Copy(archive, withComment, archive.Length);
        Array.Copy(fake, 0, withComment, archive.Length, fake.Length);
        WriteU16(withComment, archive.Length - 2, (ushort)fake.Length);

        var reader = new MemoryRangeReader(withComment);
        var (offset, size) = await ZipRangeExtractor.FindCentralDirectoryAsync(
            reader, 0, withComment.Length, CancellationToken.None);

        var entries = ZipRangeExtractor.ParseCentralDirectory(
            await reader.ReadAsync(offset, (int)size, CancellationToken.None), 0);
        Assert.AreEqual(1, entries.Count, "The real directory should win over a record planted in the comment.");
        Assert.AreEqual("a.bin", entries[0].Name);
    }

    [TestMethod]
    public async Task MemoryRangeReader_OffsetNearLongMaxValue_ThrowsWithoutOverflowing()
    {
        // offset + length overflows to a negative number, which would slip past a naive guard.
        var reader = new MemoryRangeReader(new byte[16]);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await reader.ReadAsync(long.MaxValue - 1, 8, CancellationToken.None));
    }

    [TestMethod]
    public void ParseCentralDirectory_Zip64ExtraField_Applies64BitValues()
    {
        const long compressed = 0x1_0000_0001;   // > 4 GiB, forcing the 0xFFFFFFFF marker
        const long uncompressed = 0x2_0000_0002;
        const long localOffset = 0x3_0000_0003;
        var name = Encoding.UTF8.GetBytes("big.bin");

        // ZIP64 extra: id 0x0001, dataLen 24, then uncompressed, compressed, localOffset (the fixed
        // order the parser walks, only for fields whose 32-bit slot was 0xFFFFFFFF).
        var extra = new byte[4 + 24];
        WriteU16(extra, 0, 0x0001);
        WriteU16(extra, 2, 24);
        WriteU64(extra, 4, unchecked((ulong)uncompressed));
        WriteU64(extra, 12, unchecked((ulong)compressed));
        WriteU64(extra, 20, unchecked((ulong)localOffset));

        var header = new byte[46 + name.Length + extra.Length];
        WriteU32(header, 0, 0x02014b50);          // central header signature
        WriteU16(header, 10, 0);                  // method: stored
        WriteU32(header, 20, 0xFFFFFFFF);         // compressed → ZIP64 marker
        WriteU32(header, 24, 0xFFFFFFFF);         // uncompressed → ZIP64 marker
        WriteU16(header, 28, (ushort)name.Length);
        WriteU16(header, 30, (ushort)extra.Length);
        WriteU16(header, 32, 0);                  // comment length
        WriteU32(header, 42, 0xFFFFFFFF);         // local-header offset → ZIP64 marker
        name.CopyTo(header, 46);
        extra.CopyTo(header, 46 + name.Length);

        var entries = ZipRangeExtractor.ParseCentralDirectory(header, archiveBase: 1000);

        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual("big.bin", entry.Name);
        Assert.AreEqual(compressed, entry.CompressedSize, "64-bit compressed size must come from the ZIP64 extra field.");
        Assert.AreEqual(uncompressed, entry.UncompressedSize, "64-bit uncompressed size must come from the ZIP64 extra field.");
        Assert.AreEqual(1000 + localOffset, entry.LocalHeaderOffset, "64-bit local-header offset must be applied then rebased.");
    }

    [TestMethod]
    public async Task FindCentralDirectory_CommentPackedWithCandidates_DoesNotProbeUnbounded()
    {
        // A 64 KiB comment can hold thousands of EOCD-shaped candidates. Each one that points outside
        // the tail costs a round trip on the HTTP reader, so the scan has to stop probing.
        const int total = 200_000; // larger than the tail window, so candidate heads fall outside it
        var data = new byte[total];

        // Fill the tail with back-to-back candidates whose comment lengths all run to the end. Their
        // directories start at offset 0 - outside the tail - so confirming each one costs a real read.
        for (var pos = total - 22; pos >= total - 20_000; pos -= 22)
        {
            WriteU32(data, pos, 0x06054b50);
            WriteU16(data, pos + 10, 1);
            WriteU32(data, pos + 12, (uint)pos);   // size: directory spans from 0 up to this record
            WriteU32(data, pos + 16, 0);           // offset: front of the archive, outside the tail
            WriteU16(data, pos + 20, (ushort)(total - pos - 22));
        }

        var reader = new CountingRangeReader(data);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, data.Length, CancellationToken.None));

        // One tail read plus a bounded number of candidate probes.
        Assert.IsTrue(reader.Reads <= 12, $"Expected the scan to stop probing, but it issued {reader.Reads} reads.");
    }

    [TestMethod]
    public async Task FindCentralDirectory_CommentFullOfZip64ShapedRecords_StillParses()
    {
        // These resolve nothing and cost no reads, so they must not consume the probe budget - doing so
        // would let comment bytes alone push the scan past the real record.
        var archive = BuildZip([("a.bin", Encoding.UTF8.GetBytes("stored"), CompressionLevel.NoCompression)]);

        const int fakes = 12;
        var comment = new byte[fakes * 22];
        var data = new byte[archive.Length + comment.Length];
        archive.CopyTo(data, 0);
        WriteU16(data, archive.Length - 2, (ushort)comment.Length);

        for (var n = 0; n < fakes; n++)
        {
            var pos = archive.Length + (n * 22);
            WriteU32(data, pos, 0x06054b50);
            WriteU16(data, pos + 10, 0xFFFF);       // ZIP64 markers, with no locator anywhere
            WriteU32(data, pos + 12, 0xFFFFFFFF);
            WriteU32(data, pos + 16, 0xFFFFFFFF);
            WriteU16(data, pos + 20, (ushort)(data.Length - pos - 22));
        }

        var reader = new MemoryRangeReader(data);
        var (offset, size) = await ZipRangeExtractor.FindCentralDirectoryAsync(
            reader, 0, data.Length, CancellationToken.None);

        var entries = ZipRangeExtractor.ParseCentralDirectory(
            await reader.ReadAsync(offset, (int)size, CancellationToken.None), 0);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("a.bin", entries[0].Name);
    }

    private sealed class CountingRangeReader(byte[] data) : IRangeReader
    {
        private readonly MemoryRangeReader _inner = new(data);

        public int Reads { get; private set; }

        public Task<long> GetLengthAsync(CancellationToken cancellationToken) => _inner.GetLengthAsync(cancellationToken);

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken)
        {
            Reads++;
            return _inner.ReadAsync(offset, length, cancellationToken);
        }
    }

    /// <summary>One stored, empty entry: the smallest thing that is recognisably a central directory.</summary>
    private static byte[] MinimalCentralDirectory()
    {
        var name = Encoding.UTF8.GetBytes("a.bin");
        var header = new byte[46 + name.Length];
        WriteU32(header, 0, 0x02014b50);
        WriteU16(header, 28, (ushort)name.Length);
        name.CopyTo(header, 46);
        return header;
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteU32(byte[] buffer, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteU64(byte[] buffer, int offset, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), value);

    [TestMethod]
    public void HostTokens_KnownArchitectures_Map()
    {
        Assert.AreEqual(("windbg_win-x64.msix", "amd64"), WinDbgJsProviderAcquirer.HostTokens(Architecture.X64));
        Assert.AreEqual(("windbg_win-arm64.msix", "arm64"), WinDbgJsProviderAcquirer.HostTokens(Architecture.Arm64));
        Assert.AreEqual(("windbg_win-x86.msix", "x86"), WinDbgJsProviderAcquirer.HostTokens(Architecture.X86));
    }

    [TestMethod]
    public void HostTokens_UnsupportedArchitecture_ReturnsNulls()
    {
        var (msix, prefix) = WinDbgJsProviderAcquirer.HostTokens(Architecture.Wasm);
        Assert.IsNull(msix);
        Assert.IsNull(prefix);
    }

    [TestMethod]
    public async Task FindCentralDirectory_NoEocdSignature_Throws()
    {
        // Random bytes with no EOCD record — LastIndexOfSignature returns -1.
        var data = new byte[200];
        Array.Fill(data, (byte)0xAB);
        var reader = new MemoryRangeReader(data);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, data.Length, CancellationToken.None));
    }

    [TestMethod]
    public async Task FindCentralDirectory_Zip64MarkerButNoLocator_Throws()
    {
        // EOCD advertises ZIP64 (markers) but there is no ZIP64 locator ahead of it.
        var data = new byte[122];
        var eocd = new byte[22];
        WriteU32(eocd, 0, 0x06054b50);
        WriteU16(eocd, 10, 0xFFFF);
        WriteU32(eocd, 12, 0xFFFFFFFF);
        WriteU32(eocd, 16, 0xFFFFFFFF);
        eocd.CopyTo(data, 100);
        var reader = new MemoryRangeReader(data);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, data.Length, CancellationToken.None));
    }

    [TestMethod]
    public async Task FindCentralDirectory_Zip64RecordSignatureMismatch_Throws()
    {
        var buf = new List<byte>();
        buf.AddRange(new byte[10]);                            // central-directory placeholder

        var record = new byte[56];
        WriteU32(record, 0, 0xDEADBEEF);                      // WRONG ZIP64 EOCD signature
        buf.AddRange(record);

        var locator = new byte[20];
        WriteU32(locator, 0, 0x07064b50);
        WriteU64(locator, 8, 10);                             // record is at offset 10
        buf.AddRange(locator);

        var eocd = new byte[22];
        WriteU32(eocd, 0, 0x06054b50);
        WriteU16(eocd, 10, 0xFFFF);
        WriteU32(eocd, 12, 0xFFFFFFFF);
        WriteU32(eocd, 16, 0xFFFFFFFF);
        buf.AddRange(eocd);

        var reader = new MemoryRangeReader(buf.ToArray());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, buf.Count, CancellationToken.None));
    }

    [TestMethod]
    public async Task FindCentralDirectory_Zip64RecordOutsideTailWindow_ReadsViaRanged()
    {
        // Archive larger than the EOCD search window, with the ZIP64 EOCD record right after the
        // directory so it falls outside the trailing tail read and must be fetched with a ranged read.
        const long cdOffset = 0;
        var directory = MinimalCentralDirectory();
        long cdSize = directory.Length;
        const int total = 66000; // > MaxEocdSearch (65557)
        var data = new byte[total];
        directory.CopyTo(data, (int)cdOffset);

        var record = new byte[56];
        WriteU32(record, 0, 0x06064b50);
        WriteU64(record, 40, unchecked((ulong)cdSize));
        WriteU64(record, 48, unchecked((ulong)cdOffset));
        record.CopyTo(data, (int)cdSize); // record follows the directory, outside the tail window

        var locatorOffset = total - 22 - 20;
        var locator = new byte[20];
        WriteU32(locator, 0, 0x07064b50);
        WriteU64(locator, 8, (ulong)cdSize); // relative offset of the ZIP64 EOCD record
        locator.CopyTo(data, locatorOffset);

        var eocd = new byte[22];
        WriteU32(eocd, 0, 0x06054b50);
        WriteU16(eocd, 10, 0xFFFF);
        WriteU32(eocd, 12, 0xFFFFFFFF);
        WriteU32(eocd, 16, 0xFFFFFFFF);
        eocd.CopyTo(data, total - 22);

        var reader = new MemoryRangeReader(data);

        var (offset, size) = await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, data.Length, CancellationToken.None);

        Assert.AreEqual(cdOffset, offset);
        Assert.AreEqual(cdSize, size);
    }

    [TestMethod]
    public void ParseCentralDirectory_SkipsNonZip64ExtraField_ThenAppliesZip64()
    {
        // The first extra field has a non-ZIP64 id and must be skipped before the ZIP64 field applies.
        const long uncompressed = 0x2_0000_0002;
        var name = Encoding.UTF8.GetBytes("skip.bin");

        var extra = new byte[(4 + 4) + (4 + 8)];
        WriteU16(extra, 0, 0x000A);   // non-ZIP64 extra id
        WriteU16(extra, 2, 4);        // its dataLen (4 bytes of payload, left zero)
        WriteU16(extra, 8, 0x0001);   // ZIP64 extra id
        WriteU16(extra, 10, 8);       // dataLen: only the uncompressed 64-bit value follows
        WriteU64(extra, 12, unchecked((ulong)uncompressed));

        var header = new byte[46 + name.Length + extra.Length];
        WriteU32(header, 0, 0x02014b50);
        WriteU16(header, 10, 0);                  // method: stored
        WriteU32(header, 20, 0x11111111);         // compressed: NOT a marker → left unchanged
        WriteU32(header, 24, 0xFFFFFFFF);         // uncompressed: marker → taken from ZIP64 extra
        WriteU16(header, 28, (ushort)name.Length);
        WriteU16(header, 30, (ushort)extra.Length);
        WriteU16(header, 32, 0);
        WriteU32(header, 42, 0x22222222);         // local-header offset: NOT a marker
        name.CopyTo(header, 46);
        extra.CopyTo(header, 46 + name.Length);

        var entries = ZipRangeExtractor.ParseCentralDirectory(header, archiveBase: 0);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(uncompressed, entries[0].UncompressedSize, "uncompressed size must come from the ZIP64 field after skipping the other one");
        Assert.AreEqual(0x11111111, entries[0].CompressedSize, "non-marker compressed size stays as the 32-bit value");
    }

    [TestMethod]
    public async Task ExtractEntry_CompressedSizeExceedsInt32_Throws()
    {
        var entry = new ZipEntry("too-big.bin", Method: 0, CompressedSize: (long)int.MaxValue + 1,
            UncompressedSize: 0, LocalHeaderOffset: 0);
        var reader = new MemoryRangeReader(new byte[16]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ZipRangeExtractor.ExtractEntryAsync(reader, entry, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExtractEntry_UnsupportedCompressionMethod_Throws()
    {
        // 30-byte local header (nameLen/extraLen = 0) followed by 4 bytes of data.
        var buf = new byte[34];
        var entry = new ZipEntry("weird.bin", Method: 99, CompressedSize: 4, UncompressedSize: 4, LocalHeaderOffset: 0);
        var reader = new MemoryRangeReader(buf);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
            await ZipRangeExtractor.ExtractEntryAsync(reader, entry, CancellationToken.None));
    }

    [TestMethod]
    public async Task MemoryRangeReader_RangeBeyondBuffer_Throws()
    {
        var reader = new MemoryRangeReader(new byte[10]);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await reader.ReadAsync(5, 10, CancellationToken.None));
    }

    [TestMethod]
    public async Task MemoryRangeReader_NegativeOffset_Throws()
    {
        var reader = new MemoryRangeReader(new byte[10]);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await reader.ReadAsync(-1, 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task MemoryRangeReader_GetLength_ReturnsBufferLength()
    {
        var reader = new MemoryRangeReader(new byte[42]);
        Assert.AreEqual(42L, await reader.GetLengthAsync(CancellationToken.None));
    }
}
