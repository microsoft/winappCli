// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Protocol constants shared by host and guest.</summary>
/// <remarks>
/// Envelopes declare a compatible version range rather than a single version, so a host and guest
/// that differ by a compatible revision can still talk. Comparisons elsewhere additionally use the
/// stamped winapp version plus the guest binary hash.
/// </remarks>
internal static class GuestProtocol
{
    /// <summary>Oldest protocol revision this build can speak.</summary>
    public const int MinimumVersion = 1;

    /// <summary>Newest protocol revision this build can speak.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Bytes of per-connection random each side contributes to key derivation.</summary>
    public const int HandshakeRandomSize = 32;

    /// <summary>Bytes of pre-shared secret delivered through the read-only bootstrap folder.</summary>
    public const int PreSharedKeySize = 32;

    /// <summary>Marks a winapp guest-agent handshake and its wire generation.</summary>
    public static ReadOnlySpan<byte> HandshakeMagic => "WGA1"u8;

    /// <summary>Fixed size of the plaintext hello: magic, version range, and random.</summary>
    public const int HelloSize = 4 + 2 + 2 + HandshakeRandomSize;
}

/// <summary>Which end of the connection a participant is.</summary>
internal enum GuestRole
{
    /// <summary>The winapp process on the host, which initiates the connection.</summary>
    Host,

    /// <summary>The persistent agent inside the guest, which accepts it.</summary>
    Guest,
}

/// <summary>
/// The first encrypted frame each side sends, proving it holds the pre-shared key and agrees on
/// what it is connected to.
/// </summary>
/// <remarks>
/// Producing a valid tag over this payload is only possible with the pre-shared key, so a
/// successful exchange authenticates both peers. Including the transcript hash binds the confirmation
/// to the exact hellos that produced the session keys, which stops an attacker from splicing a
/// recorded handshake onto a different connection.
/// </remarks>
internal sealed class GuestHandshakeConfirmation
{
    /// <summary>Base64 SHA-256 over the concatenated host and guest hellos.</summary>
    public required string Transcript { get; init; }

    /// <summary>Target this connection claims to serve.</summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Generation identity. A mismatch means one side is talking about a recreated environment, so
    /// the connection is refused rather than allowed to act on stale assumptions.
    /// </summary>
    public required string TargetEpoch { get; init; }

    /// <summary>Sender's role, which must be the opposite of the receiver's.</summary>
    public required string Role { get; init; }

    /// <summary>Protocol revision the sender selected from the overlapping range.</summary>
    public required int NegotiatedVersion { get; init; }
}

/// <summary>Source-generated serializer context for handshake payloads.</summary>
[JsonSerializable(typeof(GuestHandshakeConfirmation))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class GuestProtocolJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Per-connection AEAD keys derived from the pre-shared key and both sides' randoms.
/// </summary>
/// <remarks>
/// The pre-shared key is never used directly as an AEAD key. HKDF mixes in both randoms, so every
/// connection gets fresh keys even though the pre-shared key lives for the whole guest boot — which
/// means a recorded session can never be replayed against a later connection.
/// </remarks>
internal sealed class GuestSessionKeys : IDisposable
{
    private const int DirectionMaterialSize = GuestFrameCodec.KeySize + GuestFrameCodec.NoncePrefixSize;

    private GuestSessionKeys(GuestFrameCodec hostToGuest, GuestFrameCodec guestToHost)
    {
        HostToGuest = hostToGuest;
        GuestToHost = guestToHost;
    }

    /// <summary>Codec for frames the host sends to the guest.</summary>
    public GuestFrameCodec HostToGuest { get; }

    /// <summary>Codec for frames the guest sends to the host.</summary>
    public GuestFrameCodec GuestToHost { get; }

    /// <summary>
    /// Derives both directions' keys. Distinct <c>info</c> labels give each direction an independent
    /// key and nonce prefix, so a frame can never be reflected back at its sender.
    /// </summary>
    public static GuestSessionKeys Derive(
        ReadOnlySpan<byte> preSharedKey,
        ReadOnlySpan<byte> hostRandom,
        ReadOnlySpan<byte> guestRandom)
    {
        if (preSharedKey.Length != GuestProtocol.PreSharedKeySize)
        {
            throw new ArgumentException(
                $"Pre-shared key must be {GuestProtocol.PreSharedKeySize} bytes.",
                nameof(preSharedKey));
        }

        Span<byte> salt = stackalloc byte[GuestProtocol.HandshakeRandomSize * 2];
        hostRandom.CopyTo(salt);
        guestRandom.CopyTo(salt[GuestProtocol.HandshakeRandomSize..]);

        Span<byte> hostToGuest = stackalloc byte[DirectionMaterialSize];
        Span<byte> guestToHost = stackalloc byte[DirectionMaterialSize];

        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, preSharedKey, hostToGuest, salt, "winapp-guest-h2g-v1"u8);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, preSharedKey, guestToHost, salt, "winapp-guest-g2h-v1"u8);

            return new GuestSessionKeys(
                new GuestFrameCodec(hostToGuest[..GuestFrameCodec.KeySize], hostToGuest[GuestFrameCodec.KeySize..]),
                new GuestFrameCodec(guestToHost[..GuestFrameCodec.KeySize], guestToHost[GuestFrameCodec.KeySize..]));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostToGuest);
            CryptographicOperations.ZeroMemory(guestToHost);
        }
    }

    /// <summary>Codec this role uses to send.</summary>
    public GuestFrameCodec GetSendCodec(GuestRole role) =>
        role == GuestRole.Host ? HostToGuest : GuestToHost;

    /// <summary>Codec this role uses to receive.</summary>
    public GuestFrameCodec GetReceiveCodec(GuestRole role) =>
        role == GuestRole.Host ? GuestToHost : HostToGuest;

    /// <inheritdoc/>
    public void Dispose()
    {
        HostToGuest.Dispose();
        GuestToHost.Dispose();
    }
}
