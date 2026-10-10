using Microsoft.EntityFrameworkCore;

public sealed class ExpiredFileCleanupService(
    IServiceScopeFactory scopeFactory,
    StorageCapacityService storage,
    ILogger<ExpiredFileCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupAsync(stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileDropDbContext>();
            var now = DateTime.UtcNow;
            var expiredCollections = await db.SharedFiles
                .Include(file => file.CollectionFiles)
                .Where(file => file.IsCollection
                    && file.ParentShareId == null
                    && file.FileDeletedAtUtc == null
                    && ((file.ConsumedAtUtc != null
                            && (file.BatchAccessExpiresAtUtc == null || file.BatchAccessExpiresAtUtc <= now))
                        || (file.ExpiresAtUtc != null && file.ExpiresAtUtc <= now)))
                .ToListAsync(cancellationToken);

            foreach (var collection in expiredCollections)
            {
                foreach (var file in collection.CollectionFiles.Where(file => file.FileDeletedAtUtc == null))
                {
                    ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, file.StoredFileName));
                    file.FileDeletedAtUtc = now;
                    file.AccessCode = null;
                    file.PublicAccessCode = null;
                }

                collection.FileDeletedAtUtc = now;
                collection.AccessCode = null;
                collection.PublicAccessCode = null;
                collection.BatchAccessTokenHash = null;
                collection.BatchAccessExpiresAtUtc = null;
            }

            var expired = await db.SharedFiles
                .Where(file => file.FileDeletedAtUtc == null
                    && !file.IsCollection
                    && file.ParentShareId == null
                    && (file.ConsumedAtUtc != null
                        || (file.ExpiresAtUtc != null && file.ExpiresAtUtc <= now)))
                .ToListAsync(cancellationToken);

            foreach (var share in expired)
            {
                ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, share.StoredFileName));
                share.FileDeletedAtUtc = now;
                share.AccessCode = null;
                share.PublicAccessCode = null;
            }

            var expiredInvitationUploads = await db.InvitationUploads
                .Include(item => item.Invitation)
                .Where(item => item.Invitation!.ClosedAtUtc == null
                    && item.Invitation.RevokedAtUtc == null
                    && item.Invitation.ExpiresAtUtc <= now)
                .ToListAsync(cancellationToken);
            foreach (var upload in expiredInvitationUploads)
            {
                ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, upload.StoredFileName));
            }
            db.InvitationUploads.RemoveRange(expiredInvitationUploads);

            if (Directory.Exists(storage.TemporaryPath))
            {
                foreach (var temporaryFile in Directory.EnumerateFiles(storage.TemporaryPath, "*.uploading"))
                {
                    if (File.GetLastWriteTimeUtc(temporaryFile) < now.AddHours(-24))
                    {
                        ShareEndpoints.SafeDelete(temporaryFile);
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await db.UserSessions
                .Where(session => session.ExpiresAtUtc <= now)
                .ExecuteDeleteAsync(cancellationToken);
            await db.InvitationUploaderSessions
                .Where(session => session.ExpiresAtUtc <= now)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A lejárt FileDrop-fájlok takarítása sikertelen.");
        }
    }
}
