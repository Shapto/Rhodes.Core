using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Rhodes.Core.Web;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Rhodes.Core.Documents;

/// <summary>
/// Turns a file into the text Closure reads, the same way for chat attachments and the knowledge library.
/// Any text file is read as it is (code, configs, logs, Lua, Python, TOML, …), whatever its extension;
/// HTML is reduced to its readable text, PDFs to their text page by page, and the zipped-XML document formats to their content:
/// Word (.docx), Excel (.xlsx, one tab-separated row per line), PowerPoint (.pptx, slide by slide), OpenDocument (.odt, .ods, .odp)
/// and e-books (.epub, chapter by chapter). Headings, sheets, slides and chapters start with "# " so they split into sections.
/// Archives (.zip, .7z, .rar, .tar, .tar.gz, .tgz, .gz) become one text: a list of what's inside, then every readable file
/// under its own "=== file: path ===" line. Anything else gives null.
/// </summary>
public static class TextExtractor
{
    /// <summary>
    /// Starts the line that introduces each file inside an archive's text: "=== file: Source/Thing.cs ===".
    /// </summary>
    public const string ArchiveEntryMarkerStart = "=== file: ";
    public const string ArchiveEntryMarkerEnd = " ===";

    private const int SniffBytes = 8192;
    private const int MaximumArchiveEntries = 10000;
    private const long MaximumArchiveEntryBytes = 8L * 1024 * 1024;
    private const long MaximumArchiveTotalBytes = 256L * 1024 * 1024;
    private const int MaximumListedSkippedEntries = 50;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tga", ".dds", ".psd", ".svgz", ".xcf", ".kra", ".tif", ".tiff", ".heic",
        ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".mp4", ".mkv", ".webm", ".avi", ".mov",
        ".xz", ".bz2", ".jar", ".nupkg",
        ".dll", ".exe", ".so", ".dylib", ".pdb", ".obj", ".o", ".a", ".lib", ".class", ".pyc", ".wasm",
        ".db", ".sqlite", ".db-wal", ".db-shm", ".bin", ".dat", ".pak", ".bundle", ".unity3d", ".assets",
        ".ttf", ".otf", ".woff", ".woff2", ".doc", ".xls", ".ppt", ".blend", ".fbx", ".gguf", ".safetensors",
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml" };

    private static readonly XNamespace WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OpenDocumentOfficeNamespace = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace OpenDocumentTextNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace OpenDocumentTableNamespace = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace OpenDocumentDrawingNamespace = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace EpubContainerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace EpubPackageNamespace = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";

