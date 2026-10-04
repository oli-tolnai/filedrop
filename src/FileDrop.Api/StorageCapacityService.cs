using Microsoft.Extensions.Options;

public sealed class FileDropOptions
{
    public string StoragePath { get; init; } = "data/files";
    public string TemporaryPath { get; init; } = "data/incoming";
    public string DatabasePath { get; init; } = "data/filedrop.db";
    public long ReservedFreeSpaceBytes { get; init; } = 100L * 1024 * 1024 * 1024;
    public string SetupToken { get; init; } = "";
}

public sealed record StorageStatus(
    long FileDropUsedBytes,
    long DiskAvailableBytes,
    long UploadCapacityBytes,
    long ReservedFreeSpaceBytes);

public sealed class StorageCapacityService(
    IOptions<FileDropOptions> options,
    IWebHostEnvironment environment)
{
    private readonly FileDropOptions _options = options.Value;
    private readonly string _storagePath = Path.GetFullPath(
        options.Value.StoragePath,
        environment.ContentRootPath);

    public string StoragePath => _storagePath;

    public string TemporaryPath { get; } = Path.GetFullPath(
        options.Value.TemporaryPath,
        environment.ContentRootPath);

    public StorageStatus GetStatus()
    {
        var existingPath = FindNearestExistingPath(_storagePath);
        var drive = FindContainingDrive(existingPath);
        var availableBytes = drive.AvailableFreeSpace;
        var usedBytes = Directory.Exists(_storagePath) ? GetDirectorySize(_storagePath) : 0;
        var uploadCapacityBytes = Math.Max(0, availableBytes - _options.ReservedFreeSpaceBytes);

        return new StorageStatus(
            usedBytes,
            availableBytes,
            uploadCapacityBytes,
            _options.ReservedFreeSpaceBytes);
    }

    public long GetUploadCapacityBytes()
    {
        var existingPath = FindNearestExistingPath(_storagePath);
        var availableBytes = FindContainingDrive(existingPath).AvailableFreeSpace;
        return Math.Max(0, availableBytes - _options.ReservedFreeSpaceBytes);
    }

    private static string FindNearestExistingPath(string path)
    {
        var current = new DirectoryInfo(path);
        while (!current.Exists && current.Parent is not null)
        {
            current = current.Parent;
        }

        if (!current.Exists)
        {
            throw new DirectoryNotFoundException($"Nem található elérhető fájlrendszer ehhez az útvonalhoz: {path}");
        }

        return current.FullName;
    }

    private static DriveInfo FindContainingDrive(string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && path.StartsWith(drive.RootDirectory.FullName, comparison))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .FirstOrDefault()
            ?? throw new DriveNotFoundException($"Nem található a tárhelyhez tartozó fájlrendszer: {path}");
    }

    private static long GetDirectorySize(string path)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(file).Length);
        }

        return total;
    }
}
