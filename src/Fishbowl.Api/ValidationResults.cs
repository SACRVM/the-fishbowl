using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api;

// Shared translation for ResourceValidationException. Keeps every
// resource endpoint's error envelope identical (ApiErrors):
// `{ error: "resource_invalid" | "resource_too_large", message, resource,
// field, reason }`. The message names the resource so the same shape works
// for notes, contacts, and todos without per-endpoint copy-paste.
// A size cap is 413; content the server won't accept as sent is 400.
public static class ValidationResults
{
    public static IResult From(ResourceValidationException ex)
        => ex.Error.Kind == ResourceValidationKind.Invalid
            ? ApiErrors.Json(StatusCodes.Status400BadRequest, "resource_invalid", $"{ex.Error.Resource} rejected",
                new { resource = ex.Error.Resource, field = ex.Error.Field, reason = ex.Error.Reason })
            : ApiErrors.Json(StatusCodes.Status413PayloadTooLarge, "resource_too_large", $"{ex.Error.Resource} exceeds size limits",
                new { resource = ex.Error.Resource, field = ex.Error.Field, reason = ex.Error.Reason });
}
