using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rhodes.Core.Chat;

public sealed record UploadRecord(string Id, string FileName, int Characters, int EstimatedTokens, DateTimeOffset UploadedAt);

/// <summary>
/// Thrown when an upload can't be used; the message is shown to Shapto as is.
/// </summary>
public sealed class UploadRejectedException(string message) : Exception(message);

/// <summary>
/// Keeps files Shapto attaches in Data/uploads/{upload id}/: the original file plus a small upload.json.
/// Only text files for now; Closure has no vision, so images are refused.
/// </summary>
public sealed partial class UploadStore
{
    public const double TokensPerCharacter = 0.3;
    private const int MaximumBytes = 5 * 1024 * 1024;
    private const string MetadataFileName = "upload.json";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic" };
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string uploadsFolder;

    public UploadStore(string uploadsFolder)
    {
        this.uploadsFolder = uploadsFolder;
        Directory.CreateDirectory(uploadsFolder);
    }

    public async Task<UploadRecord> SaveAsync(string originalFileName, Stream content, CancellationToken cancellationToken)
    {
        var fileName = MakeSafeFileName(originalFileName);
        if (ImageExtensions.Contains(Path.GetExtension(fileName)))
        {
            throw new UploadRejectedException($"{fileName} is an image, and Closure can't see images yet.");
        }

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, cancellationToken);
        if (memoryStream.Length > MaximumBytes)
        {
            throw new UploadRejectedException($"{fileName} is over {MaximumBytes / 1024 / 1024} MB.");
        }

        var fileBytes = memoryStream.ToArray();
        if (!StartsWithUtf16ByteOrderMark(fileBytes) && Array.IndexOf(fileBytes, (byte)0) >= 0)
        {
            throw new UploadRejectedException($"{fileName} isn't a text file, so Closure can't read it yet.");
        }

        var text = DecodeText(fileBytes);
        var record = new UploadRecord(Guid.NewGuid().ToString("N")[..12], fileName, text.Length, (int)Math.Ceiling(text.Length * TokensPerCharacter), DateTimeOffset.Now);

        var uploadFolder = Path.Combine(uploadsFolder, record.Id);
        Directory.CreateDirectory(uploadFolder);
        await File.WriteAllBytesAsync(Path.Combine(uploadFolder, fileName), fileBytes, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(uploadFolder, MetadataFileName), JsonSerializer.Serialize(record, MetadataJsonOptions), cancellationToken);
        return record;
    }

    /// <summary>
    /// Returns the upload and its text, or null if the id is malformed or nothing is stored under it.
    /// </summary>
    public (UploadRecord Record, string Text)? Load(string uploadId)
    {
        if (!UploadIdPattern().IsMatch(uploadId))
        {
            return null;
        }

        var metadataPath = Path.Combine(uploadsFolder, uploadId, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var record = JsonSerializer.Deserialize<UploadRecord>(File.ReadAllText(metadataPath), MetadataJsonOptions);
        var filePath = Path.Combine(uploadsFolder, uploadId, record?.FileName ?? "");
        if (record is null || !File.Exists(filePath))
        {
            return null;
        }

        return (record, DecodeText(File.ReadAllBytes(filePath)));
    }

    /// <summary>
    /// UTF-16 text (what PowerShell's > writes) is full of zero bytes, so it would otherwise look like a binary file.
    /// </summary>
    private static bool StartsWithUtf16ByteOrderMark(byte[] fileBytes)
    {
        return fileBytes.Length >= 2 && ((fileBytes[0] == 0xFF && fileBytes[1] == 0xFE) || (fileBytes[0] == 0xFE && fileBytes[1] == 0xFF));
    }

    /// <summary>
    /// UTF-8 unless the file starts with a byte order mark saying otherwise.
    /// </summary>
    private static string DecodeText(byte[] fileBytes)
    {
        using var reader = new StreamReader(new MemoryStream(fileBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Keeps only the name part and replaces characters Windows doesn't allow in file names.
    /// </summary>
    private static string MakeSafeFileName(string originalFileName)
    {
        var nameOnly = Path.GetFileName(originalFileName);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeCharacters = nameOnly
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();
        var safeName = new string(safeCharacters).Trim();
        return safeName.Length == 0 || safeName == MetadataFileName ? "file.txt" : safeName;
    }

    [GeneratedRegex("^[a-f0-9]{12}$")]
    private static partial Regex UploadIdPattern();
}
