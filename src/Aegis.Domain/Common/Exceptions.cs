namespace Aegis.Domain.Common;

/// <summary>
/// Base of all expected, business-level errors. The API's central exception handler turns each subtype into a
/// ProblemDetails response with the right status code - no try/catch in controllers.
/// </summary>
public abstract class AegisException(string message, string code) : Exception(message)
{
    /// <summary>Stable, machine-readable error code (e.g. "request.duplicate"), returned to clients.</summary>
    public string Code { get; } = code;
}

/// <summary>Input is malformed (400).</summary>
public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : AegisException("One or more fields are invalid.", "validation")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public static ValidationFailedException For(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>A business rule forbids the operation (422).</summary>
public sealed class BusinessRuleException(string message, string code) : AegisException(message, code);

/// <summary>The operation is not possible in the current state (409).</summary>
public sealed class InvalidStateTransitionException(string entity, string state, string action)
    : AegisException($"Cannot {action} a {entity} that is {state}.", "state.transition")
{
    public string Entity { get; } = entity;
    public string State { get; } = state;
    public string Action { get; } = action;
}

/// <summary>The entity does not exist (404).</summary>
public sealed class NotFoundException(string entity, object key)
    : AegisException($"{entity} '{key}' was not found.", "not_found");

/// <summary>The current user may not do this (403).</summary>
public sealed class ForbiddenException(string message) : AegisException(message, "forbidden");

/// <summary>Someone else changed the record since it was loaded (409).</summary>
public sealed class ConcurrencyConflictException(string message) : AegisException(message, "concurrency");
