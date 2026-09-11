// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Connection material handed to a guest agent through the read-only bootstrap folder
/// (spec §"Transport and command channel").
/// </summary>
/// <remarks>
/// Carried through a read-only mapped folder rather than the command line, because a command line is
/// visible to every process in the guest and the pre-shared key must not be. The folder is read-only
/// so a co-resident guest application cannot rewrite the material to redirect the agent.
/// <para>
/// A fresh key is generated per boot, so material recovered from an earlier generation authenticates
/// nothing.
/// </para>
/// </remarks>
internal sealed record GuestBootstrapMaterial
{
    /// <summary>Schema version, so a newer agent stays readable or fails closed.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Target the agent must agree it is serving.</summary>
    public required string TargetId { get; init; }

    /// <summary>Generation the agent must agree it is serving.</summary>
    public required string TargetEpoch { get; init; }

    /// <summary>Base64 per-boot pre-shared key.</summary>
    public required string PreSharedKey { get; init; }

    /// <summary>TCP port the agent listens on.</summary>
    public required int Port { get; init; }

    /// <summary>Current schema version emitted by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name inside the bootstrap folder.</summary>
    public const string FileName = "connection.json";

    /// <summary>Generates fresh material for one boot.</summary>
    public static GuestBootstrapMaterial Create(ExecutionTargetRef target, ExecutionTargetEpoch epoch, int port) =>
        new()
        {
            SchemaVersion = CurrentSchemaVersion,
            TargetId = target.StateKey,
            TargetEpoch = epoch.Value,
            PreSharedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize)),
            Port = port,
        };

    /// <summary>The decoded pre-shared key.</summary>
    /// <exception cref="ExecutionTargetException">The material is malformed.</exception>
    public byte[] DecodeKey()
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(PreSharedKey);
        }
        catch (FormatException ex)
        {
            throw Malformed(ex);
        }

        if (key.Length != GuestProtocol.PreSharedKeySize)
        {
            throw Malformed(innerException: null);
        }

        return key;
    }

    /// <summary>Serializes this material.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, GuestBootstrapJsonContext.Default.GuestBootstrapMaterial);

    /// <summary>Parses material, returning null for anything malformed or from an unknown schema.</summary>
    public static GuestBootstrapMaterial? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var material = JsonSerializer.Deserialize(json, GuestBootstrapJsonContext.Default.GuestBootstrapMaterial);
            return material?.SchemaVersion == CurrentSchemaVersion ? material : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExecutionTargetException Malformed(Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            "The Windows Sandbox connection material is malformed.",
            userAction: "Retry the command so winapp recreates it.",
            innerException: innerException);
}

