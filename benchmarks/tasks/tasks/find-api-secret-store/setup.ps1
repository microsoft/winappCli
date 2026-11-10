# Publishes Contoso.Secrets 1.3.0 to a local folder feed and puts this run's `reports-db` secret in the
# signed-in user's store. The client type and its members get names drawn at random on every run, so
# the right calls can't be remembered from an earlier run or from training data; they can only be read
# from the package's metadata. The checker and solve.ps1 read the names and the secret from the state file.
param([string]$Workspace, [string]$FeedPath = 'C:\feeds\contoso', [string]$StatePath = 'C:\bench-state\find-api-secret-store.json',
    [string]$StoreDir = (Join-Path $env:LOCALAPPDATA 'Contoso\Secrets'))
$ErrorActionPreference = 'Stop'

# Every name is plausible for what it does, but no single name can be predicted.
$store = Get-RandomPick -From 'SecretVault', 'VaultClient', 'SecretStore', 'CredentialVault', 'SecretsClient', 'SecretLocker', 'VaultSession', 'KeyLocker'
$open = Get-RandomPick -From 'Open', 'Connect', 'ForCurrentUser', 'OpenForUser', 'Attach', 'GetDefault'
$reveal = Get-RandomPick -From 'Reveal', 'Fetch', 'Resolve', 'Unseal', 'Retrieve', 'Unlock', 'Lookup', 'Read'
$describe = Get-RandomPick -From 'Describe', 'Inspect', 'GetInfo', 'GetMetadata', 'Stat', 'Probe'
$rotate = Get-RandomPick -From 'Rotate', 'Renew', 'Regenerate', 'Cycle', 'Reissue', 'Refresh'
$list = Get-RandomPick -From 'ListNames', 'Enumerate', 'Catalog', 'GetNames'
$valueType = Get-RandomPick -From 'SecretValue', 'SecretMaterial', 'VaultEntry', 'SealedSecret', 'SecretPayload'
$plain = Get-RandomPick -From 'Plaintext', 'Cleartext', 'Material', 'Content', 'Value', 'Text'
$infoType = Get-RandomPick -From 'SecretInfo', 'SecretMetadata', 'SecretDescriptor'

$key = [byte[]]::new(16); [System.Security.Cryptography.RandomNumberGenerator]::Fill($key)
$keyB64 = [Convert]::ToBase64String($key)
$chars = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789'
$secret = 'rpt-' + (-join (1..24 | ForEach-Object { $chars[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($chars.Length)] }))

$source = @"
using System.Text;
using System.Text.Json;

namespace Contoso.Secrets;

/// <summary>A connection to the Contoso secret store of the signed-in user.</summary>
public sealed class $store : IDisposable
{
    private static readonly byte[] Key = Convert.FromBase64String("$keyB64");
    private readonly string _file;
    private readonly Dictionary<string, StoredSecret> _items;

    private $store(string file)
    {
        _file = file;
        _items = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, StoredSecret>>(File.ReadAllText(file)) ?? new() : new();
    }

    /// <summary>Opens the secret store of the signed-in user.</summary>
    /// <returns>A connection to the store. Dispose it when done.</returns>
    public static $store $open() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Contoso", "Secrets", "vault.dat"));

    /// <summary>Lists the names of the secrets the signed-in user can read.</summary>
    public IReadOnlyList<string> $list() => _items.Keys.ToList();

    /// <summary>Returns the name, version and expiry date of a secret. Does not return the secret itself.</summary>
    /// <param name="name">Secret name, for example "reports-db".</param>
    public $infoType $describe(string name) => new(name, Get(name).Version, Get(name).ExpiresOn);

    /// <summary>Decrypts and returns the current value of a secret.</summary>
    /// <param name="name">Secret name, for example "reports-db".</param>
    public $valueType $reveal(string name) => new(Crypt(Convert.FromBase64String(Get(name).Data)), Get(name).Version);

    /// <summary>Replaces a secret with a newly generated value and returns the new value. Services that still use the old value stop accepting it.</summary>
    /// <param name="name">Secret name.</param>
    public $valueType $rotate(string name)
    {
        var item = Get(name);
        var fresh = "rot-" + Guid.NewGuid().ToString("N");
        _items[name] = item with { Data = Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(fresh))), Version = item.Version + 1 };
        File.WriteAllText(_file, JsonSerializer.Serialize(_items));
        return new(Encoding.UTF8.GetBytes(fresh), item.Version + 1);
    }

    /// <summary>Closes the connection to the store.</summary>
    public void Dispose() { }

    private StoredSecret Get(string name) => _items.TryGetValue(name, out var item) ? item : throw new KeyNotFoundException("No secret named '" + name + "' in the store.");

    private static byte[] Crypt(byte[] data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++) result[i] = (byte)(data[i] ^ Key[i % Key.Length]);
        return result;
    }

    private sealed record StoredSecret(string Data, int Version, DateTimeOffset ExpiresOn);
}

/// <summary>The decrypted value of a secret.</summary>
public sealed class $valueType
{
    private readonly byte[] _bytes;
    internal $valueType(byte[] bytes, int version) { _bytes = bytes; Version = version; }

    /// <summary>The secret, as text.</summary>
    public string $plain => Encoding.UTF8.GetString(_bytes);

    /// <summary>Version number of the secret; it goes up each time the secret is replaced.</summary>
    public int Version { get; }

    /// <summary>A short hash of the secret, safe to write to logs. It is not the secret.</summary>
    public string Fingerprint => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_bytes))[..12];

    /// <summary>Returns a masked placeholder, so a secret is never written to a log by accident.</summary>
    public override string ToString() => "********";
}

/// <summary>Describes a secret without its value.</summary>
/// <param name="Name">Secret name.</param>
/// <param name="Version">Version number of the secret.</param>
/// <param name="ExpiresOn">When the secret must be replaced.</param>
public sealed record $infoType(string Name, int Version, DateTimeOffset ExpiresOn);
"@

[void](New-LocalNuGetPackage -Id 'Contoso.Secrets' -Version '1.3.0' -Source $source -FeedPath $FeedPath -Description 'Access to the Contoso secret store.')

# The store file holds this run's secret, encrypted with the key compiled into the library.
$plainBytes = [System.Text.Encoding]::UTF8.GetBytes($secret)
$enc = [byte[]]::new($plainBytes.Length)
for ($i = 0; $i -lt $plainBytes.Length; $i++) { $enc[$i] = $plainBytes[$i] -bxor $key[$i % $key.Length] }
New-Item -ItemType Directory -Force -Path $StoreDir | Out-Null
$expires = (Get-Date).AddDays(60).ToString('o')
[ordered]@{
    'reports-db'   = [ordered]@{ Data = [Convert]::ToBase64String($enc); Version = 3; ExpiresOn = $expires }
    'smtp-relay'   = [ordered]@{ Data = [Convert]::ToBase64String([byte[]](1..20)); Version = 1; ExpiresOn = $expires }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StoreDir 'vault.dat') -Encoding utf8NoBOM

New-Item -ItemType Directory -Force -Path (Split-Path $StatePath) | Out-Null
[ordered]@{ store = $store; open = $open; reveal = $reveal; describe = $describe; rotate = $rotate; list = $list; valueType = $valueType; plain = $plain; infoType = $infoType; key = $keyB64; secret = $secret; storeFile = (Join-Path $StoreDir 'vault.dat') } |
    ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding utf8NoBOM
# The names and the secret stay out of setup.log: the run's output folder is readable from inside the Sandbox.
'Published Contoso.Secrets 1.3.0 and stored reports-db'
