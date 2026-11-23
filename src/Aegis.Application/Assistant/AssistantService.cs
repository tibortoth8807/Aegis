using System.Text;
using Aegis.Application.Common;
using Microsoft.Extensions.Logging;

namespace Aegis.Application.Assistant;

public sealed record AssistantLink(string Label, string Route);

public sealed record AssistantReply(string Text, IReadOnlyList<AssistantLink> Links, IReadOnlyList<string> Suggestions, string Source);

public sealed record AskInput(string Message, IReadOnlyList<ChatTurn>? History = null);

/// <summary>Facts the assistant may use - only what the current user is allowed to see.</summary>
public interface IAssistantKnowledge
{
    Task<string> ProfileAsync(CancellationToken ct);
    Task<(int Count, IReadOnlyList<string> Lines)> MyOpenRequestsAsync(CancellationToken ct);
    Task<(int Count, IReadOnlyList<string> Lines)> AwaitingMeAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> MyTrainingsDueAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> MyAccessAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> MyOpenTicketsAsync(CancellationToken ct);
    Task<IReadOnlyList<PersonHit>> FindPeopleAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<(string System, string Owner)>> FindSystemsAsync(string name, CancellationToken ct);
}

public sealed record PersonHit(int Id, string Name, string JobTitle, string? Team, string? Manager, string Email);

/// <summary>
/// The chat assistant in the corner of every page. Recognised questions are answered from the database by the
/// built-in rules - exact and instant. Anything else goes to a language model if one is configured (Ollama runs
/// locally without registration; any OpenAI-compatible endpoint works), with the user's own facts as context.
/// Without a model, or if the model fails, the built-in assistant says what it can help with.
/// </summary>
public sealed class AssistantService(IAssistantKnowledge knowledge, IEnumerable<IChatModel> models, ILogger<AssistantService> log)
{
    private static readonly string[] DefaultSuggestions =
        ["What are my open requests?", "What is waiting for my approval?", "Which trainings are due?", "How do I request access?"];

    private readonly IChatModel? _model = models.FirstOrDefault();

    public string ModelName => _model?.Name ?? "built-in";

    public async Task<AssistantReply> AskAsync(AskInput input, CancellationToken ct)
    {
        var message = (input.Message ?? "").Trim();
        if (message.Length > 1000) message = message[..1000];
        var recognized = IntentRecognizer.Recognize(message);
        if (recognized.Intent != Intent.Unknown) return await AnswerAsync(recognized, ct);
        if (_model is null) return NotUnderstood();
        try
        {
            var prompt = await SystemPromptAsync(ct);
            var history = (input.History ?? []).TakeLast(8).Append(new ChatTurn("user", message)).ToList();
            var text = await _model.CompleteAsync(prompt, history, ct);
            return new AssistantReply(text.Trim(), [], [], _model.Name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "The language model {Model} failed; answering with the built-in assistant", _model.Name);
            var fallback = NotUnderstood();
            return fallback with { Text = "The AI model is not reachable right now. " + fallback.Text };
        }
    }

    private static AssistantReply NotUnderstood() => new(
        "I'm not sure what you mean. I can tell you about your requests, approvals, trainings, access and tickets, find colleagues and their managers, and show you how to request access.",
        [], DefaultSuggestions, "built-in");

