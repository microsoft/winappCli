// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Why a received frame was rejected. Never surfaced verbatim to users.</summary>
internal enum GuestFrameError
{
    /// <summary>The frame decoded successfully.</summary>
    None = 0,

    /// <summary>Fewer bytes than the smallest possible frame.</summary>
    TooShort,

    /// <summary>The declared length exceeds the negotiated maximum.</summary>
    TooLong,

    /// <summary>The declared length does not match the bytes supplied.</summary>
    LengthMismatch,

    /// <summary>The caller's destination buffer is too small for the plaintext.</summary>
    DestinationTooSmall,

    /// <summary>
    /// Authentication failed. The frame was corrupted, forged, replayed, or produced with a
    /// different key. These are deliberately indistinguishable.
    /// </summary>
    AuthenticationFailed,
}

/// <summary>
/// Encodes and decodes one direction of the authenticated, encrypted guest frame stream
/// (spec §"Transport and command channel", §"Security and trust model").
/// </summary>
/// <remarks>
/// Wire layout is <c>[4-byte big-endian body length][ciphertext][16-byte tag]</c>.
/// <para>
/// The nonce is <em>never transmitted</em>. It is derived as a per-direction 4-byte prefix followed
/// by the big-endian frame sequence number, and the receiver uses the sequence <em>it</em> expects
/// rather than one the peer supplies. That makes replay and reordering structurally impossible
/// instead of something the parser has to detect: a replayed frame simply fails authentication.
/// </para>
/// <para>
/// The length prefix and the sequence are both authenticated as associated data, so an attacker
/// cannot truncate, extend, or reorder frames without failing the tag check.
/// </para>
/// <para>
/// Each direction gets its own key and nonce prefix, so a frame can never be reflected back at its
/// sender and a nonce can never repeat across directions under one key.
/// </para>
/// <para>
/// Decoding is a pure function over untrusted bytes and is fuzzed
/// (<c>FuzzableCode.FuzzGuestFrame</c>): it reports failures through <see cref="GuestFrameError"/>
/// and must never throw for malformed input.
/// </para>
/// </remarks>
internal sealed class GuestFrameCodec : IDisposable
{
    /// <summary>Largest plaintext one frame may carry. Bulk data is chunked below this.</summary>
    internal const int MaxPlaintextBytes = 1024 * 1024;

    /// <summary>Bytes of AES-GCM authentication tag appended to every frame.</summary>
    internal const int TagSize = 16;

    /// <summary>Bytes of big-endian length prefixed to every frame.</summary>
    internal const int LengthPrefixSize = 4;

    /// <summary>Total AES-GCM nonce size.</summary>
    internal const int NonceSize = 12;

    /// <summary>Bytes of per-direction nonce prefix; the remaining 8 carry the sequence.</summary>
    internal const int NoncePrefixSize = 4;

    /// <summary>Required key size. AES-256 keeps one size for every deployment.</summary>
    internal const int KeySize = 32;

    private readonly AesGcm _aes;
    private readonly byte[] _noncePrefix = new byte[NoncePrefixSize];

