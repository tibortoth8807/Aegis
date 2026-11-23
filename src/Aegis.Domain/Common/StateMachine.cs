using System.Collections.Concurrent;
using System.Reflection;

namespace Aegis.Domain.Common;

/// <summary>Everything a state needs to perform a transition: who, when, why.</summary>
public sealed record TransitionContext(Actor Actor, DateTimeOffset Now, string? Comment = null);

/// <summary>
/// Marks a virtual method of a state base class as a transition ("action"). The base implementation throws
/// <see cref="InvalidStateTransitionException"/>; a concrete state supports the action by overriding it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TransitionAttribute(string action) : Attribute
{
    public string Action { get; } = action;
}

/// <summary>
/// State pattern base: one class per state instead of a switch over a status field.
/// <para>
/// Each workflow has an abstract state class (e.g. <c>AccessRequestState</c>) with one virtual method per action.
/// By default an action is not allowed. A concrete state (e.g. <c>PendingManagerApproval</c>) overrides only the
/// actions that make sense in it, checks its rules, and returns the next state. Adding a state means adding a
/// class - no existing code paths grow another <c>case</c>.
/// </para>
/// States are stateless singletons; the entity stores only the status value, which is what the database keeps.
/// </summary>
public abstract class StateBase<TStatus> where TStatus : struct, Enum
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<string>> ActionsByType = new();

    public abstract TStatus Status { get; }

    /// <summary>True for end states (no action leads out of them).</summary>
    public bool IsFinal => AllowedActions.Count == 0;

    /// <summary>The actions this state supports - derived from which transition methods it overrides.</summary>
    public IReadOnlyList<string> AllowedActions => ActionsByType.GetOrAdd(GetType(), static type =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetBaseDefinition() is var b && b.DeclaringType != m.DeclaringType)
            .Select(m => m.GetBaseDefinition().GetCustomAttribute<TransitionAttribute>()?.Action)
            .OfType<string>()
            .Distinct()
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList());

    public bool Allows(string action) => AllowedActions.Contains(action);

    public override string ToString() => Status.ToString();

    /// <summary>Helper for the base implementations of transition methods.</summary>
    protected InvalidStateTransitionException NotAllowed(string entity, string action) => new(entity, Humanize(Status.ToString()), action);

    /// <summary>"PendingManager" → "pending manager".</summary>
    public static string Humanize(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
