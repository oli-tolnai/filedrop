using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

public sealed record CollectionArchiveItem(string FilePath, string EntryName);

public sealed class CollectionZipResult(
    Guid collectionId,
    string archiveName,
    IReadOnlyList<CollectionArchiveItem> files,
    bool deleteAfterDownload,
    IServiceScopeFactory scopeFactory,
    StorageCapacityService storage) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        // A ZipArchive a központi jegyzéket lezáráskor szinkron módon írja ki.
        // Csak ennél a válasznál engedjük ezt meg: a ZIP közvetlenül a klienshez
        // áramlik, nem készül belőle ideiglenes vagy tartós szerveroldali fájl.
        var bodyControl = context.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;
        context.Response.ContentType = "application/zip";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(archiveName);
        context.Response.Headers.ContentDisposition = disposition.ToString();

        if (deleteAfterDownload)
        {
            context.Response.OnCompleted(() => DeleteCollectionAfterDownloadAsync(collectionId, scopeFactory, storage));
        }

        using var archive = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.EntryName, CompressionLevel.Fastest);
            await using var source = new FileStream(
                file.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = entry.Open();
            await source.CopyToAsync(destination, 1024 * 1024, context.RequestAborted);
        }
    }

    private static async Task DeleteCollectionAfterDownloadAsync(
        Guid collectionId,
        IServiceScopeFactory scopeFactory,
        StorageCapacityService storage)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileDropDbContext>();
        var collection = await db.SharedFiles
            .Include(file => file.CollectionFiles)
            .FirstOrDefaultAsync(file => file.Id == collectionId && file.IsCollection);
        if (collection is null || collection.FileDeletedAtUtc is not null) return;

        var deletedAtUtc = DateTime.UtcNow;
        foreach (var file in collection.CollectionFiles.Where(file => file.FileDeletedAtUtc is null))
        {
            ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, file.StoredFileName));
            file.FileDeletedAtUtc = deletedAtUtc;
            file.AccessCode = null;
        }

        collection.FileDeletedAtUtc = deletedAtUtc;
        collection.AccessCode = null;
        collection.BatchAccessTokenHash = null;
        collection.BatchAccessExpiresAtUtc = null;
        await db.SaveChangesAsync();
    }
}
