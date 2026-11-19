using System.Net.Http.Json;
using EnterpriseRag.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace EnterpriseRag.IntegrationTests;

[Collection("SqlServer2025")]
public sealed class RagEvaluationTests(SqlServer2025Fixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task SixteenQuestionBenchmark_AchievesFullHitRateAndRefusalAccuracy()
    {
        _ = fixture.Container.GetConnectionString();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development").UseSetting("ConnectionStrings:DefaultConnection", fixture.Container.GetConnectionString()).UseSetting("Gemini:ApiKey", ""));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "demo-public-key-2026");

        var policyDoc = """
            Section 1 (POPIA Section 14 Retention): All customer KYC records, identity verification logs, and double-entry wallet ledger audit trails must be retained in immutable append-only storage for exactly 5 years following account closure. Any early deletion request must be escalated to the Information Officer.

            Section 2 (Treasury Dual-Control & SARB Reporting): Any single outbound ZAR settlement exceeding R250,000.00 requires dual cryptographic approval from two senior treasury officers and triggers automated SARB FinSurv reporting within 24 hours. Cross-border SWIFT instructions above R1,000,000.00 require additional Reserve Bank Balance of Payments (BoP) category validation.

            Section 3 (Incident Escalation SLA): Suspected ledger tampering or unauthorized API key usage must be isolated within 15 minutes and reported to the Information Regulator under POPIA Section 22 as soon as reasonably possible.
            """;

        var ingest = await client.PostAsJsonAsync("/api/documents/ingest", new IngestDocumentRequest(
            "ZAR Treasury & POPIA Compliance Standard (POL-2026-09)",
            "internal://compliance/POL-2026-09",
            policyDoc));
        ingest.EnsureSuccessStatusCode();

        var cases = new (string Question, bool ExpectGrounded, string? ExpectedPhrase)[]
        {
            // 10 In-Domain Grounded Questions
            ("How long must customer KYC records and ledger audit trails be retained under POPIA Section 14?", true, "5 years"),
            ("Who must approve early deletion requests for KYC records?", true, "Information Officer"),
            ("What ZAR settlement amount requires dual cryptographic approval from senior treasury officers?", true, "R250,000.00"),
            ("Within how many hours must SARB FinSurv reporting be triggered for large outbound settlements?", true, "24 hours"),
            ("What threshold triggers Reserve Bank Balance of Payments BoP category validation on SWIFT instructions?", true, "R1,000,000.00"),
            ("Within how many minutes must suspected ledger tampering or unauthorized API key usage be isolated?", true, "15 minutes"),
            ("Under which POPIA section are security incidents reported to the Information Regulator?", true, "POPIA Section 22"),
            ("How many senior treasury officers are required for dual cryptographic approval?", true, "two senior treasury officers"),
            ("In what type of storage must double-entry wallet ledger audit trails be kept?", true, "immutable append-only storage"),
            ("What reporting is required for cross-border SWIFT instructions above R1,000,000.00?", true, "Balance of Payments"),

            // 6 Out-of-Domain / Adversarial Refusal Questions
            ("What is the orbital velocity of Mars around the Sun?", false, null),
            ("Who won the 2023 Rugby World Cup final in Paris?", false, null),
            ("What is the standard corporate income tax rate in Germany?", false, null),
            ("How do you configure BGP peering on a Cisco IOS router?", false, null),
            ("What is the melting point of titanium in degrees Celsius?", false, null),
            ("Can you recommend a recipe for Cape Malay chicken curry?", false, null)
        };

        int inDomainTotal = 0, inDomainHits = 0;
        int outDomainTotal = 0, outDomainRefused = 0;

        foreach (var (q, expectGrounded, expectedPhrase) in cases)
        {
            var res = await client.PostAsJsonAsync("/api/query", new RagQueryRequest(q, TopK: 3));
            res.EnsureSuccessStatusCode();
            var payload = await res.Content.ReadFromJsonAsync<RagQueryResponse>();

            if (expectGrounded)
            {
                inDomainTotal++;
                if (payload!.Grounded &&
                    payload.Citations.Count > 0 &&
                    (expectedPhrase is null || payload.Answer.Contains(expectedPhrase, StringComparison.OrdinalIgnoreCase)))
                {
                    inDomainHits++;
                }
            }
            else
            {
                outDomainTotal++;
                if (!payload!.Grounded && payload.Citations.Count == 0)
                {
                    outDomainRefused++;
                }
            }
        }

        double hitRate = (double)inDomainHits / inDomainTotal * 100.0;
        double refusalAccuracy = (double)outDomainRefused / outDomainTotal * 100.0;
        output.WriteLine($"Benchmark Results -> In-Domain Retrieval Hit Rate: {inDomainHits}/{inDomainTotal} ({hitRate:F1}%), Out-of-Domain Refusal Accuracy: {outDomainRefused}/{outDomainTotal} ({refusalAccuracy:F1}%)");

        Assert.Equal(inDomainTotal, inDomainHits);
        Assert.Equal(outDomainTotal, outDomainRefused);
    }
}

