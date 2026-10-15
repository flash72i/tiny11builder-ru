using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private sealed record VerifiedResume(ResumeCheckpoint Checkpoint, bool SetupPending, bool SetupAlreadyApplied,
        bool StaleSetupMount, bool StaleHiveJournals);

    /// <summary>
    /// Finishes an interrupted build from its verified checkpoint without repeating the finished work: after the
    /// installation image was committed (Setup image, Compress, ISO) or after the final image was compressed (ISO only).
    /// Every check runs before anything is changed; a refusal is an <see cref="InvalidDataException"/> with a
    /// user-facing message. Only the output path, servicing tools, final compression (before it ran) and
    /// <see cref="BuildRecipe.KeepWorkingFiles"/> may differ from the job's original recipe.
    /// </summary>
    public async Task<BuildResult> ResumeAsync(string jobFile, BuildRecipe recipe, IProgress<BuildProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var elapsed = Stopwatch.StartNew();
        var directory = ResolveJobDirectory(jobFile);
        using var job = BuildJob.Open(directory, bufferedLog: recipe.TurboMode);
        var tracking = new BuildOperationTracker(progress, job.Log);
        VerifiedResume verified;
        try
        {
            job.Log($"=== Запрошено возобновление {DateTimeOffset.UtcNow:O}: вывод {recipe.OutputPath}; сжатие {recipe.Compression}; " +
                $"сохранять рабочие файлы {recipe.KeepWorkingFiles}; dism {recipe.DismPath}; oscdimg {recipe.OscdimgPath} ===");
            verified = await VerifyResumeAsync(job, tracking, recipe, token).ConfigureAwait(false);
        }
        catch (Exception refusal)
        {
            // Nothing has been changed. The refusal is rethrown unchanged; a failing diagnostic is attached to it, never lost.
            try
            {
                job.Log("ОТКАЗ ВОЗОБНОВЛЕНИЯ: " + refusal.Message);
                job.Flush();
                tracking.Fail(refusal, BuildFailureException.IsCancellationCause(refusal));
            }
            catch (Exception diagnostic) { refusal.Data["Tiny11Builder.DiagnosticFailure"] = diagnostic.Message; }
            throw;
        }

        // ---- every check passed; changes start here ----
        var checkpoint = verified.Checkpoint;
        var mount = Path.Combine(directory, "mount");
        var scratch = Path.Combine(directory, "scratch");
        var dism = new DismClient(recipe.DismPath, runner, tracking.Output,
            new(scratch, Path.Combine(directory, "dism.log"), OptimizedMount: false));
        var session = new BuildSession
        {
            Recipe = recipe, Job = job, Tracking = tracking, CancellationProgress = progress, Dism = dism, Execution = checkpoint.Report.Execution, Image = checkpoint.Image,
            EfiRelative = checkpoint.EfiBootImage, Staging = Path.Combine(directory, "media"), Mount = mount,
            WorkingWim = Path.Combine(directory, "working.wim"), TemporaryIso = Path.Combine(directory, "output.iso"),
            ReportPath = Path.Combine(directory, "report.json"), Scratch = scratch, Elapsed = elapsed,
            PriorActive = TimeSpan.FromMilliseconds(checkpoint.ActiveMilliseconds), ResumedFrom = checkpoint.Phase,
            UsedOptimizedMount = checkpoint.Report.UsedOptimizedMount, StagingResult = checkpoint.Report.SetupMedia,
            Cleanup = checkpoint.Report.RedundantData, Checkpoint = checkpoint, SetupPending = verified.SetupPending,
            CompressPending = checkpoint.Phase == ResumePhase.Committed,
            StageName = BuildProgressText.ResumeCheckStage
        };
        session.Features.AddRange(checkpoint.Report.Features);
        session.Capabilities.AddRange(checkpoint.Report.Capabilities);
        try
        {
            job.State("running", BuildProgressText.ResumeCheckStage);
            Directory.CreateDirectory(scratch);
            Directory.CreateDirectory(mount);
            if (verified.StaleHiveJournals && OperatingSystem.IsWindows())
            {
                session.Log("Выгрузка монтирований автономного реестра, оставленных прерванным запуском.");
                OfflineHive.RecoverJob(directory);
            }
            if (verified.StaleSetupMount)
            {
                session.Log("Отключение образа установки Windows, оставленного смонтированным прерванным запуском. Зафиксированный установочный образ не затрагивается.");
                session.Mounted = true;
                await DiscardMountAsync(dism, mount).ConfigureAwait(false);
                session.Mounted = false;
            }
            if (verified.SetupAlreadyApplied)
            {
                session.Log("Изменённый образ установки Windows уже был на месте; фиксируем это.");
                var bootWim = Path.Combine(session.Staging, @"sources\boot.wim");
                UpdateCheckpoint(session, saved => saved with
                {
                    SetupState = SetupProgress.Applied,
                    StagedMedia = ReplaceEntry(saved.StagedMedia, new(@"sources\boot.wim", new FileInfo(bootWim).Length, saved.EditedBootSha256!))
                });
            }
            return await RunTailAsync(session, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw await FailAsync(session, error).ConfigureAwait(false);
        }
    }

    private async Task<VerifiedResume> VerifyResumeAsync(BuildJob job, BuildOperationTracker tracking, BuildRecipe requested,
        CancellationToken token)
    {
        var directory = job.Directory;
        var state = job.Current;
        if (state.Id != Path.GetFileName(directory)["tiny11-".Length..] || state.Purpose != "build")
            throw new InvalidDataException("Эта задача не является прерванной сборкой, поэтому её нельзя продолжить.");
        if (state.Status == "completed") throw new InvalidDataException("Эта сборка уже завершена, возобновлять нечего.");
        if (state.Status == "recovered")
            throw new InvalidDataException("Эта задача была восстановлена, а её сохранённая работа удалена, поэтому возобновление невозможно.");
        var checkpoint = CheckpointStore.Read(directory);
        tracking.Restore(checkpoint.Operations);
        tracking.BeginStage(BuildProgressText.ResumeCheckStage, "Проверка сохранённого образа, файлов установки и параметров перед продолжением.");
        var committed = checkpoint.Phase == ResumePhase.Committed;
        if (!committed && (checkpoint.SetupState is SetupProgress.Pending or SetupProgress.Replacing) &&
            !SetupInstaller.RequiresLegacy(requested))
            throw new InvalidDataException("Сохранённый прогресс несогласован: образ сжат, но установка не была завершена.");

        // ---- settings: the recipe identity, frozen fields, paths and tools (cheap checks first) ----
        var recipeFile = Path.Combine(directory, JobCheckpoint.RecipeFileName);
        PathSafety.RejectReparsePoints(recipeFile);
        if (!File.Exists(recipeFile))
            throw new InvalidDataException("Исходный рецепт задачи (recipe.json) отсутствует, поэтому сохранённой работе нельзя доверять.");
        var recipeBytes = await File.ReadAllBytesAsync(recipeFile, token).ConfigureAwait(false);
        if (!Digest.HashBytes(recipeBytes).Equals(checkpoint.RecipeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Исходный рецепт задачи изменился после сохранения прогресса, поэтому сохранённой работе нельзя доверять.");
        BuildRecipe original;
        try
        {
            original = JsonSerializer.Deserialize<BuildRecipe>(recipeBytes, JsonDefaults.Options) ??
                throw new InvalidDataException("Исходный рецепт задачи пуст.");
        }
        catch (JsonException error) { throw new InvalidDataException("Исходный рецепт задачи повреждён: " + error.Message, error); }
        original.ValidateOptions();
        var differences = FrozenDifferences(original, requested);
        if (differences.Length > 0)
            throw new InvalidDataException($"Рецепт возобновления изменяет параметры, зафиксированные для этой задачи ({string.Join(", ", differences)}). " +
                "Only the output location, servicing tools, final compression and keeping working files can change.");
        if (!committed && requested.Compression != checkpoint.CompressedWith)
            throw new InvalidDataException($"Итоговый образ уже сжат в режиме {checkpoint.CompressedWith}; " +
                $"возобновление в другом режиме невозможно: {requested.Compression}.");
        if (!Path.GetDirectoryName(directory)!.Equals(PathSafety.Full(original.WorkspacePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Эта задача находится не в той рабочей области, которая указана в её рецепте.");
        if (SetupInstaller.RequiresBootEdit(original) != (checkpoint.SetupState != SetupProgress.NotRequired) &&
            !(SetupInstaller.RequiresLegacy(original) && !original.SelectedOptions.Contains("hardware-bypass") &&
                checkpoint.SetupState == SetupProgress.NotRequired && !checkpoint.Report.LegacyInstallerEnabled))
            throw new InvalidDataException("Сохранённое состояние образа установки не соответствует рецепту задачи, поэтому сохранённой работе нельзя доверять.");
        if (string.IsNullOrEmpty(requested.OscdimgPath))
            throw new InvalidDataException("Для создания загрузочного ISO требуется oscdimg.exe из Windows ADK.");
        PathSafety.ValidateForResume(requested);
        host.ValidateTools(requested);
        host.ValidateResume(requested, checkpoint.Image, (committed ? checkpoint.WorkingImage.Length * 2 : checkpoint.CompressedImage!.Length) + (3L << 30));
        foreach (var name in new[] { "media", "mount", "scratch", "setup", "compress" })
            PathSafety.RejectReparsePoints(Path.Combine(directory, name));

        // ---- nothing of this job may still be mounted or active ----
        var jobMount = Path.Combine(directory, "mount");
        var dism = new DismClient(requested.DismPath, runner);
        var staleSetupMount = false;
        foreach (var (mountDirectory, imageFile) in ParseMounted(await dism.RunAsync(token, "/Get-MountedWimInfo").ConfigureAwait(false)))
        {
            if (!IsUnder(mountDirectory, directory) && !(imageFile is not null && IsUnder(imageFile, directory))) continue;
            // Only the disposable private copy of the Setup image may be a leftover mount; it is discarded, never committed.
            var transient = SamePath(mountDirectory, jobMount) && imageFile is not null &&
                SamePath(imageFile, Path.Combine(directory, "setup", "boot.wim"));
            if (!transient)
                throw new InvalidDataException($"Образ этой задачи всё ещё смонтирован ({mountDirectory}). " +
                    "Resume continues only from an unmounted, committed image; unmount it first.");
            staleSetupMount = true;
        }
        if (!staleSetupMount && Directory.Exists(jobMount) && Directory.EnumerateFileSystemEntries(jobMount).Any())
            throw new InvalidDataException("Папка монтирования задачи не пуста, возможно, смонтированный образ остался. Возобновление отклонено.");
        var staleHiveJournals = Directory.EnumerateFiles(directory, "Tiny11Builder_Offline_*.hive.json").Any();

        // ---- the saved image (positive proof: size, header, SHA-256 and identity, not a timestamp) ----
        if (committed)
        {
            var working = Path.Combine(directory, "working.wim");
            PathSafety.RejectReparsePoints(working);
            if (!File.Exists(working)) throw new InvalidDataException("Сохранённый рабочий образ (working.wim) отсутствует.");
            if (new FileInfo(working).Length != checkpoint.WorkingImage.Length)
                throw new InvalidDataException("Сохранённый рабочий образ отличается по размеру от момента фиксации.");
            Digest.RequireCompleteWim(working, "Сохранённый рабочий образ");
            await tracking.RunAsync("resume:image", BuildProgressText.ResumeCheckStage, "Проверка сохранённого образа Windows", async () =>
            {
                var (_, sha) = await Digest.HashAsync(working, percent => tracking.Output($"Проверка сохранённого образа Windows: {percent:0}%"), token)
                    .ConfigureAwait(false);
                if (!sha.Equals(checkpoint.WorkingImage.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Сохранённый рабочий образ изменился после фиксации (его SHA-256 отличается), поэтому ему нельзя доверять.");
            }).ConfigureAwait(false);
            RequireSameImage(await dism.GetImagesAsync(working, token).ConfigureAwait(false), checkpoint.Image, "Сохранённый рабочий образ");
        }

        // ---- the staged media: every file listed by length and SHA-256, nothing else present ----
        var alternates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (checkpoint.SetupState == SetupProgress.Replacing) alternates[@"sources\boot.wim"] = checkpoint.EditedBootSha256!;
        var tolerated = committed ?
            new HashSet<string>([@"sources\install.wim", @"sources\install.esd"], StringComparer.OrdinalIgnoreCase) :
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> matchedAlternate = [];
        var lastReported = 0d;
        await tracking.RunAsync("resume:media", BuildProgressText.ResumeCheckStage, "Проверка сохранённых файлов установки", async () =>
        {
            matchedAlternate = await MediaManifest.VerifyAsync(Path.Combine(directory, "media"), checkpoint.StagedMedia, alternates, tolerated,
                checkpoint.Report.Execution.CopyWorkers, percent =>
                {
                    if (percent < lastReported + 5 && percent < 100) return;
                    lastReported = percent;
                    tracking.Output($"Проверка сохранённых файлов установки: {percent:0}%");
                }, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!committed)
        {
            var compressed = Path.Combine(directory, "media", checkpoint.CompressedImage!.RelativePath);
            Digest.RequireCompleteWim(compressed, "Сохранённый сжатый установочный образ");
            RequireSameImage(await dism.GetImagesAsync(compressed, token).ConfigureAwait(false), checkpoint.Image,
                "Сохранённый сжатый установочный образ");
        }
        var setupAlreadyApplied = checkpoint.SetupState == SetupProgress.Replacing &&
            matchedAlternate.Contains(@"sources\boot.wim", StringComparer.OrdinalIgnoreCase);
        var setupPending = checkpoint.SetupState == SetupProgress.Pending ||
            (checkpoint.SetupState == SetupProgress.Replacing && !setupAlreadyApplied) ||
            (SetupInstaller.RequiresLegacy(original) && !checkpoint.Report.LegacyInstallerEnabled);
        return new(checkpoint, setupPending, setupAlreadyApplied, staleSetupMount, staleHiveJournals);
    }

    private static string ResolveJobDirectory(string jobFile)
    {
        jobFile = PathSafety.Full(jobFile);
        var directory = Path.GetDirectoryName(jobFile)!;
        var leaf = Path.GetFileName(directory);
        PathSafety.RejectReparsePoints(jobFile);
        if (Path.GetFileName(jobFile) != "job.json" || !leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _))
            throw new InvalidDataException("Выберите job.json внутри папки задачи tiny11.");
        return directory;
    }

    private static void RequireSameImage(IReadOnlyList<WindowsImage> images, WindowsImage expected, string what)
    {
        if (images.Count != 1) throw new InvalidDataException($"{what} должен содержать ровно один образ, но содержит {images.Count}.");
        var actual = images[0];
        if (actual.Architecture != expected.Architecture || actual.Version.Build != expected.Version.Build ||
            !actual.Edition.Equals(expected.Edition, StringComparison.OrdinalIgnoreCase) ||
            !actual.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase) ||
            !actual.Installation.Equals(expected.Installation, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{what} — это другой образ Windows ({actual.Summary}), чем тот, который собирала задача ({expected.Summary}).");
    }

    // Frozen by default: a field added to BuildRecipe later cannot change on resume unless it is listed as free here.
    private static BuildRecipe Freeze(BuildRecipe recipe) => recipe with
    {
        OutputPath = "", DismPath = "", OscdimgPath = "", Compression = default, KeepWorkingFiles = false,
        SourcePath = NormalizePath(recipe.SourcePath), WorkspacePath = NormalizePath(recipe.WorkspacePath),
        SelectedOptions = [.. recipe.SelectedOptions.Order(StringComparer.Ordinal)],
        ExpertActions = [.. recipe.ExpertActions.OrderBy(action => action.Kind).ThenBy(action => action.Identity, StringComparer.Ordinal)
            .ThenBy(action => action.Mode)]
    };

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant(); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string[] FrozenDifferences(BuildRecipe original, BuildRecipe requested)
    {
        var left = JsonSerializer.SerializeToNode(Freeze(original), JsonDefaults.Options)!.AsObject();
        var right = JsonSerializer.SerializeToNode(Freeze(requested), JsonDefaults.Options)!.AsObject();
        return left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key))
            .Where(name => left[name]?.ToJsonString() != right[name]?.ToJsonString()).ToArray();
    }

    private static IReadOnlyList<(string MountDirectory, string? ImageFile)> ParseMounted(string text)
    {
        var entries = new List<(string, string?)>();
        string? directory = null;
        string? file = null;
        foreach (var line in text.Split('\n'))
        {
            var mount = Regex.Match(line, @"^[ \t]*Mount Dir[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (mount.Success)
            {
                if (directory is not null) entries.Add((directory, file));
                directory = mount.Groups[1].Value;
                file = null;
                continue;
            }
            var image = Regex.Match(line, @"^[ \t]*Image File[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (image.Success) file = image.Groups[1].Value;
        }
        if (directory is not null) entries.Add((directory, file));
        return entries;
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsUnder(string path, string directory)
    {
        try { return SamePath(path, directory) || PathSafety.IsInside(path, directory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or InvalidDataException) { return false; }
    }
}
