namespace EnterpriseRag.Api.Services;

public sealed record TextChunkResult(int Index, string Content, int TokenEstimate);

public static class SlidingWindowChunker
{
    public static IReadOnlyList<TextChunkResult> Chunk(string text, int maxChars = 750, int overlapChars = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxChars)
            return [new TextChunkResult(0, normalized, EstimateTokens(normalized))];

        var chunks = new List<TextChunkResult>();
        var start = 0;
        var index = 0;

        while (start < normalized.Length)
        {
            var remaining = normalized.Length - start;
            var length = Math.Min(maxChars, remaining);
            var end = start + length;

            if (end < normalized.Length)
            {
                var sentenceBreak = normalized.LastIndexOfAny(['.', '!', '?', '\n'], end - 1, Math.Min(length, 220));
                if (sentenceBreak > start + (maxChars / 2))
                {
                    end = sentenceBreak + 1;
                }
                else
                {
                    var spaceBreak = normalized.LastIndexOf(' ', end - 1, Math.Min(length, 100));
                    if (spaceBreak > start + (maxChars / 2))
                        end = spaceBreak;
                }
            }

            var slice = normalized[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(slice))
            {
                chunks.Add(new TextChunkResult(index++, slice, EstimateTokens(slice)));
            }

            if (end >= normalized.Length)
                break;

            start = Math.Max(end - overlapChars, start + 1);
        }

        return chunks;
    }

    public static int EstimateTokens(string text) => Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
}
