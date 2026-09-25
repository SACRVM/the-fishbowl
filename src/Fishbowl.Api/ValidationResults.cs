using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api;

// Shared translation for ResourceValidationException. Keeps every
// resource endpoint's error envelope identical: `{ error, field, reason }`.
// The error message names the resource so the same response shape works
// for notes, contacts, and todos without per-endpoint copy-paste.
// A size cap is 413; content the server won't accept as sent is 400.
public static class ValidationResults
{
    public static IResult From(ResourceValidationException ex)
        => ex.Error.Kind == ResourceValidationKind.Invalid
            ? Results.Json(new
            {
                error = $"{ex.Error.Resource} rejected",
                field = ex.Error.Field,
                reason = ex.Error.Reason,
            }, statusCode: StatusCodes.Status400BadRequest)
            : Results.Json(new
            {
                error = $"{ex.Error.Resource} exceeds size limits",
                field = ex.Error.Field,
                reason = ex.Error.Reason,
            }, statusCode: StatusCodes.Status413PayloadTooLarge);
}
