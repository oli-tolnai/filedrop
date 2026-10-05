using System.Security.Cryptography;
using System.IO.Compression;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

public static class ShareEndpoints
{
    private static readonly SemaphoreSlim AccessCodeGate = new(1, 1);
    private const string ShareCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/shares", ListSharedFilesAsync);
        endpoints.MapPost("/api/shares", CreateShareAsync);
        endpoints.MapPost("/api/collections", CreateCollectionAsync);
        // Régi kliensverziók ezt a címet használják több fájlhoz. Az adatokat
        // már ezeknél sem ZIP-be, hanem külön fájlként tároljuk.
        endpoints.MapPost("/api/shares/bundle", CreateCollectionAsync);
        endpoints.MapGet("/api/collections/{id:guid}", OpenCollectionAsync);
        endpoints.MapGet("/api/collections/code/{code}", OpenCollectionByCodeAsync)
            .RequireRateLimiting("share-code");
        endpoints.MapPost("/api/collections/{id:guid}/begin-download", BeginCollectionDownloadAsync);
        endpoints.MapPost("/api/collections/code/{code}/begin-download", BeginCollectionDownloadByCodeAsync)
            .RequireRateLimiting("share-code");
        endpoints.MapGet("/api/collections/{id:guid}/zip", DownloadCollectionZipAsync);
        endpoints.MapGet("/api/collections/code/{code}/zip", DownloadCollectionZipByCodeAsync)
            .RequireRateLimiting("share-code");
        endpoints.MapGet("/api/collection-downloads/{id:guid}/files/{fileId:guid}", DownloadCollectionFileByTicketAsync);
        endpoints.MapGet("/api/collections/{id:guid}/files/{fileId:guid}/download", DownloadCollectionFileAsync);
        // Egy érvényes kódhoz akár sok fénykép is tartozhat; a kód feloldását és
        // a letöltési csomag indítását már sebességkorlátozás védi.
        endpoints.MapGet("/api/collections/code/{code}/files/{fileId:guid}/download", DownloadCollectionFileByCodeAsync);
        endpoints.MapPost("/api/shares/resolve-code", ResolveCodeAsync)
            .RequireRateLimiting("share-code");
        endpoints.MapGet("/api/shares/code/{code}/download", DownloadByCodeAsync)
            .RequireRateLimiting("share-code");
        endpoints.MapGet("/api/shares/{id:guid}/download", DownloadAsync);
        endpoints.MapGet("/api/me/shares", ListMySharesAsync);
        endpoints.MapDelete("/api/me/shares/{id:guid}", DeleteMyShareAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateCollectionAsync(
        HttpRequest request,
        string visibility,
        string expiration,
        string? title,
        string? note,
        string? collectionName,
        string? bundleName,
        FileDropDbContext db,
        StorageCapacityService storage,
        UploadReservationService reservations,
        AccountSessionService sessions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var owner = await sessions.GetCurrentAsync(context, db, cancellationToken);
        if (owner is null) return Results.Unauthorized();

        visibility = visibility.Trim().ToLowerInvariant();
        if (visibility is not ("shared" or "code"))
            return Results.BadRequest(new ApiError("A láthatóság csak 'shared' vagy 'code' lehet."));
        if (!TryGetExpiration(expiration, out var expiresAtUtc, out var deleteAfterFirstDownload))
            return Results.BadRequest(new ApiError("Ismeretlen lejárati beállítás."));

        var safeTitle = SanitizeMetadata(title, 120);
        if (!string.IsNullOrWhiteSpace(title) && safeTitle is null)
            return Results.BadRequest(new ApiError("A cím legfeljebb 120 karakter lehet."));
        var safeNote = SanitizeMetadata(note, 1000);
        if (!string.IsNullOrWhiteSpace(note) && safeNote is null)
            return Results.BadRequest(new ApiError("A megjegyzés legfeljebb 1000 karakter lehet."));

        var requestedCollectionName = collectionName ?? bundleName ?? "filedrop-gyujtemeny";
        var safeCollectionName = SanitizeFileName(requestedCollectionName);
        if (safeCollectionName is null)
            return Results.BadRequest(new ApiError("Érvénytelen gyűjteménynév."));
        if (safeCollectionName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            safeCollectionName = Path.GetFileNameWithoutExtension(safeCollectionName);
            if (string.IsNullOrWhiteSpace(safeCollectionName)) safeCollectionName = "filedrop-gyujtemeny";
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(mediaType.Boundary.Value))
            return Results.BadRequest(new ApiError("A gyűjtemény feltöltéséhez multipart/form-data kérés szükséges."));

        var contentLength = request.ContentLength;
        if (contentLength is null) return Results.StatusCode(StatusCodes.Status411LengthRequired);
        if (contentLength <= 0) return Results.BadRequest(new ApiError("Üres gyűjtemény nem tölthető fel."));

        using var reservation = reservations.TryReserve(contentLength.Value, storage.GetUploadCapacityBytes());
        if (reservation is null)
            return Results.Json(new ApiError("Nincs elegendő hely a 100 GB-os biztonsági tartalék megtartásával."), statusCode: StatusCodes.Status507InsufficientStorage);

        Directory.CreateDirectory(storage.StoragePath);
        Directory.CreateDirectory(storage.TemporaryPath);
        var collectionId = Guid.NewGuid();
        var uploadedFiles = new List<CollectionUpload>();

        try
        {
            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!;
            var reader = new MultipartReader(boundary, request.Body);
            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(disposition.FileName.Value) && string.IsNullOrWhiteSpace(disposition.FileNameStar.Value)))
                    continue;

                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                var suppliedPath = fieldName.StartsWith("file:", StringComparison.Ordinal)
                    ? Uri.UnescapeDataString(fieldName[5..])
                    : HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                        ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value
                        ?? "fajl";
                var safeRelativePath = MakeSafeZipEntryName(suppliedPath, relativePaths);
                if (safeRelativePath is null)
                    return Results.BadRequest(new ApiError("A gyűjtemény egyik fájlneve vagy mappaútvonala érvénytelen."));

                var id = Guid.NewGuid();
                var storedFileName = $"{id:N}.bin";
                var temporaryFilePath = Path.Combine(storage.TemporaryPath, $"{id:N}.uploading");
                var finalFilePath = Path.Combine(storage.StoragePath, storedFileName);
                long sizeBytes = 0;

                await using (var output = new FileStream(temporaryFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        sizeBytes += read;
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        reservation.ReportWritten(read);
                    }
                    await output.FlushAsync(cancellationToken);
                }

                if (sizeBytes <= 0)
                {
                    SafeDelete(temporaryFilePath);
                    return Results.BadRequest(new ApiError("Üres fájl nem lehet a gyűjteményben."));
                }

                var safeFileName = SanitizeFileName(Path.GetFileName(safeRelativePath));
                if (safeFileName is null)
                {
                    SafeDelete(temporaryFilePath);
                    return Results.BadRequest(new ApiError("A gyűjtemény egyik fájlneve érvénytelen."));
                }

                uploadedFiles.Add(new CollectionUpload(
                    id,
                    safeFileName,
                    safeRelativePath,
                    storedFileName,
                    temporaryFilePath,
                    finalFilePath,
                    SanitizeContentType(section.ContentType),
                    sizeBytes));
            }

