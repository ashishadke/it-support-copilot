using System.Text;
using System.Text.Json;

namespace ITSupport.Api.Services;

// One row of the "exam": a question plus what a correct answer should look like.
public record EvalCase(
    string Name,
    string Question,
    string? ExpectedRoute = null,        // rag / tool / direct (optional)
    string[]? ExpectedKeywords = null,   // all of these must appear in the answer
    bool MustSayDontKnow = false);       // true for out-of-scope questions

// The scored result for one case.
public record EvalCaseResult(
    string Name, string Question, string ActualRoute, string Answer,
    bool RoutePass, bool KeywordPass, bool DontKnowPass, bool Pass);

// The overall report.
public record EvalReport(
    int Total, int Passed, int RoutePassed, int KeywordPassed,
    IReadOnlyList<EvalCaseResult> Cases);

// Runs the eval set against the live agent and scores each answer.
// This is automated regression testing: change a prompt/model and re-run to
// see instantly whether answer quality went up or down.
public class EvaluationService
{
    private readonly RagService _rag;
    private readonly IWebHostEnvironment _env;

    public EvaluationService(RagService rag, IWebHostEnvironment env)
    {
        _rag = rag;
        _env = env;
    }

    public async Task<EvalReport> RunAsync()
    {
        var cases = LoadCases();
        var results = new List<EvalCaseResult>();

        foreach (var c in cases)
        {
            // 1. What path did the router choose?
            var route = await _rag.RouteAsync(c.Question);

            // 2. What does the user actually get? (drive the real streaming path and collect it)
            var sb = new StringBuilder();
            await foreach (var token in _rag.AskStreamingAsync(c.Question))
                sb.Append(token);
            var answer = sb.ToString();
            var lower = answer.ToLowerInvariant();

            // 3. Score the three checks.
            bool routePass = c.ExpectedRoute is null ||
                string.Equals(route, c.ExpectedRoute, StringComparison.OrdinalIgnoreCase);
            bool keywordPass = c.ExpectedKeywords is null ||
                c.ExpectedKeywords.All(k => lower.Contains(k.ToLowerInvariant()));
            bool dontKnowPass = !c.MustSayDontKnow || IndicatesUnknown(lower);
            bool pass = routePass && keywordPass && dontKnowPass;

            Console.WriteLine($"[EVAL] {c.Name}: route={routePass} keywords={keywordPass} dontKnow={dontKnowPass} => {(pass ? "PASS" : "FAIL")}");
            results.Add(new EvalCaseResult(c.Name, c.Question, route, answer,
                routePass, keywordPass, dontKnowPass, pass));
        }

        return new EvalReport(
            results.Count,
            results.Count(r => r.Pass),
            results.Count(r => r.RoutePass),
            results.Count(r => r.KeywordPass),
            results);
    }

    // For out-of-scope questions, a GOOD answer admits it doesn't know
    // (rather than inventing facts from unrelated chunks).
    private static bool IndicatesUnknown(string lower) =>
        new[]
        {
            "don't know", "do not know", "not in the context", "no information",
            "couldn't find", "could not find", "don't have", "not available",
            "isn't in", "is not in", "unable to"
        }.Any(lower.Contains);

    // Load the exam from eval-set.json (editable without recompiling).
    private List<EvalCase> LoadCases()
    {
        var path = Path.Combine(_env.ContentRootPath, "eval-set.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<EvalCase>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
}
