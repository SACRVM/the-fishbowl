using System.Text.Json;
using Fishbowl.Core.Apps;
using Xunit;

namespace Fishbowl.Core.Tests.Apps;

// Phase B.4 — DSL compile + validation. The DSL is read-only and table-scoped:
// inputs are validated against a fake `transactions` schema; outputs assert
// the generated SQL + parameter dictionary. SQL strings are intentionally
// brittle in tests — they pin the literal output so any future tweak surfaces
// a deliberate diff.
public class QueryDslTests
{
    private static readonly IReadOnlyList<AppColumn> Schema = BaseColumns.All
        .Concat(new[]
        {
            new AppColumn("amount", AppColumnType.Real, Nullable: false),
            new AppColumn("category", AppColumnType.Text),
            new AppColumn("date", AppColumnType.DateTime),
            new AppColumn("paid", AppColumnType.Boolean),
            new AppColumn("count", AppColumnType.Integer),
        })
        .ToList();

    private static JsonElement Json(string raw)
        => JsonDocument.Parse(raw).RootElement;

    private static QuerySpec Spec(string? whereJson = null, IReadOnlyList<OrderByClause>? orderBy = null,
        int? limit = null, int? offset = null, bool includeDeleted = false)
        => new(
            Where: whereJson is null ? null : Json(whereJson),
            OrderBy: orderBy,
            Limit: limit,
            Offset: offset,
            IncludeDeleted: includeDeleted);

