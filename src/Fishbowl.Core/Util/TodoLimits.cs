using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Hard upper bounds for TodoItem fields. Todos are short by nature —
// the title is the action, the description is optional context.
public static class TodoLimits
{
    public const string Resource = "todo";

    public const int MaxTitleLength = 1024;

    // 16 KB description. Anything longer wants to be a Note linked from
    // TodoItem.Source.
    public const int MaxDescriptionLength = 16 * 1024;

    // Source ID is a ULID (26 chars) but legacy callers may pass a URL
    // or label. Cap to fit a sensible URL without becoming an attack vector.
    public const int MaxSourceLength = 2048;

    public static ResourceValidationError? Validate(TodoItem item)
    {
        if (item.Title is { Length: > MaxTitleLength })
            return new ResourceValidationError(Resource, "title", $"exceeds {MaxTitleLength} characters");

        if (item.Description is { Length: > MaxDescriptionLength })
            return new ResourceValidationError(Resource, "description", $"exceeds {MaxDescriptionLength} characters");

        if (item.Source is { Length: > MaxSourceLength })
            return new ResourceValidationError(Resource, "source", $"exceeds {MaxSourceLength} characters");

        // Tags under the notes' rules (user/space schema v31).
        if (item.Tags is { Count: > NoteLimits.MaxTagCount })
            return new ResourceValidationError(Resource, "tags", $"exceeds {NoteLimits.MaxTagCount} entries");
        foreach (var tag in item.Tags ?? new())
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            if (!TagName.IsValid(tag))
                return new ResourceValidationError(Resource, "tags",
                    $"a tag must be 1–{NoteLimits.MaxTagLength} characters of a–z, 0–9, _, : and -",
                    ResourceValidationKind.Invalid);
        }

        return null;
    }
}