            if (uploadedFiles.Count < 2)
                return Results.BadRequest(new ApiError("A gyűjteményhez legalább két fájl szükséges."));

            foreach (var uploadedFile in uploadedFiles)
            {
                File.Move(uploadedFile.TemporaryFilePath, uploadedFile.FinalFilePath);
            }

            var collection = new SharedFile
            {
                Id = collectionId,
                OriginalFileName = safeCollectionName,
                Title = safeTitle,
                Note = safeNote,
                StoredFileName = "collection",
                ContentType = "application/x-filedrop-collection",
                SizeBytes = uploadedFiles.Sum(file => file.SizeBytes),
                Visibility = visibility,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = expiresAtUtc,
                DeleteAfterFirstDownload = deleteAfterFirstDownload,
                OwnerUserId = owner.Id,
                IsCollection = true,
            };
            var children = uploadedFiles.Select(file => new SharedFile
            {
                Id = file.Id,
                OriginalFileName = file.FileName,
                StoredFileName = file.StoredFileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
                Visibility = "internal",
                CreatedAtUtc = collection.CreatedAtUtc,
                OwnerUserId = owner.Id,
                ParentShareId = collection.Id,
                RelativePath = file.RelativePath,
            }).ToList();
            collection.CollectionFiles.AddRange(children);

