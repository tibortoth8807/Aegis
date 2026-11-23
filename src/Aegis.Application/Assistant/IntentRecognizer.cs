using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aegis.Application.Assistant;

public enum Intent
{
    Unknown,
    Help,
    MyRequests,
    Approvals,
    WhoIsManager,
    FindPerson,
    MyTrainings,
    MyAccess,
    HowToRequest,
    MyTickets,
    NewTicket,
    SystemOwner,
}

public sealed record Recognized(Intent Intent, string? Subject = null);

/// <summary>
/// Keyword-based intent recognition for the built-in assistant, in English and Hungarian. Deliberately simple and
/// predictable: what it does not recognise goes to the language model (if one is configured).
/// </summary>
public static partial class IntentRecognizer
{
    private sealed record Rule(Intent Intent, string[] AnyOf, string[]? AlsoOneOf = null);

    // order matters: the first matching rule wins
    private static readonly Rule[] Rules =
    [
        new(Intent.Approvals, ["approve", "approval", "jovahagy", "engedelyez", "decide", "dontes"]),
        new(Intent.HowToRequest, ["how", "hogyan", "hogy kell", "how do i", "mikent"], ["request", "kerj", "igeny", "access", "jogosultsag"]),
        new(Intent.MyRequests, ["my request", "requests", "kereseim", "kerelmeim", "igenyeim", "nyitott keres", "request status"]),
        new(Intent.MyTrainings, ["training", "course", "trening", "kepzes", "tanfolyam", "oktatas"]),
        new(Intent.MyAccess, ["my access", "what access", "permissions", "jogaim", "jogosultsagaim", "hozzafereseim", "mihez ferek"]),
        new(Intent.NewTicket, ["report a problem", "new ticket", "open a ticket", "hibat jelent", "uj jegy", "bejelent", "nem mukodik", "doesn't work", "does not work", "broken"]),
        new(Intent.MyTickets, ["ticket", "incident", "jegy", "hibajegy", "bejelentesem"]),
        new(Intent.Help, ["help", "segits", "segitseg", "mit tudsz", "what can you", "hello", "hi", "szia"]),
    ];

    [GeneratedRegex(@"(?:who(?:'s| is)\s+the\s+manager\s+of|manager\s+of|who\s+manages)\s+(?<name>[\p{L} .'-]+?)\s*\??$", RegexOptions.IgnoreCase)]
    private static partial Regex ManagerOfEn();

    [GeneratedRegex(@"^(?:ki\s+(?:a\s+|az\s+)?)?(?<name>[\p{L} .'-]+?)\s+(?:vezetoje|fonoke|managere)", RegexOptions.IgnoreCase)]
    private static partial Regex ManagerOfHu();

    [GeneratedRegex(@"(?:who\s+owns|owner\s+of|ki\s+a\s+gazdaja|gazdaja)\s+(?:the\s+|a\s+|az\s+)?(?<name>[\p{L}\d .-]+?)\s*(?:app|application|rendszer|alkalmazas)?\s*\??$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnerOf();

    [GeneratedRegex(@"(?:who\s+is|find|search|keresd meg|ki az a|ki az|ki)\s+(?<name>[\p{L} .'-]{3,}?)\s*\??$", RegexOptions.IgnoreCase)]
    private static partial Regex FindPerson();

    public static Recognized Recognize(string message)
    {
        var text = Fold(message);
        if (text.Length == 0) return new(Intent.Help);

        if (ManagerOfEn().Match(text) is { Success: true } m1) return new(Intent.WhoIsManager, Clean(m1.Groups["name"].Value));
        if (ManagerOfHu().Match(text) is { Success: true } m2) return new(Intent.WhoIsManager, Clean(m2.Groups["name"].Value));
        if (OwnerOf().Match(text) is { Success: true } m3) return new(Intent.SystemOwner, Clean(m3.Groups["name"].Value));

        foreach (var rule in Rules)
        {
            if (!rule.AnyOf.Any(k => ContainsWord(text, k))) continue;
            if (rule.AlsoOneOf is { } also && !also.Any(k => ContainsWord(text, k))) continue;
            return new(rule.Intent);
        }
        if (FindPerson().Match(text) is { Success: true } m4) return new(Intent.FindPerson, Clean(m4.Groups["name"].Value));
        return new(Intent.Unknown);
    }

    /// <summary>Lower case, accents removed ("Jóváhagyás" → "jovahagyas"), so both spellings match.</summary>
    public static string Fold(string s)
    {
        var normalized = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Keyword match at a word start. Longer keywords may have a suffix ("request" matches "requests", Hungarian
    /// endings too); short ones must be whole words ("hi" does not match "history").
    /// </summary>
    private static bool ContainsWord(string text, string keyword)
    {
        for (var i = text.IndexOf(keyword, StringComparison.Ordinal); i >= 0; i = text.IndexOf(keyword, i + 1, StringComparison.Ordinal))
        {
            var startOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var end = i + keyword.Length;
            var endOk = keyword.Length >= 4 || end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (startOk && endOk) return true;
        }
        return false;
    }

    private static string Clean(string name) => name.Trim().TrimEnd('?', '.', '!').Trim();
}
