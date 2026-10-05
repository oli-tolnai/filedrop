using System.Text.Json;

public static class AppReleaseEndpoints
{
    private const string ManifestFileName = "latest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IEndpointRouteBuilder MapAppReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/app-release", GetLatestAsync);
        endpoints.MapGet("/downloads/filedrop.apk", DownloadLatestAsync);
        return endpoints;
    }

    private static async Task<IResult> GetLatestAsync(
        HttpResponse response,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var release = await ReadLatestAsync(storage, cancellationToken);
        return release is null
            ? Results.NoContent()
            : Results.Ok(ToDto(release));
    }

    private static async Task<IResult> DownloadLatestAsync(
        HttpResponse response,
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var release = await ReadLatestAsync(storage, cancellationToken);
        if (release is null) return Results.NotFound();

        var fileName = $"FileDrop-{release.Manifest.VersionName}.apk";
        return Results.File(
            release.ApkPath,
            contentType: "application/vnd.android.package-archive",
            fileDownloadName: fileName,
            enableRangeProcessing: true);
    }

    private static async Task<ResolvedRelease?> ReadLatestAsync(
        StorageCapacityService storage,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(storage.ReleaseDirectoryPath, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;

        AndroidReleaseManifest? manifest;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<AndroidReleaseManifest>(manifestStream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null || !IsValid(manifest)) return null;

        var apkPath = Path.Combine(storage.ReleaseDirectoryPath, manifest.FileName);
        if (!File.Exists(apkPath)) return null;

        return new ResolvedRelease(manifest, apkPath, new FileInfo(apkPath).Length);
    }

    private static bool IsValid(AndroidReleaseManifest? manifest)
    {
        if (manifest is null
            || manifest.VersionCode < 1
            || string.IsNullOrWhiteSpace(manifest.VersionName)
            || manifest.VersionName.Length > 40
            || string.IsNullOrWhiteSpace(manifest.FileName)
            || !manifest.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.FileName, Path.GetFileName(manifest.FileName), StringComparison.Ordinal)
            || manifest.FileName.Length > 120)
        {
            return false;
        }

        return manifest.VersionName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    private static AppReleaseDto ToDto(ResolvedRelease release) => new(
        release.Manifest.VersionCode,
        release.Manifest.VersionName,
        release.SizeBytes,
        "/downloads/filedrop.apk");

    private sealed record AndroidReleaseManifest(int VersionCode, string VersionName, string FileName);
    private sealed record ResolvedRelease(AndroidReleaseManifest Manifest, string ApkPath, long SizeBytes);
}

public sealed record AppReleaseDto(int VersionCode, string VersionName, long SizeBytes, string DownloadUrl);
