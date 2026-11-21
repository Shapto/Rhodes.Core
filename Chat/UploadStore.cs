using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rhodes.Core.Documents;
using Rhodes.Core.Web;

namespace Rhodes.Core.Chat;

/// <summary>
/// One stored attachment. Kind is "text" for anything read as text, "image" for a picture (MediaType says which format).
/// </summary>
public sealed record UploadRecord(string Id, string FileName, int Characters, int EstimatedTokens, DateTimeOffset UploadedAt, string Kind = UploadStore.TextKind, string? MediaType = null);

/// <summary>
/// Thrown when an upload can't be used; the message is shown to Shapto as is.
/// </summary>
public sealed class UploadRejectedException(string message) : Exception(message);

/// <summary>
/// Keeps files Shapto attaches in Data/uploads/{upload id}/: the original file plus a small upload.json.
/// Anything TextExtractor can read is accepted (any text file, .html, .pdf, Office and OpenDocument files, .epub,
/// and .zip/.7z/.rar/.tar/.tar.gz/.gz archives of those), the same as the knowledge library;
/// images (PNG, JPEG, GIF, BMP) are stored as images: whether she sees them depends on the model (LmStudio:Vision).
/// </summary>
public sealed partial class UploadStore
{
    public const double TokensPerCharacter = 0.3;
    public const string TextKind = "text";
    public const string ImageKind = "image";

    /// <summary>
    /// A rough guess at what one image costs a vision model, for the context estimates; the real number depends on its size.
    /// </summary>
    public const int ImageTokenEstimate = 1000;

    private const int MaximumBytes = 25 * 1024 * 1024;
    private const int MaximumImageBytes = 10 * 1024 * 1024;
    private const string MetadataFileName = "upload.json";

    /// <summary>
    /// The image formats llama-server's image loader reads, with their media types.
    /// </summary>
    private static readonly Dictionary<string, string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
    };

    private static readonly HashSet<string> UnsupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff" };
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
        var extension = Path.GetExtension(fileName);
        if (UnsupportedImageExtensions.Contains(extension))
        {
            throw new UploadRejectedException($"{fileName}: images have to be PNG, JPEG, GIF or BMP. Convert it to PNG first.");
        }

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, cancellationToken);
        if (memoryStream.Length > MaximumBytes)
        {
            throw new UploadRejectedException($"{fileName} is over {MaximumBytes / 1024 / 1024} MB.");
        }

        var fileBytes = memoryStream.ToArray();
        if (ImageMediaTypes.TryGetValue(extension, out var mediaType))
        {
            if (fileBytes.Length > MaximumImageBytes)
            {
                throw new UploadRejectedException($"{fileName} is over {MaximumImageBytes / 1024 / 1024} MB; images have to be smaller.");
            }

            var imageRecord = new UploadRecord(Guid.NewGuid().ToString("N")[..12], fileName, 0, ImageTokenEstimate, DateTimeOffset.Now, ImageKind, mediaType);
            await StoreAsync(imageRecord, fileBytes, cancellationToken);
            return imageRecord;
        }

        var text = TextExtractor.ExtractText(fileName, fileBytes);
        if (text is null)
        {
            throw new UploadRejectedException($"Closure can't read {fileName} yet: she reads text files, web pages, PDFs that have text (not scans), Word, Excel and PowerPoint files (.docx, .xlsx, .pptx), OpenDocument files, .epub books, and .zip, .7z, .rar, .tar, .tar.gz or .gz archives of those.");
        }

        var record = new UploadRecord(Guid.NewGuid().ToString("N")[..12], fileName, text.Length, (int)Math.Ceiling(text.Length * TokensPerCharacter), DateTimeOffset.Now);
        await StoreAsync(record, fileBytes, cancellationToken);
        return record;
    }

    /// <summary>
    /// Returns the upload and its text (empty for an image), or null if the id is malformed or nothing is stored under it.
    /// </summary>
    public (UploadRecord Record, string Text)? Load(string uploadId)
    {
        if (LoadFile(uploadId) is not { } upload)
        {
            return null;
        }

        var text = IsImage(upload.Record) ? "" : TextExtractor.ExtractText(upload.Record.FileName, upload.FileBytes) ?? "";
        return (upload.Record, text);
    }

    /// <summary>
    /// The upload's record and the original file's bytes, or null if the id is malformed or nothing is stored under it.
    /// </summary>
    public (UploadRecord Record, byte[] FileBytes)? LoadFile(string uploadId)
    {
        if (string.IsNullOrEmpty(uploadId) || !UploadIdPattern().IsMatch(uploadId))
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

        return (record, File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// An image upload as a data URL ("data:image/png;base64,…"), the form a vision model takes it in; null if it isn't an image.
    /// </summary>
    public string? LoadImageDataUrl(string uploadId)
    {
        if (LoadFile(uploadId) is not { } upload || !IsImage(upload.Record))
        {
            return null;
        }

        return $"data:{upload.Record.MediaType};base64,{Convert.ToBase64String(upload.FileBytes)}";
    }

    public static bool IsImage(UploadRecord record)
    {
        return record.Kind == ImageKind && record.MediaType is not null;
    }

    private async Task StoreAsync(UploadRecord record, byte[] fileBytes, CancellationToken cancellationToken)
    {
        var uploadFolder = Path.Combine(uploadsFolder, record.Id);
        Directory.CreateDirectory(uploadFolder);
        await File.WriteAllBytesAsync(Path.Combine(uploadFolder, record.FileName), fileBytes, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(uploadFolder, MetadataFileName), JsonSerializer.Serialize(record, MetadataJsonOptions), cancellationToken);
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
