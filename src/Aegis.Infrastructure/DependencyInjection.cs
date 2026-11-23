using Aegis.Application.Access;
using Aegis.Application.Assistant;
using Aegis.Application.Common;
using Aegis.Application.Dashboard;
using Aegis.Application.Employees;
using Aegis.Application.Exports;
using Aegis.Application.Import;
using Aegis.Application.Incidents;
using Aegis.Application.Jobs;
using Aegis.Application.News;
using Aegis.Application.Training;
using Aegis.Infrastructure.Assistant;
using Aegis.Infrastructure.DirectorySync;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Use cases (Application layer).</summary>
    public static IServiceCollection AddAegisApplication(this IServiceCollection s)
    {
        s.AddSingleton(TimeProvider.System);
        s.AddScoped<EmployeeService>();
        s.AddScoped<OrganizationService>();
        s.AddScoped<AccessRequestService>();
        s.AddScoped<CatalogService>();
        s.AddScoped<AccessProvisioner>();
        s.AddScoped<AccessRevoker>();
        s.AddScoped<TrainingService>();
        s.AddScoped<IncidentService>();
        s.AddScoped<NewsService>();
        s.AddScoped<ExportService>();
        s.AddScoped<DirectoryImportService>();
        s.AddScoped<DailyJobs>();
        s.AddScoped<DashboardService>();
        s.AddScoped<AssistantService>();
        return s;
    }

    /// <summary>
    /// Database (Database:Provider = Sqlite | SqlServer, ConnectionStrings:Aegis), provisioning (LDAP if configured),
    /// directory source and the assistant's language model (Assistant:Provider = builtin | ollama | openai).
    /// </summary>
    public static IServiceCollection AddAegisInfrastructure(this IServiceCollection s, IConfiguration config)
    {
        var provider = config["Database:Provider"] ?? "Sqlite";
        var connection = config.GetConnectionString("Aegis") ?? "Data Source=aegis.db";
        s.AddDbContext<AegisDbContext>(o =>
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
                o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
            else
                o.UseSqlite(connection);
        });
        s.AddScoped<IAegisDbContext>(sp => sp.GetRequiredService<AegisDbContext>());
        s.AddScoped<DemoSeeder>();

        s.Configure<LdapOptions>(config.GetSection(LdapOptions.Section));
        s.AddSingleton<LoggingProvisioningGateway>();
        s.AddScoped<IProvisioningGateway>(sp => sp.GetRequiredService<IOptions<LdapOptions>>().Value.Enabled
            ? ActivatorUtilities.CreateInstance<LdapGroupProvisioningGateway>(sp)
            : sp.GetRequiredService<LoggingProvisioningGateway>());
        s.AddScoped<LdapDirectorySource>();

        s.AddScoped<IAssistantKnowledge, AssistantKnowledge>();
        var assistant = config.GetSection(AssistantOptions.Section).Get<AssistantOptions>() ?? new AssistantOptions();
        s.AddSingleton(assistant);
        switch (assistant.Provider.ToLowerInvariant())
        {
            case "ollama":
                s.AddHttpClient<IChatModel, OllamaChatModel>(c =>
                {
                    c.BaseAddress = new Uri(assistant.Endpoint);
                    c.Timeout = TimeSpan.FromSeconds(assistant.TimeoutSeconds);
                });
                break;
            case "openai":
                s.AddHttpClient<IChatModel, OpenAiChatModel>(c =>
                {
                    c.BaseAddress = new Uri(assistant.Endpoint);
                    c.Timeout = TimeSpan.FromSeconds(assistant.TimeoutSeconds);
                });
                break;
        }
        return s;
    }
}
