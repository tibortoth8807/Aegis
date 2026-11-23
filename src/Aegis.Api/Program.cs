using System.Text.Json.Serialization;
using Aegis.Api.Hosting;
using Aegis.Application.Common;
using Aegis.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAegisApplication();
builder.Services.AddAegisInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddAuthentication(DemoAuthenticationHandler.Scheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DemoAuthenticationHandler>(DemoAuthenticationHandler.Scheme, null);
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// central exception handling → RFC 9457 problem details
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AegisExceptionHandler>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx =>
    {
        // binding errors get the same shape as the central handler's problems
        var problem = new ValidationProblemDetails(ctx.ModelState) { Status = 400, Title = "Invalid input", Type = "https://httpstatuses.io/400" };
        problem.Extensions["code"] = "validation";
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    });
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));
if (builder.Configuration.GetValue("Jobs:Enabled", true)) builder.Services.AddHostedService<DailyJobsWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

await app.PrepareDatabaseAsync();
await app.RunAsync();

/// <summary>Entry point, visible to the integration and performance tests (WebApplicationFactory).</summary>
public partial class Program
{
}