            await AccessCodeGate.WaitAsync(cancellationToken);
            try
            {
                collection.AccessCode = await GenerateUniqueCodeAsync(db, cancellationToken);
                db.SharedFiles.Add(collection);
                await db.SaveChangesAsync(cancellationToken);
            }
            finally { AccessCodeGate.Release(); }

            return Results.Created($"/api/collections/{collection.Id}", ToDto(collection));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch
        {
            foreach (var uploadedFile in uploadedFiles)
            {
                SafeDelete(uploadedFile.TemporaryFilePath);
                SafeDelete(uploadedFile.FinalFilePath);
            }
            throw;
        }
        finally
        {
            foreach (var uploadedFile in uploadedFiles)
            {
                SafeDelete(uploadedFile.TemporaryFilePath);
            }
        }
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
            .Include(file => file.CollectionFiles)
            .Where(file => file.ParentShareId == null
                && file.Visibility == "shared"
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
            .Include(file => file.CollectionFiles)
            .AsNoTracking()
            .FirstOrDefaultAsync(file => file.ParentShareId == null
                && file.AccessCode == code
                && file.FileDeletedAtUtc == null
                && file.ConsumedAtUtc == null
                && (file.ExpiresAtUtc == null || file.ExpiresAtUtc > now), cancellationToken);

        if (share is null) return Results.NotFound(new ApiError("Nincs aktív megosztás ezzel a kóddal."));
        return Results.Ok(ToDto(share, codeDownload: true));
    }

    private static async Task<IResult> OpenCollectionAsync(
        Guid id,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionAsync(id, db, cancellationToken);
        if (collection is null) return Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."));
        if (await sessions.GetCurrentAsync(context, db, cancellationToken) is null) return Results.Unauthorized();
        return Results.Ok(ToCollectionDetailsDto(collection));
    }

    private static async Task<IResult> OpenCollectionByCodeAsync(
        string code,
        FileDropDbContext db,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionByCodeAsync(code, db, cancellationToken);
        return collection is null
            ? Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."))
            : Results.Ok(ToCollectionDetailsDto(collection, codeAccess: true));
    }

    private static async Task<IResult> BeginCollectionDownloadAsync(
        Guid id,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionAsync(id, db, cancellationToken);
        if (collection is null) return Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."));
        if (await sessions.GetCurrentAsync(context, db, cancellationToken) is null) return Results.Unauthorized();
        return await BeginCollectionDownloadCoreAsync(collection, db, cancellationToken);
    }

    private static async Task<IResult> BeginCollectionDownloadByCodeAsync(
        string code,
        FileDropDbContext db,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionByCodeAsync(code, db, cancellationToken);
        return collection is null
            ? Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."))
            : await BeginCollectionDownloadCoreAsync(collection, db, cancellationToken, codeAccess: true);
    }

    private static async Task<IResult> BeginCollectionDownloadCoreAsync(
        SharedFile collection,
        FileDropDbContext db,
        CancellationToken cancellationToken,
        bool codeAccess = false)
    {
        var files = GetActiveCollectionFiles(collection);
        if (files.Count == 0) return Results.NotFound(new ApiError("A fájlcsoportban már nincs letölthető fájl."));

        collection.DownloadCount++;
        string? ticket = null;
        if (collection.DeleteAfterFirstDownload)
        {
            ticket = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            collection.ConsumedAtUtc = DateTime.UtcNow;
            collection.AccessCode = null;
            collection.BatchAccessTokenHash = HashTicket(ticket);
            collection.BatchAccessExpiresAtUtc = DateTime.UtcNow.AddMinutes(30);
        }
        await db.SaveChangesAsync(cancellationToken);

        var fileDtos = files.Select(file => ToCollectionFileDto(file, collection, codeAccess, ticket)).ToList();
        return Results.Ok(new CollectionDownloadDto(fileDtos));
    }

