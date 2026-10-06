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
builder.Services.AddScoped<InvitationUploaderSessionService>();
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
    options.AddPolicy("public-invite", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
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
    EnsureInvitationTables(db);
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
app.MapInvitationEndpoints();
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

static void EnsureInvitationTables(FileDropDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS UploadInvitations (
            Id TEXT NOT NULL CONSTRAINT PK_UploadInvitations PRIMARY KEY,
            OwnerUserId TEXT NOT NULL,
            Code TEXT NOT NULL,
            Visibility TEXT NOT NULL,
            ShareExpiration TEXT NOT NULL,
            Title TEXT NULL,
            Note TEXT NULL,
            MaxTotalBytes INTEGER NOT NULL DEFAULT 1073741824,
            CreatedAtUtc TEXT NOT NULL,
            ExpiresAtUtc TEXT NOT NULL,
            ClosedAtUtc TEXT NULL,
            RevokedAtUtc TEXT NULL,
            FinalShareId TEXT NULL,
            CONSTRAINT FK_UploadInvitations_Users_OwnerUserId FOREIGN KEY (OwnerUserId) REFERENCES Users (Id) ON DELETE CASCADE,
            CONSTRAINT FK_UploadInvitations_SharedFiles_FinalShareId FOREIGN KEY (FinalShareId) REFERENCES SharedFiles (Id) ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_UploadInvitations_Code ON UploadInvitations (Code);
        CREATE INDEX IF NOT EXISTS IX_UploadInvitations_ExpiresAtUtc ON UploadInvitations (ExpiresAtUtc);
        CREATE INDEX IF NOT EXISTS IX_UploadInvitations_OwnerUserId ON UploadInvitations (OwnerUserId);
        CREATE INDEX IF NOT EXISTS IX_UploadInvitations_FinalShareId ON UploadInvitations (FinalShareId);
        CREATE TABLE IF NOT EXISTS InvitationUploaderSessions (
            Id TEXT NOT NULL CONSTRAINT PK_InvitationUploaderSessions PRIMARY KEY,
            InvitationId TEXT NOT NULL,
            TokenHash TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            LastSeenAtUtc TEXT NOT NULL,
            ExpiresAtUtc TEXT NOT NULL,
            CONSTRAINT FK_InvitationUploaderSessions_UploadInvitations_InvitationId FOREIGN KEY (InvitationId) REFERENCES UploadInvitations (Id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_InvitationUploaderSessions_TokenHash ON InvitationUploaderSessions (TokenHash);
        CREATE INDEX IF NOT EXISTS IX_InvitationUploaderSessions_ExpiresAtUtc ON InvitationUploaderSessions (ExpiresAtUtc);
        CREATE INDEX IF NOT EXISTS IX_InvitationUploaderSessions_InvitationId ON InvitationUploaderSessions (InvitationId);
        CREATE TABLE IF NOT EXISTS InvitationUploads (
            Id TEXT NOT NULL CONSTRAINT PK_InvitationUploads PRIMARY KEY,
            InvitationId TEXT NOT NULL,
            UploaderSessionId TEXT NOT NULL,
            OriginalFileName TEXT NOT NULL,
            StoredFileName TEXT NOT NULL,
            ContentType TEXT NOT NULL,
            SizeBytes INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            CONSTRAINT FK_InvitationUploads_UploadInvitations_InvitationId FOREIGN KEY (InvitationId) REFERENCES UploadInvitations (Id) ON DELETE CASCADE,
            CONSTRAINT FK_InvitationUploads_InvitationUploaderSessions_UploaderSessionId FOREIGN KEY (UploaderSessionId) REFERENCES InvitationUploaderSessions (Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_InvitationUploads_InvitationId ON InvitationUploads (InvitationId);
        CREATE INDEX IF NOT EXISTS IX_InvitationUploads_UploaderSessionId ON InvitationUploads (UploaderSessionId);
        """);
    AddInvitationColumnIfMissing(db, "MaxTotalBytes INTEGER NOT NULL DEFAULT 1073741824");
}

static void AddInvitationColumnIfMissing(FileDropDbContext db, string definition)
{
    try
    {
#pragma warning disable EF1003
        db.Database.ExecuteSqlRaw("ALTER TABLE UploadInvitations ADD COLUMN " + definition + ";");
#pragma warning restore EF1003
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
    }
}