    /// <summary>Creates a codec for one direction.</summary>
    /// <param name="key">32-byte direction key.</param>
    /// <param name="noncePrefix">4-byte direction nonce prefix.</param>
    public GuestFrameCodec(ReadOnlySpan<byte> key, ReadOnlySpan<byte> noncePrefix)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
        }

        if (noncePrefix.Length != NoncePrefixSize)
        {
            throw new ArgumentException($"Nonce prefix must be {NoncePrefixSize} bytes.", nameof(noncePrefix));
        }

        _aes = new AesGcm(key, TagSize);
        noncePrefix.CopyTo(_noncePrefix);
    }

    /// <summary>Total encoded size, including the length prefix, for a given plaintext size.</summary>
    public static int GetEncodedSize(int plaintextLength) =>
        LengthPrefixSize + plaintextLength + TagSize;

    /// <summary>Smallest possible complete frame: prefix plus tag, carrying empty plaintext.</summary>
    public static int MinimumEncodedSize => LengthPrefixSize + TagSize;

    /// <summary>
    /// Encodes <paramref name="plaintext"/> as a complete frame into <paramref name="destination"/>.
    /// </summary>
    /// <returns>Bytes written.</returns>
    public int Encode(ReadOnlySpan<byte> plaintext, ulong sequence, Span<byte> destination)
    {
        if (plaintext.Length > MaxPlaintextBytes)
        {
            throw new ArgumentException($"Frame plaintext exceeds {MaxPlaintextBytes} bytes.", nameof(plaintext));
        }

        var total = GetEncodedSize(plaintext.Length);
        if (destination.Length < total)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        var bodyLength = plaintext.Length + TagSize;
        BinaryPrimitives.WriteUInt32BigEndian(destination[..LengthPrefixSize], (uint)bodyLength);

        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(sequence, nonce);

        Span<byte> associatedData = stackalloc byte[LengthPrefixSize + sizeof(ulong)];
        BuildAssociatedData((uint)bodyLength, sequence, associatedData);

        var ciphertext = destination.Slice(LengthPrefixSize, plaintext.Length);
        var tag = destination.Slice(LengthPrefixSize + plaintext.Length, TagSize);
        _aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return total;
    }

    /// <summary>
    /// Reads the declared body length from a frame header.
    /// </summary>
    /// <remarks>
    /// Callers read the 4-byte prefix first so they know exactly how many more bytes to await,
    /// which is what stops a hostile peer from making the reader allocate on a bogus length.
    /// </remarks>
    public static bool TryReadBodyLength(ReadOnlySpan<byte> header, out int bodyLength, out GuestFrameError error)
    {
        bodyLength = 0;

        if (header.Length < LengthPrefixSize)
        {
            error = GuestFrameError.TooShort;
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32BigEndian(header[..LengthPrefixSize]);
        if (declared < TagSize)
        {
            error = GuestFrameError.TooShort;
            return false;
        }

        if (declared > (uint)(MaxPlaintextBytes + TagSize))
        {
            error = GuestFrameError.TooLong;
            return false;
        }

        bodyLength = (int)declared;
        error = GuestFrameError.None;
        return true;
    }

    /// <summary>
    /// Decodes a complete frame — length prefix included — into <paramref name="destination"/>.
    /// </summary>
    /// <param name="frame">The complete frame.</param>
    /// <param name="sequence">The sequence number the receiver expects for this frame.</param>
    /// <param name="destination">Receives the plaintext.</param>
    /// <param name="written">Plaintext bytes written.</param>
    /// <param name="error">Why decoding failed.</param>
    /// <returns><see langword="true"/> when the frame authenticated successfully.</returns>
    public bool TryDecode(
        ReadOnlySpan<byte> frame,
        ulong sequence,
        Span<byte> destination,
        out int written,
        out GuestFrameError error)
    {
        written = 0;

        if (!TryReadBodyLength(frame, out var bodyLength, out error))
        {
            return false;
        }

        if (frame.Length != LengthPrefixSize + bodyLength)
        {
            error = GuestFrameError.LengthMismatch;
            return false;
        }

        var plaintextLength = bodyLength - TagSize;
        if (destination.Length < plaintextLength)
        {
            error = GuestFrameError.DestinationTooSmall;
            return false;
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(sequence, nonce);

        Span<byte> associatedData = stackalloc byte[LengthPrefixSize + sizeof(ulong)];
        BuildAssociatedData((uint)bodyLength, sequence, associatedData);

        var ciphertext = frame.Slice(LengthPrefixSize, plaintextLength);
        var tag = frame.Slice(LengthPrefixSize + plaintextLength, TagSize);

        try
        {
            _aes.Decrypt(nonce, ciphertext, tag, destination[..plaintextLength], associatedData);
        }
        catch (CryptographicException)
        {
            // Corrupt, forged, replayed, and wrong-key frames are deliberately indistinguishable so
            // a caller cannot use the failure reason as an oracle.
            destination[..plaintextLength].Clear();
            error = GuestFrameError.AuthenticationFailed;
            return false;
        }

        written = plaintextLength;
        error = GuestFrameError.None;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() => _aes.Dispose();

    private void BuildNonce(ulong sequence, Span<byte> nonce)
    {
        _noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[NoncePrefixSize..], sequence);
    }

    private static void BuildAssociatedData(uint bodyLength, ulong sequence, Span<byte> associatedData)
    {
        BinaryPrimitives.WriteUInt32BigEndian(associatedData[..LengthPrefixSize], bodyLength);
        BinaryPrimitives.WriteUInt64BigEndian(associatedData[LengthPrefixSize..], sequence);
    }
}
