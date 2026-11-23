using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Incidents;
using Aegis.Domain.News;
using Aegis.Domain.Organization;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Persistence;

/// <summary>
/// Fills an empty database with the fictional company from seed/demo-data.json (the same data the browser demo
/// uses). Workflows are not inserted as finished rows: every request, training and ticket is replayed through the
/// domain's state classes, one day per step, so the data is consistent with the rules and has a real history.
/// </summary>
public sealed class DemoSeeder(AegisDbContext db, TimeProvider time)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.Employees.AnyAsync(ct)) return;
        var data = Load();
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        DateTimeOffset DaysAgo(double d) => now.AddDays(-d);

        db.Countries.AddRange(data.Countries.Select(c => new Country { Code = c.Code, Name = c.Name, Eea = c.Eea }));
        var locations = data.Locations.ToDictionary(l => l.Name, l => new Location { Name = l.Name, CountryCode = l.Country });
        db.Locations.AddRange(locations.Values);
        var teams = new Dictionary<string, Team>();
        foreach (var d in data.Departments)
        {
            var dept = new Department { Code = d.Code, Name = d.Name };
            foreach (var t in d.Teams) dept.Teams.Add(teams[t.Name] = new Team { Name = t.Name });
            db.Departments.Add(dept);
        }
        await db.SaveChangesAsync(ct);

        // employees, in file order: managers come before their reports, so the reporting line can be set at hire
        var people = new Dictionary<string, Employee>();
        foreach (var p in data.Employees)
        {
            var e = new Employee
            {
                EmployeeNumber = p.Number, FirstName = p.First, LastName = p.Last, Email = p.Email, JobTitle = p.Title,
                Type = Enum.Parse<EmploymentType>(p.Type), ExternalCompany = p.Company, NationalityCode = p.Nationality,
                Location = locations.GetValueOrDefault(p.Location), Team = teams[p.Team], Source = "seed",
                ManagerId = p.Manager is null ? null : people[p.Manager].Id,
                StartDate = today.AddDays(-p.StartDaysAgo), EndDate = p.EndInDays is int end ? today.AddDays(end) : null,
                PortalRoles = p.Roles.Aggregate(PortalRole.None, (acc, r) => acc | Enum.Parse<PortalRole>(r)),
            };
            Employee.Hire(e, today, new TransitionContext(Actor.System with { Name = "HR import" }, DaysAgo(Math.Max(p.StartDaysAgo, 0) + 14)));
            db.Employees.Add(e);
            await db.SaveChangesAsync(ct);
            people[p.Number] = e;
        }
        foreach (var d in data.Departments)
            foreach (var t in d.Teams.Where(t => t.Lead is not null))
                teams[t.Name].LeadId = people[t.Lead!].Id;

        Actor ActorOf(string number) => number == "SYSTEM" ? Actor.System : people[number].AsActor();

        var courses = data.Courses.ToDictionary(c => c.Code, c => new Course
        {
            Code = c.Code, Title = c.Title, Description = c.Description, Mandatory = c.Mandatory, DueDays = c.DueDays,
            ValidityMonths = c.ValidityMonths, PassScore = c.PassScore,
        });
        db.Courses.AddRange(courses.Values);
        await db.SaveChangesAsync(ct);

        var roles = new Dictionary<(string, string), SystemRole>();
        foreach (var s in data.Systems)
        {
            var system = new TargetSystem
            {
                Code = s.Code, Name = s.Name, Description = s.Description, OwnerId = people[s.Owner].Id, Provisioning = s.Provisioning,
                RequiredTrainingId = s.RequiredCourse is null ? null : courses[s.RequiredCourse].Id,
            };
            foreach (var r in s.Roles)
                system.Roles.Add(roles[(s.Code, r.Name)] = new SystemRole
                {
                    Name = r.Name, Description = r.Description, Privileged = r.Privileged, ValidityDays = r.ValidityDays, ProvisioningGroup = r.Group, System = system,
                });
            db.Systems.Add(system);
        }
        await db.SaveChangesAsync(ct);

        // trainings
        foreach (var a in data.Assignments)
        {
            var assignedAt = DaysAgo(a.AssignedDaysAgo);
            var x = TrainingAssignment.Assign(people[a.Employee], courses[a.Course], DateOnly.FromDateTime(assignedAt.UtcDateTime),
                new TransitionContext(Actor.System with { Name = "Onboarding" }, assignedAt));
            var step = 0;
            foreach (var act in a.Actions)
            {
                var ctx = new TransitionContext(ActorOf(act[1].GetString()!), assignedAt.AddDays(++step * 3));
                if (act[0].GetString() == "start") x.Start(ctx);
                else x.Complete(act[2].GetInt32(), ctx);
            }
            db.Assignments.Add(x);
        }

        // access requests, replayed through the approval workflow
        foreach (var r in data.Requests)
        {
            var created = DaysAgo(r.CreatedDaysAgo);
            var role = roles[(r.System, r.Role)];
            var req = AccessRequest.Create(people[r.Requester], people[r.Beneficiary], role, r.Justification, new TransitionContext(ActorOf(r.Requester), created));
            db.AccessRequests.Add(req);
            await db.SaveChangesAsync(ct);
            req.Number = $"REQ-{req.Id:000000}";
            var step = 0;
            foreach (var act in r.Actions)
            {
                var at = created.AddHours(++step * 20);
                var ctx = new TransitionContext(ActorOf(act[1].GetString()!), at, act.Length > 2 ? act[2].GetString() : null);
                switch (act[0].GetString())
                {
                    case "submit": req.Submit(ctx); break;
                    case "approve": req.Approve(ctx); break;
                    case "reject": req.Reject(ctx); break;
                    case "cancel": req.Cancel(ctx); break;
                    case "provision": Provision(req, ctx); break;
                    case "revoke":
                        req.Revoke(ctx);
                        foreach (var g in db.Grants.Local.Where(g => g.RequestId == req.Id)) g.EndedAt = at;
                        break;
                }
                // automatic provisioning right after the last approval (like the live service)
                if (req.Status == AccessRequestStatus.Approved && role.System!.Provisioning != "manual")
                    Provision(req, ctx with { Actor = Actor.System with { Name = "Provisioning" }, Comment = null });
            }
        }

        // tickets
        foreach (var t in data.Incidents)
        {
            var created = DaysAgo(t.CreatedDaysAgo).AddHours(-3);
            var i = Incident.Open(people[t.Reporter], t.Title, t.Description, Enum.Parse<IncidentPriority>(t.Priority), t.Category, new TransitionContext(ActorOf(t.Reporter), created));
            db.Incidents.Add(i);
            await db.SaveChangesAsync(ct);
            i.Number = $"INC-{i.Id:000000}";
            var step = 0;
            foreach (var act in t.Actions)
            {
                var ctx = new TransitionContext(ActorOf(act[1].GetString()!), created.AddHours(++step), act.Length > 2 ? act[2].GetString() : null);
                switch (act[0].GetString())
                {
                    case "take": i.Take(ctx); break;
                    case "hold": i.Hold(ctx); break;
                    case "resolve": i.Resolve(ctx); break;
                    case "close": i.Close(ctx); break;
                }
            }
        }

        foreach (var n in data.News)
            db.News.Add(new NewsItem { Title = n.Title, Body = n.Body, AuthorId = people[n.Author].Id, PublishedAt = DaysAgo(n.DaysAgo), Pinned = n.Pinned });

        // employment changes come last: preboarding joiners, notice, leave
        foreach (var p in data.Employees.Where(p => p.Status is not null))
        {
            var e = people[p.Number];
            var ctx = new TransitionContext(Actor.System with { Name = "HR" }, DaysAgo(3), "Recorded by HR");
            if (p.Status == "OnLeave") e.StartLeave(ctx);
            if (p.Status == "Offboarding") e.GiveNotice(e.EndDate ?? today.AddDays(30), ctx);
        }
        await db.SaveChangesAsync(ct);

        void Provision(AccessRequest req, TransitionContext ctx)
        {
            req.Provision(ctx);
            db.Grants.Add(new AccessGrant
            {
                EmployeeId = req.BeneficiaryId, RoleId = req.RoleId, RequestId = req.Id, GrantedAt = ctx.Now, ValidUntil = req.ValidUntil ?? ctx.Now,
            });
        }
    }

    // ------------------------------------------------------------------ the JSON file

    public static DemoData Load()
    {
        using var stream = typeof(DemoSeeder).Assembly.GetManifestResourceStream("Aegis.demo-data.json")
            ?? throw new InvalidOperationException("The embedded demo data is missing.");
        return JsonSerializer.Deserialize<DemoData>(stream, JsonOptions) ?? throw new InvalidOperationException("Empty demo data.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public sealed record DemoData(
        Personas Personas, List<CountryData> Countries, List<LocationData> Locations, List<DepartmentData> Departments, List<EmployeeData> Employees,
        List<CourseData> Courses, List<SystemData> Systems, List<AssignmentData> Assignments, List<RequestData> Requests,
        List<IncidentData> Incidents, List<NewsData> News);

    public sealed record Personas(string Employee, string Manager, string Owner, string Hr, string Security, string Admin);
    public sealed record CountryData(string Code, string Name, bool Eea);
    public sealed record LocationData(string Name, string Country);
    public sealed record TeamData(string Name, string? Lead);
    public sealed record DepartmentData(string Code, string Name, List<TeamData> Teams);
    public sealed record EmployeeData(
        string Number, string First, string Last, string Email, string Title, string Type, string? Company, string Nationality, string Location,
        string Team, string? Manager, int StartDaysAgo, int? EndInDays, List<string> Roles, string? Status);
    public sealed record CourseData(string Code, string Title, string Description, bool Mandatory, int DueDays, int? ValidityMonths, int PassScore);
    public sealed record RoleData(string Name, string Description, bool Privileged, int ValidityDays, string? Group);
    public sealed record SystemData(string Code, string Name, string Description, string Owner, string Provisioning, string? RequiredCourse, List<RoleData> Roles);
    public sealed record AssignmentData(string Employee, string Course, int AssignedDaysAgo, List<JsonElement[]> Actions);
    public sealed record RequestData(string Requester, string Beneficiary, string System, string Role, string Justification, int CreatedDaysAgo, List<JsonElement[]> Actions);
    public sealed record IncidentData(string Reporter, string Title, string Description, string Priority, string Category, int CreatedDaysAgo, List<JsonElement[]> Actions);
    public sealed record NewsData(string Title, string Body, string Author, int DaysAgo, bool Pinned);
}
