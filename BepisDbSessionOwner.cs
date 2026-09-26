namespace SceneGallery.Plugin.BepisDb;

/// <summary>
/// Owns cookie generations and fetcher leases. Replacing cookies retires a transport;
/// normal retirement waits for its last lease, while plugin shutdown may force teardown.
/// Authentication callbacks may only change the generation that is still current.
/// </summary>
internal sealed class BepisDbSessionOwner : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<PluginSettings, Action, IBepisDbFetcher> _factory;
    private readonly HashSet<Generation> _generations = [];
    private Generation? _current;
    private long _nextGeneration;
    private bool _disposed;

    internal BepisDbSessionOwner(PluginSettings settings, Func<PluginSettings, Action, IBepisDbFetcher> factory)
    {
        _factory = factory;
        Replace(settings, resetCookieSetup: false);
    }

    internal bool NeedsCookieSetup
    {
        get { lock (_gate) return _current is null || !_current.HasCookie || _current.CookieSetupRequired; }
    }

    internal void Replace(PluginSettings settings, bool resetCookieSetup)
    {
        IBepisDbFetcher? retired = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The factory receives a detached snapshot, never the mutable settings document.
            var snapshot = new PluginSettings
            {
                CfClearanceCookie = settings.CfClearanceCookie,
                UserAgent = settings.UserAgent,
                DestinationFolderName = settings.DestinationFolderName,
            };
            var next = new Generation(++_nextGeneration, !string.IsNullOrEmpty(snapshot.CfClearanceCookie))
            {
                CookieSetupRequired = !resetCookieSetup && (_current?.CookieSetupRequired ?? false),
            };
            next.Fetcher = _factory(snapshot, () => MarkCookieSetupRequired(next));
            var previous = _current;
            _current = next;
            _generations.Add(next);
            if (previous is not null)
            {
                previous.Retired = true;
                if (previous.LeaseCount == 0)
                    retired = TakeForDisposal(previous);
            }
        }
        retired?.Dispose();
    }

    internal Lease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = _current!;
            generation.LeaseCount++;
            return new Lease(this, generation);
        }
    }

    internal void SetValidationResult(Lease lease, bool usable)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, lease.Session))
                _current.CookieSetupRequired = !usable;
        }
    }

    // Checking generation and publishing provider state form one synchronous operation.
    // An admitted producer may still publish after shutdown while persistence disposal is deferred.
    internal bool TryPublish(Lease lease, Action publish)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, lease.Session)) return false;
            publish();
            return true;
        }
    }

    private void MarkCookieSetupRequired(Generation generation)
    {
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(_current, generation))
                generation.CookieSetupRequired = true;
        }
    }

    private void Release(Generation generation)
    {
        IBepisDbFetcher? retired = null;
        lock (_gate)
        {
            generation.LeaseCount--;
            if (generation.LeaseCount == 0 && generation.Retired)
                retired = TakeForDisposal(generation);
        }
        retired?.Dispose();
    }

    private IBepisDbFetcher? TakeForDisposal(Generation generation)
    {
        if (generation.Disposed) return null;
        generation.Disposed = true;
        _generations.Remove(generation);
        return generation.Fetcher;
    }

    public void Dispose()
    {
        List<IBepisDbFetcher> fetchers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            fetchers = _generations.Select(generation =>
            {
                generation.Retired = true;
                generation.Disposed = true;
                return generation.Fetcher;
            }).ToList();
            _generations.Clear();
        }
        // Teardown must not run while holding the session or operation-count lock.
        List<Exception>? failures = null;
        foreach (var fetcher in fetchers)
        {
            try { fetcher.Dispose(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is not null) throw new AggregateException("BepisDB transport teardown failed.", failures);
    }

    internal sealed class Generation(long id, bool hasCookie)
    {
        internal long Id { get; } = id;
        internal bool HasCookie { get; } = hasCookie;
        internal IBepisDbFetcher Fetcher { get; set; } = null!;
        internal int LeaseCount;
        internal bool CookieSetupRequired;
        internal bool Retired;
        internal bool Disposed;
    }

    internal sealed class Lease(BepisDbSessionOwner owner, Generation session) : IDisposable
    {
        private BepisDbSessionOwner? _owner = owner;
        internal Generation Session { get; } = session;
        internal long GenerationId => Session.Id;
        internal bool HasCookie => Session.HasCookie;
        internal IBepisDbFetcher Fetcher => Session.Fetcher;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(Session);
    }
}
