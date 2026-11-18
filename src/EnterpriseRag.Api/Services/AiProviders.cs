using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseRag.Api.Services;

public sealed class GeminiOptions
{
    public string? ApiKey { get; set; }
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";
    public string ChatModel { get; set; } = "gemini-2.5-flash";
    public int Dimensions { get; set; } = 768;
}

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, string taskType, CancellationToken ct = default);
}

public sealed record RetrievedChunkContext(
    Guid ChunkId,
    string DocumentTitle,
    string SourceUri,
    int ChunkIndex,
    string Content,
    double VectorSimilarity,
    double KeywordScore,
    double RrfScore);

public interface IChatCompletionProvider
{
    string ProviderName { get; }
    Task<string> GenerateGroundedAnswerAsync(string question, IReadOnlyList<RetrievedChunkContext> contexts, CancellationToken ct = default);
}

public sealed class GeminiEmbeddingProvider(HttpClient http, GeminiOptions options) : IEmbeddingProvider
{
    public async Task<float[]> EmbedAsync(string text, string taskType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return DeterministicHashEmbedding.Create(text, options.Dimensions);
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{options.EmbeddingModel}:embedContent?key={options.ApiKey}";
        var payload = new
        {
            model = $"models/{options.EmbeddingModel}",
            content = new { parts = new[] { new { text } } },
            taskType,
            outputDimensionality = options.Dimensions
        };

        using var res = await http.PostAsJsonAsync(url, payload, ct);
        res.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var values = doc.RootElement.GetProperty("embedding").GetProperty("values");

        var vec = new float[values.GetArrayLength()];
        int i = 0;
        foreach (var v in values.EnumerateArray())
        {
            vec[i++] = v.GetSingle();
        }

        return DeterministicHashEmbedding.NormalizeL2(vec);
    }
}

public sealed class GeminiChatCompletionProvider(HttpClient http, GeminiOptions options) : IChatCompletionProvider
{
    private string _activeProvider = string.IsNullOrWhiteSpace(options.ApiKey)
        ? "DeterministicExtractiveFallback"
        : $"Gemini:{options.ChatModel}";

    public string ProviderName => _activeProvider;

    public async Task<string> GenerateGroundedAnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunkContext> contexts,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _activeProvider = "DeterministicExtractiveFallback";
            return BuildExtractiveCitationAnswer(contexts);
        }

        var contextBlock = string.Join("\n\n", contexts.Select(c =>
            $"[{c.DocumentTitle} | Chunk #{c.ChunkIndex}] (Score: {c.RrfScore:F4})\n{c.Content}"));

        var prompt = $"""
            You are an enterprise compliance and knowledge assistant.
            Answer the user's question using ONLY the provided context passages below.
            Rules:
            1. Every factual claim MUST include an inline citation in the exact format [Document Title | Chunk #N].
            2. Do NOT use outside knowledge, assumptions, or extrapolation.
            3. If the provided passages do not contain enough evidence to answer the question completely, reply ONLY with: INSUFFICIENT_GROUNDING

            Context Passages:
            {contextBlock}

            User Question:
            {question}
            """;

        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.1 }
        };

        var modelsToTry = await DiscoverCandidateModelsAsync(ct);

        foreach (var model in modelsToTry)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(15));

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={options.ApiKey}";
                using var res = await http.PostAsJsonAsync(url, payload, attemptCts.Token);
                var body = await res.Content.ReadAsStringAsync(attemptCts.Token);

                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    var text = doc.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();

                    _activeProvider = $"Gemini:{model}";
                    return string.IsNullOrWhiteSpace(text) ? "INSUFFICIENT_GROUNDING" : text.Trim();
                }

                int code = (int)res.StatusCode;
                if (code is not (404 or 429 or 500 or 503))
                {
                    throw new InvalidOperationException($"Gemini '{model}' failed with HTTP {code}: {body}");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Individual model timed out after 15s; try next discovered model
            }
        }

        // Graceful degradation if all hosted Gemini models are experiencing transient 503/timeout
        _activeProvider = "ExtractiveCitationFallback (Gemini 503/Timeout)";
        return BuildExtractiveCitationAnswer(contexts);
    }

    private async Task<List<string>> DiscoverCandidateModelsAsync(CancellationToken ct)
    {
        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ChatModel))
            ordered.Add(options.ChatModel);

        try
        {
            using var listCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listCts.CancelAfter(TimeSpan.FromSeconds(8));

            var listUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key={options.ApiKey}";
            using var res = await http.GetAsync(listUrl, listCts.Token);
            if (res.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(listCts.Token), cancellationToken: listCts.Token);
                if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                {
                    foreach (var m in modelsArr.EnumerateArray())
                    {
                        var fullName = m.GetProperty("name").GetString() ?? "";
                        var slug = fullName.StartsWith("models/") ? fullName["models/".Length..] : fullName;

                        var supportsGenerate = m.TryGetProperty("supportedGenerationMethods", out var methods)
                            && methods.EnumerateArray().Any(x => x.GetString() == "generateContent");

                        if (supportsGenerate && slug.Contains("flash", StringComparison.OrdinalIgnoreCase) && !slug.Contains("tts", StringComparison.OrdinalIgnoreCase) && !slug.Contains("image", StringComparison.OrdinalIgnoreCase) && !slug.Contains("audio", StringComparison.OrdinalIgnoreCase) && !slug.Contains("customtools", StringComparison.OrdinalIgnoreCase) && !ordered.Contains(slug))
                            ordered.Add(slug);
                    }
                }
            }
        }
        catch
        {
            // Fall through to static defaults if discovery fails
        }

        foreach (var fallback in new[] { "gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.0-flash" })
        {
            if (!ordered.Contains(fallback))
                ordered.Add(fallback);
        }

        return ordered.Take(4).ToList();
    }

    private static string BuildExtractiveCitationAnswer(IReadOnlyList<RetrievedChunkContext> contexts)
    {
        var top = contexts[0];
        return $"Based on [{top.DocumentTitle} | Chunk #{top.ChunkIndex}]: {top.Content}";
    }
}

public static class DeterministicHashEmbedding
{
    public static float[] Create(string text, int dimensions = 768)
    {
        var vec = new float[dimensions];
        var tokens = text.ToLowerInvariant()
            .Split([' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\'', '-', '/'],
                   StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            uint h1 = BitConverter.ToUInt32(hash, 0);
            uint h2 = BitConverter.ToUInt32(hash, 4);
            int idx = (int)(h1 % (uint)dimensions);
            float sign = (h2 & 1) == 0 ? 1.0f : -1.0f;
            vec[idx] += sign;
        }

        return NormalizeL2(vec);
    }

    public static float[] NormalizeL2(float[] vec)
    {
        double sumSq = 0;
        for (int i = 0; i < vec.Length; i++) sumSq += vec[i] * vec[i];
        if (sumSq <= 1e-12) return vec;

        float norm = (float)Math.Sqrt(sumSq);
        for (int i = 0; i < vec.Length; i++) vec[i] /= norm;
        return vec;
    }
}