    private static async Task<IResult> DownloadCollectionZipAsync(
        Guid id,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        StorageCapacityService storage,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionAsync(id, db, cancellationToken);
        if (collection is null) return Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."));
        if (await sessions.GetCurrentAsync(context, db, cancellationToken) is null) return Results.Unauthorized();
        return await CreateCollectionZipResultAsync(collection, db, storage, scopeFactory, cancellationToken);
    }

    private static async Task<IResult> DownloadCollectionZipByCodeAsync(
        string code,
        FileDropDbContext db,
        StorageCapacityService storage,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionByCodeAsync(code, db, cancellationToken);
        return collection is null
            ? Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."))
            : await CreateCollectionZipResultAsync(collection, db, storage, scopeFactory, cancellationToken);
    }

    private static async Task<IResult> CreateCollectionZipResultAsync(
        SharedFile collection,
        FileDropDbContext db,
        StorageCapacityService storage,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        var files = GetActiveCollectionFiles(collection);
        if (files.Count == 0) return Results.NotFound(new ApiError("A fájlcsoportban már nincs letölthető fájl."));

        var archiveItems = new List<CollectionArchiveItem>(files.Count);
        foreach (var file in files)
        {
            var path = Path.Combine(storage.StoragePath, file.StoredFileName);
            if (!File.Exists(path)) return Results.NotFound(new ApiError("A fájlcsoport egyik fájlja már nem található a tárhelyen."));
            archiveItems.Add(new CollectionArchiveItem(path, file.RelativePath ?? file.OriginalFileName));
        }

        collection.DownloadCount++;
        if (collection.DeleteAfterFirstDownload)
        {
            collection.ConsumedAtUtc = DateTime.UtcNow;
            collection.AccessCode = null;
            // Az időablak megakadályozza, hogy az időzített takarítás a ZIP írása közben törölje a fájlokat.
            collection.BatchAccessExpiresAtUtc = DateTime.UtcNow.AddMinutes(30);
        }
        await db.SaveChangesAsync(cancellationToken);

        var archiveName = collection.OriginalFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? collection.OriginalFileName
            : $"{collection.OriginalFileName}.zip";
        return new CollectionZipResult(
            collection.Id,
            archiveName,
            archiveItems,
            collection.DeleteAfterFirstDownload,
            scopeFactory,
            storage);
    }

    private static async Task<IResult> DownloadCollectionFileAsync(
        Guid id,
        Guid fileId,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionAsync(id, db, cancellationToken);
        if (collection is null) return Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."));
        if (await sessions.GetCurrentAsync(context, db, cancellationToken) is null) return Results.Unauthorized();
        if (collection.DeleteAfterFirstDownload)
            return Results.BadRequest(new ApiError("Az egyszeri letöltéshez az „Összes fájl” gombot használd."));
        return DownloadCollectionChild(collection, fileId, storage);
    }

    private static async Task<IResult> DownloadCollectionFileByCodeAsync(
        string code,
        Guid fileId,
        FileDropDbContext db,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        var collection = await LoadActiveCollectionByCodeAsync(code, db, cancellationToken);
        if (collection is null) return Results.NotFound(new ApiError("A fájlcsoport nem található vagy már lejárt."));
        if (collection.DeleteAfterFirstDownload)
            return Results.BadRequest(new ApiError("Az egyszeri letöltéshez az „Összes fájl” gombot használd."));
        return DownloadCollectionChild(collection, fileId, storage);
    }

