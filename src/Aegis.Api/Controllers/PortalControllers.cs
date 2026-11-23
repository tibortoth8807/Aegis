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
using Aegis.Domain.Common;
using Aegis.Infrastructure.DirectorySync;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

// Thin controllers: bind the request, call the use case, return the result. Errors are exceptions that the
// central handler (Hosting/ExceptionHandling.cs) turns into problem responses.

[ApiController]
[Route("api")]
public sealed class MeController(DashboardService dashboard) : ControllerBase
{
    [HttpGet("me")]
    public Task<Me> GetMe(CancellationToken ct) => dashboard.MeAsync(ct);

    [HttpGet("me/counters")]
    public Task<Counters> GetCounters(CancellationToken ct) => dashboard.CountersAsync(ct);

    /// <summary>The people the demo "view as" switcher offers (one per portal role).</summary>
    [AllowAnonymous]
    [HttpGet("demo/personas")]
    public object Personas()
    {
        var p = DemoSeeder.Load().Personas;
        return new[]
        {
            new { role = "Employee", number = p.Employee }, new { role = "Manager", number = p.Manager }, new { role = "Application owner", number = p.Owner },
            new { role = "HR", number = p.Hr }, new { role = "Security officer", number = p.Security }, new { role = "Administrator", number = p.Admin },
        };
    }
}

[ApiController]
[Route("api/employees")]
public sealed class EmployeesController(EmployeeService employees) : ControllerBase
{
    [HttpGet]
    public Task<Page<EmployeeListItem>> List([FromQuery] PageQuery q, [FromQuery] EmployeeFilter f, CancellationToken ct) => employees.ListAsync(q, f, ct);

    [HttpGet("{id:int}")]
    public Task<EmployeeDetail> Get(int id, CancellationToken ct) => employees.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<EmployeeDetail>> Create(EmployeeInput input, CancellationToken ct)
    {
        var e = await employees.CreateAsync(input, ct);
        return CreatedAtAction(nameof(Get), new { id = e.Id }, e);
    }

    [HttpPut("{id:int}")]
    public Task<EmployeeDetail> Update(int id, EmployeeInput input, CancellationToken ct) => employees.UpdateAsync(id, input, ct);

    /// <summary>Lifecycle actions: activate, start-leave, return, give-notice (endDate), withdraw-notice, leave.</summary>
    [HttpPost("{id:int}/transitions")]
    public Task<EmployeeDetail> Transition(int id, EmployeeTransition t, CancellationToken ct) => employees.TransitionAsync(id, t, ct);
}

[ApiController]
[Route("api")]
public sealed class OrganizationController(OrganizationService org) : ControllerBase
{
    [HttpGet("lookups")]
    public Task<Lookups> GetLookups(CancellationToken ct) => org.LookupsAsync(ct);

    public sealed record CountryInput(string Name, bool Eea);

    [HttpPut("countries/{code}")]
    public Task<CountryDto> SaveCountry(string code, CountryInput input, CancellationToken ct) => org.SaveCountryAsync(code, input.Name, input.Eea, ct);

    [HttpDelete("countries/{code}")]
    public async Task<IActionResult> DeleteCountry(string code, CancellationToken ct)
    {
        await org.DeleteCountryAsync(code, ct);
        return NoContent();
    }

    [HttpPost("teams")]
    public Task<TeamDto> CreateTeam(TeamInput input, CancellationToken ct) => org.SaveTeamAsync(null, input, ct);

    [HttpPut("teams/{id:int}")]
    public Task<TeamDto> UpdateTeam(int id, TeamInput input, CancellationToken ct) => org.SaveTeamAsync(id, input, ct);
}

[ApiController]
[Route("api/applications")]
public sealed class ApplicationsController(CatalogService catalog) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<SystemDto>> List([FromQuery] string? search, CancellationToken ct) => catalog.ListAsync(search, ct);

    [HttpPost]
    public Task<SystemDto> Create(SystemInput input, CancellationToken ct) => catalog.SaveSystemAsync(null, input, ct);

    [HttpPut("{id:int}")]
    public Task<SystemDto> Update(int id, SystemInput input, CancellationToken ct) => catalog.SaveSystemAsync(id, input, ct);

    [HttpPost("{id:int}/roles")]
    public Task<SystemDto> AddRole(int id, RoleInput input, CancellationToken ct) => catalog.SaveRoleAsync(id, null, input, ct);

    [HttpPut("{id:int}/roles/{roleId:int}")]
    public Task<SystemDto> UpdateRole(int id, int roleId, RoleInput input, CancellationToken ct) => catalog.SaveRoleAsync(id, roleId, input, ct);
}

[ApiController]
[Route("api/requests")]
public sealed class RequestsController(AccessRequestService requests) : ControllerBase
{
    /// <summary>scope: mine | approvals | team | all; status: a status name or "open".</summary>
    [HttpGet]
    public Task<Page<RequestListItem>> List([FromQuery] PageQuery q, [FromQuery] RequestFilter f, CancellationToken ct) => requests.ListAsync(q, f, ct);

    [HttpGet("{id:int}")]
    public Task<RequestDetail> Get(int id, CancellationToken ct) => requests.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<RequestDetail>> Create(NewRequest input, CancellationToken ct)
    {
        var r = await requests.CreateAsync(input, ct);
        return CreatedAtAction(nameof(Get), new { id = r.Id }, r);
    }

