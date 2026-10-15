using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tiny11Builder.Core;

/// <summary>Well-known files of the durable progress record kept inside every tiny11 job directory.</summary>
public static class JobCheckpoint
{
    /// <summary>Written atomically after the installation image is committed, and again once the final image is compressed.</summary>
    public const string FileName = "checkpoint.json";

    /// <summary>The recipe the job was started with. It is never rewritten, so its hash identifies the original choices.</summary>
    public const string RecipeFileName = "recipe.json";

    /// <summary>Existence only. It enables a Resume button; the engine re-verifies everything before it changes anything.</summary>
    public static bool Exists(string jobDirectory) => File.Exists(Path.Combine(jobDirectory, FileName));
}

/// <summary>Recovery refused because the job holds verified saved progress that Recovery would permanently delete.</summary>
public sealed class CheckpointedJobException(string message) : InvalidOperationException(message);

internal static class ResumePhase
{
    public const string Committed = "committed";
    public const string Compressed = "compressed";
}

internal static class SetupProgress
{
    public const string NotRequired = "not-required";
    public const string Pending = "pending";
    public const string Replacing = "replacing";
    public const string Applied = "applied";
}

internal sealed record CheckpointFile(string RelativePath, long Length, string Sha256);

internal sealed record SourceIdentity
{
    public required string Location { get; init; }
    public required int ImageIndex { get; init; }
    public required string InstallFile { get; init; }
    public required long InstallLength { get; init; }
    public required DateTimeOffset InstallLastWriteUtc { get; init; }
}

/// <summary>Everything the final report needs that only the first part of the build knows.</summary>
internal sealed record CheckpointReport
{
    public bool LegacyInstallerEnabled { get; init; }
    public required bool UsedOptimizedMount { get; init; }
    public required BuildExecutionPlan Execution { get; init; }
    public MediaStagingResult? SetupMedia { get; init; }
    public IReadOnlyList<ImageCleanupResult> RedundantData { get; init; } = [];
    public IReadOnlyList<FeatureRemovalVerification> Features { get; init; } = [];
    public IReadOnlyList<RemovalVerification> Capabilities { get; init; } = [];

    /// <summary>Problems found before the commit (for example a skipped component cleanup). A resume reports them again.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

internal sealed record ResumeCheckpoint
{
    public const int CurrentSchema = 1;

    public required int SchemaVersion { get; init; }
    public required string JobId { get; init; }
    public required string Phase { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string RecipeSha256 { get; init; }
    public required SourceIdentity Source { get; init; }
    public required WindowsImage Image { get; init; }
    public required CheckpointFile WorkingImage { get; init; }
    public CheckpointFile? CompressedImage { get; init; }
    public Compression? CompressedWith { get; init; }
    public required string SetupState { get; init; }
    public string? EditedBootSha256 { get; init; }
    public required string EfiBootImage { get; init; }
    public required IReadOnlyList<CheckpointFile> StagedMedia { get; init; }
    public required CheckpointReport Report { get; init; }
    public required double ActiveMilliseconds { get; init; }
    public required IReadOnlyList<OperationRecord> Operations { get; init; }
}

internal static class CheckpointStore
{
    private const long MaximumBytes = 64L << 20;
    public const string NoCheckpointMessage = "This job has no verified checkpoint, so it is not available for resume. " +
        "Jobs created before checkpoints existed are never adopted automatically; Recover workspace can only discard them.";

    public static void WriteAtomic(string jobDirectory, ResumeCheckpoint checkpoint)
    {
        var file = Path.Combine(jobDirectory, JobCheckpoint.FileName);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, checkpoint, JsonDefaults.Options);
                stream.Flush(flushToDisk: true);   // the bytes are on disk before the rename publishes them
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ResumeCheckpoint Read(string jobDirectory)
    {
        var file = Path.Combine(jobDirectory, JobCheckpoint.FileName);
        PathSafety.RejectReparsePoints(file);
        if (!File.Exists(file)) throw new InvalidDataException(NoCheckpointMessage);
        if (new FileInfo(file).Length > MaximumBytes)
            throw new InvalidDataException("Файл сохранённого прогресса намного больше ожидаемого и не был прочитан.");
        ResumeCheckpoint? checkpoint;
        try { checkpoint = JsonSerializer.Deserialize<ResumeCheckpoint>(File.ReadAllBytes(file), JsonDefaults.Options); }
        catch (JsonException error)
        {
            throw new InvalidDataException("Файл сохранённого прогресса повреждён или неполон, поэтому ему нельзя доверять: " + error.Message, error);
        }
        if (checkpoint is null) throw new InvalidDataException("Файл сохранённого прогресса пуст.");
        Validate(checkpoint, jobDirectory);
        return checkpoint;
    }

