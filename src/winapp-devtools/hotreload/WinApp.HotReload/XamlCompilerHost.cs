// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.HotReload;

/// <summary>A diagnostic from the XAML compiler.</summary>
internal sealed record XamlCompilerDiagnostic(bool IsError, string? Code, string? File, int Line, int Column, string? Message);

/// <summary>What a reload of the compiler's inputs did (API v3): references loaded and unloaded, or everything released.</summary>
internal sealed record XamlReloadOutput(bool Success, double ElapsedMs, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool FullReset, IReadOnlyList<XamlCompilerDiagnostic> Diagnostics);

/// <summary>What one compile produced: generated sources and .xbf, by full path in the service's output folder.</summary>
internal sealed record XamlCompileOutput(bool Success, double ElapsedMs, bool XamlTypeInfoMayBeStale, IReadOnlyDictionary<string, string> GeneratedText,
    IReadOnlyDictionary<string, byte[]> GeneratedXbf, IReadOnlyList<XamlCompilerDiagnostic> Diagnostics);

/// <summary>
/// The WinUI XAML compiler kept warm in its own process (XamlCompiler.exe --serve): the same compiler a build runs,
/// compiling only the pages a save changed. It reads the inputs the app's last build gave it (input.json, next to the
/// generated files) and writes to its own folder, so the build's incremental state is left alone.
/// </summary>
internal sealed class XamlCompilerHost : IDisposable
{
    /// <summary>A folder holding XamlCompiler.exe with --serve (or the exe itself).</summary>
    public const string LocationVariable = "WINAPP_XAML_COMPILER_SERVICE";

    /// <summary>Where builds of the compiler service are staged while it isn't in the WinUI package yet.</summary>
    private const string StagedBuilds = @"C:\hr-shared\xcsvc";

    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private int _nextId;

