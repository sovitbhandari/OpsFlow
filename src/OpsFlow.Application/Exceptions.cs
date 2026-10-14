namespace OpsFlow.Application;

public abstract class AppException(string message) : Exception(message);

public sealed class NotFoundException(string message = "Resource was not found.") : AppException(message);

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.") : AppException(message);

public sealed class ConflictException(string message) : AppException(message);

public sealed class PreconditionFailedException(string message) : AppException(message);

public sealed class InvalidTransitionException(string message) : AppException(message);

public sealed class AppValidationException(IReadOnlyDictionary<string, string[]> errors)
    : AppException("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
