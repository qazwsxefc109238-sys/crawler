namespace Crawler_project.Services;

public static class DatabaseConnectionStringResolver
{
    public static string? Resolve(IConfiguration configuration)
    {
        return configuration.GetConnectionString("Default")
               ?? configuration["ConnectionStrings:Postgres"]
               ?? configuration["DB_CONNECTION_STRING"]
               ?? configuration["DATABASE_URL"]
               ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
               ?? Environment.GetEnvironmentVariable("DATABASE_URL");
    }
}