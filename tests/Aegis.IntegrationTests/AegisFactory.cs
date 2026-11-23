using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.IntegrationTests;

/// <summary>A clock the tests control: fixed "today", can be moved forward to trigger the daily jobs.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// The real API (all layers, real EF Core, real SQL) on an in-memory SQLite database seeded with the demo company.
/// Every test gets a fresh database. Clients sign in with the demo header.
/// </summary>
public sealed class AegisFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Start = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    // the in-memory database lives as long as one connection to it is open
    private readonly string _connectionString = $"Data Source=aegis-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public TestClock Clock { get; } = new(Start);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public AegisFactory()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Aegis", _connectionString);
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Assistant:Provider", "builtin");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    /// <summary>A client signed in as the given employee number (null = anonymous).</summary>
    public HttpClient As(string? employeeNumber)
    {
        var c = CreateClient();
        if (employeeNumber is not null) c.DefaultRequestHeaders.Add(DemoAuthenticationHandler.Header, employeeNumber);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _keepAlive.Dispose();
    }
}

public static class HttpExtensions
{
    public static async Task<T> Read<T>(this HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode) Assert.Fail($"{(int)r.StatusCode} {r.ReasonPhrase}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(AegisFactory.Json))!;
    }

    public static async Task<T> GetJson<T>(this HttpClient c, string url) => await (await c.GetAsync(url)).Read<T>();

    public static async Task<T> PostJson<T>(this HttpClient c, string url, object body) => await (await c.PostAsJsonAsync(url, body, AegisFactory.Json)).Read<T>();

    /// <summary>Asserts a problem response and returns it.</summary>
    public static async Task<JsonElement> Problem(this HttpResponseMessage r, int status)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.AreEqual(status, (int)r.StatusCode, text);
        Assert.AreEqual("application/problem+json", r.Content.Headers.ContentType?.MediaType, text);
        return JsonDocument.Parse(text).RootElement;
    }
}
