using System.Diagnostics;
using System.Text.Json;
using Aegis.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Api.Hosting;

/// <summary>
/// Central exception handling: every exception that leaves a controller ends up here and becomes an RFC 9457
/// problem response. Controllers and services just throw - no try/catch, no status-code juggling at call sites.
/// <list type="bullet">
/// <item>ValidationFailedException → 400 with field errors</item>
/// <item>ForbiddenException → 403, NotFoundException → 404</item>
/// <item>InvalidStateTransitionException, ConcurrencyConflictException, EF concurrency → 409</item>
/// <item>BusinessRuleException → 422</item>
/// <item>anything else → 500, logged with the trace id; details only in Development</item>
/// </list>
/// Every response carries a stable <c>code</c> and the <c>traceId</c> to find the request in the logs.
/// </summary>
public sealed class AegisExceptionHandler(IProblemDetailsService problems, IHostEnvironment env, ILogger<AegisExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title, code) = exception switch
        {
            ValidationFailedException => (StatusCodes.Status400BadRequest, "Invalid input", "validation"),
            BadHttpRequestException or JsonException => (StatusCodes.Status400BadRequest, "Malformed request", "bad_request"),
            ForbiddenException e => (StatusCodes.Status403Forbidden, "Not allowed", e.Code),
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found", e.Code),
            InvalidStateTransitionException e => (StatusCodes.Status409Conflict, "Not possible in the current state", e.Code),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Changed by someone else", "concurrency"),
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Changed by someone else", "concurrency"),
            BusinessRuleException e => (StatusCodes.Status422UnprocessableEntity, "Business rule violated", e.Code),
            OperationCanceledException when http.RequestAborted.IsCancellationRequested => (499, "Client closed the request", "cancelled"),
            _ => (StatusCodes.Status500InternalServerError, "Something went wrong", "internal"),
        };

        if (status >= 500) log.LogError(exception, "Unhandled exception on {Method} {Path}", http.Request.Method, http.Request.Path);
        else log.LogInformation("{Status} {Code} on {Method} {Path}: {Message}", status, code, http.Request.Method, http.Request.Path, exception.Message);

        var problem = exception is ValidationFailedException v
            ? new ValidationProblemDetails(v.Errors.ToDictionary(x => x.Key, x => x.Value)) { Title = title }
            : new ProblemDetails { Title = title };
        problem.Status = status;
        problem.Detail = status < 500 || env.IsDevelopment() ? exception.Message : "The error was logged. Quote the trace id when you report it.";
        problem.Type = $"https://httpstatuses.io/{status}";
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
        if (exception is InvalidStateTransitionException t)
        {
            problem.Extensions["state"] = t.State;
            problem.Extensions["action"] = t.Action;
        }

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }
}
