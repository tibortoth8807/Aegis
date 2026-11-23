using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aegis.Application.Access;
using Aegis.Application.Assistant;
using Aegis.Application.Common;
using Aegis.Application.Dashboard;
using Aegis.Application.Employees;
using Aegis.Application.Exports;
using Aegis.Application.Import;
using Aegis.Application.Incidents;
using Aegis.Application.Jobs;
using Aegis.Application.Training;
using Aegis.Infrastructure.Persistence;

namespace Aegis.IntegrationTests;

[TestClass]
public class ApiTests
{
    private static readonly DemoSeeder.Personas P = DemoSeeder.Load().Personas;
    private AegisFactory _app = null!;

    [TestInitialize]
    public void Start() => _app = new AegisFactory();

    [TestCleanup]
    public void Stop() => _app.Dispose();

    // ------------------------------------------------------------------ security and error contract

    [TestMethod]
    public async Task WithoutSignIn_TheApiAnswers401_ButHealthAndPersonasAreOpen()
    {
        var anonymous = _app.As(null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/employees")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        var personas = await anonymous.GetJson<JsonElement>("/api/demo/personas");
        Assert.AreEqual(6, personas.GetArrayLength());
    }

    [TestMethod]
    public async Task UnknownEmployeeInTheHeader_Is401()
    {
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _app.As("E99999").GetAsync("/api/me")).StatusCode);
    }

    [TestMethod]
    public async Task NotFound_IsAProblemWithCodeAndTraceId()
    {
        var p = await (await _app.As(P.Hr).GetAsync("/api/employees/999999")).Problem(404);
        Assert.AreEqual("not_found", p.GetProperty("code").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(p.GetProperty("traceId").GetString()));
    }

    // ------------------------------------------------------------------ employees

    [TestMethod]
    public async Task EmployeeList_IsPagedSearchableAndFilterable()
    {
        var hr = _app.As(P.Hr);
        var page = await hr.GetJson<Page<EmployeeListItem>>("/api/employees?pageSize=10&page=2");
        Assert.AreEqual(10, page.Items.Count);
        Assert.IsTrue(page.Total > 60);

        var externals = await hr.GetJson<Page<EmployeeListItem>>("/api/employees?type=External&pageSize=50");
        Assert.AreEqual(4, externals.Total);
        Assert.IsTrue(externals.Items.All(e => e.EmployeeNumber.StartsWith('X')));

        var me = await hr.GetJson<Me>("/api/me");
        var search = await hr.GetJson<Page<EmployeeListItem>>($"/api/employees?search={Uri.EscapeDataString(me.Name.Split(' ')[1])}");
        Assert.IsTrue(search.Items.Any(e => e.Id == me.Id));
    }

    [TestMethod]
    public async Task Hr_HiresSomeone_WhoGetsTheMandatoryTrainings()
    {
        var hr = _app.As(P.Hr);
        var lookups = await hr.GetJson<Lookups>("/api/lookups");
        var team = lookups.Departments.SelectMany(d => d.Teams).First(t => t.Name == "Platform");
        var created = await hr.PostJson<EmployeeDetail>("/api/employees", new EmployeeInput(
            "E20001", "Teszt", "Elek", "teszt.elek@aegis.example", "QA Engineer", "Internal", null, "HU",
            lookups.Locations.First().Id, team.Id, team.Lead!.Id, new DateOnly(2027, 1, 4), null));

        Assert.AreEqual("Preboarding", created.Status);
        Assert.AreEqual(3, created.Trainings.Count, "DP-101, IS-101 and COC-100 are mandatory");
        CollectionAssert.AreEquivalent(new[] { "activate", "give-notice" }, created.AllowedActions.ToArray());
    }

    [TestMethod]
    public async Task OnlyHrCanHire_AndDuplicateNumbersAreRejected()
    {
        var input = new EmployeeInput(P.Employee, "Dup", "Licate", "dup@aegis.example", "Tester", "Internal", null, null, null, null, null, new DateOnly(2026, 12, 1), null);
        (await (await _app.As(P.Employee).PostAsJsonAsync("/api/employees", input)).Problem(403)).GetProperty("code");
        var p = await (await _app.As(P.Hr).PostAsJsonAsync("/api/employees", input)).Problem(422);
        Assert.AreEqual("employee.duplicate_number", p.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StaleVersion_IsAConflict()
    {
        var hr = _app.As(P.Hr);
        var me = await hr.GetJson<Me>("/api/me");
        var e = await hr.GetJson<EmployeeDetail>($"/api/employees/{me.Id}");
        EmployeeInput Edit(string title, int version) => new(e.EmployeeNumber, e.FirstName, e.LastName, e.Email, title, e.Type, e.ExternalCompany,
            e.NationalityCode, e.LocationId, e.TeamId, e.Manager?.Id, e.StartDate, e.EndDate, null, version);

        var first = await (await hr.PutAsJsonAsync($"/api/employees/{me.Id}", Edit("Chief People Officer", e.Version))).Read<EmployeeDetail>();
        Assert.AreEqual(e.Version + 1, first.Version);
        var p = await (await hr.PutAsJsonAsync($"/api/employees/{me.Id}", Edit("Something else", e.Version))).Problem(409);
        Assert.AreEqual("concurrency", p.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task ValidationErrors_NameTheField()
    {
        var hr = _app.As(P.Hr);
        var input = new EmployeeInput("E20002", "", "Nobody", "nobody@aegis.example", "Tester", "Internal", null, "XX", null, null, null, new DateOnly(2026, 12, 1), null);
        var p = await (await hr.PostAsJsonAsync("/api/employees", input)).Problem(400);
        Assert.IsTrue(p.GetProperty("errors").TryGetProperty("FirstName", out _));
    }

    // ------------------------------------------------------------------ access request workflow, end to end

    [TestMethod]
    public async Task AccessRequest_FromSubmissionToActiveAccess()
    {
        var employee = _app.As(P.Employee);
        var me = await employee.GetJson<Me>("/api/me");
        var profile = await employee.GetJson<EmployeeDetail>($"/api/employees/{me.Id}");
        var apps = await employee.GetJson<List<SystemDto>>("/api/applications");
        var git = apps.Single(a => a.Name == "Source code hosting");
        var developer = git.Roles.Single(r => r.Name == "Developer");

        var created = await employee.PostJson<RequestDetail>("/api/requests", new NewRequest(developer.Id, "Contributing to the analytics dashboards"));
        Assert.AreEqual("PendingManager", created.Status);
        StringAssert.StartsWith(created.Number, "REQ-");

        // the manager sees it in their approvals and approves
        var manager = _app.As(await NumberOf(profile.Manager!.Id));
        var waiting = await manager.GetJson<Page<RequestListItem>>("/api/requests?scope=approvals");
        Assert.IsTrue(waiting.Items.Any(r => r.Id == created.Id && r.AwaitsMe));
        var afterManager = await manager.PostJson<RequestDetail>($"/api/requests/{created.Id}/transitions", new RequestTransition("approve"));
        Assert.AreEqual("PendingOwner", afterManager.Status);

        // the owner approves; LDAP-group provisioning activates the access right away
        var owner = _app.As(await NumberOf(git.Owner!.Id));
        var done = await owner.PostJson<RequestDetail>($"/api/requests/{created.Id}/transitions", new RequestTransition("approve", "ok"));
        Assert.AreEqual("Active", done.Status);
        Assert.AreEqual(AegisFactory.Start.AddDays(365), done.ValidUntil);

        var after = await employee.GetJson<EmployeeDetail>($"/api/employees/{me.Id}");
        Assert.IsTrue(after.Access.Any(g => g.Role == "Developer" && g.System == "Source code hosting"));
        CollectionAssert.AreEqual(new[] { "provision", "approve", "approve", "submit", "create" }, done.History.Select(h => h.Action).ToArray());
    }

    [TestMethod]
    public async Task RequestRules_AreEnforcedOverHttp()
    {
        var employee = _app.As(P.Employee);
        var apps = await employee.GetJson<List<SystemDto>>("/api/applications");
        var vpn = apps.Single(a => a.Code == "ITSM").Roles.Single(r => r.Name == "Agent");   // no training prerequisite

        var tooShort = await (await employee.PostAsJsonAsync("/api/requests", new NewRequest(vpn.Id, "pls"))).Problem(400);
        Assert.IsTrue(tooShort.GetProperty("errors").TryGetProperty("Justification", out _));

        var first = await employee.PostJson<RequestDetail>("/api/requests", new NewRequest(vpn.Id, "Working from home on Fridays"));
        var duplicate = await (await employee.PostAsJsonAsync("/api/requests", new NewRequest(vpn.Id, "Working from home on Fridays"))).Problem(422);
        Assert.AreEqual("request.duplicate", duplicate.GetProperty("code").GetString());

        // approving your own request: forbidden; approving twice: invalid transition after cancel
        Assert.AreEqual("forbidden", (await (await employee.PostAsJsonAsync($"/api/requests/{first.Id}/transitions", new RequestTransition("approve"))).Problem(403))
            .GetProperty("code").GetString());
        await employee.PostJson<RequestDetail>($"/api/requests/{first.Id}/transitions", new RequestTransition("cancel"));
        var conflict = await (await employee.PostAsJsonAsync($"/api/requests/{first.Id}/transitions", new RequestTransition("cancel"))).Problem(409);
        Assert.AreEqual("state.transition", conflict.GetProperty("code").GetString());
        Assert.AreEqual("cancelled", conflict.GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task RequiredTraining_IsCheckedBeforeRequesting()
    {
        var employee = _app.As(P.Employee);
        var cloud = (await employee.GetJson<List<SystemDto>>("/api/applications")).Single(a => a.Code == "CLD");
        Assert.AreEqual("Privileged access handling", cloud.RequiredTraining);
        var p = await (await employee.PostAsJsonAsync("/api/requests", new NewRequest(cloud.Roles[0].Id, "Need to look at the production logs"))).Problem(422);
        Assert.AreEqual("request.training_required", p.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ trainings, tickets, jobs

    [TestMethod]
    public async Task Training_CompleteWithAPassingScore()
    {
        var employee = _app.As(P.Employee);
        var mine = await employee.GetJson<Page<AssignmentDto>>("/api/assignments?scope=mine");
        Assert.IsTrue(mine.Total >= 3);
        var open = mine.Items.FirstOrDefault(a => a.AllowedActions.Contains("complete"));
        if (open is null) Assert.Inconclusive("The demo persona has no open training in this seed.");
        var failed = await employee.PostJson<AssignmentDto>($"/api/assignments/{open.Id}/transitions", new AssignmentTransition("complete", 10));
        Assert.AreNotEqual("Completed", failed.Status);
        var passed = await employee.PostJson<AssignmentDto>($"/api/assignments/{open.Id}/transitions", new AssignmentTransition("complete", 100));
        Assert.AreEqual("Completed", passed.Status);
        Assert.AreEqual(failed.Attempts + 1, passed.Attempts);
    }

    [TestMethod]
    public async Task Ticket_IsTakenAndResolvedByTheSupportTeam()
    {
        var reporter = _app.As(P.Employee);
        var t = await reporter.PostJson<IncidentDetail>("/api/tickets", new NewIncident("Cannot open the CRM", "Error 403 since this morning", "P2", "access"));
        Assert.AreEqual("New", t.Status);
        Assert.AreEqual(AegisFactory.Start.AddHours(8), t.DueAt);

        Assert.AreEqual(403, (int)(await reporter.GetAsync("/api/tickets?scope=queue")).StatusCode);
        var admin = _app.As(P.Admin);
        var queue = await admin.GetJson<Page<IncidentListItem>>("/api/tickets?scope=queue&status=open");
        Assert.IsTrue(queue.Items.Any(x => x.Id == t.Id));
        await admin.PostJson<IncidentDetail>($"/api/tickets/{t.Id}/transitions", new IncidentTransition("take"));
        var resolved = await admin.PostJson<IncidentDetail>($"/api/tickets/{t.Id}/transitions", new IncidentTransition("resolve", "Added to the crm-readers group"));
        Assert.AreEqual("Resolved", resolved.Status);
        var seen = await reporter.GetJson<IncidentDetail>($"/api/tickets/{t.Id}");
        CollectionAssert.AreEquivalent(new[] { "close", "reopen" }, seen.AllowedActions.ToArray());
    }

    [TestMethod]
    public async Task DailyJobs_MoveTimeDrivenStatesForward()
    {
        var admin = _app.As(P.Admin);
        var first = await admin.PostJson<JobReport>("/api/jobs/daily", new { });
        var again = await admin.PostJson<JobReport>("/api/jobs/daily", new { });
        Assert.AreEqual(0, again.Total, "running the jobs twice changes nothing the second time");

        _app.Clock.Advance(TimeSpan.FromDays(40));   // joiners start, the leaver's end date passes, trainings fall due
        var later = await admin.PostJson<JobReport>("/api/jobs/daily", new { });
        Assert.AreEqual(2, later.Activated);
        Assert.AreEqual(1, later.Left);
        Assert.IsTrue(later.TrainingsOverdue > 0);
        Assert.IsTrue(first.Total >= 0);

        Assert.AreEqual(403, (int)(await _app.As(P.Employee).PostAsJsonAsync("/api/jobs/daily", new { })).StatusCode);
    }

    // ------------------------------------------------------------------ exports, import, assistant

    [TestMethod]
    public async Task Exports_AreFormattedFiles_ForTheRightRoles()
    {
        var hr = _app.As(P.Hr);
        var list = await hr.GetJson<List<ExportInfo>>("/api/exports");
        CollectionAssert.IsSubsetOf(new[] { "employees", "managers", "training" }, list.Select(x => x.Key).ToArray());

        var xlsx = await hr.GetAsync("/api/exports/employees?format=xlsx");
        Assert.AreEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
        var bytes = await xlsx.Content.ReadAsByteArrayAsync();
        Assert.AreEqual((byte)'P', bytes[0], "an xlsx file is a zip");

        var csv = Encoding.UTF8.GetString(await (await hr.GetAsync("/api/exports/managers?format=csv")).Content.ReadAsByteArrayAsync());
        StringAssert.Contains(csv, "Direct reports");

        Assert.AreEqual(403, (int)(await _app.As(P.Employee).GetAsync("/api/exports/admins")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _app.As(P.Security).GetAsync("/api/exports/admins")).StatusCode);
    }

    [TestMethod]
    public async Task DirectoryImport_PreviewsChangesWithoutSaving()
    {
        var hr = _app.As(P.Hr);
        var me = await hr.GetJson<EmployeeDetail>($"/api/employees/{(await hr.GetJson<Me>("/api/me")).Id}");
        var csv = "employeeNumber;firstName;lastName;email;jobTitle;team;managerNumber;type\n" +
                  $"{me.EmployeeNumber};{me.FirstName};{me.LastName};{me.Email};Chief People Officer;HR Operations;;internal\n" +
                  $"E30001;Új;Kolléga;uj.kollega@aegis.example;Analyst;Sales Analytics;{P.Employee};internal\n" +
                  "X30002;Ext;Ernal;ext@partner.example;Consultant;Platform;;external\n";
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", "people.csv");

        var report = await (await hr.PostAsync("/api/import/file?dryRun=true", form)).Read<ImportReport>();

        Assert.IsTrue(report.DryRun);
        Assert.AreEqual(1, report.Created);
        Assert.AreEqual(1, report.Updated);
        StringAssert.Contains(report.Changes.Single(c => c.Kind == "updated").Details, "job title");
        Assert.IsTrue(report.Warnings.Any(w => w.Contains("X30002")), "externals are not imported");
        Assert.AreEqual(0, (await hr.GetJson<Page<EmployeeListItem>>("/api/employees?search=E30001")).Total, "dry run saves nothing");
    }

    [TestMethod]
    public async Task Assistant_AnswersFromTheUsersOwnData()
    {
        var reply = await _app.As(P.Manager).PostJson<AssistantReply>("/api/assistant", new AskInput("Mit kell jóváhagynom?"));
        Assert.AreEqual("built-in", reply.Source);
        Assert.IsTrue(reply.Text.Contains("waiting") || reply.Text.Contains("Nothing"), reply.Text);
    }

    private async Task<string> NumberOf(int employeeId) =>
        (await _app.As(P.Hr).GetJson<EmployeeDetail>($"/api/employees/{employeeId}")).EmployeeNumber;
}
