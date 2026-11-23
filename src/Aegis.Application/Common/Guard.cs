using Aegis.Domain.Common;

namespace Aegis.Application.Common;

public static class Guard
{
    /// <summary>Throws 403 unless the user has one of the roles (Admin always passes).</summary>
    public static void RequireAny(this ICurrentUser user, params PortalRole[] roles)
    {
        var a = user.Actor;
        if (a.IsAdmin || roles.Any(a.Is)) return;
        throw new ForbiddenException($"This needs one of the roles: {string.Join(", ", roles)}.");
    }

    public static T OrNotFound<T>(this T? value, string entity, object key) where T : class =>
        value ?? throw new NotFoundException(entity, key);

    public static string Required(string? value, string field, int max = 200)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0) throw ValidationFailedException.For(field, "Required.");
        if (v.Length > max) throw ValidationFailedException.For(field, $"At most {max} characters.");
        return v;
    }
}
