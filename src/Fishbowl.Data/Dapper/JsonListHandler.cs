using System.Data;
using System.Text.Json;
using Dapper;

namespace Fishbowl.Data.Dapper;

/// <summary>
/// Serializes a List&lt;T&gt; column as JSON text (camelCase) — a contact's
/// emails, phones and addresses.
/// </summary>
public class JsonListHandler<T> : SqlMapper.TypeHandler<List<T>>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public override List<T>? Parse(object value)
    {
        if (value is null or DBNull) return new List<T>();
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return new List<T>();
        return JsonSerializer.Deserialize<List<T>>(s, Options) ?? new List<T>();
    }

    public override void SetValue(IDbDataParameter parameter, List<T>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = JsonSerializer.Serialize(value ?? new List<T>(), Options);
    }
}
