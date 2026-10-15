using System.Text.Json;

namespace CPALauncher.Services;

public sealed class TokenImportService
{
    public TokenImportResult ImportJsonFiles(string? authDirectory, IEnumerable<string>? sourceFiles)
    {
        if (string.IsNullOrWhiteSpace(authDirectory))
        {
            return TokenImportResult.Rejected("当前未配置可用的认证目录。");
        }

        if (sourceFiles is null)
        {
            return TokenImportResult.Rejected("未检测到可导入的凭证文件。");
        }

        var files = sourceFiles.ToArray();
        if (files.Length == 0)
        {
            return TokenImportResult.Rejected("未检测到可导入的凭证文件。");
        }

        if (files.Any(file => string.IsNullOrWhiteSpace(file) || IsInvalidSourceFile(file)))
        {
            return TokenImportResult.Rejected("仅支持导入 .json 凭证文件。");
        }

        var targetPathInfos = files
            .Select(sourceFile => new SourceTargetPair(sourceFile, Path.Combine(authDirectory, Path.GetFileName(sourceFile))))
            .ToArray();

        var selfDragPairs = targetPathInfos
            .Where(pair => PathsReferToSameFile(pair.SourceFile, pair.TargetPath))
            .ToArray();

        var copyPairs = targetPathInfos
            .Except(selfDragPairs)
            .ToArray();

        var duplicateFileNames = copyPairs
            .Select(pair => Path.GetFileName(pair.SourceFile))
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Select(fileName => fileName!)
            .GroupBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateFileNames.Length > 0)
        {
            var message = BuildDuplicateFileNameMessage(duplicateFileNames);
            return TokenImportResult.Rejected(message);
        }

        try
        {
            Directory.CreateDirectory(authDirectory);
        }
        catch (Exception ex)
        {
            return TokenImportResult.Rejected($"创建认证目录失败：{ex.Message}");
        }

        var importedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;
        var errors = new List<string>();
        var overwrittenFiles = new List<string>();
        var skippedFiles = new List<string>();

        foreach (var pair in selfDragPairs)
        {
            var fileName = Path.GetFileName(pair.SourceFile);
            skippedCount++;
            skippedFiles.Add(fileName);
        }

        foreach (var pair in copyPairs)
        {
            var sourceFile = pair.SourceFile;
            var targetPath = pair.TargetPath;
            var fileName = Path.GetFileName(sourceFile);

            try
            {
                var bytes = File.ReadAllBytes(sourceFile);
                // Accept UTF-8 BOM without rewriting the imported credential's bytes.
                var json = bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("凭证内容必须是 JSON 对象。");
                using var guard = new FileStream(targetPath + ".import-lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                var willOverwrite = File.Exists(targetPath);
                AtomicFile.WriteBytes(targetPath, bytes, targetPath + ".bak");

                if (willOverwrite)
                {
                    overwrittenFiles.Add(fileName);
                }

                importedCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                errors.Add($"导入 {fileName} 失败：{(ex is JsonException ? "JSON 格式无效，原文件未改动。" : ex.Message)}");
            }
        }

        return new TokenImportResult
        {
            Status = DetermineStatus(importedCount, failedCount, skippedCount),
            ImportedCount = importedCount,
            FailedCount = failedCount,
            SkippedCount = skippedCount,
            Errors = errors,
            OverwrittenFiles = overwrittenFiles,
            SkippedFiles = skippedFiles,
            SummaryMessage = BuildSummaryMessage(importedCount, failedCount, skippedCount),
        };
    }

    private static bool IsInvalidSourceFile(string sourceFile)
        => Directory.Exists(sourceFile) || !string.Equals(Path.GetExtension(sourceFile), ".json", StringComparison.OrdinalIgnoreCase);

    private static bool PathsReferToSameFile(string sourceFile, string targetPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(sourceFile),
                Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static TokenImportStatus DetermineStatus(int importedCount, int failedCount, int skippedCount)
    {
        if (importedCount == 0 && failedCount == 0)
        {
            return skippedCount > 0 ? TokenImportStatus.NoOp : TokenImportStatus.NoOp;
        }

        if (failedCount == 0)
        {
            return TokenImportStatus.Succeeded;
        }

        return importedCount > 0 ? TokenImportStatus.PartiallySucceeded : TokenImportStatus.Failed;
    }

    private static string BuildSummaryMessage(int importedCount, int failedCount, int skippedCount)
    {
        if (failedCount == 0 && skippedCount == 0)
        {
            return $"已导入 {importedCount} 个凭证文件。";
        }

        if (failedCount == 0)
        {
            return importedCount > 0
                ? $"已导入 {importedCount} 个凭证文件，跳过 {skippedCount} 个同路径文件。"
                : $"已跳过 {skippedCount} 个与目标目录同路径的文件。";
        }

        if (importedCount == 0)
        {
            return skippedCount > 0
                ? $"有 {failedCount} 个文件导入失败，跳过 {skippedCount} 个同路径文件。"
                : $"有 {failedCount} 个文件导入失败。";
        }

        return skippedCount > 0
            ? $"已导入 {importedCount} 个凭证文件，{failedCount} 个文件失败，跳过 {skippedCount} 个同路径文件。"
            : $"已导入 {importedCount} 个凭证文件，{failedCount} 个文件失败。";
    }

    private static string BuildDuplicateFileNameMessage(IEnumerable<string> duplicateFileNames)
        => $"导入批次中存在重复的目标文件名：{string.Join("、", duplicateFileNames)}。";

    private sealed record SourceTargetPair(string SourceFile, string TargetPath);
}

public enum TokenImportStatus
{
    Rejected = 0,
    NoOp = 1,
    Succeeded = 2,
    PartiallySucceeded = 3,
    Failed = 4,
}

public sealed class TokenImportResult
{
    public required TokenImportStatus Status { get; init; }

    public int ImportedCount { get; init; }

    public int FailedCount { get; init; }

    public int SkippedCount { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> OverwrittenFiles { get; init; } = [];

    public IReadOnlyList<string> SkippedFiles { get; init; } = [];

    public string SummaryMessage { get; init; } = string.Empty;

    public static TokenImportResult Rejected(string message) => new()
    {
        Status = TokenImportStatus.Rejected,
        Errors = [message],
        SummaryMessage = message,
    };
}