    private XamlCompilerHost(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(line);
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    public string OutputDirectory { get; private init; } = "";

    /// <summary>XamlCompiler.exe with --serve: WINAPP_XAML_COMPILER_SERVICE, else the newest staged build; null when there is none.</summary>
    public static string? Locate()
    {
        var candidates = new List<string?> { Environment.GetEnvironmentVariable(LocationVariable) };
        var latest = Path.Combine(StagedBuilds, "LATEST.txt");
        if (File.Exists(latest))
        {
            var name = File.ReadAllText(latest).Trim();
            candidates.Add(Path.IsPathRooted(name) ? name : Path.Combine(StagedBuilds, name));
        }
        foreach (var candidate in candidates.OfType<string>().Where(c => c.Length > 0))
        {
            if (File.Exists(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
            if (Directory.Exists(candidate))
            {
                // The package layout (tools\net472\XamlCompiler.exe) or the exe's own folder.
                var exe = Directory.EnumerateFiles(candidate, "XamlCompiler.exe", SearchOption.AllDirectories)
                    .OrderBy(p => p.Contains("net472", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
                if (exe is not null)
                {
                    return exe;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// <paramref name="outputDirectory"/> null: the service writes where the build does (the build's obj folder), which is
    /// also where the compiler keeps its hot reload state (the connection ID ledger).
    /// </summary>
    public static XamlCompilerHost Start(string exe, string inputJson, string? outputDirectory, string intermediateDirectory, string projectDirectory)
    {
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add(inputJson);
        if (outputDirectory is not null)
        {
            start.ArgumentList.Add(outputDirectory);
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {exe}.");
        return new XamlCompilerHost(process) { OutputDirectory = outputDirectory ?? intermediateDirectory };
    }

    /// <summary>Pass 1: the .g.i.cs (the named fields and InitializeComponent the app's own code compiles against).</summary>
    public Task<XamlCompileOutput> Pass1Async(IReadOnlyCollection<string>? files, CancellationToken cancellationToken) =>
        SendAsync(new ServeRequest { Pass = 1, Files = files?.ToList() }, cancellationToken);

    /// <summary>
    /// Pass 2: the .g.cs and .xbf. <paramref name="image"/> is the app's assembly (metadata is enough); null uses the one
    /// the build produced. <paramref name="typeInfo"/> regenerates XamlTypeInfo, which needs every page (files null).
    /// </summary>
    public Task<XamlCompileOutput> Pass2Async(IReadOnlyCollection<string>? files, byte[]? image, bool typeInfo, CancellationToken cancellationToken) =>
        SendAsync(new ServeRequest { Pass = 2, Files = files?.ToList(), RegenerateTypeInfo = typeInfo, LocalAssemblyImage = image is null ? null : Convert.ToBase64String(image) }, cancellationToken);

    /// <summary>
    /// API v3: the service takes a new input.json from a build of the same project (new references, new pages); unchanged
    /// references stay loaded. Null when this compiler doesn't know the request (before v3): start it again instead.
    /// </summary>
    public async Task<XamlReloadOutput?> ReloadAsync(string inputJson, CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(new ServeRequest { Op = "reload", InputJson = inputJson }, cancellationToken).ConfigureAwait(false);
        return response.Added is null && response.FullReset is null ? null
            : new XamlReloadOutput(response.Success, response.ElapsedMs, response.Added ?? [], response.Removed ?? [], response.FullReset ?? false,
                response.Diagnostics?.Select(d => new XamlCompilerDiagnostic(d.IsError, d.Code, d.File, d.Line, d.Column, d.Message)).ToList() ?? []);
    }

    private async Task<XamlCompileOutput> SendAsync(ServeRequest request, CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        return new XamlCompileOutput(response.Success, response.ElapsedMs, response.XamlTypeInfoMayBeStale,
            response.GeneratedText ?? [],
            (response.GeneratedXbf ?? []).ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value), StringComparer.OrdinalIgnoreCase),
            response.Diagnostics?.Select(d => new XamlCompilerDiagnostic(d.IsError, d.Code, d.File, d.Line, d.Column, d.Message)).ToList() ?? []);
    }

    private async Task<ServeResponse> ExchangeAsync(ServeRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException("The XAML compiler stopped: " + Stderr());
            }
            request.Id = ++_nextId;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The XAML compiler stopped: " + Stderr());
                // The compiler may print other lines; responses are the JSON objects.
                if (!line.StartsWith('{'))
                {
                    continue;
                }
                var response = JsonSerializer.Deserialize<ServeResponse>(line) ?? throw new InvalidOperationException("The XAML compiler sent an empty response.");
                if (response.Id != request.Id)
                {
                    continue;
                }
                return response;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Stderr()
    {
        lock (_stderr)
        {
            return _stderr.Length > 0 ? _stderr.ToString().Trim() : $"exit code {(_process.HasExited ? _process.ExitCode : 0)}";
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine();
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
        _process.Dispose();
    }

    private sealed class ServeRequest
    {
        public int Id { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Op { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InputJson { get; set; }
        public int Pass { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Files { get; set; }
        public bool RegenerateTypeInfo { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LocalAssemblyImage { get; set; }
    }

    private sealed class ServeDiagnostic
    {
        public bool IsError { get; set; }
        public string? Code { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string? Message { get; set; }
    }

    private sealed class ServeResponse
    {
        public int Id { get; set; }
        public bool Success { get; set; }
        public double ElapsedMs { get; set; }
        public bool XamlTypeInfoMayBeStale { get; set; }
        public Dictionary<string, string>? GeneratedText { get; set; }
        public Dictionary<string, string>? GeneratedXbf { get; set; }
        public List<ServeDiagnostic>? Diagnostics { get; set; }
        /// <summary>Reload (API v3) only: references loaded, unloaded, and whether everything was released.</summary>
        public List<string>? Added { get; set; }
        public List<string>? Removed { get; set; }
        public bool? FullReset { get; set; }
    }
}