    private static async Task<IResult> DownloadCollectionFileByTicketAsync(
        Guid id,
        Guid fileId,
        string? ticket,
        FileDropDbContext db,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return Results.NotFound(new ApiError("A letöltési munkamenet nem található."));
        var collection = await db.SharedFiles
            .Include(file => file.CollectionFiles)
            .FirstOrDefaultAsync(file => file.Id == id && file.IsCollection && file.ParentShareId == null, cancellationToken);
        if (collection is null
            || collection.BatchAccessExpiresAtUtc is null
            || collection.BatchAccessExpiresAtUtc <= DateTime.UtcNow
            || !HasValidTicket(collection, ticket))
        {
            return Results.NotFound(new ApiError("A letöltési munkamenet lejárt vagy nem található."));
        }

        return DownloadCollectionChild(collection, fileId, storage);
    }

    private static IResult DownloadCollectionChild(SharedFile collection, Guid fileId, StorageCapacityService storage)
    {
        var file = GetActiveCollectionFiles(collection).FirstOrDefault(item => item.Id == fileId);
        if (file is null) return Results.NotFound(new ApiError("A fájl nem található."));
        var filePath = Path.Combine(storage.StoragePath, file.StoredFileName);
        if (!File.Exists(filePath)) return Results.NotFound(new ApiError("A fájl már nincs a tárhelyen."));
        return Results.File(filePath, file.ContentType, file.OriginalFileName, enableRangeProcessing: true);
    }

    private static async Task<SharedFile?> LoadActiveCollectionAsync(Guid id, FileDropDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return await db.SharedFiles
            .Include(file => file.Owner)
            .Include(file => file.CollectionFiles)
            .FirstOrDefaultAsync(file => file.Id == id
                && file.IsCollection
                && file.ParentShareId == null
                && file.FileDeletedAtUtc == null
                && file.ConsumedAtUtc == null
                && (file.ExpiresAtUtc == null || file.ExpiresAtUtc > now), cancellationToken);
    }

    private static async Task<SharedFile?> LoadActiveCollectionByCodeAsync(string code, FileDropDbContext db, CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if ((normalized.Length != 4 && normalized.Length != 6) || normalized.Any(character => !char.IsAsciiLetterOrDigit(character))) return null;
        var now = DateTime.UtcNow;
        return await db.SharedFiles
            .Include(file => file.Owner)
            .Include(file => file.CollectionFiles)
            .FirstOrDefaultAsync(file => file.IsCollection
                && file.ParentShareId == null
                && file.AccessCode == normalized
                && file.FileDeletedAtUtc == null
                && file.ConsumedAtUtc == null
                && (file.ExpiresAtUtc == null || file.ExpiresAtUtc > now), cancellationToken);
    }

