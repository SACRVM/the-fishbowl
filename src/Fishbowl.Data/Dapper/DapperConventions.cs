using Dapper;

namespace Fishbowl.Data.Dapper;

public static class DapperConventions
{
    private static bool _installed;
    private static readonly object _lock = new();

    /// <summary>
    /// Enables snake_case ↔ PascalCase column mapping and registers custom type handlers.
    /// Safe to call multiple times; installs exactly once per process.
    /// </summary>
    public static void Install()
    {
        lock (_lock)
        {
            if (_installed) return;

            DefaultTypeMap.MatchNamesWithUnderscores = true;
            SqlMapper.AddTypeHandler(new JsonTagsHandler());
            SqlMapper.AddTypeHandler(new JsonListHandler<Fishbowl.Core.Models.ContactValue>());
            SqlMapper.AddTypeHandler(new JsonListHandler<Fishbowl.Core.Models.ContactAddress>());

            _installed = true;
        }
    }
}
