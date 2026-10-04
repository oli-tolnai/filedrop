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
            var expired = await db.SharedFiles
                .Where(file => file.FileDeletedAtUtc == null
                    && (file.ConsumedAtUtc != null
                        || (file.ExpiresAtUtc != null && file.ExpiresAtUtc <= now)))
                .ToListAsync(cancellationToken);

            foreach (var share in expired)
            {
                ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, share.StoredFileName));
                share.FileDeletedAtUtc = now;
                share.AccessCode = null;
            }

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