    private static IReadOnlyList<SharedFile> GetActiveCollectionFiles(SharedFile collection) => collection.CollectionFiles
        .Where(file => file.FileDeletedAtUtc == null)
        .OrderBy(file => file.RelativePath ?? file.OriginalFileName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static CollectionDetailsDto ToCollectionDetailsDto(SharedFile collection, bool codeAccess = false)
    {
        var files = GetActiveCollectionFiles(collection)
            .Select(file => ToCollectionFileDto(file, collection, codeAccess))
            .ToList();
        var prefix = codeAccess && collection.AccessCode is not null
            ? $"/api/collections/code/{collection.AccessCode}"
            : $"/api/collections/{collection.Id}";
        return new CollectionDetailsDto(
            ToDto(collection, codeDownload: codeAccess),
            files,
            $"{prefix}/zip",
            $"{prefix}/begin-download");
    }

    private static CollectionFileDto ToCollectionFileDto(SharedFile file, SharedFile collection, bool codeAccess, string? ticket = null)
    {
        var downloadUrl = ticket is not null
            ? $"/api/collection-downloads/{collection.Id}/files/{file.Id}?ticket={Uri.EscapeDataString(ticket)}"
            : codeAccess && collection.AccessCode is not null
                ? $"/api/collections/code/{collection.AccessCode}/files/{file.Id}/download"
                : $"/api/collections/{collection.Id}/files/{file.Id}/download";
        return new CollectionFileDto(file.Id, file.OriginalFileName, file.RelativePath, file.SizeBytes, downloadUrl);
    }

    private static string HashTicket(string ticket) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ticket)));

    private static bool HasValidTicket(SharedFile collection, string ticket)
    {
        if (collection.BatchAccessTokenHash is not { Length: 64 } expected) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(HashTicket(ticket)));
        }
        catch (FormatException)
        {
            return false;
        }
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

        if (share.IsCollection || share.ParentShareId is not null)
        {
            return Results.BadRequest(new ApiError("Ezt a fájlcsoportot előbb meg kell nyitni."));
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

        var shareExists = await db.SharedFiles.AnyAsync(file => file.ParentShareId == null
            && file.AccessCode == normalized
            && file.FileDeletedAtUtc == null
            && file.ConsumedAtUtc == null
            && (file.ExpiresAtUtc == null || file.ExpiresAtUtc > DateTime.UtcNow), cancellationToken);
        if (!shareExists) return Results.NotFound(new ApiError("A megosztás nem található vagy már lejárt."));

        var share = await db.SharedFiles.AsNoTracking()
            .FirstAsync(file => file.ParentShareId == null && file.AccessCode == normalized, cancellationToken);
        if (share.IsCollection)
        {
            return Results.Redirect($"/api/collections/code/{normalized}");
        }
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
            ? file.IsCollection
                ? $"/api/collections/code/{file.AccessCode}"
                : $"/api/shares/code/{file.AccessCode}/download"
            : file.IsCollection
                ? $"/api/collections/{file.Id}"
                : $"/api/shares/{file.Id}/download",
        file.IsCollection,
        file.IsCollection ? file.CollectionFiles.Count : 1);

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
            .Include(file => file.CollectionFiles)
            .Where(file => file.OwnerUserId == account.Id && file.ParentShareId == null)
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
        if (share.ParentShareId is not null) return Results.NotFound();
        if (share.OwnerUserId != account.Id && !account.IsAdmin) return Results.Forbid();

        var deletedAtUtc = DateTime.UtcNow;
        if (share.IsCollection)
        {
            var children = await db.SharedFiles
                .Where(file => file.ParentShareId == share.Id && file.FileDeletedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var child in children)
            {
                SafeDelete(Path.Combine(storage.StoragePath, child.StoredFileName));
                child.FileDeletedAtUtc = deletedAtUtc;
            }
            share.BatchAccessTokenHash = null;
            share.BatchAccessExpiresAtUtc = null;
        }
        else
        {
            SafeDelete(Path.Combine(storage.StoragePath, share.StoredFileName));
        }
        share.FileDeletedAtUtc = deletedAtUtc;
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

    private static string? MakeSafeZipEntryName(string suppliedPath, HashSet<string> existingNames)
    {
        var parts = suppliedPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or "..")) return null;

        var safeParts = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 255 || trimmed.Any(char.IsControl)) return null;
            foreach (var invalid in Path.GetInvalidFileNameChars()) trimmed = trimmed.Replace(invalid, '_');
            if (trimmed is "." or ".." || string.IsNullOrWhiteSpace(trimmed)) return null;
            safeParts.Add(trimmed);
        }

        var candidate = string.Join('/', safeParts);
        if (candidate.Length > 1000) return null;
        if (existingNames.Add(candidate)) return candidate;

        var directory = string.Join('/', safeParts.Take(safeParts.Count - 1));
        var fileName = safeParts[^1];
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 2; index <= 9999; index++)
        {
            var renamed = $"{baseName} ({index}){extension}";
            var path = string.IsNullOrEmpty(directory) ? renamed : $"{directory}/{renamed}";
            if (existingNames.Add(path)) return path;
        }

        return null;
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
public sealed record CollectionFileDto(Guid Id, string FileName, string? RelativePath, long SizeBytes, string DownloadUrl);
public sealed record CollectionDetailsDto(ShareDto Collection, IReadOnlyList<CollectionFileDto> Files, string ZipDownloadUrl, string BeginDownloadUrl);
public sealed record CollectionDownloadDto(IReadOnlyList<CollectionFileDto> Files);

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
    string DownloadUrl,
    bool IsCollection,
    int FileCount);

file sealed record CollectionUpload(
    Guid Id,
    string FileName,
    string RelativePath,
    string StoredFileName,
    string TemporaryFilePath,
    string FinalFilePath,
    string ContentType,
    long SizeBytes);