/// <summary>Source-generated serializer context for bootstrap material.</summary>
[JsonSerializable(typeof(GuestBootstrapMaterial))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class GuestBootstrapJsonContext : JsonSerializerContext
{
}

/// <summary>
/// An authenticated, encrypted framed TCP connection to a guest agent.
/// </summary>
/// <remarks>
/// TCP is used because Windows Sandbox exposes a guest IP and no lower-level channel; a future
/// backend with Hyper-V sockets or a remote API substitutes its own transport without any of the
/// command-channel semantics changing.
/// <para>
/// The listener is deliberately bound to the loopback-facing address the guest connects <em>to</em>
/// rather than all interfaces, and the pre-shared handshake means a connection from anywhere else
/// fails authentication before it can send a single operation.
/// </para>
/// </remarks>
internal static class GuestTcpTransport
{
    /// <summary>How long the host waits for the guest agent to accept a connection.</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Delay between connection attempts while the agent is still starting.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Connects to a guest agent and completes the authenticated handshake, retrying while the
    /// agent is still coming up.
    /// </summary>
    /// <remarks>
    /// Retrying only covers connection refusal. A handshake that fails is never retried: it means
    /// the peer is not the agent winapp started, or is serving a different generation, and repeating
    /// the attempt would only turn a clear authentication failure into a timeout.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// The agent never accepted a connection, or failed to authenticate.
    /// </exception>
    public static async Task<IGuestTransport> ConnectAsync(
        string address,
        GuestBootstrapMaterial material,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(material);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        var socket = await ConnectSocketAsync(address, material.Port, timeout.Token, cancellationToken)
            .ConfigureAwait(false);

        var stream = new NetworkStream(socket, ownsSocket: true);

        try
        {
            return await GuestSecureChannel.EstablishAsync(
                stream,
                GuestRole.Host,
                material.DecodeKey(),
                material.TargetId,
                material.TargetEpoch,
                timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Accepts one inbound TCP connection, without authenticating it.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="EstablishAsync"/> so the agent's accept loop never blocks on a
    /// handshake. A peer that connects and then stalls delays only its own connection, not every
    /// other host waiting to be accepted.
    /// </remarks>
    public static Task<TcpClient> AcceptClientAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);

        return listener.AcceptTcpClientAsync(cancellationToken).AsTask();
    }

    /// <summary>
    /// Completes the authenticated handshake for an accepted connection, from inside the guest.
    /// </summary>
    /// <remarks>
    /// Each connection derives its own session keys from fresh handshake randoms and tracks its own
    /// directional sequence numbers, so concurrent channels can never decrypt, replay, or reorder
    /// one another's frames even though they share one pre-shared key.
    /// <para>
    /// Ownership of <paramref name="client"/> transfers to the returned transport on success and is
    /// released here on failure, so a peer that fails authentication leaks no socket.
    /// </para>
    /// </remarks>
    public static async Task<IGuestTransport> EstablishAsync(
        TcpClient client,
        GuestBootstrapMaterial material,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(material);

        try
        {
            return await GuestSecureChannel.EstablishAsync(
                client.GetStream(),
                GuestRole.Guest,
                material.DecodeKey(),
                material.TargetId,
                material.TargetEpoch,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Binds a listener, letting the OS choose the port when none is fixed.</summary>
    /// <remarks>
    /// Binds every interface, which is what the guest agent needs: the host reaches it across the
    /// Sandbox's virtual network, not over loopback. That is also why a host-side caller must never
    /// use this overload — binding all interfaces on the host raises a Windows Firewall consent
    /// prompt for whichever executable did it.
    /// </remarks>
    /// <returns>The listener and the port it actually bound.</returns>
    public static (TcpListener Listener, int Port) Listen(int requestedPort) =>
        Listen(requestedPort, IPAddress.Any);

    /// <summary>Binds a listener to one specific address.</summary>
    /// <remarks>
    /// Ownership transfers to the caller on success. If <see cref="TcpListener.Start()"/> or reading
    /// the bound endpoint fails, the listener is disposed here — otherwise a failed bind would leak
    /// a socket, and the agent retries binding on the path that reports the failure.
    /// <para>
    /// The address is explicit rather than assumed because the two callers need opposite things. The
    /// guest agent must be reachable from the host and binds every interface; anything running on
    /// the host — a test standing in for the agent, most of all — must bind
    /// <see cref="IPAddress.Loopback"/>, which is exempt from the firewall and so never prompts.
    /// </para>
    /// </remarks>
    /// <returns>The listener and the port it actually bound.</returns>
    public static (TcpListener Listener, int Port) Listen(int requestedPort, IPAddress bindAddress)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);

        var listener = new TcpListener(bindAddress, requestedPort);

        try
        {
            listener.Start();
            return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether anything is still accepting connections at this address, without handshaking.
    /// </summary>
    /// <remarks>
    /// The disambiguator for a handshake the peer closed. On the wire, an agent dropping a
    /// connection at its tracked ceiling and an agent that has just died look identical — both
    /// reset a connection they had accepted. What separates them is what happens next: a live agent
    /// still accepts, a dead one refuses.
    /// <para>
    /// Deliberately connect-only. Completing a handshake here would consume one of the very channels
    /// whose scarcity is being diagnosed, and the answer needed is only "is something listening",
    /// which the TCP handshake alone settles. The connection is closed immediately, so the agent
    /// sheds it as an unauthenticated peer rather than admitting it.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsListeningAsync(
        string address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(IPAddress.Parse(address), port, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (
            ex is SocketException or OperationCanceledException or FormatException or ArgumentException)
        {
            // Refused, unreachable, or too slow to answer. Any of those means "not serving".
            return false;
        }
    }

    private static async Task<Socket> ConnectSocketAsync(
        string address,
        int port,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        while (true)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(IPAddress.Parse(address), port, timeoutToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                socket.Dispose();

                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TransportFailed,
                    "The Windows Sandbox agent did not start accepting connections in time.",
                    userAction: "Retry the command. If it keeps failing, close Windows Sandbox and try again.",
                    context: new Dictionary<string, string>
                    {
                        ["guestAddress"] = address,
                        ["port"] = port.ToString(CultureInfo.InvariantCulture),
                    });
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            // The agent is still coming up. Only connection refusal reaches here; anything else has
            // already been rethrown.
            await Task.Delay(RetryDelay, timeoutToken).ConfigureAwait(false);
        }
    }
}