    /// <summary>
    /// The document formats that are a zip of XML files, by extension, with the reader that turns the opened zip into text.
    /// </summary>
    private static readonly Dictionary<string, Func<ZipArchive, string?>> ZipDocumentReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = ReadWordDocument,
        [".xlsx"] = ReadSpreadsheet,
        [".pptx"] = ReadPresentation,
        [".odt"] = ReadOpenDocument,
        [".ods"] = ReadOpenDocument,
        [".odp"] = ReadOpenDocument,
        [".epub"] = ReadEpub,
    };

    /// <summary>
    /// True for file types that are never readable text, judged by the extension alone.
    /// </summary>
    public static bool IsKnownBinary(string fileName)
    {
        return BinaryExtensions.Contains(Path.GetExtension(fileName));
    }

    private static readonly string[] ArchiveSuffixes = [".zip", ".7z", ".rar", ".tar", ".tgz", ".gz"];

    /// <summary>
    /// True for the archive types Closure can look inside: .zip, .7z, .rar, .tar, .tar.gz, .tgz and single-file .gz.
    /// </summary>
    public static bool IsArchive(string fileName)
    {
        var lowerName = fileName.ToLowerInvariant();
        return ArchiveSuffixes.Any(archiveSuffix => lowerName.EndsWith(archiveSuffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The text of a file on disk, or null when it isn't something Closure can read.
    /// </summary>
    public static string? ReadText(string fullPath)
    {
        return IsKnownBinary(fullPath) ? null : ExtractText(fullPath, File.ReadAllBytes(fullPath));
    }

    /// <summary>
    /// The text of a file's bytes; fileName decides how it's read. Null when it isn't something Closure can read
    /// (a known binary type, or a NUL byte near the start).
    /// </summary>
    public static string? ExtractText(string fileName, byte[] fileBytes)
    {
        var extension = Path.GetExtension(fileName);
        if (ZipDocumentReaders.TryGetValue(extension, out var zipDocumentReader))
        {
            return ReadZipDocument(fileBytes, zipDocumentReader);
        }

        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPdf(fileBytes);
        }

        if (IsArchive(fileName))
        {
            return ReadArchive(fileName, fileBytes);
        }

        if (IsKnownBinary(fileName) || LooksBinary(fileBytes))
        {
            return null;
        }

        var text = DecodeText(fileBytes);
        return HtmlExtensions.Contains(extension) ? WebReader.HtmlToText(text) : text;
    }

    /// <summary>
    /// A NUL byte in the first few KB, unless the file starts with a UTF-16 byte order mark (UTF-16 text is full of NULs,
    /// and it's what PowerShell's > writes).
    /// </summary>
    private static bool LooksBinary(byte[] fileBytes)
    {
        var hasUtf16ByteOrderMark = fileBytes.Length >= 2 && ((fileBytes[0] == 0xFF && fileBytes[1] == 0xFE) || (fileBytes[0] == 0xFE && fileBytes[1] == 0xFF));
        return !hasUtf16ByteOrderMark && Array.IndexOf(fileBytes, (byte)0, 0, Math.Min(SniffBytes, fileBytes.Length)) >= 0;
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
    /// An archive as one text: a summary of what's inside, then each readable file (text, .html, .docx, .pdf) under its marker line.
    /// Archives inside the archive, binary files and files over MaximumArchiveEntryBytes are listed as skipped.
    /// Reading stops at MaximumArchiveEntries files or MaximumArchiveTotalBytes unpacked, so a zip bomb can't run away.
    /// Null when the archive itself can't be opened.
    /// </summary>
    private static string? ReadArchive(string fileName, byte[] fileBytes)
    {
        var readFiles = new List<(string EntryPath, string Text)>();
        var skippedFiles = new List<string>();
        var stoppedEarly = false;
        try
        {
            long unpackedBytes = 0;
            foreach (var (entryPath, entryBytes) in EnumerateArchiveEntries(fileName, fileBytes))
            {
                if (readFiles.Count + skippedFiles.Count >= MaximumArchiveEntries || unpackedBytes > MaximumArchiveTotalBytes)
                {
                    stoppedEarly = true;
                    break;
                }

                if (entryBytes is null)
                {
                    skippedFiles.Add($"{entryPath} (too big)");
                    continue;
                }

                unpackedBytes += entryBytes.Length;
                var entryText = IsArchive(entryPath) ? null : ExtractText(entryPath, entryBytes);
                if (entryText is null)
                {
                    skippedFiles.Add(IsArchive(entryPath) ? $"{entryPath} (archive inside the archive)" : $"{entryPath} (not text)");
                    continue;
                }

                readFiles.Add((entryPath, entryText));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Broken, encrypted or unsupported archives throw many different exception types; any of them means "can't read it".
            return null;
        }

        var builder = new StringBuilder();
        builder.Append($"Archive {Path.GetFileName(fileName)}: {readFiles.Count + skippedFiles.Count} files, {readFiles.Count} read, {skippedFiles.Count} skipped").Append(stoppedEarly ? " (stopped early: too many files or too much data)" : "").Append(".\n");
        foreach (var skippedFile in skippedFiles.Take(MaximumListedSkippedEntries))
        {
            builder.Append("Skipped: ").Append(skippedFile).Append('\n');
        }

        if (skippedFiles.Count > MaximumListedSkippedEntries)
        {
            builder.Append($"Skipped: … and {skippedFiles.Count - MaximumListedSkippedEntries} more\n");
        }

        foreach (var (entryPath, entryText) in readFiles)
        {
            builder.Append('\n').Append(ArchiveEntryMarkerStart).Append(entryPath).Append(ArchiveEntryMarkerEnd).Append('\n');
            builder.Append(entryText.Replace("\r\n", "\n").TrimEnd('\n')).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Every file in a .zip, .7z, .rar, .tar, .tar.gz/.tgz or single-file .gz, with its bytes (null when it's over MaximumArchiveEntryBytes).
    /// .7z and .rar go through SharpCompress; the rest use what .NET has built in.
    /// </summary>
    private static IEnumerable<(string EntryPath, byte[]? EntryBytes)> EnumerateArchiveEntries(string fileName, byte[] fileBytes)
    {
        var lowerName = fileName.ToLowerInvariant();
        var isSevenZip = lowerName.EndsWith(".7z", StringComparison.Ordinal);
        if (isSevenZip || lowerName.EndsWith(".rar", StringComparison.Ordinal))
        {
            using IArchive sharpArchive = isSevenZip ? SevenZipArchive.OpenArchive(new MemoryStream(fileBytes)) : RarArchive.OpenArchive(new MemoryStream(fileBytes));
            foreach (var entry in sharpArchive.Entries)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                var entryPath = entry.Key ?? "";
                if (entry.Size > MaximumArchiveEntryBytes)
                {
                    yield return (entryPath, null);
                    continue;
                }

                using var entryStream = entry.OpenEntryStream();
                yield return (entryPath, ReadAtMost(entryStream, MaximumArchiveEntryBytes));
            }

            yield break;
        }
        if (lowerName.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var archive = new ZipArchive(new MemoryStream(fileBytes), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                using var entryStream = entry.Open();
                yield return (entry.FullName, entry.Length > MaximumArchiveEntryBytes ? null : ReadAtMost(entryStream, MaximumArchiveEntryBytes));
            }

            yield break;
        }

        var isTar = lowerName.EndsWith(".tar", StringComparison.Ordinal) || lowerName.EndsWith(".tar.gz", StringComparison.Ordinal) || lowerName.EndsWith(".tgz", StringComparison.Ordinal);
        Stream archiveStream = new MemoryStream(fileBytes);
        if (!lowerName.EndsWith(".tar", StringComparison.Ordinal))
        {
            archiveStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        }

        using (archiveStream)
        {
            if (!isTar)
            {
                // A single gzipped file: "notes.txt.gz" holds "notes.txt".
                yield return (Path.GetFileNameWithoutExtension(fileName), ReadAtMost(archiveStream, MaximumArchiveEntryBytes));
                yield break;
            }

            using var tarReader = new TarReader(archiveStream);
            while (tarReader.GetNextEntry() is { } tarEntry)
            {
                if (tarEntry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || tarEntry.DataStream is null)
                {
                    continue;
                }

                yield return (tarEntry.Name, tarEntry.Length > MaximumArchiveEntryBytes ? null : ReadAtMost(tarEntry.DataStream, MaximumArchiveEntryBytes));
            }
        }
    }

    /// <summary>
    /// The whole stream, or null if it holds more than maximumBytes (sizes inside archives can lie, so this counts what really comes out).
    /// </summary>
    private static byte[]? ReadAtMost(Stream stream, long maximumBytes)
    {
        using var memoryStream = new MemoryStream();
        var buffer = new byte[81920];
        int readCount;
        while ((readCount = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memoryStream.Length + readCount > maximumBytes)
            {
                return null;
            }

            memoryStream.Write(buffer, 0, readCount);
        }

        return memoryStream.ToArray();
    }

    /// <summary>
    /// A PDF's text page by page in reading order, each page starting with a "# Page N" line so it splits into one section per page.
    /// Null when it can't be read (damaged, password-protected) or has no text at all (a scan without a text layer).
    /// </summary>
    private static string? ReadPdf(byte[] fileBytes)
    {
        try
        {
            using var document = PdfDocument.Open(fileBytes);
            var builder = new StringBuilder();
            var foundText = false;
            foreach (var page in document.GetPages())
            {
                var pageText = ContentOrderTextExtractor.GetText(page).TrimEnd();
                foundText |= pageText.Trim().Length > 0;
                builder.Append("# Page ").Append(page.Number).Append('\n').Append(pageText).Append("\n\n");
            }

            return foundText ? builder.ToString() : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // PdfPig throws many different exception types on broken or encrypted files; any of them means "can't read it".
            return null;
        }
    }

    /// <summary>
    /// Opens a zipped-XML document and hands it to its reader. Null if it isn't a valid zip or its XML is broken.
    /// </summary>
    private static string? ReadZipDocument(byte[] fileBytes, Func<ZipArchive, string?> zipDocumentReader)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(fileBytes), ZipArchiveMode.Read);
            return zipDocumentReader(archive);
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// One XML file from inside a zip, or null if it isn't there.
    /// </summary>
    private static XDocument? ReadZipXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            return null;
        }

        using var entryStream = entry.Open();
        return XDocument.Load(entryStream);
    }

    /// <summary>
    /// A .docx file's paragraphs, one per line, from word/document.xml. Heading paragraphs get a "# " in front.
    /// </summary>
    private static string? ReadWordDocument(ZipArchive archive)
    {
        var document = ReadZipXml(archive, "word/document.xml");
        if (document is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants(WordNamespace + "p"))
        {
            var paragraphText = string.Concat(paragraph.Descendants(WordNamespace + "t").Select(textElement => textElement.Value));
            var styleName = paragraph.Element(WordNamespace + "pPr")?.Element(WordNamespace + "pStyle")?.Attribute(WordNamespace + "val")?.Value ?? "";
            var isHeading = styleName.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || styleName.Equals("Title", StringComparison.OrdinalIgnoreCase);
            builder.Append(isHeading && paragraphText.Length > 0 ? "# " + paragraphText : paragraphText).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// An .xlsx workbook: "# Sheet: name" for each sheet, then one line per row with its cells separated by tabs
    /// (empty cells keep their place, so columns line up). Shared strings, inline strings and booleans are resolved;
    /// formula cells show their last calculated value.
    /// </summary>
    private static string? ReadSpreadsheet(ZipArchive archive)
    {
        var workbook = ReadZipXml(archive, "xl/workbook.xml");
        if (workbook is null)
        {
            return null;
        }

        var sharedStrings = ReadZipXml(archive, "xl/sharedStrings.xml")?.Root?
            .Elements(SpreadsheetNamespace + "si")
            .Select(stringItem => string.Concat(stringItem.Descendants(SpreadsheetNamespace + "t").Select(textElement => textElement.Value)))
            .ToList() ?? [];
        var sheetTargets = ReadZipXml(archive, "xl/_rels/workbook.xml.rels")?.Root?
            .Elements(PackageRelationshipNamespace + "Relationship")
            .ToDictionary(relationship => (string?)relationship.Attribute("Id") ?? "", relationship => (string?)relationship.Attribute("Target") ?? "") ?? [];

        var builder = new StringBuilder();
        foreach (var sheet in workbook.Descendants(SpreadsheetNamespace + "sheet"))
        {
            var relationshipId = (string?)sheet.Attribute(OfficeRelationshipNamespace + "id") ?? "";
            if (!sheetTargets.TryGetValue(relationshipId, out var target))
            {
                continue;
            }

            var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var sheetDocument = ReadZipXml(archive, sheetPath);
            if (sheetDocument is null)
            {
                continue;
            }

            builder.Append("# Sheet: ").Append((string?)sheet.Attribute("name") ?? sheetPath).Append('\n');
            foreach (var row in sheetDocument.Descendants(SpreadsheetNamespace + "row"))
            {
                var cellTexts = new List<string>();
                foreach (var cell in row.Elements(SpreadsheetNamespace + "c"))
                {
                    var columnIndex = GetColumnIndex((string?)cell.Attribute("r"));
                    while (columnIndex is { } knownColumnIndex && cellTexts.Count < knownColumnIndex)
                    {
                        cellTexts.Add("");
                    }

                    cellTexts.Add(ReadCellText(cell, sharedStrings));
                }

                var rowText = string.Join('\t', cellTexts).TrimEnd('\t');
                if (rowText.Length > 0)
                {
                    builder.Append(rowText).Append('\n');
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The value shown in one spreadsheet cell.
    /// </summary>
    private static string ReadCellText(XElement cell, List<string> sharedStrings)
    {
        var cellType = (string?)cell.Attribute("t");
        var value = cell.Element(SpreadsheetNamespace + "v")?.Value ?? "";
        var cellText = cellType switch
        {
            "s" => int.TryParse(value, out var stringIndex) && stringIndex >= 0 && stringIndex < sharedStrings.Count ? sharedStrings[stringIndex] : "",
            "inlineStr" => string.Concat(cell.Descendants(SpreadsheetNamespace + "t").Select(textElement => textElement.Value)),
            "b" => value == "1" ? "TRUE" : "FALSE",
            _ => value,
        };

        // A line break or tab inside a cell would break the one-row-per-line, tab-separated layout.
        return cellText.Replace('\n', ' ').Replace('\t', ' ');
    }

    /// <summary>
    /// The 0-based column of a cell reference ("C7" is 2), or null if there's no reference.
    /// </summary>
    private static int? GetColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
        {
            return null;
        }

        var columnNumber = 0;
        foreach (var character in cellReference.TakeWhile(char.IsLetter))
        {
            columnNumber = columnNumber * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return columnNumber == 0 ? null : columnNumber - 1;
    }

    /// <summary>
    /// A .pptx deck: "# Slide N" for each slide in file order, then its text, one paragraph per line.
    /// </summary>
    private static string? ReadPresentation(ZipArchive archive)
    {
        var slideEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .Select(entry => (Entry: entry, Number: int.TryParse(entry.FullName["ppt/slides/slide".Length..^".xml".Length], out var slideNumber) ? slideNumber : int.MaxValue))
            .OrderBy(slide => slide.Number)
            .ToList();
        if (slideEntries.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var (entry, number) in slideEntries)
        {
            var slideDocument = ReadZipXml(archive, entry.FullName);
            builder.Append("# Slide ").Append(number).Append('\n');
            foreach (var paragraph in slideDocument?.Descendants(DrawingNamespace + "p") ?? [])
            {
                var paragraphText = string.Concat(paragraph.Descendants(DrawingNamespace + "t").Select(textElement => textElement.Value));
                if (paragraphText.Length > 0)
                {
                    builder.Append(paragraphText).Append('\n');
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// An OpenDocument file from content.xml: .ods as sheets of tab-separated rows, .odp as slides,
    /// .odt as paragraphs with its headings marked "# ".
    /// </summary>
    private static string? ReadOpenDocument(ZipArchive archive)
    {
        var body = ReadZipXml(archive, "content.xml")?.Descendants(OpenDocumentOfficeNamespace + "body").FirstOrDefault();
        if (body is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        if (body.Element(OpenDocumentOfficeNamespace + "spreadsheet") is { } spreadsheet)
        {
            foreach (var table in spreadsheet.Elements(OpenDocumentTableNamespace + "table"))
            {
                builder.Append("# Sheet: ").Append((string?)table.Attribute(OpenDocumentTableNamespace + "name") ?? "").Append('\n');
                foreach (var row in table.Descendants(OpenDocumentTableNamespace + "table-row"))
                {
                    var cellTexts = row.Elements()
                        .Where(cell => cell.Name == OpenDocumentTableNamespace + "table-cell" || cell.Name == OpenDocumentTableNamespace + "covered-table-cell")
                        .Select(cell => string.Join(' ', cell.Elements(OpenDocumentTextNamespace + "p").Select(paragraph => paragraph.Value)))
                        .ToList();
                    var rowText = string.Join('\t', cellTexts).TrimEnd('\t');
                    if (rowText.Length > 0)
                    {
                        builder.Append(rowText).Append('\n');
                    }
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        if (body.Element(OpenDocumentOfficeNamespace + "presentation") is { } presentation)
        {
            var slideNumber = 0;
            foreach (var page in presentation.Elements(OpenDocumentDrawingNamespace + "page"))
            {
                slideNumber++;
                builder.Append("# Slide ").Append(slideNumber).Append('\n');
                AppendOpenDocumentParagraphs(builder, page);
                builder.Append('\n');
            }

            return builder.ToString();
        }

        AppendOpenDocumentParagraphs(builder, body);
        return builder.ToString();
    }

    /// <summary>
    /// Every heading ("# " in front) and paragraph under an OpenDocument element, in document order, one per line.
    /// </summary>
    private static void AppendOpenDocumentParagraphs(StringBuilder builder, XElement parentElement)
    {
        foreach (var element in parentElement.Descendants())
        {
            if (element.Name == OpenDocumentTextNamespace + "h")
            {
                builder.Append("# ").Append(element.Value).Append('\n');
            }
            else if (element.Name == OpenDocumentTextNamespace + "p" && element.Value.Length > 0)
            {
                builder.Append(element.Value).Append('\n');
            }
        }
    }

    /// <summary>
    /// An .epub book: its title, then each chapter in reading order (the spine) as "# file name" and its readable text.
    /// </summary>
    private static string? ReadEpub(ZipArchive archive)
    {
        var packagePath = ReadZipXml(archive, "META-INF/container.xml")?
            .Descendants(EpubContainerNamespace + "rootfile")
            .Select(rootFile => (string?)rootFile.Attribute("full-path"))
            .FirstOrDefault(path => !string.IsNullOrEmpty(path));
        var package = packagePath is null ? null : ReadZipXml(archive, packagePath);
        if (packagePath is null || package is null)
        {
            return null;
        }

        var packageFolder = packagePath.Contains('/') ? packagePath[..(packagePath.LastIndexOf('/') + 1)] : "";
        var manifestHrefs = package.Descendants(EpubPackageNamespace + "item")
            .Where(item => item.Attribute("id") is not null && item.Attribute("href") is not null)
            .GroupBy(item => (string)item.Attribute("id")!)
            .ToDictionary(itemGroup => itemGroup.Key, itemGroup => (string)itemGroup.First().Attribute("href")!);

        var builder = new StringBuilder();
        var bookTitle = package.Descendants(DublinCoreNamespace + "title").FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(bookTitle))
        {
            builder.Append(bookTitle.Trim()).Append("\n\n");
        }

        foreach (var itemReference in package.Descendants(EpubPackageNamespace + "itemref"))
        {
            var itemId = (string?)itemReference.Attribute("idref") ?? "";
            if (!manifestHrefs.TryGetValue(itemId, out var href))
            {
                continue;
            }

            var chapterPath = packageFolder + Uri.UnescapeDataString(href.Split('#')[0]);
            var chapterEntry = archive.GetEntry(chapterPath);
            if (chapterEntry is null)
            {
                continue;
            }

            using var chapterReader = new StreamReader(chapterEntry.Open(), Encoding.UTF8);
            builder.Append("# ").Append(Path.GetFileNameWithoutExtension(chapterPath)).Append('\n');
            builder.Append(WebReader.HtmlToText(chapterReader.ReadToEnd()).TrimEnd()).Append("\n\n");
        }

        return builder.ToString();
    }
}
