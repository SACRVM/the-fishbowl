using Microsoft.AspNetCore.Http;

namespace Fishbowl.Api;

// The error envelope for everything a person may read: a machine-readable
// `error` code, the English `message` (what agents, logs and API clients
// read), and named arguments the client's i18n table fills in
// (`{ error: "out_of_range", message: "…", field: "Digest:Hour", min: 0, max: 23 }`).
// The SPA turns the code + arguments into the page's language
// (js/lib/errors.js); without a table entry it shows `message`.
public static class ApiErrors
{
    public static IResult Json(int status, string code, string message, object? args = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["error"] = code,
            ["message"] = message,
        };
        foreach (var (key, value) in Args(args))
        {
            var name = char.ToLowerInvariant(key[0]) + key[1..];
            if (name is "error" or "message") continue;
            body[name] = value;
        }
        return Results.Json(body, statusCode: status);
    }

    /// <summary>The named values of an anonymous object or a dictionary.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> Args(object? args) => args switch
    {
        null => [],
        IEnumerable<KeyValuePair<string, object?>> pairs => pairs,
        _ => args.GetType().GetProperties().Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(args))),
    };

    public static IResult BadRequest(string code, string message, object? args = null) =>
        Json(StatusCodes.Status400BadRequest, code, message, args);

    public static IResult Conflict(string code, string message, object? args = null) =>
        Json(StatusCodes.Status409Conflict, code, message, args);

    public static IResult NotFound(string code, string message, object? args = null) =>
        Json(StatusCodes.Status404NotFound, code, message, args);
}
