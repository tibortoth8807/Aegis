using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aegis.PerformanceTests.Framework;

/// <summary>
/// A small load-testing framework: virtual users run weighted steps against an HTTP API for a while, every call is
/// timed, and the run produces latency percentiles, throughput and error rates per step - checked against
/// thresholds, written as JSON and Markdown (and into the GitHub Actions job summary).
/// It runs in-process against the test host (fast feedback in CI) or against a deployed URL (real numbers).
/// </summary>
public sealed class VirtualUser(int id, HttpClient client, int seed)
{
    public int Id { get; } = id;
    public HttpClient Client { get; } = client;
    public Random Random { get; } = new(seed);
    /// <summary>Per-user state between steps (ids created earlier, etc.).</summary>
    public Dictionary<string, object> State { get; } = new();
}

/// <param name="Name">Name in the report.</param>
/// <param name="Send">One call. A non-success status counts as an error unless <paramref name="Accept"/> says otherwise.</param>
/// <param name="Weight">Relative frequency.</param>
public sealed record Step(string Name, Func<VirtualUser, Task<HttpResponseMessage>> Send, double Weight = 1, Func<HttpResponseMessage, bool>? Accept = null);

/// <param name="Sequential">True: each user runs the steps in order, round and round (a user journey). False: weighted random mix.</param>
public sealed record Scenario(string Name, Func<int, HttpClient> ClientFor, IReadOnlyList<Step> Steps, Func<VirtualUser, Task>? Setup = null, bool Sequential = false);

/// <param name="VirtualUsers">Concurrent users.</param>
/// <param name="Duration">Run time after ramp-up starts.</param>
/// <param name="RampUp">Users start evenly spread over this period.</param>
/// <param name="ThinkTime">Pause between a user's calls (0 = as fast as possible).</param>
public sealed record LoadProfile(int VirtualUsers, TimeSpan Duration, TimeSpan RampUp, TimeSpan ThinkTime = default);

public sealed record Sample(string Step, double Milliseconds, int Status, bool Ok, string? Error);

public sealed record StepStats(
    string Step, int Count, int Errors, double ErrorRate, double Rps, double Min, double Mean, double P50, double P90, double P95, double P99, double Max);

public static class LoadRunner
{
    public static async Task<LoadReport> RunAsync(Scenario scenario, LoadProfile profile, CancellationToken ct = default)
    {
        var samples = new ConcurrentBag<Sample>();
        var total = scenario.Steps.Sum(s => s.Weight);
        var started = Stopwatch.StartNew();
        var deadline = profile.Duration;

        async Task User(int i)
        {
            var delay = profile.VirtualUsers <= 1 ? TimeSpan.Zero : profile.RampUp * i / (profile.VirtualUsers - 1);
            await Task.Delay(delay, ct);
            using var client = scenario.ClientFor(i);
            var user = new VirtualUser(i, client, 1000 + i);
            if (scenario.Setup is not null) await scenario.Setup(user);
            var n = 0;
            while (started.Elapsed < deadline && !ct.IsCancellationRequested)
            {
                var step = scenario.Sequential ? scenario.Steps[n++ % scenario.Steps.Count] : Pick(scenario.Steps, user.Random.NextDouble() * total);
                var sw = Stopwatch.StartNew();
                try
                {
                    using var response = await step.Send(user);
                    sw.Stop();
                    var ok = step.Accept?.Invoke(response) ?? response.IsSuccessStatusCode;
                    samples.Add(new Sample(step.Name, sw.Elapsed.TotalMilliseconds, (int)response.StatusCode, ok, ok ? null : $"HTTP {(int)response.StatusCode}"));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    sw.Stop();
                    samples.Add(new Sample(step.Name, sw.Elapsed.TotalMilliseconds, 0, false, e.GetType().Name));
                }
                if (profile.ThinkTime > TimeSpan.Zero) await Task.Delay(profile.ThinkTime, ct);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, profile.VirtualUsers).Select(User));
        return new LoadReport(scenario.Name, profile, started.Elapsed, samples.ToList());
    }

    private static Step Pick(IReadOnlyList<Step> steps, double r)
    {
        foreach (var s in steps)
        {
            if (r < s.Weight) return s;
            r -= s.Weight;
        }
        return steps[^1];
    }
}

public sealed record Threshold(string Description, Func<LoadReport, double> Measure, double Limit, bool Below = true)
{
    public static Threshold P95(string step, double ms) => new($"p95 of {step} < {ms} ms", r => r.Step(step).P95, ms);

    public static Threshold P95All(double ms) => new($"p95 of every step < {ms} ms", r => r.Steps.Max(s => s.P95), ms);

