using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Plooto.Ap.Infrastructure.Persistence;

public static class DbErrors
{
    private const int SqliteConstraint = 19;

    /// <summary>True when the failure is a UNIQUE/PRIMARY KEY violation (SQLite extended codes 1555/2067).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: 1555 or 2067 };
}
