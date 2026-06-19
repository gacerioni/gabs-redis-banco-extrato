using StackExchange.Redis;

namespace Itau.Extrato.Search.Infra;

/// <summary>
/// Singleton wrapper sobre ConnectionMultiplexer. Lê REDIS_URL (rediss:// ou
/// redis://, com auth e db opcional). Suporta cluster / TLS / Redis Cloud.
/// </summary>
public sealed class RedisConnection : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _muxer;
    internal string ConnectionString { get; }

    public RedisConnection(string connectionString)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _muxer = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(BuildOptions(connectionString)));
    }

    public ConnectionMultiplexer Multiplexer => _muxer.Value;
    public IDatabase Db => _muxer.Value.GetDatabase();

    public TimeSpan Ping() => Db.Ping();

    public TimeSpan? TryPing()
    {
        try { return Db.Ping(); }
        catch { return null; }
    }

    private static ConfigurationOptions BuildOptions(string url)
    {
        if (url.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            var opts = new ConfigurationOptions
            {
                EndPoints = { { uri.Host, uri.IsDefaultPort ? 6379 : uri.Port } },
                Ssl = uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase),
                AbortOnConnectFail = false,
                ConnectTimeout = 10000,
                SyncTimeout = 15000,
                AsyncTimeout = 15000,
            };
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                if (parts.Length == 2)
                {
                    opts.User = Uri.UnescapeDataString(parts[0]);
                    opts.Password = Uri.UnescapeDataString(parts[1]);
                }
                else
                {
                    opts.Password = Uri.UnescapeDataString(parts[0]);
                }
            }
            if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath.Length > 1
                && int.TryParse(uri.AbsolutePath[1..], out var db))
            {
                opts.DefaultDatabase = db;
            }
            return opts;
        }
        return ConfigurationOptions.Parse(url);
    }

    public void Dispose()
    {
        if (_muxer.IsValueCreated) _muxer.Value.Dispose();
    }
}
