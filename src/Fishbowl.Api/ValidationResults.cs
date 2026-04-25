using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api;

// Shared 413 translation for ResourceValidationException. Keeps every
// resource endpoint's error envelope identical: `{ error, field, reason }`.
// The error message names the resource so the same response shape works
// for notes, contacts, and todos without per-endpoint copy-paste.
public static class ValidationResults
{
    public static IResult PayloadTooLarge(ResourceValidationException ex)
        => Results.Json(new
        {
            error = $"{ex.Error.Resource} exceeds size limits",
            field = ex.Error.Field,
            reason = ex.Error.Reason,
        }, statusCode: StatusCodes.Status413PayloadTooLarge);
}
