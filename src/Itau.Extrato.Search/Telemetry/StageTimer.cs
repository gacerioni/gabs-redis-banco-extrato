using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Itau.Extrato.Search.Telemetry;

/// <summary>
/// Per-request stage timer: using-blocks nomeados, cada um cronometrado, e
/// somatórios separados pra Redis ops vs outras. Vira o bloco `metrics` da
/// response, útil pra demonstrar onde o tempo vai e onde o cache ajuda.
/// </summary>
public sealed class StageTimer
{
    private readonly Stopwatch _global = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _stages = new();
    private readonly object _lock = new();
    private double _redisTotalMs = 0;
    private int _redisOps = 0;

    public IDisposable Stage(string name, bool isRedis = false) => new Span(this, name, isRedis);
    public IDisposable RedisStage(string name) => new Span(this, name, isRedis: true);

    public void Record(string name, double ms, bool isRedis = false)
    {
        lock (_lock)
        {
            _stages[name] = (_stages.TryGetValue(name, out var prev) ? prev : 0) + ms;
            if (isRedis) { _redisTotalMs += ms; _redisOps += 1; }
        }
    }

    public RequestMetrics Build(
        string? mode = null,
        string? llmRewriteJson = null,
        int? totalResults = null,
        double? llmCostUsd = null)
    {
        _global.Stop();
        return new RequestMetrics(
            TotalMs: Round(_global.Elapsed.TotalMilliseconds),
            Stages: _stages.ToDictionary(kv => kv.Key, kv => Round(kv.Value)),
            RedisTotalMs: Round(_redisTotalMs),
            RedisOps: _redisOps,
            Mode: mode,
            LlmRewriteJson: llmRewriteJson,
            TotalResults: totalResults,
            LlmCostUsd: llmCostUsd);
    }

    private static double Round(double x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    private sealed class Span : IDisposable
    {
        private readonly StageTimer _owner;
        private readonly string _name;
        private readonly bool _isRedis;
        private readonly Stopwatch _sw;
        public Span(StageTimer owner, string name, bool isRedis)
        { _owner = owner; _name = name; _isRedis = isRedis; _sw = Stopwatch.StartNew(); }
        public void Dispose()
        { _sw.Stop(); _owner.Record(_name, _sw.Elapsed.TotalMilliseconds, _isRedis); }
    }
}

public sealed record RequestMetrics(
    [property: JsonPropertyName("total_ms")] double TotalMs,
    [property: JsonPropertyName("stages")] IReadOnlyDictionary<string, double> Stages,
    [property: JsonPropertyName("redis_total_ms")] double RedisTotalMs,
    [property: JsonPropertyName("redis_ops")] int RedisOps,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("llm_rewrite_json")] string? LlmRewriteJson,
    [property: JsonPropertyName("total_results")] int? TotalResults,
    [property: JsonPropertyName("llm_cost_usd")] double? LlmCostUsd);
