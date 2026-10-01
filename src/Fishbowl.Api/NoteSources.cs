using System.Security.Claims;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;

namespace Fishbowl.Api;

// Who wrote a note decides its tags: a key's write is an agent's
// (source:mcp + review:pending), a person's edit approves.
public static class NoteSources
{
    public static NoteSource Of(ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == McpContextClaims.BearerScheme ? NoteSource.Mcp : NoteSource.Human;
}
