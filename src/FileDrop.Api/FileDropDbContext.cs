using Microsoft.EntityFrameworkCore;

public sealed class FileDropDbContext(DbContextOptions<FileDropDbContext> options) : DbContext(options)
{
    public DbSet<SharedFile> SharedFiles => Set<SharedFile>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var share = modelBuilder.Entity<SharedFile>();
        share.HasKey(item => item.Id);
        share.Property(item => item.OriginalFileName).HasMaxLength(255);
        share.Property(item => item.StoredFileName).HasMaxLength(80);
        share.Property(item => item.ContentType).HasMaxLength(200);
        share.Property(item => item.Visibility).HasMaxLength(20);
        share.Property(item => item.AccessCode).HasMaxLength(4);
        share.HasIndex(item => item.AccessCode).IsUnique();
        share.HasIndex(item => item.ExpiresAtUtc);

        var user = modelBuilder.Entity<AppUser>();
        user.HasKey(item => item.Id);
        user.Property(item => item.DisplayName).HasMaxLength(60);
        user.Property(item => item.NormalizedName).HasMaxLength(60);
        user.Property(item => item.PasswordHash).HasMaxLength(500);
        user.HasIndex(item => item.NormalizedName).IsUnique();

        var session = modelBuilder.Entity<UserSession>();
        session.HasKey(item => item.Id);
        session.Property(item => item.TokenHash).HasMaxLength(64);
        session.Property(item => item.DeviceName).HasMaxLength(80);
        session.HasIndex(item => item.TokenHash).IsUnique();
        session.HasIndex(item => item.ExpiresAtUtc);
        session.HasOne<AppUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);

        share.HasOne<AppUser>().WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class SharedFile
{
    public Guid Id { get; set; }
    public required string OriginalFileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string Visibility { get; set; }
    public string? AccessCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool DeleteAfterFirstDownload { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public DateTime? FileDeletedAtUtc { get; set; }
    public int DownloadCount { get; set; }
    public Guid? OwnerUserId { get; set; }
}

public sealed class AppUser
{
    public Guid Id { get; set; }
    public required string DisplayName { get; set; }
    public required string NormalizedName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsAdmin { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public required string DeviceName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
