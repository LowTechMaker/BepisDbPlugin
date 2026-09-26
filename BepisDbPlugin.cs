using System.Reflection;
using SceneGallery.PluginSdk;

[assembly: AssemblyMetadata("PluginDescription", "Imports card metadata from BepisDB (db.bepis.moe)")]
[assembly: AssemblyMetadata("PluginUpdateUrl", "https://github.com/LowTechMaker/BepisDbPlugin")]

namespace SceneGallery.Plugin.BepisDb;

/// <summary>SDK capability adapter. Runtime ownership and provider policy live below this boundary.</summary>
public sealed class BepisDbPlugin : IFolderAuthorProvider, ICardImportProvider, IImportDestinationProvider, ICookieSetupValidator, IPluginSettingsProvider, IDisposable
{
    private BepisDbRuntime? _runtime;
    private readonly object _initializationGate = new();
    private bool _disposed;

    public string Name => "BepisDB";
    public string Version => typeof(BepisDbPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string ProviderId => BepisDbFilenameParser.ProviderId;
    public string DestinationFolderName => _runtime?.DestinationFolderName ?? "BepisDB";
    public bool UsesRatingFolders => false;
    public IReadOnlyList<PluginSettingDefinition> Settings { get; } =
    [
        new("destinationFolderName", "Destination folder",
            "Folder inserted below the organized import subfolder. Leave empty to skip the provider folder.",
            PluginSettingValueType.Text, "BepisDB"),
    ];

    public string SetupUrl => "https://db.bepis.moe/";
    public string CookieDomain => "db.bepis.moe";
    public string CompletionTitleHint => "BepisDB";
    public bool NeedsCookieSetup => _runtime?.NeedsCookieSetup ?? true;

    public void Initialize(IPluginHost host) => InitializeRuntime(() => new BepisDbRuntime(host));

    internal void InitializeForTests(IPluginHost host, IBepisDbFetcher fetcher)
        => InitializeForTests(host, (_, _) => fetcher);

    internal void InitializeForTests(IPluginHost host,
        Func<PluginSettings, Action, IBepisDbFetcher> fetcherFactory, TimeSpan? disposeTimeout = null)
        => InitializeRuntime(() => new BepisDbRuntime(host, fetcherFactory, disposeTimeout));

    private void InitializeRuntime(Func<BepisDbRuntime> createRuntime)
    {
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtime is not null)
                throw new InvalidOperationException("BepisDB is already initialized.");
            _runtime = createRuntime();
        }
    }

    public void ApplyCookies(IReadOnlyDictionary<string, string> cookies, string userAgent)
        => GetRuntime()?.ApplyCookies(cookies, userAgent);

    public string? GetSettingValue(string key)
    {
        var runtime = GetRuntime();
        return key == "destinationFolderName" ? runtime?.DestinationFolderName ?? "BepisDB" : null;
    }
    public void SetSettingValue(string key, string? value) => GetRuntime()?.SetSettingValue(key, value);
    public Task<bool> HasUsableCookiesAsync(CancellationToken ct)
        => GetRuntime()?.HasUsableCookiesAsync(ct) ?? Task.FromResult(false);

    public ParsedAuthor? TryParseFolderName(string folderName) => BepisDbAuthorFolderNameParser.TryParse(folderName);
    public string GetProfileUrl(AuthorKey key) => BepisDbCardMapper.GetProfileUrl(key);

    /// <summary>BepisDB authors come from artwork metadata; there is no independent author refresh API.</summary>
    public Task<AuthorInfo?> GetAuthorInfoAsync(AuthorKey key, bool forceRefresh, CancellationToken ct)
    {
        var runtime = GetRuntime();
        ct.ThrowIfCancellationRequested();
        return runtime?.GetAuthorInfoAsync(key, ct) ?? Task.FromResult<AuthorInfo?>(null);
    }

    public ArtworkId? TryParseFilename(string fileName) => BepisDbFilenameParser.TryParse(fileName);
    public ArtworkId? TryParseUrl(string url) => BepisDbFilenameParser.TryParseUrl(url);
    public ArtworkId? TryParseArtworkFolderName(string folderName) => BepisDbFilenameParser.TryParseFolder(folderName);
    public string GetArtworkUrl(ArtworkId id) => BepisDbCardMapper.GetArtworkUrl(id);
    public Task<ArtworkInfo?> FetchArtworkInfoAsync(ArtworkId id, CancellationToken ct, bool saveToLocalCache = true)
        => GetRuntime()?.FetchArtworkInfoAsync(id, ct, saveToLocalCache) ?? Task.FromResult<ArtworkInfo?>(null);

    private BepisDbRuntime? GetRuntime()
    {
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _runtime;
        }
    }

    public void Dispose()
    {
        BepisDbRuntime? runtime;
        lock (_initializationGate)
        {
            _disposed = true;
            runtime = _runtime;
        }
        runtime?.Dispose();
    }
}