    /// <summary>Structural validation only. The hashes of the files themselves are verified separately.</summary>
    public static void Validate(ResumeCheckpoint checkpoint, string jobDirectory)
    {
        var leaf = Path.GetFileName(PathSafety.Full(jobDirectory));
        if (checkpoint.SchemaVersion != ResumeCheckpoint.CurrentSchema)
            throw new InvalidDataException($"Сохранённый прогресс использует схему {checkpoint.SchemaVersion}, которую эта версия не может возобновить.");
        if (checkpoint.JobId.Length == 0 || leaf != "tiny11-" + checkpoint.JobId)
            throw new InvalidDataException("Сохранённый прогресс принадлежит другой задаче.");
        if (checkpoint.Phase is not (ResumePhase.Committed or ResumePhase.Compressed))
            throw new InvalidDataException($"Сохранённый прогресс имеет неизвестную фазу '{checkpoint.Phase}'.");
        if (checkpoint.SetupState is not (SetupProgress.NotRequired or SetupProgress.Pending or SetupProgress.Replacing or SetupProgress.Applied))
            throw new InvalidDataException($"Сохранённый прогресс имеет неизвестное состояние установки '{checkpoint.SetupState}'.");
        if (checkpoint.Image is null || checkpoint.Source is null || checkpoint.Report is null || checkpoint.StagedMedia is null ||
            checkpoint.Operations is null || checkpoint.WorkingImage is null || checkpoint.Report.Execution is null)
            throw new InvalidDataException("В сохранённом прогрессе отсутствуют обязательные разделы.");
        RequireDigest(checkpoint.RecipeSha256, "рецепт");
        RequireDigest(checkpoint.WorkingImage.Sha256, "рабочий образ");
        if (checkpoint.WorkingImage.RelativePath != "working.wim" || checkpoint.WorkingImage.Length <= 0)
            throw new InvalidDataException("Сохранённый прогресс не описывает рабочий образ этой задачи.");
        if (checkpoint.EditedBootSha256 is not null) RequireDigest(checkpoint.EditedBootSha256, "изменённый образ установки");
        var media = Path.Combine(PathSafety.Full(jobDirectory), "media");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in checkpoint.StagedMedia)
        {
            RequireDigest(entry.Sha256, "подготовленный файл носителя");
            RequireInside(media, entry.RelativePath);
            if (entry.Length < 0 || !seen.Add(entry.RelativePath))
                throw new InvalidDataException($"Сохранённый список носителей несогласован: '{entry.RelativePath}'.");
        }
        if (!seen.Contains(checkpoint.EfiBootImage) || !seen.Contains(@"sources\boot.wim") || !seen.Contains("autounattend.xml"))
            throw new InvalidDataException("В сохранённом списке носителей отсутствуют загрузочные, установочные файлы или файлы ответов.");
        var compressed = checkpoint.CompressedImage;
        if (checkpoint.Phase == ResumePhase.Compressed)
        {
            if (compressed is null || checkpoint.CompressedWith is not { } compression || !Enum.IsDefined(compression) ||
                compressed.RelativePath != CompressedRelativePath(compression) || !seen.Contains(compressed.RelativePath))
                throw new InvalidDataException("Сохранённый прогресс не описывает сжатый установочный образ.");
            RequireDigest(compressed.Sha256, "сжатый образ");
        }
        else if (compressed is not null)
            throw new InvalidDataException("Сохранённый прогресс содержит сжатый образ, хотя сжатие не завершилось.");
        if (checkpoint.SetupState == SetupProgress.Replacing && checkpoint.EditedBootSha256 is null)
            throw new InvalidDataException("Сохранённое состояние образа установки неполно.");
    }

    public static string CompressedRelativePath(Compression compression) =>
        compression == Compression.Recovery ? @"sources\install.esd" : @"sources\install.wim";

    public static void Delete(string jobDirectory)
    {
        var file = Path.Combine(jobDirectory, JobCheckpoint.FileName);
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Keeps the evidence but takes the file out of the resumable name.</summary>
    public static void Retire(string jobDirectory, string newLeaf)
    {
        var file = Path.Combine(jobDirectory, JobCheckpoint.FileName);
        if (File.Exists(file)) File.Move(file, Path.Combine(jobDirectory, newLeaf), overwrite: true);
    }

    private static void RequireDigest(string? value, string what)
    {
        if (!Digest.IsSha256(value)) throw new InvalidDataException($"Сохранённый прогресс имеет недопустимый хеш: {what}.");
    }

    private static void RequireInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            !PathSafety.IsInside(Path.Combine(root, relative), root))
            throw new InvalidDataException($"Сохранённый список носителей указывает путь вне задачи: '{relative}'.");
    }
}

