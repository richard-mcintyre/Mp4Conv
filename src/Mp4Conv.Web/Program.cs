using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Mp4Conv.Web.Components;
using Mp4Conv.Web.Data;
using Mp4Conv.Web.Services;

namespace Mp4Conv.Web;

public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        if (!builder.Environment.IsDevelopment())
            builder.Logging.AddEventLog(settings => settings.SourceName = "Mp4Conv");

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddHttpContextAccessor();

        string dbPath = Path.Combine(AppContext.BaseDirectory, "conv.db");
        builder.Services.AddDbContextFactory<Mp4ConvDbContext>(opts =>
            opts.UseSqlite($"Data Source={dbPath}"));

        AppSettings appsettings = builder.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();
        builder.Services.AddSingleton(appsettings);

        builder.Services.AddSingleton<UncConnectionService>();
        builder.Services.AddSingleton<ConversionProgressService>();
        builder.Services.AddSingleton<ConversionBackgroundService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConversionBackgroundService>());

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "keys")));

        WebApplication app = builder.Build();

        using (Mp4ConvDbContext dbContext = app.Services.GetRequiredService<IDbContextFactory<Mp4ConvDbContext>>().CreateDbContext())
        {
            dbContext.Database.EnsureCreated();

            if (!dbContext.ConfigSettings.Any())
            {
                dbContext.ConfigSettings.Add(new ConfigSettingsEntity());
                dbContext.SaveChanges();
            }

            // Reset any InProgress entries to NotStarted (recover from unclean shutdown)
            dbContext.Database.ExecuteSqlRaw("""
                UPDATE "ConversionQueue" SET "Status" = 0, "StatusChangedAt" = NULL
                WHERE "Status" = 1
                """);

            // Create UncCredentials table for databases that pre-date this feature
            dbContext.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "UncCredentials" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "UncPath" TEXT NOT NULL DEFAULT '',
                    "Username" TEXT NOT NULL DEFAULT '',
                    "EncryptedPassword" TEXT NOT NULL DEFAULT ''
                )
                """);

            // Add ProcessorAffinityMask column for databases that pre-date this feature
            try
            {
                dbContext.Database.ExecuteSqlRaw("""
                    ALTER TABLE "ConfigSettings" ADD COLUMN "ProcessorAffinityMask" INTEGER NOT NULL DEFAULT 0
                    """);
            }
            catch { /* column already exists */ }

            // Add StartedAt column for databases that pre-date this feature
            try
            {
                dbContext.Database.ExecuteSqlRaw("""
                    ALTER TABLE "ConversionQueue" ADD COLUMN "StartedAt" TEXT NULL
                    """);
            }
            catch { /* column already exists */ }
        }

        app.Services.GetRequiredService<UncConnectionService>().ConnectAllAsync().GetAwaiter().GetResult();

        // Support running under an IIS virtual folder
        // Set "PathBase": "/cameras" in appsettings.json (or an env-specific override) for IIS deployments.
        // Leave it absent/empty when running locally.
        string? pathBase = app.Configuration["PathBase"];
        if (!string.IsNullOrWhiteSpace(pathBase))
            app.UsePathBase(pathBase);

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.MapStaticAssets();
        app.UseAntiforgery();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
    }
}
