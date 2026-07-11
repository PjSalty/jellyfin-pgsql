using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Pgsql.Cache;

/// <summary>
/// Forwards every category logged by the plugin private service collection to the
/// host provided logger, so cache diagnostics land in the normal Jellyfin log.
/// </summary>
internal sealed class ForwardingLoggerProvider : ILoggerProvider
{
    private readonly ILogger _target;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForwardingLoggerProvider"/> class.
    /// </summary>
    /// <param name="target">The host logger every category forwards to.</param>
    public ForwardingLoggerProvider(ILogger target)
    {
        _target = target;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        return _target;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
