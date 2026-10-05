using System.Threading.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FileDropOptions>(builder.Configuration.GetSection("FileDrop"));
builder.Services.AddSingleton<StorageCapacityService>();
builder.Services.AddSingleton<UploadReservationService>();
builder.Services.AddScoped<AccountSessionService>();
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

var fileDropOptions = builder.Configuration.GetSection("FileDrop").Get<FileDropOptions>()
    ?? new FileDropOptions();
var databasePath = Path.GetFullPath(fileDropOptions.DatabasePath, builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared,
}.ToString();

builder.Services.AddDbContext<FileDropDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddHostedService<ExpiredFileCleanupService>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("share-code", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));
});
builder.WebHost.ConfigureKestrel(options =>
{
    // A valódi felső határt a mindenkori szabad hely és a biztonsági tartalék adja.
    options.Limits.MaxRequestBodySize = null;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var storage = scope.ServiceProvider.GetRequiredService<StorageCapacityService>();
    Directory.CreateDirectory(storage.StoragePath);
    Directory.CreateDirectory(storage.TemporaryPath);
    Directory.CreateDirectory(storage.ReleaseDirectoryPath);
    var db = scope.ServiceProvider.GetRequiredService<FileDropDbContext>();
    db.Database.EnsureCreated();
    EnsureShareMetadataColumns(db);
}

app.UseRateLimiter();
var hasBundledWebApp = Directory.Exists(app.Environment.WebRootPath)
    && File.Exists(Path.Combine(app.Environment.WebRootPath, "index.html"));
if (hasBundledWebApp)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/storage", (StorageCapacityService storage) => Results.Ok(storage.GetStatus()));
app.MapAppReleaseEndpoints();
app.MapShareEndpoints();
app.MapAccountEndpoints();
if (hasBundledWebApp)
{
    app.MapFallbackToFile("index.html");
}

app.Run();

static void EnsureShareMetadataColumns(FileDropDbContext db)
{
    AddShareColumnIfMissing(db, "Title TEXT NULL");
    AddShareColumnIfMissing(db, "Note TEXT NULL");
    AddShareColumnIfMissing(db, "IsCollection INTEGER NOT NULL DEFAULT 0");
    AddShareColumnIfMissing(db, "ParentShareId TEXT NULL");
    AddShareColumnIfMissing(db, "RelativePath TEXT NULL");
    AddShareColumnIfMissing(db, "BatchAccessTokenHash TEXT NULL");
    AddShareColumnIfMissing(db, "BatchAccessExpiresAtUtc TEXT NULL");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_SharedFiles_ParentShareId ON SharedFiles (ParentShareId);");
}

static void AddShareColumnIfMissing(FileDropDbContext db, string definition)
{
    try
    {
#pragma warning disable EF1003 // The definitions are hard-coded immediately above, never user input.
        db.Database.ExecuteSqlRaw("ALTER TABLE SharedFiles ADD COLUMN " + definition + ";");
#pragma warning restore EF1003
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
        // Existing installations already have this column.
    }
}
