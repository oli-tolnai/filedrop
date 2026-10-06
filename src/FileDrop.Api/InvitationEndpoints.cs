using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

public sealed class InvitationUploaderSessionService
{
    private const string CookiePrefix = "filedrop_invite_";

    public async Task<InvitationUploaderSession> GetOrCreateAsync(
        HttpContext context,
        FileDropDbContext db,
        UploadInvitation invitation,
        CancellationToken cancellationToken)
    {
        var cookieName = CookiePrefix + invitation.Code;
        if (context.Request.Cookies.TryGetValue(cookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            var tokenHash = HashToken(token);
            var existing = await db.InvitationUploaderSessions.FirstOrDefaultAsync(
                item => item.InvitationId == invitation.Id && item.TokenHash == tokenHash && item.ExpiresAtUtc > DateTime.UtcNow,
                cancellationToken);
            if (existing is not null)
            {
                existing.LastSeenAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return existing;
            }
        }

        var newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var session = new InvitationUploaderSession
        {
            Id = Guid.NewGuid(),
            InvitationId = invitation.Id,
            TokenHash = HashToken(newToken),
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = invitation.ExpiresAtUtc,
        };
        db.InvitationUploaderSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        var cookieExpiresAtUtc = DateTime.SpecifyKind(invitation.ExpiresAtUtc, DateTimeKind.Utc);
        context.Response.Cookies.Append(cookieName, newToken, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Expires = cookieExpiresAtUtc,
            Path = "/",
        });
        return session;
    }

