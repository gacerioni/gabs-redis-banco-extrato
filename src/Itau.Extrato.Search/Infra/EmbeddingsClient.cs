using OpenAI;
using OpenAI.Embeddings;

namespace Itau.Extrato.Search.Infra;

/// <summary>
/// Wrapper sobre o client de embeddings da OpenAI. Default
/// <c>text-embedding-3-small</c> (1536 dimensões), que funciona muito bem
/// em PT-BR e é barato (~$0.02 / 1M tokens).
/// </summary>
public sealed class EmbeddingsClient
{
    public const string DefaultModel = "text-embedding-3-small";
    public const int DefaultDim = 1536;

    private readonly EmbeddingClient _client;
    public string Model { get; }
    public int Dim { get; }

    public EmbeddingsClient(string apiKey, string model = DefaultModel, int dim = DefaultDim)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OPENAI_API_KEY é obrigatório.", nameof(apiKey));
        Model = model;
        Dim = dim;
        _client = new OpenAIClient(apiKey).GetEmbeddingClient(model);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var resp = await _client.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return resp.Value.ToFloats().ToArray();
    }

    /// <summary>Batch embedding em uma única chamada — barato e MUITO mais rápido que N requests.</summary>
    public async Task<List<float[]>> EmbedManyAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var list = texts.ToList();
        if (list.Count == 0) return new List<float[]>();
        var resp = await _client.GenerateEmbeddingsAsync(list, cancellationToken: ct);
        return resp.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }

    /// <summary>
    /// Serializa float[] como bytes pequeno-endian, formato que o VECTOR field do
    /// RediSearch espera (FLOAT32). Necessário pros KNN params.
    /// </summary>
    public static byte[] ToBytes(float[] vec)
    {
        var bytes = new byte[vec.Length * sizeof(float)];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
