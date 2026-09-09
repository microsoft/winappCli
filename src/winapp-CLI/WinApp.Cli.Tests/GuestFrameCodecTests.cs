// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="GuestFrameCodec"/>, the authenticated framing every host/guest byte passes
/// through. The connection is treated as untrusted, so these tests focus on what the decoder does
/// with hostile input as much as on the happy path.
/// </summary>
[TestClass]
public class GuestFrameCodecTests
{
    private static (byte[] Key, byte[] NoncePrefix) NewMaterial(byte seed = 1)
    {
        var key = new byte[GuestFrameCodec.KeySize];
        var prefix = new byte[GuestFrameCodec.NoncePrefixSize];
        Array.Fill(key, seed);
        Array.Fill(prefix, seed);
        return (key, prefix);
    }

    private static GuestFrameCodec NewCodec(byte seed = 1)
    {
        var (key, prefix) = NewMaterial(seed);
        return new GuestFrameCodec(key, prefix);
    }

    private static byte[] Encode(GuestFrameCodec codec, byte[] plaintext, ulong sequence)
    {
        var frame = new byte[GuestFrameCodec.GetEncodedSize(plaintext.Length)];
        var written = codec.Encode(plaintext, sequence, frame);
        Assert.AreEqual(frame.Length, written);
        return frame;
    }

    [TestMethod]
    public void RoundTrip_PreservesPayload()
    {
        using var codec = NewCodec();
        var plaintext = Encoding.UTF8.GetBytes("hello guest \u2014 unicode \U0001F600");

        var frame = Encode(codec, plaintext, sequence: 7);

        var destination = new byte[plaintext.Length];
        Assert.IsTrue(codec.TryDecode(frame, 7, destination, out var written, out var error));
        Assert.AreEqual(GuestFrameError.None, error);
        CollectionAssert.AreEqual(plaintext, destination[..written]);
    }

    [TestMethod]
    public void RoundTrip_EmptyPayload_IsValid()
    {
        using var codec = NewCodec();

        var frame = Encode(codec, [], sequence: 0);

        Assert.AreEqual(GuestFrameCodec.MinimumEncodedSize, frame.Length);
        Assert.IsTrue(codec.TryDecode(frame, 0, [], out var written, out _));
        Assert.AreEqual(0, written);
    }

    [TestMethod]
    public void Decode_WrongSequence_FailsAuthentication()
    {
        using var codec = NewCodec();
        var frame = Encode(codec, [1, 2, 3], sequence: 5);

        // The sequence is authenticated but never transmitted, so replaying a frame at a different
        // position cannot authenticate. This is what makes replay and reordering impossible rather
        // than something the parser must detect.
        var destination = new byte[3];
        Assert.IsFalse(codec.TryDecode(frame, 6, destination, out _, out var error));
        Assert.AreEqual(GuestFrameError.AuthenticationFailed, error);
    }

    [TestMethod]
    public void Decode_ReplayedFrame_FailsAtTheNextPosition()
    {
        using var codec = NewCodec();
        var first = Encode(codec, [9], sequence: 0);

        var destination = new byte[1];
        Assert.IsTrue(codec.TryDecode(first, 0, destination, out _, out _));
        Assert.IsFalse(codec.TryDecode(first, 1, destination, out _, out var error), "A replayed frame must not authenticate.");
        Assert.AreEqual(GuestFrameError.AuthenticationFailed, error);
    }

    [TestMethod]
    public void Decode_DifferentKey_FailsAuthentication()
    {
        using var sender = NewCodec(seed: 1);
        using var receiver = NewCodec(seed: 2);

        var frame = Encode(sender, [4, 5, 6], sequence: 0);

        Assert.IsFalse(receiver.TryDecode(frame, 0, new byte[3], out _, out var error));
        Assert.AreEqual(GuestFrameError.AuthenticationFailed, error);
    }

    [TestMethod]
    public void Decode_TamperedCiphertext_FailsAuthentication()
    {
        using var codec = NewCodec();
        var frame = Encode(codec, [1, 2, 3, 4], sequence: 0);
        frame[GuestFrameCodec.LengthPrefixSize] ^= 0xFF;

        Assert.IsFalse(codec.TryDecode(frame, 0, new byte[4], out _, out var error));
        Assert.AreEqual(GuestFrameError.AuthenticationFailed, error);
    }

