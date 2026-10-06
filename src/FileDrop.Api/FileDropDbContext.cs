using Microsoft.EntityFrameworkCore;

public sealed class FileDropDbContext(DbContextOptions<FileDropDbContext> options) : DbContext(options)
{
    public DbSet<SharedFile> SharedFiles => Set<SharedFile>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UploadInvitation> UploadInvitations => Set<UploadInvitation>();
    public DbSet<InvitationUploaderSession> InvitationUploaderSessions => Set<InvitationUploaderSession>();
    public DbSet<InvitationUpload> InvitationUploads => Set<InvitationUpload>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var share = modelBuilder.Entity<SharedFile>();
        share.HasKey(item => item.Id);
        share.Property(item => item.OriginalFileName).HasMaxLength(255);
        share.Property(item => item.StoredFileName).HasMaxLength(80);
        share.Property(item => item.ContentType).HasMaxLength(200);
        share.Property(item => item.Title).HasMaxLength(120);
        share.Property(item => item.Note).HasMaxLength(1000);
        share.Property(item => item.Visibility).HasMaxLength(20);
        share.Property(item => item.AccessCode).HasMaxLength(6);
        share.Property(item => item.RelativePath).HasMaxLength(1000);
        share.Property(item => item.BatchAccessTokenHash).HasMaxLength(64);
        share.HasIndex(item => item.AccessCode).IsUnique();
        share.HasIndex(item => item.ExpiresAtUtc);
        share.HasIndex(item => item.ParentShareId);

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

        share.HasOne(item => item.Owner).WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        share.HasOne(item => item.ParentShare).WithMany(item => item.CollectionFiles).HasForeignKey(item => item.ParentShareId).OnDelete(DeleteBehavior.Restrict);

        var invitation = modelBuilder.Entity<UploadInvitation>();
        invitation.HasKey(item => item.Id);
        invitation.Property(item => item.Code).HasMaxLength(8);
        invitation.Property(item => item.Visibility).HasMaxLength(20);
        invitation.Property(item => item.ShareExpiration).HasMaxLength(20);
        invitation.Property(item => item.Title).HasMaxLength(120);
        invitation.Property(item => item.Note).HasMaxLength(1000);
        invitation.HasIndex(item => item.Code).IsUnique();
        invitation.HasIndex(item => item.ExpiresAtUtc);
        invitation.HasOne(item => item.Owner).WithMany().HasForeignKey(item => item.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        invitation.HasOne(item => item.FinalShare).WithMany().HasForeignKey(item => item.FinalShareId).OnDelete(DeleteBehavior.SetNull);

        var uploaderSession = modelBuilder.Entity<InvitationUploaderSession>();
        uploaderSession.HasKey(item => item.Id);
        uploaderSession.Property(item => item.TokenHash).HasMaxLength(64);
        uploaderSession.HasIndex(item => item.TokenHash).IsUnique();
        uploaderSession.HasIndex(item => item.ExpiresAtUtc);
        uploaderSession.HasOne(item => item.Invitation).WithMany(item => item.UploaderSessions).HasForeignKey(item => item.InvitationId).OnDelete(DeleteBehavior.Cascade);

        var invitationUpload = modelBuilder.Entity<InvitationUpload>();
        invitationUpload.HasKey(item => item.Id);
        invitationUpload.Property(item => item.OriginalFileName).HasMaxLength(255);
        invitationUpload.Property(item => item.StoredFileName).HasMaxLength(80);
        invitationUpload.Property(item => item.ContentType).HasMaxLength(200);
        invitationUpload.HasIndex(item => item.InvitationId);
        invitationUpload.HasIndex(item => item.UploaderSessionId);
        invitationUpload.HasOne(item => item.Invitation).WithMany(item => item.Uploads).HasForeignKey(item => item.InvitationId).OnDelete(DeleteBehavior.Cascade);
        invitationUpload.HasOne(item => item.UploaderSession).WithMany(item => item.Uploads).HasForeignKey(item => item.UploaderSessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SharedFile
{
    public Guid Id { get; set; }
    public required string OriginalFileName { get; set; }
    public string? Title { get; set; }
    public string? Note { get; set; }
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
    public AppUser? Owner { get; set; }
    public bool IsCollection { get; set; }
    public Guid? ParentShareId { get; set; }
    public SharedFile? ParentShare { get; set; }
    public List<SharedFile> CollectionFiles { get; } = [];
    public string? RelativePath { get; set; }
    public string? BatchAccessTokenHash { get; set; }
    public DateTime? BatchAccessExpiresAtUtc { get; set; }
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

public sealed class UploadInvitation
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public AppUser? Owner { get; set; }
    public required string Code { get; set; }
    public required string Visibility { get; set; }
    public required string ShareExpiration { get; set; }
    public string? Title { get; set; }
    public string? Note { get; set; }
    public long MaxTotalBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? FinalShareId { get; set; }
    public SharedFile? FinalShare { get; set; }
    public List<InvitationUploaderSession> UploaderSessions { get; } = [];
    public List<InvitationUpload> Uploads { get; } = [];
}

public sealed class InvitationUploaderSession
{
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public UploadInvitation? Invitation { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public List<InvitationUpload> Uploads { get; } = [];
}

public sealed class InvitationUpload
{
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public UploadInvitation? Invitation { get; set; }
    public Guid UploaderSessionId { get; set; }
    public InvitationUploaderSession? UploaderSession { get; set; }
    public required string OriginalFileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
