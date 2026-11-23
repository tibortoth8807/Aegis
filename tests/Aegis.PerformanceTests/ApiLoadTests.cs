using System.Net;
using System.Net.Http.Json;
using Aegis.Api.Hosting;
using Aegis.Application.Access;
using Aegis.Application.Assistant;
using Aegis.Application.Common;
using Aegis.Application.Employees;
using Aegis.Infrastructure.Persistence;
using Aegis.IntegrationTests;
using Aegis.PerformanceTests.Framework;

namespace Aegis.PerformanceTests;

/// <summary>
/// Load scenarios with pass/fail thresholds. By default they run in-process against the test host (SQLite in
/// memory), which catches regressions such as N+1 queries or a missing index cheaply in CI.
/// Set AEGIS_PERF_URL to run the same scenarios against a deployed API, AEGIS_PERF_USERS / AEGIS_PERF_SECONDS to scale.
/// Excluded from the normal test run: <c>dotnet test --filter TestCategory=Performance</c>.
/// </summary>
[TestClass]
[TestCategory("Performance")]
public class ApiLoadTests
{
    private static AegisFactory? _host;
    private static readonly DemoSeeder.DemoData Data = DemoSeeder.Load();
    private static readonly string? Url = Environment.GetEnvironmentVariable("AEGIS_PERF_URL");
    private static int Users => int.TryParse(Environment.GetEnvironmentVariable("AEGIS_PERF_USERS"), out var u) ? u : 20;
    private static TimeSpan Duration => TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("AEGIS_PERF_SECONDS"), out var s) ? s : 10);

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        if (Url is null) _host = new AegisFactory();
    }

    [ClassCleanup]
    public static void Stop() => _host?.Dispose();

    /// <summary>An HTTP client signed in as the given person, in-process or remote.</summary>
    private static HttpClient Client(string employeeNumber)
    {
        var c = Url is null ? _host!.CreateClient() : new HttpClient { BaseAddress = new Uri(Url) };
        c.DefaultRequestHeaders.Add(DemoAuthenticationHandler.Header, employeeNumber);
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    private static string[] People(Func<DemoSeeder.EmployeeData, bool>? filter = null) =>
        Data.Employees.Where(e => e.Status is null or "OnLeave" && (filter?.Invoke(e) ?? true)).Select(e => e.Number).ToArray();

    private LoadReport Finish(LoadReport report, params Threshold[] thresholds)
    {
        var violations = report.Check(thresholds);
        report.Save(violations);
        TestContext.WriteLine(report.ToMarkdown(violations));
        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
        return report;
    }

    [TestMethod]
    public async Task Browse_ReadHeavyMix()
    {
        var people = People();
        var maxId = Data.Employees.Count;
        var scenario = new Scenario("Browse - read-heavy mix", i => Client(people[i % people.Length]),
        [
            new("GET me/counters", u => u.Client.GetAsync("/api/me/counters"), Weight: 3),
            new("GET employees (page, search)", u => u.Client.GetAsync($"/api/employees?page={u.Random.Next(1, 4)}&pageSize=25&search={(u.Random.Next(3) == 0 ? "an" : "")}"), Weight: 3),
            new("GET employee detail", u => u.Client.GetAsync($"/api/employees/{u.Random.Next(1, maxId + 1)}"), Weight: 2),
            new("GET my requests", u => u.Client.GetAsync("/api/requests?scope=mine"), Weight: 2),
            new("GET applications", u => u.Client.GetAsync("/api/applications")),
            new("GET my trainings", u => u.Client.GetAsync("/api/assignments?scope=mine")),
            new("POST assistant", u => u.Client.PostAsJsonAsync("/api/assistant", new AskInput("what are my open requests?"))),
        ]);

        var report = await LoadRunner.RunAsync(scenario, new LoadProfile(Users, Duration, RampUp: TimeSpan.FromSeconds(2)));

        Finish(report,
            Threshold.ErrorRate(0.01),
            Threshold.P95("GET me/counters", Url is null ? 400 : 300),
            Threshold.P95("GET employees (page, search)", Url is null ? 500 : 300),
            Threshold.P95All(Url is null ? 1000 : 800),
            Threshold.MinThroughput(20));
    }

    /// <summary>The write path, as a user journey: create an access request, cancel it, again and again.</summary>
    [TestMethod]
    public async Task RequestWorkflow_CreateAndCancel()
    {
        // people from business teams: they have no IT service management access yet, so the request is always valid
        var business = new[] { "Sales", "Accounting", "Controlling", "HR Operations", "Talent Acquisition", "Customer Success" };
        var people = People(e => business.Any(b => e.Team.StartsWith(b, StringComparison.Ordinal)));
        int roleId = 0;
        var scenario = new Scenario("Request workflow - create and cancel", i => Client(people[i % people.Length]),
        [
            new("POST request", async u =>
            {
                var r = await u.Client.PostAsJsonAsync("/api/requests", new NewRequest(roleId, $"Load test run {u.Id}: joining the service desk rotation"));
                if (r.IsSuccessStatusCode) u.State["open"] = (await r.Content.ReadFromJsonAsync<RequestDetail>(AegisFactory.Json))!.Id;
                return r;
            }),
            new("POST cancel", u => u.State.Remove("open", out var id)
                ? u.Client.PostAsJsonAsync($"/api/requests/{id}/transitions", new RequestTransition("cancel"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))),
        ],
        Setup: async u =>
        {
            if (roleId == 0)
            {
                var apps = await u.Client.GetFromJsonAsync<List<SystemDto>>("/api/applications", AegisFactory.Json);
                Interlocked.CompareExchange(ref roleId, apps!.Single(a => a.Code == "ITSM").Roles.Single(r => r.Name == "Agent").Id, 0);
            }
        },
        Sequential: true);

        var users = Math.Min(Users, people.Length);
        var report = await LoadRunner.RunAsync(scenario, new LoadProfile(users, Duration, RampUp: TimeSpan.FromSeconds(1)));

        Finish(report,
            Threshold.ErrorRate(0.01),
            Threshold.P95("POST request", Url is null ? 800 : 500),
            Threshold.P95("POST cancel", Url is null ? 600 : 400));
    }

    [TestMethod]
    public void PercentilesUseNearestRank()
    {
        double[] values = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.AreEqual(50, LoadReport.Percentile(values, 50));
        Assert.AreEqual(100, LoadReport.Percentile(values, 99));
        Assert.AreEqual(10, LoadReport.Percentile(values, 1));
        Assert.AreEqual(90, LoadReport.Percentile(values, 90));
    }
}