    /// <summary>submit, approve, reject (comment), cancel, provision, revoke (comment).</summary>
    [HttpPost("{id:int}/transitions")]
    public Task<RequestDetail> Transition(int id, RequestTransition t, CancellationToken ct) => requests.TransitionAsync(id, t, ct);
}

[ApiController]
[Route("api")]
public sealed class TrainingController(TrainingService training) : ControllerBase
{
    [HttpGet("courses")]
    public Task<IReadOnlyList<CourseDto>> Courses(CancellationToken ct) => training.CoursesAsync(ct);

    [HttpPost("courses")]
    public Task<CourseDto> CreateCourse(CourseInput input, CancellationToken ct) => training.SaveCourseAsync(null, input, ct);

    [HttpPut("courses/{id:int}")]
    public Task<CourseDto> UpdateCourse(int id, CourseInput input, CancellationToken ct) => training.SaveCourseAsync(id, input, ct);

    /// <summary>scope: mine | team | all (HR).</summary>
    [HttpGet("assignments")]
    public Task<Page<AssignmentDto>> Assignments([FromQuery] PageQuery q, [FromQuery] string scope = "mine", [FromQuery] string? status = null,
        [FromQuery] int? courseId = null, CancellationToken ct = default) => training.AssignmentsAsync(q, scope, status, courseId, ct);

    [HttpPost("assignments")]
    public async Task<object> Assign(AssignInput input, CancellationToken ct) => new { assigned = await training.AssignAsync(input, ct) };

    /// <summary>start, complete (score), waive (comment).</summary>
    [HttpPost("assignments/{id:int}/transitions")]
    public Task<AssignmentDto> Transition(int id, AssignmentTransition t, CancellationToken ct) => training.TransitionAsync(id, t, ct);
}

[ApiController]
[Route("api/tickets")]
public sealed class TicketsController(IncidentService tickets) : ControllerBase
{
    /// <summary>scope: mine | queue (support team); status: a status name or "open".</summary>
    [HttpGet]
    public Task<Page<IncidentListItem>> List([FromQuery] PageQuery q, [FromQuery] string scope = "mine", [FromQuery] string? status = null, CancellationToken ct = default) =>
        tickets.ListAsync(q, scope, status, ct);

    [HttpGet("{id:int}")]
    public Task<IncidentDetail> Get(int id, CancellationToken ct) => tickets.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<IncidentDetail>> Create(NewIncident input, CancellationToken ct)
    {
        var i = await tickets.CreateAsync(input, ct);
        return CreatedAtAction(nameof(Get), new { id = i.Id }, i);
    }

    /// <summary>take, hold (comment), resume, resolve (comment), reopen, close, cancel.</summary>
    [HttpPost("{id:int}/transitions")]
    public Task<IncidentDetail> Transition(int id, IncidentTransition t, CancellationToken ct) => tickets.TransitionAsync(id, t, ct);
}

[ApiController]
[Route("api/news")]
public sealed class NewsController(NewsService news) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<NewsDto>> List([FromQuery] int take = 10, CancellationToken ct = default) => news.ListAsync(take, ct);

    [HttpPost]
    public Task<NewsDto> Publish(NewsInput input, CancellationToken ct) => news.PublishAsync(input, ct);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await news.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/exports")]
public sealed class ExportsController(ExportService exports) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<ExportInfo> List() => exports.Available();

    [HttpGet("{key}")]
    public async Task<IActionResult> Download(string key, [FromQuery] string format = "xlsx", CancellationToken ct = default)
    {
        var file = await exports.ExportAsync(key, format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}

[ApiController]
[Route("api/import")]
public sealed class ImportController(DirectoryImportService import, LdapDirectorySource ldap) : ControllerBase
{
    /// <summary>Upload an LDIF (.ldif) or CSV export. dryRun=true shows the changes without saving.</summary>
    [HttpPost("file")]
    [RequestSizeLimit(10_000_000)]
    public async Task<ImportReport> Upload(IFormFile file, [FromQuery] bool dryRun = true, CancellationToken ct = default)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        var content = await reader.ReadToEndAsync(ct);
        IDirectorySource source = file.FileName.EndsWith(".ldif", StringComparison.OrdinalIgnoreCase) || content.TrimStart().StartsWith("dn:", StringComparison.Ordinal) ||
            content.TrimStart().StartsWith("version:", StringComparison.Ordinal)
            ? new LdifDirectorySource(file.FileName, content)
            : new CsvDirectorySource(file.FileName, content);
        return await import.ImportAsync(source, dryRun, ct);
    }

    /// <summary>Synchronise directly from the configured LDAP server.</summary>
    [HttpPost("ldap")]
    public Task<ImportReport> Ldap([FromQuery] bool dryRun = true, CancellationToken ct = default) => import.ImportAsync(ldap, dryRun, ct);
}

[ApiController]
[Route("api/jobs")]
public sealed class JobsController(DailyJobs jobs, ICurrentUser user) : ControllerBase
{
    /// <summary>Runs the daily jobs now (administrators).</summary>
    [HttpPost("daily")]
    public Task<JobReport> RunDaily(CancellationToken ct)
    {
        user.RequireAny(PortalRole.Admin);
        return jobs.RunAsync(ct);
    }
}

[ApiController]
[Route("api/assistant")]
public sealed class AssistantController(AssistantService assistant) : ControllerBase
{
    [HttpPost]
    public Task<AssistantReply> Ask(AskInput input, CancellationToken ct) => assistant.AskAsync(input, ct);

    [HttpGet("info")]
    public object Info() => new { model = assistant.ModelName };
}