    [TestMethod]
    public void Decode_TamperedLengthPrefix_IsRejected()
    {
        using var codec = NewCodec();
        var frame = Encode(codec, [1, 2, 3, 4], sequence: 0);

        // Shrink the declared body so the frame claims less than it carries. The length is
        // authenticated as associated data, so this cannot pass even if the sizes lined up.
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)(frame.Length - GuestFrameCodec.LengthPrefixSize - 1));

        Assert.IsFalse(codec.TryDecode(frame, 0, new byte[4], out _, out var error));
        Assert.AreEqual(GuestFrameError.LengthMismatch, error);
    }

    [TestMethod]
    public void TryReadBodyLength_RejectsOversizedDeclaration()
    {
        var header = new byte[GuestFrameCodec.LengthPrefixSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, uint.MaxValue);

        Assert.IsFalse(GuestFrameCodec.TryReadBodyLength(header, out _, out var error));
        Assert.AreEqual(GuestFrameError.TooLong, error);
    }

    [TestMethod]
    public void TryReadBodyLength_RejectsBodyShorterThanATag()
    {
        var header = new byte[GuestFrameCodec.LengthPrefixSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, GuestFrameCodec.TagSize - 1u);

        Assert.IsFalse(GuestFrameCodec.TryReadBodyLength(header, out _, out var error));
        Assert.AreEqual(GuestFrameError.TooShort, error);
    }

    [TestMethod]
    public void TryReadBodyLength_RejectsTruncatedHeader()
    {
        Assert.IsFalse(GuestFrameCodec.TryReadBodyLength([1, 2], out _, out var error));
        Assert.AreEqual(GuestFrameError.TooShort, error);
    }

    [TestMethod]
    public void Decode_DestinationTooSmall_IsReportedNotThrown()
    {
        using var codec = NewCodec();
        var frame = Encode(codec, [1, 2, 3, 4], sequence: 0);

        Assert.IsFalse(codec.TryDecode(frame, 0, new byte[2], out _, out var error));
        Assert.AreEqual(GuestFrameError.DestinationTooSmall, error);
    }

    [TestMethod]
    public void Encode_OversizedPlaintext_IsRejected()
    {
        using var codec = NewCodec();
        var oversized = new byte[GuestFrameCodec.MaxPlaintextBytes + 1];

        Assert.ThrowsExactly<ArgumentException>(
            () => codec.Encode(oversized, 0, new byte[GuestFrameCodec.GetEncodedSize(oversized.Length)]));
    }

    [TestMethod]
    public void Constructor_RejectsWrongSizedMaterial()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GuestFrameCodec(new byte[16], new byte[4]));
        Assert.ThrowsExactly<ArgumentException>(() => new GuestFrameCodec(new byte[32], new byte[8]));
    }

    [TestMethod]
    public void Decode_ArbitraryBytes_NeverThrows()
    {
        // Mirrors the OneFuzz target: this decoder is reached by unauthenticated network bytes, so
        // malformed input must be reported through GuestFrameError, never thrown.
        using var codec = NewCodec();
        var random = new Random(20260820);

        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var input = new byte[random.Next(0, 96)];
            random.NextBytes(input);

            var destination = new byte[Math.Max(1, input.Length)];
            _ = GuestFrameCodec.TryReadBodyLength(input, out _, out _);
            _ = codec.TryDecode(input, (ulong)iteration, destination, out _, out _);
        }
    }

    [TestMethod]
    public void Decode_MutatedValidFrames_NeverThrowsAndNeverFalselyAuthenticates()
    {
        using var codec = NewCodec();
        var plaintext = Encoding.UTF8.GetBytes("operation payload");
        var pristine = Encode(codec, plaintext, sequence: 3);
        var random = new Random(20260821);

        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var mutated = (byte[])pristine.Clone();
            mutated[random.Next(mutated.Length)] ^= (byte)(1 << random.Next(8));

            var destination = new byte[plaintext.Length];
            var decoded = codec.TryDecode(mutated, 3, destination, out var written, out _);

            if (decoded)
            {
                // The only mutation that can still authenticate is one that produced an identical
                // frame, which means the plaintext must be identical too.
                CollectionAssert.AreEqual(plaintext, destination[..written]);
            }
        }
    }

    [TestMethod]
    public void SessionKeys_GiveEachDirectionIndependentMaterial()
    {
        var psk = RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize);
        var hostRandom = RandomNumberGenerator.GetBytes(GuestProtocol.HandshakeRandomSize);
        var guestRandom = RandomNumberGenerator.GetBytes(GuestProtocol.HandshakeRandomSize);

        using var keys = GuestSessionKeys.Derive(psk, hostRandom, guestRandom);

        var frame = new byte[GuestFrameCodec.GetEncodedSize(4)];
        keys.HostToGuest.Encode([1, 2, 3, 4], 0, frame);

        // Reflecting a host frame back at the host must fail: the directions use different keys, so
        // a frame can never be replayed against its own sender.
        Assert.IsFalse(keys.GuestToHost.TryDecode(frame, 0, new byte[4], out _, out var error));
        Assert.AreEqual(GuestFrameError.AuthenticationFailed, error);
    }

    [TestMethod]
    public void SessionKeys_AreDeterministicForTheSameInputs()
    {
        var psk = RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize);
        var hostRandom = RandomNumberGenerator.GetBytes(GuestProtocol.HandshakeRandomSize);
        var guestRandom = RandomNumberGenerator.GetBytes(GuestProtocol.HandshakeRandomSize);

        using var first = GuestSessionKeys.Derive(psk, hostRandom, guestRandom);
        using var second = GuestSessionKeys.Derive(psk, hostRandom, guestRandom);

        var frame = new byte[GuestFrameCodec.GetEncodedSize(3)];
        first.HostToGuest.Encode([7, 8, 9], 42, frame);

        // Both peers derive independently and must agree, otherwise no frame would ever decode.
        Assert.IsTrue(second.HostToGuest.TryDecode(frame, 42, new byte[3], out _, out _));
    }

    [TestMethod]
    public void SessionKeys_DifferPerConnection()
    {
        var psk = RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize);
        var hostRandom = RandomNumberGenerator.GetBytes(GuestProtocol.HandshakeRandomSize);

        using var first = GuestSessionKeys.Derive(psk, hostRandom, RandomNumberGenerator.GetBytes(GuestProtocol.HandshakeRandomSize));
        using var second = GuestSessionKeys.Derive(psk, hostRandom, RandomNumberGenerator.GetBytes(GuestProtocol.HandshakeRandomSize));

        var frame = new byte[GuestFrameCodec.GetEncodedSize(2)];
        first.HostToGuest.Encode([1, 2], 0, frame);

        // A recorded session must not replay against a later connection even though the pre-shared
        // key lives for the whole guest boot.
        Assert.IsFalse(second.HostToGuest.TryDecode(frame, 0, new byte[2], out _, out _));
    }

    [TestMethod]
    public void SessionKeys_RejectWrongSizedPreSharedKey()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GuestSessionKeys.Derive(
            new byte[16],
            new byte[GuestProtocol.HandshakeRandomSize],
            new byte[GuestProtocol.HandshakeRandomSize]));
    }
}