    public async Task<InvitationUploaderSession?> GetCurrentAsync(
        HttpContext context,
        FileDropDbContext db,
        UploadInvitation invitation,
        CancellationToken cancellationToken)
    {
        var cookieName = CookiePrefix + invitation.Code;
        if (!context.Request.Cookies.TryGetValue(cookieName, out var token) || string.IsNullOrWhiteSpace(token)) return null;
        var tokenHash = HashToken(token);
        return await db.InvitationUploaderSessions.FirstOrDefaultAsync(
            item => item.InvitationId == invitation.Id && item.TokenHash == tokenHash && item.ExpiresAtUtc > DateTime.UtcNow,
            cancellationToken);
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class InvitationEndpoints
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly SemaphoreSlim CodeGate = new(1, 1);
    private static readonly SemaphoreSlim PublicUploadGate = new(1, 1);

    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/invitations", CreateAsync);
        endpoints.MapGet("/api/invitations", ListOwnedAsync);
        endpoints.MapDelete("/api/invitations/{id:guid}/uploads/{uploadId:guid}", DeleteAsOwnerAsync);
        endpoints.MapPost("/api/invitations/{id:guid}/close", CloseAsync);
        endpoints.MapDelete("/api/invitations/{id:guid}", RevokeAsync);

        endpoints.MapGet("/api/public/invitations/{code}", OpenPublicAsync).RequireRateLimiting("public-invite");
        endpoints.MapPost("/api/public/invitations/{code}/uploads", UploadPublicAsync).RequireRateLimiting("public-invite");
        endpoints.MapDelete("/api/public/invitations/{code}/uploads/{uploadId:guid}", DeletePublicAsync).RequireRateLimiting("public-invite");
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateInvitationRequest request,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService accounts,
        CancellationToken cancellationToken)
    {
        var owner = await accounts.GetCurrentAsync(context, db, cancellationToken);
        if (owner is null) return Results.Unauthorized();

        var visibility = request.Visibility?.Trim().ToLowerInvariant();
        if (visibility is not ("shared" or "code")) return Results.BadRequest(new ApiError("A láthatóság csak közös vagy kódos lehet."));
        if (!TryInviteExpiry(request.InviteExpiration, out var inviteExpiry)) return Results.BadRequest(new ApiError("Ismeretlen meghívó-lejárat."));
        if (!TryShareExpiry(request.ShareExpiration, DateTime.UtcNow, out _, out _)) return Results.BadRequest(new ApiError("Ismeretlen fájllejárat."));
        var maxTotalBytes = request.MaxTotalBytes ?? 1_073_741_824L;
        if (maxTotalBytes is < 104_857_600L or > 107_374_182_400L) return Results.BadRequest(new ApiError("A meghívó kerete 100 MB és 100 GB között lehet."));
        var title = CleanText(request.Title, 120);
        var note = CleanText(request.Note, 1000);
        if (!string.IsNullOrWhiteSpace(request.Title) && title is null) return Results.BadRequest(new ApiError("A cím legfeljebb 120 karakter lehet."));
        if (!string.IsNullOrWhiteSpace(request.Note) && note is null) return Results.BadRequest(new ApiError("A megjegyzés legfeljebb 1000 karakter lehet."));

        await CodeGate.WaitAsync(cancellationToken);
        try
        {
            var invitation = new UploadInvitation
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner.Id,
                Code = await GenerateCodeAsync(db, 8, invitation: true, cancellationToken),
                Visibility = visibility,
                ShareExpiration = request.ShareExpiration!.Trim().ToLowerInvariant(),
                Title = title,
                Note = note,
                MaxTotalBytes = maxTotalBytes,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = inviteExpiry,
            };
            db.UploadInvitations.Add(invitation);
            await db.SaveChangesAsync(cancellationToken);
            invitation.Owner = await db.Users.AsNoTracking().FirstAsync(item => item.Id == owner.Id, cancellationToken);
            return Results.Created($"/api/invitations/{invitation.Id}", ToOwnerDto(invitation));
        }
        finally { CodeGate.Release(); }
    }

    private static async Task<IResult> ListOwnedAsync(
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService accounts,
        CancellationToken cancellationToken)
    {
        var owner = await accounts.GetCurrentAsync(context, db, cancellationToken);
        if (owner is null) return Results.Unauthorized();
        var invitations = await db.UploadInvitations.AsNoTracking()
            .Include(item => item.Owner)
            .Include(item => item.Uploads)
            .ThenInclude(item => item.UploaderSession)
            .Where(item => item.OwnerUserId == owner.Id)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return Results.Ok(invitations.Select(ToOwnerDto));
    }

    private static async Task<IResult> OpenPublicAsync(
        string code,
        HttpContext context,
        FileDropDbContext db,
        InvitationUploaderSessionService uploaderSessions,
        CancellationToken cancellationToken)
    {
        var invitation = await FindOpenInvitationAsync(code, db, cancellationToken);
        if (invitation is null) return Results.NotFound(new ApiError("A feltöltési meghívó nem található, lezárták vagy lejárt."));
        var session = await uploaderSessions.GetOrCreateAsync(context, db, invitation, cancellationToken);
        var uploads = await db.InvitationUploads.AsNoTracking()
            .Where(item => item.InvitationId == invitation.Id && item.UploaderSessionId == session.Id)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => ToUploadDto(item, null))
            .ToListAsync(cancellationToken);
        return Results.Ok(ToPublicDto(invitation, uploads));
    }

    private static async Task<IResult> UploadPublicAsync(
        string code,
        HttpRequest request,
        HttpContext context,
        FileDropDbContext db,
        InvitationUploaderSessionService uploaderSessions,
        StorageCapacityService storage,
        UploadReservationService reservations,
        CancellationToken cancellationToken)
    {
        var invitation = await FindOpenInvitationAsync(code, db, cancellationToken);
        if (invitation is null) return Results.NotFound(new ApiError("A feltöltési meghívó nem található, lezárták vagy lejárt."));
        var session = await uploaderSessions.GetOrCreateAsync(context, db, invitation, cancellationToken);

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(mediaType.Boundary.Value))
            return Results.BadRequest(new ApiError("A feltöltéshez multipart/form-data kérés szükséges."));
        if (request.ContentLength is null) return Results.StatusCode(StatusCodes.Status411LengthRequired);
        if (request.ContentLength <= 0) return Results.BadRequest(new ApiError("Üres feltöltés nem küldhető."));
        await PublicUploadGate.WaitAsync(cancellationToken);
        try
        {
            var alreadyUploaded = await db.InvitationUploads.Where(item => item.InvitationId == invitation.Id).SumAsync(item => (long?)item.SizeBytes, cancellationToken) ?? 0;
            if (alreadyUploaded + request.ContentLength.Value > invitation.MaxTotalBytes)
                return Results.Json(new ApiError("Ez a feltöltés már túllépné a meghívó megadott méretkeretét."), statusCode: StatusCodes.Status413PayloadTooLarge);

            using var reservation = reservations.TryReserve(request.ContentLength.Value, storage.GetUploadCapacityBytes());
            if (reservation is null) return Results.Json(new ApiError("Nincs elegendő szabad tárhely."), statusCode: StatusCodes.Status507InsufficientStorage);

            Directory.CreateDirectory(storage.StoragePath);
            Directory.CreateDirectory(storage.TemporaryPath);
            var pending = new List<(InvitationUpload Entity, string Temporary, string Final)>();
            try
            {
            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!;
            var reader = new MultipartReader(boundary, request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(disposition.FileName.Value) && string.IsNullOrWhiteSpace(disposition.FileNameStar.Value))) continue;
                var originalName = CleanFileName(HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                    ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? "fajl");
                if (originalName is null)
                {
                    foreach (var earlier in pending) ShareEndpoints.SafeDelete(earlier.Temporary);
                    return Results.BadRequest(new ApiError("Az egyik fájlnév érvénytelen."));
                }

                var id = Guid.NewGuid();
                var storedName = $"{id:N}.bin";
                var temporary = Path.Combine(storage.TemporaryPath, $"{id:N}.uploading");
                var final = Path.Combine(storage.StoragePath, storedName);
                long size = 0;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        size += read;
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        reservation.ReportWritten(read);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (size <= 0)
                {
                    ShareEndpoints.SafeDelete(temporary);
                    foreach (var earlier in pending) ShareEndpoints.SafeDelete(earlier.Temporary);
                    return Results.BadRequest(new ApiError("Üres fájl nem tölthető fel."));
                }
                pending.Add((new InvitationUpload
                {
                    Id = id,
                    InvitationId = invitation.Id,
                    UploaderSessionId = session.Id,
                    OriginalFileName = originalName,
                    StoredFileName = storedName,
                    ContentType = CleanContentType(section.ContentType),
                    SizeBytes = size,
                    CreatedAtUtc = DateTime.UtcNow,
                }, temporary, final));
            }
            if (pending.Count == 0) return Results.BadRequest(new ApiError("Nem érkezett fájl."));
            foreach (var item in pending) File.Move(item.Temporary, item.Final);
            db.InvitationUploads.AddRange(pending.Select(item => item.Entity));
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(pending.Select(item => ToUploadDto(item.Entity, null)));
            }
            catch
            {
                foreach (var item in pending)
                {
                    ShareEndpoints.SafeDelete(item.Temporary);
                    ShareEndpoints.SafeDelete(item.Final);
                }
                throw;
            }
        }
        finally { PublicUploadGate.Release(); }
    }

    private static async Task<IResult> DeletePublicAsync(
        string code,
        Guid uploadId,
        HttpContext context,
        FileDropDbContext db,
        InvitationUploaderSessionService uploaderSessions,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        var invitation = await FindOpenInvitationAsync(code, db, cancellationToken);
        if (invitation is null) return Results.NotFound(new ApiError("A feltöltési meghívó nem használható."));
        var session = await uploaderSessions.GetCurrentAsync(context, db, invitation, cancellationToken);
        if (session is null) return Results.NotFound(new ApiError("A fájl nem található."));
        var upload = await db.InvitationUploads.FirstOrDefaultAsync(
            item => item.Id == uploadId && item.InvitationId == invitation.Id && item.UploaderSessionId == session.Id,
            cancellationToken);
        if (upload is null) return Results.NotFound(new ApiError("A fájl nem található."));
        ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, upload.StoredFileName));
        db.InvitationUploads.Remove(upload);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsOwnerAsync(
        Guid id,
        Guid uploadId,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService accounts,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        var owner = await accounts.GetCurrentAsync(context, db, cancellationToken);
        if (owner is null) return Results.Unauthorized();
        var upload = await db.InvitationUploads.Include(item => item.Invitation).FirstOrDefaultAsync(
            item => item.Id == uploadId && item.InvitationId == id && item.Invitation!.OwnerUserId == owner.Id,
            cancellationToken);
        if (upload is null) return Results.NotFound(new ApiError("A fájl nem található."));
        if (upload.Invitation!.ClosedAtUtc is not null || upload.Invitation.RevokedAtUtc is not null) return Results.Conflict(new ApiError("A meghívó már le van zárva."));
        ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, upload.StoredFileName));
        db.InvitationUploads.Remove(upload);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CloseAsync(
        Guid id,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService accounts,
        CancellationToken cancellationToken)
    {
        var owner = await accounts.GetCurrentAsync(context, db, cancellationToken);
        if (owner is null) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invitation = await db.UploadInvitations.Include(item => item.Uploads).FirstOrDefaultAsync(
            item => item.Id == id && item.OwnerUserId == owner.Id,
            cancellationToken);
        if (invitation is null) return Results.NotFound(new ApiError("A meghívó nem található."));
        if (invitation.ClosedAtUtc is not null || invitation.RevokedAtUtc is not null) return Results.Conflict(new ApiError("A meghívó már le van zárva."));
        if (invitation.Uploads.Count == 0) return Results.BadRequest(new ApiError("A meghívóhoz még nem érkezett fájl."));

        var now = DateTime.UtcNow;
        if (!TryShareExpiry(invitation.ShareExpiration, now, out var expiresAt, out var afterDownload)) return Results.BadRequest(new ApiError("A fájlok lejárata érvénytelen."));
        var share = new SharedFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = invitation.Uploads.Count == 1 ? invitation.Uploads[0].OriginalFileName : (invitation.Title ?? "Beérkezett fájlok"),
            Title = invitation.Title,
            Note = invitation.Note,
            StoredFileName = invitation.Uploads.Count == 1 ? invitation.Uploads[0].StoredFileName : "collection",
            ContentType = invitation.Uploads.Count == 1 ? invitation.Uploads[0].ContentType : "application/x-filedrop-collection",
            SizeBytes = invitation.Uploads.Sum(item => item.SizeBytes),
            Visibility = invitation.Visibility,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            DeleteAfterFirstDownload = afterDownload,
            OwnerUserId = owner.Id,
            IsCollection = invitation.Uploads.Count > 1,
        };
        if (share.IsCollection)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var upload in invitation.Uploads.OrderBy(item => item.CreatedAtUtc))
            {
                var uniqueName = MakeUniqueName(upload.OriginalFileName, usedNames);
                share.CollectionFiles.Add(new SharedFile
                {
                    Id = Guid.NewGuid(),
                    OriginalFileName = uniqueName,
                    StoredFileName = upload.StoredFileName,
                    ContentType = upload.ContentType,
                    SizeBytes = upload.SizeBytes,
                    Visibility = "internal",
                    CreatedAtUtc = now,
                    OwnerUserId = owner.Id,
                    ParentShareId = share.Id,
                    RelativePath = uniqueName,
                });
            }
        }

        await CodeGate.WaitAsync(cancellationToken);
        try
        {
            share.AccessCode = await GenerateCodeAsync(db, 6, invitation: false, cancellationToken);
            db.SharedFiles.Add(share);
            db.InvitationUploads.RemoveRange(invitation.Uploads);
            invitation.ClosedAtUtc = now;
            invitation.FinalShareId = share.Id;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { CodeGate.Release(); }

        share.Owner = await db.Users.AsNoTracking().FirstAsync(item => item.Id == owner.Id, cancellationToken);
        return Results.Ok(ShareEndpoints.ToDto(share));
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService accounts,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        var owner = await accounts.GetCurrentAsync(context, db, cancellationToken);
        if (owner is null) return Results.Unauthorized();
        var invitation = await db.UploadInvitations.Include(item => item.Uploads).FirstOrDefaultAsync(
            item => item.Id == id && item.OwnerUserId == owner.Id,
            cancellationToken);
        if (invitation is null) return Results.NotFound(new ApiError("A meghívó nem található."));
        if (invitation.ClosedAtUtc is not null) return Results.Conflict(new ApiError("A lezárt meghívóból már normál megosztás készült."));
        foreach (var upload in invitation.Uploads) ShareEndpoints.SafeDelete(Path.Combine(storage.StoragePath, upload.StoredFileName));
        db.InvitationUploads.RemoveRange(invitation.Uploads);
        invitation.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<UploadInvitation?> FindOpenInvitationAsync(string code, FileDropDbContext db, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null) return null;
        var now = DateTime.UtcNow;
        return await db.UploadInvitations.AsNoTracking().Include(item => item.Owner).FirstOrDefaultAsync(
            item => item.Code == normalized && item.ExpiresAtUtc > now && item.ClosedAtUtc == null && item.RevokedAtUtc == null,
            cancellationToken);
    }

    private static OwnerInvitationDto ToOwnerDto(UploadInvitation invitation)
    {
        var sessionOrdinals = invitation.Uploads.Select(item => item.UploaderSessionId).Distinct().Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index + 1);
        var state = invitation.RevokedAtUtc is not null ? "revoked" : invitation.ClosedAtUtc is not null ? "closed" : invitation.ExpiresAtUtc <= DateTime.UtcNow ? "expired" : "open";
        return new OwnerInvitationDto(invitation.Id, FormatCode(invitation.Code), invitation.Visibility, invitation.ShareExpiration, invitation.Title, invitation.Note, invitation.MaxTotalBytes,
            invitation.Owner?.DisplayName, invitation.CreatedAtUtc, invitation.ExpiresAtUtc, state, invitation.FinalShareId,
            invitation.Uploads.OrderBy(item => item.CreatedAtUtc).Select(item => ToUploadDto(item, sessionOrdinals[item.UploaderSessionId])).ToList());
    }

    private static PublicInvitationDto ToPublicDto(UploadInvitation invitation, IReadOnlyList<InvitationUploadDto> uploads) =>
        new(FormatCode(invitation.Code), invitation.Title, invitation.Note, invitation.Owner?.DisplayName, invitation.ExpiresAtUtc, invitation.MaxTotalBytes, uploads);

    private static InvitationUploadDto ToUploadDto(InvitationUpload upload, int? uploaderOrdinal) =>
        new(upload.Id, upload.OriginalFileName, upload.SizeBytes, upload.CreatedAtUtc, uploaderOrdinal);

    private static async Task<string> GenerateCodeAsync(FileDropDbContext db, int length, bool invitation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var bytes = RandomNumberGenerator.GetBytes(length);
            var code = new string(bytes.Select(value => CodeAlphabet[value % CodeAlphabet.Length]).ToArray());
            var exists = invitation
                ? await db.UploadInvitations.AnyAsync(item => item.Code == code, cancellationToken)
                : await db.SharedFiles.AnyAsync(item => item.AccessCode == code, cancellationToken);
            if (!exists) return code;
        }
        throw new InvalidOperationException("Nem sikerült egyedi kódot létrehozni.");
    }

    private static string? NormalizeCode(string code)
    {
        var normalized = new string(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 8 && normalized.All(CodeAlphabet.Contains) ? normalized : null;
    }

    private static string FormatCode(string code) => code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;

    private static bool TryInviteExpiry(string? value, out DateTime expiry)
    {
        var now = DateTime.UtcNow;
        expiry = value?.Trim().ToLowerInvariant() switch
        {
            "15m" => now.AddMinutes(15), "1h" => now.AddHours(1), "24h" => now.AddDays(1), "7d" => now.AddDays(7), _ => default,
        };
        return expiry != default;
    }

    private static bool TryShareExpiry(string? value, DateTime now, out DateTime? expiry, out bool afterDownload)
    {
        afterDownload = false;
        expiry = value?.Trim().ToLowerInvariant() switch
        {
            "after-download" => null, "15m" => now.AddMinutes(15), "1h" => now.AddHours(1), "24h" => now.AddDays(1), "7d" => now.AddDays(7), "manual" => null, _ => DateTime.MinValue,
        };
        afterDownload = value?.Trim().Equals("after-download", StringComparison.OrdinalIgnoreCase) == true;
        return expiry != DateTime.MinValue;
    }

    private static string? CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim();
        return clean.Length <= maxLength ? clean : null;
    }

    private static string? CleanFileName(string value)
    {
        var name = Path.GetFileName(value.Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name;
    }

    private static string CleanContentType(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 200 ? "application/octet-stream" : value;

    private static string MakeUniqueName(string fileName, HashSet<string> usedNames)
    {
        if (usedNames.Add(fileName)) return fileName;
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 2; ; index++)
        {
            var suffix = $" ({index})";
            var maxStemLength = Math.Max(1, 255 - extension.Length - suffix.Length);
            var candidate = $"{stem[..Math.Min(stem.Length, maxStemLength)]}{suffix}{extension}";
            if (usedNames.Add(candidate)) return candidate;
        }
    }
}

public sealed record CreateInvitationRequest(string? Visibility, string? InviteExpiration, string? ShareExpiration, string? Title, string? Note, long? MaxTotalBytes);
public sealed record InvitationUploadDto(Guid Id, string FileName, long SizeBytes, DateTime CreatedAtUtc, int? UploaderOrdinal);
public sealed record PublicInvitationDto(string Code, string? Title, string? Note, string? OwnerDisplayName, DateTime ExpiresAtUtc, long MaxTotalBytes, IReadOnlyList<InvitationUploadDto> Uploads);
public sealed record OwnerInvitationDto(Guid Id, string Code, string Visibility, string ShareExpiration, string? Title, string? Note, long MaxTotalBytes, string? OwnerDisplayName, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, string State, Guid? FinalShareId, IReadOnlyList<InvitationUploadDto> Uploads);