internal static class Digest
{
    private const int WimHeaderSize = 208;

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static async Task<(long Length, string Sha256)> HashAsync(string path, Action<double>? progress, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 << 20];
        var length = stream.Length;
        long total = 0;
        var nextReport = 5d;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
            if (progress is null || length <= 0) continue;
            var percent = total * 100d / length;
            if (percent < nextReport) continue;
            progress(Math.Min(percent, 100));
            nextReport = Math.Floor(percent / 5) * 5 + 5;
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// A DISM commit that really finished leaves a complete header. A missing signature, a wrong header size, no images or a
    /// set write-in-progress flag means the file was cut off mid-write and must never be checkpointed or reused.
    /// </summary>
    public static void RequireCompleteWim(string path, string description)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < WimHeaderSize) throw new InvalidDataException($"{description} слишком мал, чтобы быть образом Windows.");
        Span<byte> header = stackalloc byte[WimHeaderSize];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual("MSWIM\0\0\0"u8))
            throw new InvalidDataException($"{description} не имеет допустимого заголовка WIM.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != WimHeaderSize)
            throw new InvalidDataException($"{description} имеет неожиданный размер заголовка WIM.");
        if ((BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) & 0x40) != 0)
            throw new InvalidDataException($"{description} остался в состоянии незавершённой записи (установлен флаг write-in-progress).");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[44..]) == 0)
            throw new InvalidDataException($"{description} не содержит образов.");
    }
}

internal static class MediaManifest
{
    public static IEnumerable<string> Files(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Подготовленный носитель содержит точку повторного анализа, по которой никогда не выполняется переход: {entry}");
            if (Directory.Exists(entry))
            {
                foreach (var file in Files(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    public static async Task<IReadOnlyList<CheckpointFile>> CreateAsync(string root, int workers, CancellationToken token)
    {
        var files = Files(root).ToArray();
        var entries = new CheckpointFile[files.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(workers, 1, 8), CancellationToken = token },
            async (index, cancellation) =>
            {
                var (length, sha) = await Digest.HashAsync(files[index], null, cancellation).ConfigureAwait(false);
                entries[index] = new(Path.GetRelativePath(root, files[index]), length, sha);
            }).ConfigureAwait(false);
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Verifies every listed file by length and SHA-256 and refuses unlisted files, so nothing can be added to or swapped in
    /// the staged media between runs. <paramref name="alternates"/> names files that may legitimately carry a second hash
    /// (the Setup image before or after its atomic replacement); the files that matched it are returned.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VerifyAsync(string root, IReadOnlyList<CheckpointFile> expected,
        IReadOnlyDictionary<string, string> alternates, IReadOnlySet<string> toleratedExtras, int workers,
        Action<double>? progress, CancellationToken token)
    {
        var actual = Files(root).Select(file => Path.GetRelativePath(root, file)).ToArray();
        var listed = expected.Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = actual.FirstOrDefault(file => !listed.Contains(file) && !toleratedExtras.Contains(file));
        if (extra is not null) throw new InvalidDataException($"Сохранённый носитель содержит неожиданный файл: {extra}");
        var present = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = expected.FirstOrDefault(entry => !present.Contains(entry.RelativePath));
        if (missing is not null) throw new InvalidDataException($"Отсутствует сохранённый файл носителя: {missing.RelativePath}");
        var matchedAlternate = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        await Parallel.ForEachAsync(expected,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(workers, 1, 8), CancellationToken = token },
            async (entry, cancellation) =>
            {
                var path = Path.Combine(root, entry.RelativePath);
                var (length, sha) = await Digest.HashAsync(path, null, cancellation).ConfigureAwait(false);
                var matchesAlternate = alternates.TryGetValue(entry.RelativePath, out var other) &&
                    sha.Equals(other, StringComparison.OrdinalIgnoreCase);
                if (matchesAlternate) matchedAlternate.Add(entry.RelativePath);
                else if (length != entry.Length || !sha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Сохранённый файл носителя изменился после контрольной точки: {entry.RelativePath}");
                progress?.Invoke(Interlocked.Increment(ref done) * 100d / expected.Count);
            }).ConfigureAwait(false);
        return matchedAlternate.ToArray();
    }
}
