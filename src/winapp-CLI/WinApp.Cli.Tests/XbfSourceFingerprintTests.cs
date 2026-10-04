// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class XbfSourceFingerprintTests
{
    [TestMethod]
    public void CapturedWinUi21Header_ReadsTheActualMetadataOffsets()
    {
        var bytes = new byte[15384];
        // Captured header only; the remaining bytes are not an executable XBF fixture.
        Convert.FromHexString(
            "58424600FA1500001226000002000000010000007800000000000000EE14000000000000" +
            "F214000000000000F6140000000000003615000000000000E215000000000000" +
            "41363335363436414138413936454338334238453743444137373846304241313137393636343833413344363046324446323835334344333037353533373937")
            .CopyTo(bytes, 0);
        Assert.AreEqual("A635646AA8A96EC83B8E7CDA778F0BA117966483A3D60F2DF2853CD307553797",
            XbfSourceFingerprint.Read(bytes));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void SupportedHeader_ContainsOriginalRawBytesHash(int minor)
    {
        byte[] source = [0xEF, 0xBB, 0xBF, .. "<Page />\r\n"u8.ToArray()];
        var xbf = XamlCoordinateMapTests.FingerprintedXbf(source);
        BinaryPrimitives.WriteUInt32LittleEndian(xbf.AsSpan(16), (uint)minor);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(source)), XbfSourceFingerprint.Read(xbf));
        XbfSourceFingerprint.Verify(source, xbf);
        Assert.ThrowsExactly<InvalidDataException>(() => XbfSourceFingerprint.Verify("<Page />\n"u8, xbf));
    }

    [TestMethod]
    [DataRow("magic")]
    [DataRow("version")]
    [DataRow("minor")]
    [DataRow("length")]
    [DataRow("offset")]
    [DataRow("reversed-offset")]
    [DataRow("missing-hash")]
    [DataRow("zero-hash")]
    [DataRow("nonhex")]
    [DataRow("truncated")]
    public void InvalidHeaderFailsClosed(string mode)
    {
        var xbf = XamlCoordinateMapTests.FingerprintedXbf("<Page />"u8.ToArray());
        switch (mode)
        {
            case "magic": xbf[0] = 0; break;
            case "version": xbf[12] = 3; break;
            case "minor": xbf[16] = 2; break;
            case "length": xbf[4]++; break;
            case "offset": xbf[20]++; break;
            case "reversed-offset": xbf[28] = 119; break;
            case "missing-hash": xbf.AsSpan(68, 64).Clear(); break;
            case "zero-hash": xbf.AsSpan(68, 64).Fill((byte)'0'); break;
            case "nonhex": xbf[68] = (byte)'X'; break;
            case "truncated": xbf = xbf[..131]; break;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => XbfSourceFingerprint.Read(xbf));
    }
}