    [Fact]
    public void Empty_WherePrependsSoftDeleteFilter()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema, Spec());
        Assert.Equal(
            "SELECT * FROM \"transactions\" WHERE \"is_deleted\" = 0 LIMIT 100",
            compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void IncludeDeleted_SkipsSoftDeleteFilter()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema, Spec(includeDeleted: true));
        Assert.Equal(
            "SELECT * FROM \"transactions\" LIMIT 100",
            compiled.Sql);
    }

    [Fact]
    public void ScalarSugar_DesugarsToEq()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema, Spec("""{ "category": "food" }"""));
        Assert.Contains("\"category\" = @category_0", compiled.Sql);
        Assert.Equal("food", compiled.Parameters["category_0"]);
    }

    [Fact]
    public void CompoundOperatorsOnSameColumn_AndedTogether()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema,
            Spec("""{ "amount": { "$gt": 0, "$lte": 1000 } }"""));
        Assert.Contains("\"amount\" > @amount_0", compiled.Sql);
        Assert.Contains("\"amount\" <= @amount_1", compiled.Sql);
        Assert.Equal(0d, compiled.Parameters["amount_0"]);
        Assert.Equal(1000d, compiled.Parameters["amount_1"]);
    }

    [Fact]
    public void DollarIn_ProducesPlaceholderList()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema,
            Spec("""{ "category": { "$in": ["food", "fuel"] } }"""));
        Assert.Contains("\"category\" IN (@category_0, @category_1)", compiled.Sql);
        Assert.Equal("food", compiled.Parameters["category_0"]);
        Assert.Equal("fuel", compiled.Parameters["category_1"]);
    }

    [Fact]
    public void DollarLike_OnText_OK()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema,
            Spec("""{ "category": { "$like": "%food%" } }"""));
        Assert.Contains("\"category\" LIKE @category_0", compiled.Sql);
    }

    [Fact]
    public void DollarLike_OnNonText_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "amount": { "$like": "%5%" } }""")));
        Assert.Equal(QueryDslErrorCodes.OperatorNotAllowed, ex.Code);
    }

    [Fact]
    public void DollarAnd_DollarOr_DollarNot_AllCompile()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema, Spec("""
            {
              "$and": [{ "amount": { "$gt": 5 } }, { "category": "food" }],
              "$or":  [{ "paid": true }, { "count": { "$gte": 1 } }],
              "$not": { "category": { "$eq": "refund" } }
            }
            """));
        Assert.Contains("AND", compiled.Sql);
        Assert.Contains("OR", compiled.Sql);
        Assert.Contains("NOT", compiled.Sql);
    }

    [Fact]
    public void IsNull_AndIsNotNull_AreLiteralTrueOnly()
    {
        var ok = QueryDsl.CompileSelect("transactions", Schema,
            Spec("""{ "date": { "$isNull": true } }"""));
        Assert.Contains("\"date\" IS NULL", ok.Sql);

        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "date": { "$isNull": false } }""")));
        Assert.Equal(QueryDslErrorCodes.BadShape, ex.Code);
    }

    [Fact]
    public void UnknownColumn_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "ghost": { "$eq": 1 } }""")));
        Assert.Equal(QueryDslErrorCodes.UnknownColumn, ex.Code);
        Assert.Equal("ghost", ex.Field);
    }

    [Fact]
    public void AdditionalData_FilterRejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "additional_data": { "$eq": "x" } }""")));
        Assert.Equal(QueryDslErrorCodes.UnqueryableColumn, ex.Code);
        Assert.Equal("additional_data", ex.Field);
    }

    [Fact]
    public void TypeMismatch_StringForRealColumn_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "amount": { "$gt": "five" } }""")));
        Assert.Equal(QueryDslErrorCodes.TypeMismatch, ex.Code);
    }

    [Fact]
    public void DepthExceeded_Rejected()
    {
        // 6 levels of nested $and exceeds MaxDepth=5.
        var nested = """{ "$and": [{ "$and": [{ "$and": [{ "$and": [{ "$and": [{ "$and": [{ "amount": 1 }] }] }] }] }] }] }""";
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema, Spec(nested)));
        Assert.Equal(QueryDslErrorCodes.DepthExceeded, ex.Code);
    }

    [Fact]
    public void LeavesExceeded_Rejected()
    {
        // 101 sibling $eq predicates beats MaxLeaves=100.
        var sb = new System.Text.StringBuilder();
        sb.Append("{ \"$and\": [");
        for (var i = 0; i < 101; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{ \"amount\": ").Append(i).Append(" }");
        }
        sb.Append("] }");
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema, Spec(sb.ToString())));
        Assert.Equal(QueryDslErrorCodes.LeavesExceeded, ex.Code);
    }

    [Fact]
    public void InTooLarge_Rejected()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{ \"category\": { \"$in\": [");
        for (var i = 0; i < 51; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("\"v").Append(i).Append('"');
        }
        sb.Append("] } }");
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema, Spec(sb.ToString())));
        Assert.Equal(QueryDslErrorCodes.InTooLarge, ex.Code);
    }

    [Fact]
    public void Limit_ClampedToMax()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema, Spec(limit: 99999));
        Assert.Contains($"LIMIT {QueryDsl.MaxLimit}", compiled.Sql);
    }

    [Fact]
    public void Limit_Defaults()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema, Spec());
        Assert.Contains($"LIMIT {QueryDsl.DefaultLimit}", compiled.Sql);
    }

    [Fact]
    public void OrderBy_HappyPath()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema,
            Spec(orderBy: new[] { new OrderByClause("date", "desc") }));
        Assert.Contains("ORDER BY \"date\" DESC", compiled.Sql);
    }

    [Fact]
    public void OrderBy_UnknownColumn_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec(orderBy: new[] { new OrderByClause("ghost", "asc") })));
        Assert.Equal(QueryDslErrorCodes.UnknownColumn, ex.Code);
    }

    [Fact]
    public void OrderBy_AdditionalData_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec(orderBy: new[] { new OrderByClause("additional_data", "asc") })));
        Assert.Equal(QueryDslErrorCodes.UnqueryableColumn, ex.Code);
    }

    [Fact]
    public void OrderBy_BadDirection_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec(orderBy: new[] { new OrderByClause("date", "diagonal") })));
        Assert.Equal(QueryDslErrorCodes.BadDirection, ex.Code);
    }

    [Fact]
    public void Offset_Appended()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema, Spec(limit: 10, offset: 5));
        Assert.EndsWith("LIMIT 10 OFFSET 5", compiled.Sql);
    }

    [Fact]
    public void CountQuery_OmitsOrderByLimitOffset()
    {
        var compiled = QueryDsl.CompileCount("transactions", Schema,
            Spec("""{ "amount": { "$gt": 0 } }""", orderBy: new[] { new OrderByClause("date", "desc") },
                limit: 10, offset: 5));
        Assert.StartsWith("SELECT COUNT(*)", compiled.Sql);
        Assert.DoesNotContain("ORDER BY", compiled.Sql);
        Assert.DoesNotContain("LIMIT", compiled.Sql);
        Assert.DoesNotContain("OFFSET", compiled.Sql);
    }

    [Fact]
    public void DollarIn_EmptyArray_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "category": { "$in": [] } }""")));
        Assert.Equal(QueryDslErrorCodes.BadShape, ex.Code);
    }

    [Fact]
    public void BoolValueOnIntegerColumn_Coerced()
    {
        var compiled = QueryDsl.CompileSelect("transactions", Schema,
            Spec("""{ "paid": true }"""));
        Assert.Equal(1L, compiled.Parameters["paid_0"]);
    }

    [Fact]
    public void DollarOr_RequiresArray()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "$or": { "amount": 5 } }""")));
        Assert.Equal(QueryDslErrorCodes.BadCombinator, ex.Code);
    }

    [Fact]
    public void DollarNot_RequiresObject()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "$not": [{ "amount": 5 }] }""")));
        Assert.Equal(QueryDslErrorCodes.BadCombinator, ex.Code);
    }

    [Fact]
    public void UnknownOperator_Rejected()
    {
        var ex = Assert.Throws<QueryDslException>(() =>
            QueryDsl.CompileSelect("transactions", Schema,
                Spec("""{ "amount": { "$lol": 1 } }""")));
        Assert.Equal(QueryDslErrorCodes.UnknownOperator, ex.Code);
    }
}
