namespace Paladin.Core.Replay;

/// <summary>
/// Resolves a request to the provider that can serve it. The session pipeline talks
/// only to this, so a new archive provider is a registration, not a code change.
/// </summary>
public sealed class ReplayProviderRegistry
{
    private readonly List<IReplayProvider> _providers = new();

    public ReplayProviderRegistry Register(IReplayProvider provider)
    {
        _providers.Add(provider);
        return this;
    }

    public IReadOnlyList<IReplayProvider> Providers => _providers;

    public IReplayProvider Resolve(ReplayRequest request) =>
        _providers.FirstOrDefault(p => p.CanHandle(request))
        ?? throw new ReplayAcquisitionException(
            $"No replay provider can handle '{request.Kind}'. Registered providers: " +
            string.Join(", ", _providers.Select(p => p.Id)));

    public Task<AcquiredReplay> AcquireAsync(ReplayRequest request, string workingDirectory, CancellationToken ct) =>
        Resolve(request).AcquireAsync(request, workingDirectory, ct);

    /// <summary>
    /// Classifies a raw command-line argument. A string that parses as an http(s) URL is a
    /// download; anything else is treated as a path.
    /// </summary>
    public static ReplayRequest ClassifyRawInput(string raw)
    {
        raw = raw.Trim().Trim('"');
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ReplayRequest { Kind = "url", Value = raw };
        }
        return new ReplayRequest { Kind = "local", Value = raw };
    }
}
