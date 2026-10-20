using Microsoft.EntityFrameworkCore;

namespace NexZen.Infrastructure.Data;

/// <summary>Creates the SitePermissions table in existing databases (EnsureCreated skips them). Matches NexZenDbContext.</summary>
public static class SitePermissionSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS "SitePermissions" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_SitePermissions" PRIMARY KEY AUTOINCREMENT,
            "Site" TEXT NOT NULL,
            "Permission" TEXT NOT NULL,
            "Value" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SitePermissions_Site_Permission" ON "SitePermissions" ("Site", "Permission");
        """;

    public static void Ensure(NexZenDbContext context) => context.Database.ExecuteSqlRaw(Sql);
}
