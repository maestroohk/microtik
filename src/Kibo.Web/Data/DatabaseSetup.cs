using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Data;

public static class DatabaseSetup
{
    public static async Task MigrateAsync(AppDbContext db)
    {
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(ReadEmbeddedSql("radius_functions.sql"));
    }

    private static string ReadEmbeddedSql(string fileName)
    {
        var assembly = typeof(DatabaseSetup).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
