using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Data;

/// <summary>Tells a "this row already exists" save failure apart from every other database error.</summary>
public static class DbErrors
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>True when the save broke a unique index, for example because two requests added the same row at once.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException
        {
            SqliteErrorCode: SqliteConstraint,
            SqliteExtendedErrorCode: SqliteConstraintUnique or SqliteConstraintPrimaryKey,
        };
}