    public static Threshold ErrorRate(double max) => new($"error rate < {max:P1}", r => r.ErrorRate, max);

    public static Threshold MinThroughput(double rps) => new($"throughput > {rps} req/s", r => r.Rps, rps, Below: false);
}

public sealed class LoadReport(string scenario, LoadProfile profile, TimeSpan elapsed, IReadOnlyList<Sample> samples)
{
    public string Scenario { get; } = scenario;
    public LoadProfile Profile { get; } = profile;
    public TimeSpan Elapsed { get; } = elapsed;
    public int Total => samples.Count;
    public int Errors => samples.Count(s => !s.Ok);
    public double ErrorRate => Total == 0 ? 1 : (double)Errors / Total;
    public double Rps => Total / Math.Max(0.001, Elapsed.TotalSeconds);

    public IReadOnlyList<StepStats> Steps { get; } = samples.GroupBy(s => s.Step).OrderBy(g => g.Key).Select(g => Stats(g.Key, g.ToList(), elapsed)).ToList();

    public IReadOnlyDictionary<string, int> ErrorKinds { get; } =
        samples.Where(s => !s.Ok).GroupBy(s => $"{s.Step}: {s.Error}").ToDictionary(g => g.Key, g => g.Count());

    public StepStats Step(string name) => Steps.FirstOrDefault(s => s.Step == name) ?? throw new ArgumentException($"No samples for step {name}.");

    private static StepStats Stats(string step, List<Sample> s, TimeSpan elapsed)
    {
        var ms = s.Select(x => x.Milliseconds).Order().ToArray();
        var errors = s.Count(x => !x.Ok);
        return new StepStats(step, s.Count, errors, (double)errors / s.Count, s.Count / Math.Max(0.001, elapsed.TotalSeconds),
            ms[0], ms.Average(), Percentile(ms, 50), Percentile(ms, 90), Percentile(ms, 95), Percentile(ms, 99), ms[^1]);
    }

    /// <summary>Nearest-rank percentile of sorted values.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        var rank = (int)Math.Ceiling(p / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <returns>The violated thresholds (empty = passed).</returns>
    public IReadOnlyList<string> Check(params Threshold[] thresholds) =>
        thresholds.Select(t => (t, value: t.Measure(this)))
            .Where(x => x.t.Below ? !(x.value < x.t.Limit) : !(x.value > x.t.Limit))
            .Select(x => $"{x.t.Description}: measured {x.value.ToString("0.###", CultureInfo.InvariantCulture)}")
            .ToList();

    public string ToMarkdown(IReadOnlyList<string>? violations = null)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"### {Scenario}").AppendLine();
        sb.AppendLine(inv, $"{Profile.VirtualUsers} virtual users, {Elapsed.TotalSeconds:0.0} s, {Total} requests, **{Rps:0.0} req/s**, errors {ErrorRate:P2}").AppendLine();
        sb.AppendLine("| Step | Count | Errors | req/s | p50 ms | p90 ms | p95 ms | p99 ms | max ms |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in Steps)
            sb.AppendLine(inv, $"| {s.Step} | {s.Count} | {s.Errors} | {s.Rps:0.0} | {s.P50:0.0} | {s.P90:0.0} | {s.P95:0.0} | {s.P99:0.0} | {s.Max:0.0} |");
        if (ErrorKinds.Count > 0)
        {
            sb.AppendLine().AppendLine("Errors:");
            foreach (var (kind, count) in ErrorKinds) sb.AppendLine(inv, $"- {kind} × {count}");
        }
        if (violations is not null)
            sb.AppendLine().AppendLine(violations.Count == 0 ? "✅ All thresholds met." : "❌ " + string.Join("; ", violations));
        return sb.ToString();
    }

    /// <summary>Writes perf-results/{scenario}.json and .md, and appends to the GitHub job summary when running in Actions.</summary>
    public void Save(IReadOnlyList<string> violations)
    {
        var dir = Environment.GetEnvironmentVariable("AEGIS_PERF_OUT") ?? Path.Combine(AppContext.BaseDirectory, "perf-results");
        Directory.CreateDirectory(dir);
        var file = string.Concat(Scenario.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(dir, file + ".json"), JsonSerializer.Serialize(new
        {
            Scenario, Profile.VirtualUsers, Seconds = Elapsed.TotalSeconds, Total, Errors, Rps, ErrorRate, Steps, ErrorKinds, Violations = violations,
        }, new JsonSerializerOptions { WriteIndented = true }));
        var md = ToMarkdown(violations);
        File.WriteAllText(Path.Combine(dir, file + ".md"), md);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary)) File.AppendAllText(summary, md + Environment.NewLine);
    }
}
