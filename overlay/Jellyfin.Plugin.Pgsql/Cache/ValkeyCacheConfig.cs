using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.DbConfiguration;

namespace Jellyfin.Plugin.Pgsql.Cache;

/// <summary>
/// Configuration for the Valkey backed EF Core second level cache.
/// Every value is read from the CustomProviderOptions in database.xml first,
/// then from an environment variable of the same name, then defaulted.
/// </summary>
public sealed class ValkeyCacheConfig
{
    /// <summary>
    /// Tables excluded from caching by default: security relevant lookups that must
    /// always read database truth (users, permissions, device tokens, api keys) and
    /// the insert heavy activity log. Everything else is cached and invalidated on write.
    /// </summary>
    private static readonly string[] DefaultExcludedTables =
    [
        "Users",
        "Permissions",
        "Preferences",
        "AccessSchedules",
        "Devices",
        "DeviceOptions",
        "ApiKeys",
        "ActivityLogs"
    ];

    /// <summary>
    /// Gets a value indicating whether the second level cache is enabled. Key: CACHE_ENABLED. Default: false.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the Valkey host. Key: VALKEY_HOST. Default: localhost.
    /// </summary>
    public string Host { get; init; } = "localhost";

    /// <summary>
    /// Gets the Valkey port. Key: VALKEY_PORT. Default: 6379.
    /// </summary>
    public int Port { get; init; } = 6379;

    /// <summary>
    /// Gets the Valkey password. Key: VALKEY_PASSWORD. Default: none.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets the Valkey logical database index. Key: VALKEY_DB. Default: 0.
    /// </summary>
    public int Db { get; init; }

    /// <summary>
    /// Gets the absolute expiration for cached entries in minutes. Key: CACHE_TTL_MINUTES. Default: 30.
    /// The TTL is a backstop: invalidation on write is the primary consistency mechanism.
    /// </summary>
    public int TtlMinutes { get; init; } = 30;

    /// <summary>
    /// Gets how long catalogue queries (BaseItems and its satellites) stay cached.
    /// These change only when a library scan runs, so hours is correct and is what
    /// makes browsing instant on the second visit.
    /// </summary>
    public int CatalogueTtlMinutes { get; init; } = 360;

    /// <summary>
    /// Gets how long a query naming UserData stays cached. Watched-state is rewritten
    /// every few seconds during playback, so this is deliberately a micro-TTL: long
    /// enough to absorb a page's worth of repeat lookups, short enough that progress
    /// is never visibly stale.
    /// </summary>
    public int WatchedStateTtlSeconds { get; init; } = 20;

    /// <summary>
    /// Gets the tables excluded from caching. Key: CACHE_EXCLUDED_TABLES (comma separated).
    /// Default: security relevant and insert heavy tables, see <see cref="DefaultExcludedTables"/>.
    /// </summary>
    public IReadOnlyList<string> ExcludedTables { get; init; } = DefaultExcludedTables;

    /// <summary>
    /// Gets the cache key prefix. Key: CACHE_KEY_PREFIX. Default: JF_.
    /// </summary>
    public string KeyPrefix { get; init; } = "JF_";

    /// <summary>
    /// Gets a value indicating whether cached payloads are LZ4 compressed. Key: CACHE_COMPRESSION. Default: true.
    /// </summary>
    public bool Compression { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether verbose cache logging is enabled. Key: CACHE_DEBUG. Default: false.
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>
    /// Builds the configuration from database.xml custom options with environment variable fallback.
    /// Invalid numeric values fall back to their defaults instead of failing database startup.
    /// </summary>
    /// <param name="options">The CustomProviderOptions key value pairs from database.xml, may be null.</param>
    /// <returns>The resolved configuration.</returns>
    public static ValkeyCacheConfig Load(ICollection<CustomDatabaseOption>? options)
    {
        return new ValkeyCacheConfig
        {
            Enabled = ReadBool(options, "CACHE_ENABLED", defaultValue: false),
            Host = Read(options, "VALKEY_HOST") ?? "localhost",

            // Out-of-range values fall back to defaults rather than failing startup:
            // a zero or negative TTL would make every cache write throw and silently
            // disable the cache; an invalid port or db index can never connect.
            Port = Clamp(ReadInt(options, "VALKEY_PORT", 6379), 1, 65535, 6379),
            Password = Read(options, "VALKEY_PASSWORD"),
            Db = Clamp(ReadInt(options, "VALKEY_DB", 0), 0, 15, 0),
            TtlMinutes = Clamp(ReadInt(options, "CACHE_TTL_MINUTES", 30), 1, int.MaxValue, 30),
            CatalogueTtlMinutes = Clamp(ReadInt(options, "CACHE_CATALOGUE_TTL_MINUTES", 360), 1, int.MaxValue, 360),
            WatchedStateTtlSeconds = Clamp(ReadInt(options, "CACHE_WATCHED_TTL_SECONDS", 20), 1, 3600, 20),
            ExcludedTables = ReadList(options, "CACHE_EXCLUDED_TABLES", DefaultExcludedTables),
            KeyPrefix = Read(options, "CACHE_KEY_PREFIX") ?? "JF_",
            Compression = ReadBool(options, "CACHE_COMPRESSION", defaultValue: true),
            Debug = ReadBool(options, "CACHE_DEBUG", defaultValue: false)
        };
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        return value < min || value > max ? fallback : value;
    }

    private static string? Read(ICollection<CustomDatabaseOption>? options, string key)
    {
        var fromOptions = options?.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        return string.IsNullOrWhiteSpace(fromOptions) ? Environment.GetEnvironmentVariable(key) : fromOptions;
    }

    private static bool ReadBool(ICollection<CustomDatabaseOption>? options, string key, bool defaultValue)
    {
        var value = Read(options, key);
        return value is null ? defaultValue : value.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(ICollection<CustomDatabaseOption>? options, string key, int defaultValue)
    {
        var value = Read(options, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    private static string[] ReadList(ICollection<CustomDatabaseOption>? options, string key, string[] defaultValue)
    {
        var value = Read(options, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
