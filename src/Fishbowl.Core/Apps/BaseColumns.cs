namespace Fishbowl.Core.Apps;

// The eight mandated row-hygiene columns server-injected at CREATE TABLE on
// every owner-defined app table. Centralised so the DDL generator, the row
// validator, and the "may the owner touch this column?" guard share one source
// of truth.
public static class BaseColumns
{
    public const string Id = "id";
    public const string Title = "title";
    public const string Author = "author";
    public const string CreatedAt = "created_at";
    public const string LastModified = "last_modified";
    public const string RowVersion = "row_version";
    public const string IsDeleted = "is_deleted";
    public const string DeletedAt = "deleted_at";
    public const string AdditionalData = "additional_data";

    // Typed mirror of the DDL below. Kept in declaration order so DescribeTable
    // can return base columns at the head of the column list.
    public static readonly IReadOnlyList<AppColumn> All = new[]
    {
        new AppColumn(Id, AppColumnType.Text, Nullable: false, IsBaseColumn: true),
        new AppColumn(Title, AppColumnType.Text, Nullable: false, IsBaseColumn: true),
        new AppColumn(Author, AppColumnType.Text, Nullable: true, IsBaseColumn: true),
        new AppColumn(CreatedAt, AppColumnType.DateTime, Nullable: false, IsBaseColumn: true),
        new AppColumn(LastModified, AppColumnType.DateTime, Nullable: false, IsBaseColumn: true),
        new AppColumn(RowVersion, AppColumnType.Integer, Nullable: false, IsBaseColumn: true),
        new AppColumn(IsDeleted, AppColumnType.Integer, Nullable: false, IsBaseColumn: true),
        new AppColumn(DeletedAt, AppColumnType.DateTime, Nullable: true, IsBaseColumn: true),
        new AppColumn(AdditionalData, AppColumnType.Json, Nullable: true, IsBaseColumn: true),
    };

    public static readonly HashSet<string> Names = new(All.Select(c => c.Name), StringComparer.Ordinal);

    public static bool IsBase(string columnName) => Names.Contains(columnName);

    // DDL fragment for the head of a CREATE TABLE. The row layer assumes the
    // exact types and defaults baked in here — keep them in lockstep.
    public const string DdlFragment =
        "id              TEXT PRIMARY KEY,\n    " +
        "title           TEXT NOT NULL,\n    " +
        "author          TEXT,\n    " +
        "created_at      TEXT NOT NULL,\n    " +
        "last_modified   TEXT NOT NULL,\n    " +
        "row_version     INTEGER NOT NULL DEFAULT 1,\n    " +
        "is_deleted      INTEGER NOT NULL DEFAULT 0,\n    " +
        "deleted_at      TEXT,\n    " +
        "additional_data TEXT";

    public static string SoftDeleteIndexName(string tableName) => $"idx_{tableName}_is_deleted";
}
