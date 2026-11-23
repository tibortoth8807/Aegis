using Aegis.Application.Jobs;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Api.Hosting;

public static class DatabaseStartup
{
    /// <summary>
    /// Brings the database to the current model: applies the code-first migrations when the project has them
    /// (<c>dotnet ef migrations add ...</c>), otherwise creates the schema from the model. Then seeds the demo company.
    /// </summary>
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
        if (app.Configuration.GetValue("Database:Seed", true)) await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
    }
}

/// <summary>Runs the daily jobs on a timer (immediately at start, then every Jobs:IntervalMinutes).</summary>
public sealed class DailyJobsWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<DailyJobsWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Jobs:IntervalMinutes", 60)));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DailyJobs>().RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Daily jobs failed; retrying at the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
