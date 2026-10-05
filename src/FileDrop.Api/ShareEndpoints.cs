using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

public static class ShareEndpoints
{
    private static readonly SemaphoreSlim AccessCodeGate = new(1, 1);
    private const string ShareCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/shares", ListSharedFilesAsync);
        endpoints.MapPost("/api/shares", CreateShareAsync);
        endpoints.MapPost("/api/shares/resolve-code", ResolveCodeAsync)
            .RequireRateLimiting("share-code");
        endpoints.MapGet("/api/shares/code/{code}/download", DownloadByCodeAsync)
            .RequireRateLimiting("share-code");
        endpoints.MapGet("/api/shares/{id:guid}/download", DownloadAsync);
        endpoints.MapGet("/api/me/shares", ListMySharesAsync);
        endpoints.MapDelete("/api/me/shares/{id:guid}", DeleteMyShareAsync);
        return endpoints;
    }

    private static async Task<IResult> ListSharedFilesAsync(
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var account = await sessions.GetCurrentAsync(context, db, cancellationToken);
        if (account is null)
        {
            return TypedResults.Unauthorized();
        }

        var now = DateTime.UtcNow;
        var files = await db.SharedFiles
            .Include(file => file.Owner)
            .Where(file => file.Visibility == "shared"
                && file.FileDeletedAtUtc == null
                && file.ConsumedAtUtc == null
                && (file.ExpiresAtUtc == null || file.ExpiresAtUtc > now))
            .OrderByDescending(file => file.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        await EnsureAccessCodesAsync(files, db, cancellationToken);

        return Results.Ok<IReadOnlyList<ShareDto>>(files.Select(file => ToDto(file)).ToList());
    }

    private static async Task<IResult> CreateShareAsync(
        HttpRequest request,
        string fileName,
        string visibility,
        string expiration,
        string? title,
        string? note,
        FileDropDbContext db,
        StorageCapacityService storage,
        UploadReservationService reservations,
        AccountSessionService sessions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var safeFileName = SanitizeFileName(fileName);
        if (safeFileName is null)
        {
            return Results.BadRequest(new ApiError("Érvénytelen vagy túl hosszú fájlnév."));
        }

        visibility = visibility.Trim().ToLowerInvariant();
        if (visibility is not ("shared" or "code"))
        {
            return Results.BadRequest(new ApiError("A láthatóság csak 'shared' vagy 'code' lehet."));
        }

        if (!TryGetExpiration(expiration, out var expiresAtUtc, out var deleteAfterFirstDownload))
        {
            return Results.BadRequest(new ApiError("Ismeretlen lejárati beállítás."));
        }

        var safeTitle = SanitizeMetadata(title, 120);
        if (!string.IsNullOrWhiteSpace(title) && safeTitle is null)
        {
            return Results.BadRequest(new ApiError("A cím legfeljebb 120 karakter lehet."));
        }

        var safeNote = SanitizeMetadata(note, 1000);
        if (!string.IsNullOrWhiteSpace(note) && safeNote is null)
        {
            return Results.BadRequest(new ApiError("A megjegyzés legfeljebb 1000 karakter lehet."));
        }

        var owner = await sessions.GetCurrentAsync(context, db, cancellationToken);
        if (owner is null)
        {
            return Results.Unauthorized();
        }

        var contentLength = request.ContentLength;
        if (contentLength is null)
        {
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        }

        if (contentLength <= 0)
        {
            return Results.BadRequest(new ApiError("Üres fájl nem tölthető fel."));
        }

        using var reservation = reservations.TryReserve(contentLength.Value, storage.GetUploadCapacityBytes());
        if (reservation is null)
        {
            return Results.Json(
                new ApiError("Nincs elegendő hely a 100 GB-os biztonsági tartalék megtartásával."),
                statusCode: StatusCodes.Status507InsufficientStorage);
        }

        Directory.CreateDirectory(storage.StoragePath);
        Directory.CreateDirectory(storage.TemporaryPath);

        var id = Guid.NewGuid();
        var storedFileName = $"{id:N}.bin";
        var temporaryFilePath = Path.Combine(storage.TemporaryPath, $"{id:N}.uploading");
        var finalFilePath = Path.Combine(storage.StoragePath, storedFileName);

        try
        {
            long writtenBytes = 0;
            await using (var output = new FileStream(
                temporaryFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    writtenBytes += read;
                    if (writtenBytes > contentLength.Value)
                    {
                        return Results.BadRequest(new ApiError("A feltöltött adat nagyobb a jelzett fájlméretnél."));
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    reservation.ReportWritten(read);
                }

                await output.FlushAsync(cancellationToken);
            }

            if (writtenBytes != contentLength.Value)
            {
                return Results.BadRequest(new ApiError("A feltöltés nem érkezett meg teljesen."));
            }

            File.Move(temporaryFilePath, finalFilePath);

            var share = new SharedFile
            {
                Id = id,
                OriginalFileName = safeFileName,
                Title = safeTitle,
                Note = safeNote,
                StoredFileName = storedFileName,
                ContentType = SanitizeContentType(request.ContentType),
                SizeBytes = writtenBytes,
                Visibility = visibility,
                AccessCode = null,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = expiresAtUtc,
                DeleteAfterFirstDownload = deleteAfterFirstDownload,
                OwnerUserId = owner.Id,
                Owner = null,
            };

            await AccessCodeGate.WaitAsync(cancellationToken);
            try
            {
                share.AccessCode = await GenerateUniqueCodeAsync(db, cancellationToken);
                db.SharedFiles.Add(share);
                await db.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                AccessCodeGate.Release();
            }

            return Results.Created($"/api/shares/{share.Id}/download", ToDto(share));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch
        {
            SafeDelete(temporaryFilePath);
            SafeDelete(finalFilePath);
            throw;
        }
        finally
        {
            SafeDelete(temporaryFilePath);
        }
    }

    private static async Task<IResult> ResolveCodeAsync(
        ResolveCodeRequest request,
        FileDropDbContext db,
        CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim();
        if (code is null || (code.Length != 4 && code.Length != 6) || code.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return Results.BadRequest(new ApiError("Az új megosztási kód 6 betűből és/vagy számból áll."));
        }

        code = code.ToUpperInvariant();

        var now = DateTime.UtcNow;
        var share = await db.SharedFiles
            .Include(file => file.Owner)
            .AsNoTracking()
            .FirstOrDefaultAsync(file => file.AccessCode == code
                && file.FileDeletedAtUtc == null
                && file.ConsumedAtUtc == null
                && (file.ExpiresAtUtc == null || file.ExpiresAtUtc > now), cancellationToken);

        return share is null
            ? Results.NotFound(new ApiError("Nincs aktív megosztás ezzel a kóddal."))
            : Results.Ok(ToDto(share, codeDownload: true));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        StorageCapacityService storage,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken,
        bool allowAnonymousCode = false)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var share = await db.SharedFiles.FirstOrDefaultAsync(file => file.Id == id, cancellationToken);

        if (share is null
            || share.FileDeletedAtUtc is not null
            || share.ConsumedAtUtc is not null
            || (share.ExpiresAtUtc is not null && share.ExpiresAtUtc <= now))
        {
            return Results.NotFound(new ApiError("A fájl nem található vagy már lejárt."));
        }

        if (share.Visibility == "shared"
            && !allowAnonymousCode
            && await sessions.GetCurrentAsync(context, db, cancellationToken) is null)
        {
            return Results.Unauthorized();
        }

        var filePath = Path.Combine(storage.StoragePath, share.StoredFileName);
        if (!File.Exists(filePath))
        {
            share.FileDeletedAtUtc = now;
            share.AccessCode = null;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.NotFound(new ApiError("A fájl már nincs a tárhelyen."));
        }

        share.DownloadCount++;
        if (share.DeleteAfterFirstDownload)
        {
            share.ConsumedAtUtc = now;
            share.AccessCode = null;
            context.Response.OnCompleted(async () =>
            {
                SafeDelete(filePath);
                using var scope = scopeFactory.CreateScope();
                var completionDb = scope.ServiceProvider.GetRequiredService<FileDropDbContext>();
                var completedShare = await completionDb.SharedFiles.FindAsync(id);
                if (completedShare is not null)
                {
                    completedShare.FileDeletedAtUtc = DateTime.UtcNow;
                    completedShare.AccessCode = null;
                    await completionDb.SaveChangesAsync();
                }
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.File(
            filePath,
            contentType: share.ContentType,
            fileDownloadName: share.OriginalFileName,
            enableRangeProcessing: !share.DeleteAfterFirstDownload);
    }

    private static async Task<IResult> DownloadByCodeAsync(
        string code,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        StorageCapacityService storage,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if ((normalized.Length != 4 && normalized.Length != 6)
            || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return Results.NotFound(new ApiError("A megosztás nem található."));
        }

        var shareExists = await db.SharedFiles.AnyAsync(file => file.AccessCode == normalized
            && file.FileDeletedAtUtc == null
            && file.ConsumedAtUtc == null
            && (file.ExpiresAtUtc == null || file.ExpiresAtUtc > DateTime.UtcNow), cancellationToken);
        if (!shareExists) return Results.NotFound(new ApiError("A megosztás nem található vagy már lejárt."));

        var share = await db.SharedFiles.AsNoTracking()
            .FirstAsync(file => file.AccessCode == normalized, cancellationToken);
        return await DownloadAsync(share.Id, context, db, sessions, storage, scopeFactory, cancellationToken, allowAnonymousCode: true);
    }

    private static ShareDto ToDto(SharedFile file, bool codeDownload = false) => new(
        file.Id,
        file.OriginalFileName,
        file.Title,
        file.Note,
        file.SizeBytes,
        file.Visibility,
        file.AccessCode,
        file.Owner?.DisplayName,
        file.CreatedAtUtc,
        file.ExpiresAtUtc,
        file.DeleteAfterFirstDownload,
        file.DownloadCount,
        codeDownload && file.AccessCode is not null
            ? $"/api/shares/code/{file.AccessCode}/download"
            : $"/api/shares/{file.Id}/download");

    private static async Task<IResult> ListMySharesAsync(
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var account = await sessions.GetCurrentAsync(context, db, cancellationToken);
        if (account is null) return Results.Unauthorized();

        var files = await db.SharedFiles
            .Include(file => file.Owner)
            .Where(file => file.OwnerUserId == account.Id)
            .OrderByDescending(file => file.CreatedAtUtc)
            .Take(250)
            .ToListAsync(cancellationToken);
        await EnsureAccessCodesAsync(files, db, cancellationToken);
        return Results.Ok(files.Select(file => new OwnedShareDto(
            ToDto(file),
            file.FileDeletedAtUtc is not null || file.ConsumedAtUtc is not null
                ? "deleted"
                : file.ExpiresAtUtc is not null && file.ExpiresAtUtc <= DateTime.UtcNow
                    ? "expired"
                    : "active")).ToList());
    }

    private static async Task<IResult> DeleteMyShareAsync(
        Guid id,
        HttpContext context,
        FileDropDbContext db,
        StorageCapacityService storage,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var account = await sessions.GetCurrentAsync(context, db, cancellationToken);
        if (account is null) return Results.Unauthorized();

        var share = await db.SharedFiles.FirstOrDefaultAsync(file => file.Id == id, cancellationToken);
        if (share is null) return Results.NotFound();
        if (share.OwnerUserId != account.Id && !account.IsAdmin) return Results.Forbid();

        SafeDelete(Path.Combine(storage.StoragePath, share.StoredFileName));
        share.FileDeletedAtUtc = DateTime.UtcNow;
        share.AccessCode = null;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static string? SanitizeFileName(string fileName)
    {
        var result = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(result) || result.Length > 255 || result.Any(char.IsControl))
        {
            return null;
        }

        return result;
    }

    private static string SanitizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 200 || contentType.Any(char.IsControl))
        {
            return "application/octet-stream";
        }

        return contentType;
    }

    private static string? SanitizeMetadata(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    private static bool TryGetExpiration(
        string value,
        out DateTime? expiresAtUtc,
        out bool deleteAfterFirstDownload)
    {
        var now = DateTime.UtcNow;
        deleteAfterFirstDownload = false;
        expiresAtUtc = value.Trim().ToLowerInvariant() switch
        {
            "15m" => now.AddMinutes(15),
            "1h" => now.AddHours(1),
            "24h" => now.AddHours(24),
            "7d" => now.AddDays(7),
            "manual" => null,
            "after-download" => null,
            _ => DateTime.MinValue,
        };

        if (expiresAtUtc == DateTime.MinValue)
        {
            expiresAtUtc = null;
            return false;
        }

        deleteAfterFirstDownload = value.Equals("after-download", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static async Task<string> GenerateUniqueCodeAsync(
        FileDropDbContext db,
        CancellationToken cancellationToken)
    {
        var codeChars = new char[6];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            for (var index = 0; index < codeChars.Length; index++)
            {
                codeChars[index] = ShareCodeAlphabet[RandomNumberGenerator.GetInt32(ShareCodeAlphabet.Length)];
            }

            var code = new string(codeChars);
            if (!await db.SharedFiles.AnyAsync(file => file.AccessCode == code, cancellationToken))
            {
                return code;
            }
        }

        throw new InvalidOperationException("Nem sikerült szabad megosztási kódot létrehozni.");
    }

    private static async Task EnsureAccessCodesAsync(
        IReadOnlyCollection<SharedFile> files,
        FileDropDbContext db,
        CancellationToken cancellationToken)
    {
        var missing = files
            .Where(file => file.AccessCode is null
                && file.Visibility is ("shared" or "code")
                && file.FileDeletedAtUtc is null
                && file.ConsumedAtUtc is null)
            .ToList();
        if (missing.Count == 0) return;

        await AccessCodeGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in missing)
            {
                file.AccessCode = await GenerateUniqueCodeAsync(db, cancellationToken);
                db.Entry(file).State = EntityState.Modified;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            AccessCodeGate.Release();
        }
    }

    internal static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A következő takarítás újra megpróbálja.
        }
        catch (UnauthorizedAccessException)
        {
            // A hibát a következő állapotellenőrzés láthatóvá teszi.
        }
    }
}

public sealed record ResolveCodeRequest(string? Code);
public sealed record OwnedShareDto(ShareDto Share, string Status);

public sealed record ApiError(string Message);

public sealed record ShareDto(
    Guid Id,
    string FileName,
    string? Title,
    string? Note,
    long SizeBytes,
    string Visibility,
    string? AccessCode,
    string? OwnerDisplayName,
    DateTime CreatedAtUtc,
    DateTime? ExpiresAtUtc,
    bool DeleteAfterFirstDownload,
    int DownloadCount,
    string DownloadUrl);
