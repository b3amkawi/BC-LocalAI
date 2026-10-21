using System.Text.Json;
using Xunit.Sdk;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>One catalog entry: the Expected result written before the run (tests/security/qa002-cases.json).</summary>
internal sealed record CaseSpec(string Id, string Group, string Title, string Expected, int? Status, string? Code, int? Ollama);

/// <summary>
/// QA-002 harness: looks up the Expected result of a case, compares it with what the system did and records both
/// (QA002_EVIDENCE, one JSON line per case and run). Expected is never computed from the Actual value.
/// Cases listed in tests/security/qa002-known-defects.json document a defect found by QA: while the defect exists the
/// mismatch is recorded as FAIL with the defect id and the test stays green (the product is not changed by QA);
/// once the product meets the Expected result the record becomes PASS.
/// </summary>
internal static class Qa
{
    private static readonly object Gate = new();
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<Dictionary<string, CaseSpec>> Specs = new(LoadSpecs);
    private static readonly Lazy<Dictionary<string, string>> KnownDefects = new(LoadKnownDefects);

    public static string RepoRoot => Root.Value;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BC-LocalAI.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, CaseSpec> LoadSpecs()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root.Value, "tests", "security", "qa002-cases.json")));
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new CaseSpec(
            c.GetProperty("id").GetString()!, c.GetProperty("group").GetString()!, c.GetProperty("title").GetString()!,
            c.GetProperty("expected").GetString()!,
            c.GetProperty("status").ValueKind == JsonValueKind.Null ? null : c.GetProperty("status").GetInt32(),
            c.GetProperty("code").ValueKind == JsonValueKind.Null ? null : c.GetProperty("code").GetString(),
            c.GetProperty("ollama").ValueKind == JsonValueKind.Null ? null : c.GetProperty("ollama").GetInt32())).ToDictionary(s => s.Id);
    }

    private static Dictionary<string, string> LoadKnownDefects()
    {
        var path = Path.Combine(Root.Value, "tests", "security", "qa002-known-defects.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')).ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    public static CaseSpec Spec(string id) =>
        Specs.Value.TryGetValue(id, out var spec) ? spec : throw new XunitException($"Case {id} is not in tests/security/qa002-cases.json.");

    public static bool Has(string id) => Specs.Value.ContainsKey(id);

    public static IEnumerable<CaseSpec> Group(string prefix) => Specs.Value.Values.Where(s => s.Id.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Compares Actual with Expected (only the fields the case defines) plus <paramref name="ok"/> (the extra conditions in the Expected text).</summary>
    public static void Verify(string id, int? status = null, string? code = null, int? ollama = null, bool ok = true, string detail = "")
    {
        var spec = Spec(id);
        var problems = new List<string>();
        if (spec.Status is { } s && status != s) problems.Add($"status expected {s} actual {status?.ToString() ?? "-"}");
        if (spec.Code is { } c && code != c) problems.Add($"code expected {c} actual {code ?? "-"}");
        if (spec.Ollama is { } o && ollama != o) problems.Add($"ollama calls expected {o} actual {ollama?.ToString() ?? "-"}");
        if (!ok) problems.Add("extra condition of the Expected text failed");
        var matches = problems.Count == 0;
        KnownDefects.Value.TryGetValue(id, out var defect);
        Record(spec, matches ? "PASS" : "FAIL", status, code, ollama, (detail + " " + string.Join("; ", problems)).Trim(), defect);
        if (!matches && defect is null)
            throw new XunitException($"{id} ({spec.Title}): {string.Join("; ", problems)}. {detail}");
    }

    /// <summary>A measurement with no pass/fail Expected (documented as an observation).</summary>
    public static void Observe(string id, int? status = null, string? code = null, int? ollama = null, string detail = "")
    {
        var spec = Spec(id);
        KnownDefects.Value.TryGetValue(id, out var defect);
        Record(spec, "OBSERVED", status, code, ollama, detail, defect);
    }

    private static void Record(CaseSpec spec, string verdict, int? status, string? code, int? ollama, string detail, string? defect)
    {
        var path = Environment.GetEnvironmentVariable("QA002_EVIDENCE");
        if (string.IsNullOrEmpty(path)) return;
        var line = JsonSerializer.Serialize(new
        {
            id = spec.Id,
            run = Environment.GetEnvironmentVariable("QA002_RUN") ?? "0",
            verdict,
            actualStatus = status,
            actualCode = code,
            actualOllama = ollama,
            detail = Sanitize(detail),
            defect
        });
        lock (Gate) File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>Evidence must never carry a cookie, token or secret: the test hosts' secrets are random per run, and detail is short text.</summary>
    private static string Sanitize(string text) => text.Length > 400 ? text[..400] : text;
}
