using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Memo.Services;

public sealed class MarkdownImageStore {
    public const long MaximumImageBytes = 20L * 1024 * 1024;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg",
    };

    public MarkdownImageStore(string? rootDirectory = null) {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Memo");
        AssetsDirectory = Path.Combine(RootDirectory, "assets");
        Directory.CreateDirectory(AssetsDirectory);
    }

    public string RootDirectory { get; }
    public string AssetsDirectory { get; }

    public static bool IsSupportedFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static bool IsSafeRemoteImageUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public async Task<string> StoreFileAsync(string sourcePath, CancellationToken cancellationToken = default) {
        if (!IsSupportedFile(sourcePath))
            throw new NotSupportedException("不支持该图片格式。");

        await using var stream = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return await StoreAsync(stream, Path.GetExtension(sourcePath), cancellationToken);
    }

    public async Task<string> StoreAsync(
        Stream source,
        string extension,
        CancellationToken cancellationToken = default) {
        extension = NormalizeExtension(extension);
        if (!SupportedExtensions.Contains(extension))
            throw new NotSupportedException("不支持该图片格式。");

        var temporaryPath = Path.Combine(AssetsDirectory, $".{Guid.NewGuid():N}.tmp");
        try {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true)) {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) {
                    total += read;
                    if (total > MaximumImageBytes)
                        throw new InvalidDataException("图片不能超过 20 MB。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var fileName = digest + extension;
            var destination = Path.Combine(AssetsDirectory, fileName);
            if (File.Exists(destination)) File.Delete(temporaryPath);
            else File.Move(temporaryPath, destination);
            return $"assets/{fileName}";
        }
        catch {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static string NormalizeExtension(string extension) {
        var normalized = extension.Trim().ToLowerInvariant();
        if (!normalized.StartsWith('.')) normalized = "." + normalized;
        return normalized == ".jpeg" ? ".jpg" : normalized;
    }
}
