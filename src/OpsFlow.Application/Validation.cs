using OpsFlow.Domain;

namespace OpsFlow.Application;

public static class RequestValidation
{
    public const int MaxPageSize = 100;

    public static void EnsureCreate(CreateWorkOrderRequest request)
    {
        var errors = new Dictionary<string, List<string>>();
        RequireText(errors, "title", request.Title, 200);
        OptionalText(errors, "description", request.Description, 4000);
        RequireText(errors, "category", request.Category, 100);
        if (!Enum.IsDefined(request.Priority))
        {
            Add(errors, "priority", "Priority is invalid.");
        }

        ThrowIfAny(errors);
    }

    public static void EnsureUpdate(UpdateWorkOrderRequest request)
    {
        var errors = new Dictionary<string, List<string>>();
        RequireText(errors, "title", request.Title, 200);
        OptionalText(errors, "description", request.Description, 4000);
        RequireText(errors, "category", request.Category, 100);
        if (!Enum.IsDefined(request.Priority))
        {
            Add(errors, "priority", "Priority is invalid.");
        }

        ThrowIfAny(errors);
    }

    public static void EnsureComment(string body)
    {
        var errors = new Dictionary<string, List<string>>();
        RequireText(errors, "body", body, 2000);
        ThrowIfAny(errors);
    }

    public static void EnsureMember(CreateMemberRequest request, bool passwordRequired)
    {
        var errors = new Dictionary<string, List<string>>();
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256 || !request.Email.Contains('@'))
        {
            Add(errors, "email", "A valid email address is required.");
        }

        RequireText(errors, "displayName", request.DisplayName, 120);
        if (!Enum.IsDefined(request.Role))
        {
            Add(errors, "role", "Role is invalid.");
        }

        if (passwordRequired && (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 12 || request.Password.Length > 128))
        {
            Add(errors, "password", "A new user requires a password between 12 and 128 characters.");
        }

        ThrowIfAny(errors);
    }

    public static void EnsureSla(IReadOnlyList<UpdateSlaPolicyRequest> policies)
    {
        var errors = new Dictionary<string, List<string>>();
        var priorities = Enum.GetValues<WorkOrderPriority>();
        if (policies.Count != priorities.Length || policies.Select(p => p.Priority).Distinct().Count() != priorities.Length)
        {
            Add(errors, "policies", "Provide exactly one duration for Low, Medium, High, and Critical.");
        }

        foreach (var policy in policies)
        {
            if (policy.DurationSeconds < 15 * 60 || policy.DurationSeconds > 90 * 24 * 60 * 60)
            {
                Add(errors, policy.Priority.ToString(), "Duration must be between 15 minutes and 90 days.");
            }
        }

        ThrowIfAny(errors);
    }

    public static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        var errors = new Dictionary<string, List<string>>();
        if (page < 1)
        {
            Add(errors, "page", "Page must be at least 1.");
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            Add(errors, "pageSize", $"Page size must be between 1 and {MaxPageSize}.");
        }

        ThrowIfAny(errors);
        return (page, pageSize);
    }

    private static void RequireText(Dictionary<string, List<string>> errors, string key, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, key, "This field is required.");
            return;
        }

        if (value.Trim().Length > max)
        {
            Add(errors, key, $"Must be at most {max} characters.");
        }
    }

    private static void OptionalText(Dictionary<string, List<string>> errors, string key, string? value, int max)
    {
        if (value is not null && value.Length > max)
        {
            Add(errors, key, $"Must be at most {max} characters.");
        }
    }

    private static void Add(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var list))
        {
            list = [];
            errors[key] = list;
        }

        list.Add(message);
    }

    private static void ThrowIfAny(Dictionary<string, List<string>> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        throw new AppValidationException(errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
    }
}
