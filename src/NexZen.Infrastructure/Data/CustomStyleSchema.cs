using Microsoft.EntityFrameworkCore;

namespace NexZen.Infrastructure.Data;

/// <summary>Creates the CustomStyles table in existing databases (EnsureCreated skips them). Matches NexZenDbContext.</summary>
public static class CustomStyleSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS "CustomStyles" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CustomStyles" PRIMARY KEY AUTOINCREMENT,
            "Site" TEXT NOT NULL,
            "Css" TEXT NOT NULL,
            "Enabled" INTEGER NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_CustomStyles_Site" ON "CustomStyles" ("Site");
        """;

    public static void Ensure(NexZenDbContext context) => context.Database.ExecuteSqlRaw(Sql);
}
