using Microsoft.EntityFrameworkCore;

namespace NexZen.Infrastructure.Data;

/// <summary>
/// EnsureCreated does nothing on a database that already exists, so new tables are added
/// with CREATE TABLE IF NOT EXISTS at startup (as for DownloadRecords and SavedPasswords).
/// Matches the EF model in NexZenDbContext.
/// </summary>
public static class WorkspaceSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS "Workspaces" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Workspaces" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "Color" TEXT NULL,
            "SortOrder" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS "WorkspaceTabs" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceTabs" PRIMARY KEY AUTOINCREMENT,
            "WorkspaceId" INTEGER NOT NULL,
            "Title" TEXT NOT NULL,
            "Url" TEXT NOT NULL,
            "FaviconUrl" TEXT NULL,
            "SortOrder" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_WorkspaceTabs_Workspaces_WorkspaceId" FOREIGN KEY ("WorkspaceId")
                REFERENCES "Workspaces" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_WorkspaceTabs_WorkspaceId" ON "WorkspaceTabs" ("WorkspaceId");
        """;

    public static void Ensure(NexZenDbContext context)
    {
        context.Database.ExecuteSqlRaw(Sql);
        // Columns added after the first release of the table.
        if (!HasColumn(context, "Workspaces", "Color"))
            context.Database.ExecuteSqlRaw("""ALTER TABLE "Workspaces" ADD COLUMN "Color" TEXT NULL;""");
    }

    private static bool HasColumn(NexZenDbContext context, string table, string column)
    {
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
            connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        finally
        {
            if (opened)
                connection.Close();
        }
    }
}