    private async Task<AssistantReply> AnswerAsync(Recognized r, CancellationToken ct)
    {
        switch (r.Intent)
        {
            case Intent.Help:
                return Reply("Hi! I'm the Aegis assistant. Ask me about your access requests, approvals waiting for you, your trainings, " +
                    "your tickets - or who someone is and who their manager is.", [], DefaultSuggestions);

            case Intent.MyRequests:
            {
                var (count, lines) = await knowledge.MyOpenRequestsAsync(ct);
                return count == 0
                    ? Reply("You have no open access requests.", [new("Request access", "/requests/new")])
                    : Reply($"You have {count} open request{(count == 1 ? "" : "s")}:\n" + Bullets(lines), [new("My requests", "/requests")]);
            }

            case Intent.Approvals:
            {
                var (count, lines) = await knowledge.AwaitingMeAsync(ct);
                return count == 0
                    ? Reply("Nothing is waiting for your approval. 🎉", [])
                    : Reply($"{count} request{(count == 1 ? " is" : "s are")} waiting for your decision:\n" + Bullets(lines), [new("Open approvals", "/approvals")]);
            }

            case Intent.MyTrainings:
            {
                var due = await knowledge.MyTrainingsDueAsync(ct);
                return due.Count == 0
                    ? Reply("You have no open trainings - all done.", [new("My trainings", "/trainings")])
                    : Reply("Your open trainings:\n" + Bullets(due), [new("My trainings", "/trainings")]);
            }

            case Intent.MyAccess:
            {
                var access = await knowledge.MyAccessAsync(ct);
                return access.Count == 0
                    ? Reply("You have no access granted through Aegis yet.", [new("Request access", "/requests/new")])
                    : Reply("Your current access:\n" + Bullets(access), [new("My profile", "/me")]);
            }

            case Intent.HowToRequest:
                return Reply(
                    "1. Open **Request access** and pick the application and role.\n2. Explain why you need it (at least 15 characters).\n" +
                    "3. Your manager approves, then the application owner; privileged roles also need a security officer.\n" +
                    "4. Access is set up automatically for most applications and is valid for the role's validity period.",
                    [new("Request access", "/requests/new")]);

            case Intent.MyTickets:
            {
                var tickets = await knowledge.MyOpenTicketsAsync(ct);
                return tickets.Count == 0
                    ? Reply("You have no open tickets.", [new("Report a problem", "/tickets/new")])
                    : Reply("Your open tickets:\n" + Bullets(tickets), [new("My tickets", "/tickets")]);
            }

            case Intent.NewTicket:
                return Reply("Sorry to hear that. Open a ticket with a short description and the support team will pick it up - P1 tickets within 4 hours.",
                    [new("Report a problem", "/tickets/new")]);

            case Intent.WhoIsManager:
            case Intent.FindPerson:
            {
                var hits = await knowledge.FindPeopleAsync(r.Subject ?? "", ct);
                if (hits.Count == 0) return Reply($"I couldn't find anyone called \"{r.Subject}\".", [new("Employee directory", "/employees")]);
                if (hits.Count > 1 && r.Intent == Intent.WhoIsManager)
                    return Reply($"Several people match \"{r.Subject}\":\n" + Bullets(hits.Take(5).Select(h => $"{h.Name} - {h.JobTitle}")) + "\nWhich one do you mean?",
                        hits.Take(5).Select(h => new AssistantLink(h.Name, $"/employees/{h.Id}")).ToList());
                var p = hits[0];
                var text = r.Intent == Intent.WhoIsManager
                    ? (p.Manager is null ? $"{p.Name} has no manager in Aegis." : $"{p.Name}'s manager is **{p.Manager}**.")
                    : $"**{p.Name}** - {p.JobTitle}{(p.Team is null ? "" : $", {p.Team}")}. Manager: {p.Manager ?? "–"}. E-mail: {p.Email}." +
                      (hits.Count > 1 ? $"\n({hits.Count - 1} more match{(hits.Count == 2 ? "es" : "")} in the directory.)" : "");
                return Reply(text, hits.Take(3).Select(h => new AssistantLink(h.Name, $"/employees/{h.Id}")).ToList());
            }

            case Intent.SystemOwner:
            {
                var systems = await knowledge.FindSystemsAsync(r.Subject ?? "", ct);
                return systems.Count == 0
                    ? Reply($"I don't know an application called \"{r.Subject}\".", [new("Applications", "/applications")])
                    : Reply(string.Join("\n", systems.Select(s => $"**{s.System}** is owned by {s.Owner}.")), [new("Applications", "/applications")]);
            }

            default:
                return NotUnderstood();
        }
    }

    private static AssistantReply Reply(string text, IReadOnlyList<AssistantLink> links, IReadOnlyList<string>? suggestions = null) =>
        new(text, links, suggestions ?? [], "built-in");

    private static string Bullets(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "• " + l));

    /// <summary>Context for the language model: the rules of the portal and the user's own data, nothing else.</summary>
    private async Task<string> SystemPromptAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the assistant of Aegis, a company's employee and access portal. Answer briefly and in the user's language.");
        sb.AppendLine("Only use the facts below; if they do not answer the question, say so and point to the right page. Never invent people, numbers or rules.");
        sb.AppendLine("Pages: /requests/new (request access), /requests (my requests), /approvals, /trainings, /tickets/new (report a problem), /employees, /applications.");
        sb.AppendLine("Rules: access requests need a justification of 15+ characters; the manager, then the application owner approves;");
        sb.AppendLine("privileged roles also need a security officer and last at most 30 days; nobody approves their own request.");
        sb.AppendLine().AppendLine("About the user: " + await knowledge.ProfileAsync(ct));
        var (rc, rl) = await knowledge.MyOpenRequestsAsync(ct);
        sb.AppendLine($"Open requests ({rc}): " + string.Join("; ", rl));
        var (ac, al) = await knowledge.AwaitingMeAsync(ct);
        sb.AppendLine($"Waiting for their approval ({ac}): " + string.Join("; ", al));
        sb.AppendLine("Open trainings: " + string.Join("; ", await knowledge.MyTrainingsDueAsync(ct)));
        sb.AppendLine("Current access: " + string.Join("; ", await knowledge.MyAccessAsync(ct)));
        sb.AppendLine("Open tickets: " + string.Join("; ", await knowledge.MyOpenTicketsAsync(ct)));
        return sb.ToString();
    }
}
