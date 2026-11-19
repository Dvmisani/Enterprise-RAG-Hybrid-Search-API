using System.Text.RegularExpressions;

namespace EnterpriseRag.Api.Services;

public sealed record CitationVerificationResult(bool IsValid, IReadOnlyList<string> InvalidCitations, int VerifiedCount);

public static partial class CitationVerifier
{
    [GeneratedRegex(@"\[([^\[\]\|]+?)\s*\|\s*Chunk\s*#(\d+)\]", RegexOptions.Compiled)]
    private static partial Regex CitationPattern();

    public static CitationVerificationResult Verify(string answer, IReadOnlyList<RetrievedChunkContext> retrievedChunks)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return new CitationVerificationResult(false, [], 0);

        var allowed = retrievedChunks
            .Select(c => $"{c.DocumentTitle.Trim()}|#{c.ChunkIndex}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = CitationPattern().Matches(answer);
        if (matches.Count == 0)
        {
            return new CitationVerificationResult(false, ["<no_inline_citations_found>"], 0);
        }

        var invalid = new List<string>();
        int verified = 0;

        foreach (Match m in matches)
        {
            var title = m.Groups[1].Value.Trim();
            var idx = m.Groups[2].Value.Trim();
            var key = $"{title}|#{idx}";

            if (allowed.Contains(key))
                verified++;
            else
                invalid.Add(m.Value);
        }

        return new CitationVerificationResult(invalid.Count == 0 && verified > 0, invalid, verified);
    }
}
