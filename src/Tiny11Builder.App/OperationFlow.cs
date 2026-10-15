using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Tiny11Builder.Core;

namespace Tiny11Builder.App;

// Window-independent decisions behind MainWindow. This file has no WinUI types, so the same source is
// exercised by the unit tests: what an operation was, how it ended, what the user is told, and how
// quickly the page is allowed to repaint.

public enum OperationKind { Other, Choose, Check, Inspect, Inventory, Download, Build, Resume, Recover, Elevate, Recipe }

public enum NoticeLevel { Information, Success, Warning, Error }

internal sealed record OperationOutcome(OperationKind Kind, bool Cancelled, string Title, string Message,
    NoticeLevel Level, string? WorkDirectory, string Details, bool RecoveryRequired, string Warning = "")
{
    public bool IsBuildRun => Kind is OperationKind.Build or OperationKind.Resume;

    // What the page notice says: the outcome, then any problems that cleanup itself ran into.
    public string FullMessage => Warning.Length == 0 ? Message : Message + " " + Warning;
}

internal static class OperationOutcomes
{
    public static DownloadServiceException? DownloadServiceProblem(Exception error) => error switch
    {
        DownloadServiceException download => download,
        AggregateException { InnerExceptions.Count: > 0 } aggregate => DownloadServiceProblem(aggregate.InnerExceptions[0]),
        BuildFailureException { InnerException: { } inner } => DownloadServiceProblem(inner),
        _ => null
    };

    public static bool RequiresServicingTools(Exception error) => error switch
    {
        ServicingToolsRequiredException => true,
        AggregateException { InnerExceptions.Count: > 0 } aggregate => RequiresServicingTools(aggregate.InnerExceptions[0]),
        BuildFailureException { InnerException: { } inner } => RequiresServicingTools(inner),
        _ => false
    };

    // The Win32 code for "the user declined the elevation prompt".
    private const int UserDeclinedElevation = 1223;

    public static OperationOutcome Classify(OperationKind kind, Exception error, bool canResume = false)
    {
        var failure = error as BuildFailureException;
        var cancelled = IsCancellation(kind, error);
        var recovery = failure is not null && NeedsRecovery(failure);
        var (title, message) = cancelled ? Cancelled(kind, canResume) : Failed(kind, ErrorText.Reason(error), canResume);
        if (recovery)
            message += " Смонтированную копию задачи не удалось отключить. Используйте «Восстановить рабочую область» и выберите job.json этой задачи.";
        return new(kind, cancelled, title, message, cancelled && !recovery ? NoticeLevel.Warning : NoticeLevel.Error,
            failure?.WorkDirectory, error.ToString(), recovery, failure is not null && !recovery ? CleanupProblems(failure) : "");
    }

    // Problems met while cleaning up travel with the primary cause, which is always listed first. They are reported
    // as a warning beside the outcome; they never replace the cause.
    private static string CleanupProblems(BuildFailureException failure)
    {
        if (failure.InnerException is not AggregateException { InnerExceptions.Count: > 1 } aggregate) return "";
        var reasons = aggregate.InnerExceptions.Skip(1).Select(ErrorText.Reason).Distinct(StringComparer.Ordinal);
        return ErrorText.Clip("Некоторые шаги очистки сообщили о проблемах: " + string.Join(" ", reasons), 320);
    }

    public static bool IsCancellation(OperationKind kind, Exception error) => error switch
    {
        OperationCanceledException => true,
        BuildFailureException failure => failure.IsCancellation,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(inner => IsCancellation(kind, inner)),
        Win32Exception { NativeErrorCode: UserDeclinedElevation } => kind == OperationKind.Elevate,
        _ => false
    };

    private static bool NeedsRecovery(BuildFailureException failure) => failure.RecoveryRequired;

    private static (string Title, string Message) Cancelled(OperationKind kind, bool canResume) => kind switch
    {
        OperationKind.Build => ("Сборка отменена.", "Исходник не изменён. Рабочие файлы и журналы сохранены." +
            (canResume ? " Сохранённый прогресс можно продолжить." : "")),
        OperationKind.Resume => ("Возобновление отменено.", "Сохранённый прогресс и журналы сохранены." + (canResume ? " Можно возобновить снова." : "")),
        OperationKind.Inspect => ("Изучение отменено.", "Образ не изменён."),
        OperationKind.Inventory => ("Получение списка компонентов отменено.", "Образ не изменён."),
        OperationKind.Download => ("Загрузка отменена.", "Незавершённые загрузки сохранены, чтобы продолжить позже. Исходный носитель не изменён."),
        OperationKind.Recover => ("Восстановление отменено.", "Запустите «Восстановить рабочую область» ещё раз, чтобы завершить очистку этой задачи."),
        OperationKind.Elevate => ("Перезапуск отменён.", "Приложение продолжает работать без прав администратора."),
        _ => ("Отменено.", "Ничего не изменено.")
    };

    private static (string Title, string Message) Failed(OperationKind kind, string reason, bool canResume) => kind switch
    {
        OperationKind.Build => ("Сбой сборки.", reason + (canResume ? " Сохранённый прогресс можно продолжить." : "")),
        OperationKind.Resume => ("Сбой возобновления.", reason + (canResume ? " Сохранённый прогресс остался, можно возобновить снова." : "")),
        OperationKind.Inspect => ("Не удалось изучить образ.", reason),
        OperationKind.Inventory => ("Не удалось загрузить список компонентов.", reason),
        OperationKind.Download => ("Загрузка не завершена.", reason),
        OperationKind.Recover => ("Восстановление не завершено.", reason),
        OperationKind.Elevate => ("Не удалось перезапустить приложение от имени администратора.", reason),
        _ => ("Операция не завершена.", reason)
    };
}

internal static class ErrorText
{
    // Enough for one readable paragraph in a notice. Everything else stays in the job log and in details.
    public const int NoticeLimit = 280;
    private static readonly Regex StackFrame = new(@"^at\s+\S+\(.*?\)", RegexOptions.CultureInvariant);
    private static readonly Regex Marker = new(@"^---.*---$", RegexOptions.CultureInvariant);
    private static readonly Regex TypePrefix = new(@"^(?:[A-Za-z_][\w.`+]*\.)?[A-Za-z_]\w*Exception:\s*", RegexOptions.CultureInvariant);
    private static readonly Regex Banner = new(@"\s*Deployment Image Servicing and Management tool\s*", RegexOptions.CultureInvariant);
    private static readonly Regex ToolNoise = new(
        @"^(?:(?:Image )?Version\s*:.*|For more information, refer to the log file\.?|The DISM log file can be found at.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The message of the underlying cause, without wrapper text, stack frames or tool banners.
    public static string Reason(Exception error)
    {
        var summary = Summary(Primary(error).Message);
        return summary.Length > 0 ? summary : "Дополнительных сведений не сообщено.";
    }

    public static string Summary(string? text, int limit = NoticeLimit)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var parts = new List<string>();
        foreach (var raw in BuildProgressText.ForDisplay(text).Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw;
            var inner = line.IndexOf(" ---> ", StringComparison.Ordinal);
            if (inner >= 0) line = line[..inner];
            line = Banner.Replace(TypePrefix.Replace(line, ""), " ").Trim();
            if (line.Length == 0 || line.StartsWith("--->", StringComparison.Ordinal) || StackFrame.IsMatch(line) ||
                Marker.IsMatch(line) || ToolNoise.IsMatch(line)) continue;
            parts.Add(line);
        }
        return Clip(Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim(), limit);
    }

    public static string Clip(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var cut = text[..(limit - 1)];
        var space = cut.LastIndexOf(' ');
        if (space > limit / 2) cut = cut[..space];
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.TrimEnd() + "…";
    }

    private static Exception Primary(Exception error) => error switch
    {
        BuildFailureException { InnerException: { } inner and not AggregateException } => Primary(inner),
        AggregateException { InnerExceptions.Count: > 0 } aggregate => Primary(aggregate.InnerExceptions[0]),
        _ => error
    };
}

// The text behind the "Build details" box. The box is only rewritten when that cannot disturb the
// reader: never while it is collapsed or while text in it is selected.
internal sealed class LiveLog(int capacity = 250)
{
    private readonly Queue<string> lines = new();
    public bool Dirty { get; private set; }
    public int Count => lines.Count;

    public void Add(string line)
    {
        lines.Enqueue(line);
        while (lines.Count > capacity) lines.Dequeue();
        Dirty = true;
    }

    public void Clear()
    {
        lines.Clear();
        Dirty = true;
    }

    public bool TryRender(bool visible, bool reading, bool force, out string text)
    {
        text = "";
        if (!Dirty || (!force && (!visible || reading))) return false;
        text = string.Join(Environment.NewLine, lines);
        Dirty = false;
        return true;
    }
}

// Progress is reported from worker threads far faster than the page should repaint. Only the newest
// value is kept; the page takes it on its own timer, and takes it once more when the work ends,
// so the final value is never lost.
internal sealed class LatestProgress<T> : IProgress<T> where T : class
{
    private T? latest;

    public void Report(T value) => Interlocked.Exchange(ref latest, value);

    public bool TryTake(out T value)
    {
        var taken = Interlocked.Exchange(ref latest, null);
        value = taken!;
        return taken is not null;
    }
}

internal sealed record ResumeCandidate(string JobFile, string JobDirectory, BuildRecipe? Recipe, string? Problem);

internal static class ResumeSupport
{
    public const string JobFileName = "job.json";

    public const string NoCheckpoint =
        "This build has no saved progress to resume. It was started before progress was saved, or it stopped before its image was saved, " +
        "so it can't be continued. Nothing was changed, and its logs and files are kept.";

    // hasCheckpoint is advisory: it only decides what to offer. The engine verifies the saved work itself.
    public static ResumeCandidate Inspect(string jobFile, Func<string, bool> hasCheckpoint, string recipeFileName = "recipe.json")
    {
        string file, directory;
        try
        {
            file = Path.GetFullPath(jobFile);
            directory = Path.GetDirectoryName(file) ?? "";
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(jobFile, "", null, "Это недопустимый путь к файлу задачи.");
        }
        if (!Path.GetFileName(file).Equals(JobFileName, StringComparison.OrdinalIgnoreCase))
            return new(file, directory, null, "Выберите файл job.json внутри папки сборки tiny11-….");
        if (!hasCheckpoint(directory)) return new(file, directory, null, NoCheckpoint);
        var recipeFile = Path.Combine(directory, recipeFileName);
        if (!File.Exists(recipeFile))
            return new(file, directory, null, "Сохранённый рецепт этой задачи отсутствует, поэтому возобновление невозможно.");
        try { return new(file, directory, BuildRecipe.Read(recipeFile), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new(file, directory, null, "Не удалось прочитать сохранённый рецепт этой задачи. " + ErrorText.Reason(error));
        }
    }

    public static string Describe(BuildRecipe recipe)
    {
        var mode = CleanupCatalog.ModeForRecipe(recipe) switch
        {
            BuildMode.Tiny11 => "Tiny11", BuildMode.Tiny11Core => "Tiny11 Core",
            _ => recipe.Flavor == BuildFlavor.Core ? "Свой набор + удаления Core" : "Свой набор"
        };
        var compression = recipe.Compression switch
        {
            Compression.Recovery => "Recovery ESD", Compression.Maximum => "Максимальный WIM", _ => "Быстрый WIM"
        };
        var text = new StringBuilder();
        text.AppendLine($"Результат: {recipe.OutputPath}");
        text.AppendLine($"Режим: {mode} · {recipe.SelectedOptions.Length} групп удаления · {recipe.ExpertActions.Length} отдельных действий");
        text.AppendLine($"Сжатие: {compression}");
        text.AppendLine();
        text.AppendLine("Выбор удалений из исходной сборки сохраняется. При возобновлении его нельзя изменить.");
        if (recipe.Flavor == BuildFlavor.Core)
            text.AppendLine("Core: без Windows Update, без Microsoft Defender, без среды восстановления. Используйте только в изолированной тестовой ВМ.");
        if (SetupLabels.Legacy(recipe) is { } legacy) text.AppendLine(legacy + ".");
        if (recipe.ExpertActions.Length > 0)
            text.AppendLine("Отдельные удаления включают защищённые элементы. Образ может не загрузиться.");
        text.AppendLine();
        text.Append("Сначала сохранённый прогресс проверяется. При любом несоответствии ничего не изменяется.");
        return text.ToString();
    }
}

// What the app says about the legacy installer. It is never a choice: the engine turns it on when the removals need
// it (SetupInstaller.RequiresLegacy), so the app only reports it and the recipe carries no flag for it.
internal static class SetupLabels
{
    // Why the legacy installer is on, or null when it is not. Core decides whether it is needed.
    public static string? LegacyReason(BuildRecipe recipe)
    {
        if (!SetupInstaller.RequiresLegacy(recipe)) return null;
        var core = recipe.Flavor == BuildFlavor.Core;
        var payload = recipe.ExpertActions.Any(a => a.Kind == ImageItemKind.ComponentPayload && a.Mode == ExpertActionMode.Remove);
        return core && payload ? "удалений Core и компонентов" : core ? "удалений Core" :
            payload ? "удалений компонентов" : "выбранных удалений";
    }

    public static string? Legacy(BuildRecipe recipe) =>
        LegacyReason(recipe) is { } reason ? $"Устаревший установщик: автоматически (нужен для {reason})" : null;
}

// "3 of 7 stages done": the count shown over the stage history, read from the same rows the history draws.
internal static class StageSummary
{
    private static bool IsDone(BuildStageChoice stage) => stage.Status is "Завершено" or "Завершено ранее";

    public static string Describe(BuildStageBoard board)
    {
        var stages = board.Stages;
        var done = stages.Count(IsDone);
        var failed = stages.Count(stage => stage.Operations.Any(o => o.State == BuildOperationState.Failed));
        var cancelled = stages.Any(stage => stage.Operations.Any(o => o.State == BuildOperationState.Cancelled));
        var text = done == stages.Length ? $"Все {stages.Length} этапов завершено" : $"{done} из {stages.Length} этапов завершено";
        return failed > 0 ? $"{text} · {failed} с ошибкой" : cancelled ? text + " · отменено" : text;
    }
}

// ---- Cancelling an operation ---------------------------------------------------------------------------------------
// Decision logic only, so the tests run the code the window runs. The window draws one dialog from it: first the
// question, then the stop in progress, then what really happened. Nothing here reads a clock, and no percentage is
// invented: the bar is a wait, and a percentage is only ever the current operation's own.

public enum RunResult { Succeeded, Cancelled, Failed }

public enum CancelRequest { Requested, AlreadyRequested, Settled }

// How a run really ended. RunOperation records it once, before any page code that could throw, so the first outcome
// is the real one and a later problem cannot replace it.
internal sealed record OperationEnd(OperationKind Kind, RunResult Result, bool RecoveryRequired, string Title, string Message,
    string? WorkDirectory, string Details, string Warning = "")
{
    // Anything that needs the user's attention may not look finished.
    public bool NeedsAttention => RecoveryRequired || Result == RunResult.Failed;

    public static OperationEnd Succeeded(OperationKind kind) =>
        new(kind, RunResult.Succeeded, false, "Завершено.", "", null, "");

    public static OperationEnd From(OperationOutcome outcome) =>
        new(outcome.Kind, outcome.Cancelled ? RunResult.Cancelled : RunResult.Failed, outcome.RecoveryRequired,
            outcome.Title, outcome.Message, outcome.WorkDirectory, outcome.Details, outcome.Warning);

    public static OperationEnd Unknown(OperationKind kind) =>
        new(kind, RunResult.Failed, false, "Операция не завершена.",
            "Завершение не было зафиксировано, поэтому операция не показана как выполненная. Проверьте журналы.", null, "");
}

// One call of RunOperation as seen by whoever may cancel it. A prompt is bound to the run it was opened for, so it can
// never cancel a later run, and it never touches a token source that has already been disposed.
internal sealed class OperationRun
{
    private static long counter;
    private readonly object gate = new();
    private readonly CancellationTokenSource source;
    private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool requested;
    private bool done;
    private bool promptOpen;
    private OperationEnd? end;
    private string warning = "";

    public OperationRun(OperationKind kind) : this(kind, new CancellationTokenSource()) { }

    // The token source can be supplied so that a test can make its disposal fail.
    internal OperationRun(OperationKind kind, CancellationTokenSource source)
    {
        this.source = source;
        Kind = kind;
        Id = Interlocked.Increment(ref counter);
        Token = source.Token;
    }

    public long Id { get; }
    public OperationKind Kind { get; }
    public CancellationToken Token { get; }

    // Completes once RunOperation has finished everything, its own cleanup included. It never faults.
    public Task Settled => settled.Task;

    public bool CancelRequested { get { lock (gate) return requested; } }
    public bool IsSettled { get { lock (gate) return done; } }
    public string Warning { get { lock (gate) return warning; } }

    // The recorded outcome, whatever it was, with what the run itself noted along the way added to its warning: a cancel
    // callback that failed, a token source that would not dispose, the warnings of a build that finished.
    public OperationEnd? End
    {
        get
        {
            lock (gate)
                return end is null || warning.Length == 0 ? end :
                    end with { Warning = end.Warning.Length == 0 ? warning : end.Warning + " " + warning };
        }
    }

    // The only place a cancel is sent. A settled run is never signalled (its source is gone, and whatever runs next
    // has its own), and a run that was already asked is not asked twice.
    public CancelRequest RequestCancel()
    {
        lock (gate)
        {
            if (done) return CancelRequest.Settled;
            if (requested) return CancelRequest.AlreadyRequested;
            requested = true;
        }
        try { source.Cancel(); }
        catch (ObjectDisposedException) { return CancelRequest.Settled; }   // settled between the check and the call: nothing was signalled
        catch (AggregateException failure)
        {
            // The token is cancelled all the same, but a step that reacts to it failed. That is shown, never swallowed.
            NoteWarning("Отмена отправлена, но шаг, который на неё реагирует, не выполнен: " + ErrorText.Reason(failure));
        }
        return CancelRequest.Requested;
    }

    public void Record(OperationEnd outcome) { lock (gate) end ??= outcome; }

    // Every problem is kept once, in the order it happened.
    public void NoteWarning(string text)
    {
        lock (gate)
            if (!warning.Contains(text, StringComparison.Ordinal)) warning = warning.Length == 0 ? text : warning + " " + text;
    }

    public void Settle()
    {
        lock (gate)
        {
            if (done) return;
            done = true;
            end ??= OperationEnd.Unknown(Kind);
        }
        try { source.Dispose(); }
        catch (Exception error) { NoteWarning("Не удалось освободить токен отмены: " + ErrorText.Reason(error)); }
        finally { settled.TrySetResult(); }
    }

    internal bool TryOpenPrompt() { lock (gate) { if (promptOpen) return false; promptOpen = true; return true; } }

    internal void ClosePrompt() { lock (gate) promptOpen = false; }
}

// The words for cancelling each kind of operation. The question, the wait and the result all read from here, so every
// busy operation is cancelled the same way.
internal sealed record CancelCopy(string Noun, string Question, string Explanation, string Confirm, string Keep,
    string Hint, string Stopping, string Waiting, string Finished, bool CleansUp);

internal static class CancelPrompts
{
    // The caption of the shell button for every busy operation, exactly as the user named it. What it would cancel is
    // in its hint (the tooltip and help text), so the caption is the same everywhere.
    public const string ButtonLabel = "Cancel safely";

    public static CancelCopy For(OperationKind kind) => kind switch
    {
        OperationKind.Build => new("Сборка", "Отменить эту сборку?",
            "The build stops at the next safe point. That can take a few minutes, because the image operation that is running is allowed to finish first. " +
            "Your source is never changed. The mounted copy is safely unmounted; working files, saved progress and logs are kept.",
            "Отменить сборку", "Продолжить сборку", "Останавливает сборку в следующей безопасной точке. Исходник не изменяется.",
            "Отмена сборки", "Ожидание завершения текущей операции", "ISO и отчёт сборки сохранены.", true),
        OperationKind.Resume => new("Возобновление", "Отменить возобновление?",
            "The resume stops at the next safe point. That can take a few minutes, because the image operation that is running is allowed to finish first. " +
            "The mounted copy is safely unmounted; working files, saved progress and logs are kept, so you can resume again.",
            "Отменить возобновление", "Продолжить возобновление", "Останавливает возобновление в следующей безопасной точке. Сохранённый прогресс остаётся.",
            "Отмена возобновления", "Ожидание завершения текущей операции", "ISO и отчёт сборки сохранены.", true),
        OperationKind.Download => new("Загрузка", "Отменить загрузку?",
            "Загрузка остановится. Незавершённый файл сохранится, чтобы продолжить позже. Исходный носитель не изменяется.",
            "Отменить загрузку", "Продолжить загрузку", "Останавливает загрузку. Незавершённый файл сохраняется.",
            "Отмена загрузки", "Остановка загрузки", "Загрузка завершена.", false),
        OperationKind.Inspect => new("Изучение", "Отменить изучение этого образа?",
            "Изучение остановится после текущего шага. Образ не изменяется.",
            "Отменить изучение", "Продолжить", "Останавливает изучение образа. Ничего не изменяется.",
            "Отмена изучения", "Ожидание завершения текущего шага", "Образ изучен.", false),
        OperationKind.Inventory => new("Список компонентов", "Отменить получение списка компонентов?",
            "Получение списка остановится в следующей безопасной точке. Монтирование «только чтение» отменяется, образ не изменяется.",
            "Отменить получение списка", "Продолжить", "Останавливает получение списка компонентов. Образ не изменяется.",
            "Отмена получения списка", "Ожидание завершения текущей операции", "Список компонентов загружен.", true),
        OperationKind.Recover => new("Восстановление", "Отменить восстановление?",
            "Восстановление остановится в следующей безопасной точке. Запустите «Восстановить рабочую область» ещё раз, чтобы завершить очистку этой задачи.",
            "Отменить восстановление", "Продолжить восстановление", "Останавливает восстановление в следующей безопасной точке.",
            "Отмена восстановления", "Ожидание завершения текущей операции", "Рабочая область восстановлена.", false),
        OperationKind.Elevate => new("Перезапуск", "Отменить перезапуск?",
            "Приложение продолжит работать без прав администратора.",
            "Отменить перезапуск", "Продолжить", "Останавливает перезапуск приложения от имени администратора.",
            "Отмена перезапуска", "Ожидание завершения текущего шага", "Приложение перезапущено.", false),
        OperationKind.Check => new("Проверка", "Отменить проверку?",
            "Проверка остановится. Ничего не изменено.",
            "Отменить проверку", "Продолжить", "Останавливает проверку вашего выбора. Ничего не изменяется.",
            "Отмена проверки", "Ожидание завершения текущего шага", "Проверка завершена.", false),
        _ => new("Операция", "Отменить эту операцию?",
            "Операция остановится после текущего шага. Ничего не изменено.",
            "Отмена", "Продолжить", "Останавливает выполняемую операцию.",
            "Отмена", "Ожидание завершения текущего шага", "Операция завершена.", false)
    };
}

// Progress events Core may send while it winds a cancelled run down. Until it does, the wait is one honest state.
internal static class CancelCleanup
{
    public const string Stage = "Очистка после отмены";
    public const string MountOperation = "cancel:mount";
    public const string LogsOperation = "cancel:logs";
}

internal enum CancelStage { Confirming, Cancelling, Ended, Closed }

internal enum CancelPhase { Waiting, DiscardingMount, SavingLogs }

internal enum CancelTone { Neutral, Done, Warning, Error }

internal enum CancelLink { None, OpenLogs, CopyDetails }

// Everything the dialog shows at one moment. Null button text means the button is hidden.
internal sealed record CancelView(CancelStage Stage, CancelTone Tone, string Title, string Body, string Status, string Detail,
    string Footnote, string Warning, bool ShowProgress, bool Indeterminate, double Percent, string? ConfirmText,
    string? CloseText, bool CanClose, CancelLink Link, string LinkText);

// One cancel interaction for one run: asking, stopping, ended. Asking sends nothing; the cancel is sent once, by Confirm,
// and only while the run is still alive. If the run ends first, the question turns into what actually happened.
internal sealed class CancellationFlow
{
    private readonly OperationRun run;
    private readonly CancelCopy copy;
    private CancelPhase phase = CancelPhase.Waiting;
    private OperationEnd? end;
    private bool endedBeforeConfirm;
    private string current = "";
    private double? percent;
    private bool percentFinished;
    private string shown = "";

    private CancellationFlow(OperationRun run)
    {
        this.run = run;
        copy = CancelPrompts.For(run.Kind);
        if (run.IsSettled) Conclude(beforeConfirm: true);
    }

    // One prompt per run: a second request while one is open gets nothing, so a prompt never stacks on a prompt.
    public static CancellationFlow? TryBegin(OperationRun run) => run.TryOpenPrompt() ? new(run) : null;

    public event Action? Changed;

    public CancelStage Stage { get; private set; } = CancelStage.Confirming;

    public OperationRun Run => run;

    // Escape, the close button and closing the app are refused only while the stop is being carried out.
    public bool CanClose => Stage != CancelStage.Cancelling;

    // True only for the call that sent the cancel. The run having ended, or a cancel already sent, sends nothing.
    public bool Confirm()
    {
        if (Stage != CancelStage.Confirming) return false;
        switch (run.RequestCancel())
        {
            case CancelRequest.Settled:
                Conclude(beforeConfirm: true);
                return false;
            case CancelRequest.AlreadyRequested:
                Stage = CancelStage.Cancelling;
                shown = Display();
                Changed?.Invoke();
                return false;
            default:
                Stage = CancelStage.Cancelling;
                shown = Display();
                Changed?.Invoke();
                return true;
        }
    }

    // "Keep building": nothing was sent, and nothing is.
    public void Decline()
    {
        if (Stage == CancelStage.Confirming) Close();
    }

    // The dialog is gone; the run may be prompted again.
    public void Close()
    {
        if (Stage == CancelStage.Closed) return;
        Stage = CancelStage.Closed;
        run.ClosePrompt();
    }

    // The run has settled, cleanup included. Reads what really happened from the run.
    public void RunSettled()
    {
        if (Stage is CancelStage.Ended or CancelStage.Closed) return;
        Conclude(beforeConfirm: Stage == CancelStage.Confirming);
    }

    // Engine progress. The percentage is the current operation's own and is dropped once it reaches 100, because the next
    // percentages (for example the discard of a mount) belong to something that has no name here.
    public void Observe(BuildProgress progress)
    {
        if (Stage is CancelStage.Ended or CancelStage.Closed) return;
        if (progress.Stage == CancelCleanup.Stage)
        {
            ObserveCleanup(progress);
            return;
        }
        if (progress.OperationId is null || phase != CancelPhase.Waiting) return;
        if (progress.State == BuildOperationState.Running)
        {
            current = progress.Message;
            percent = null;
            percentFinished = false;
        }
        else if (progress.State is null && progress.Percent is { } value && double.IsFinite(value) && !percentFinished)
        {
            if (current.Length == 0) current = progress.Message;
            percent = Math.Clamp(value, 0, 100);
            percentFinished = percent >= 100;
        }
        else return;
        if (Stage == CancelStage.Cancelling) RaiseIfDisplayChanged();
    }

    public CancelView View => Stage switch
    {
        CancelStage.Confirming => Asking(),
        CancelStage.Cancelling => Stopping(),
        _ => end is null ? Asking() : Ended(end)
    };

    private void ObserveCleanup(BuildProgress progress)
    {
        if (Stage != CancelStage.Cancelling || !copy.CleansUp || progress.State != BuildOperationState.Running) return;
        var next = progress.OperationId switch
        {
            CancelCleanup.MountOperation => CancelPhase.DiscardingMount,
            CancelCleanup.LogsOperation => CancelPhase.SavingLogs,
            _ => phase
        };
        if (next <= phase) return;
        phase = next;
        RaiseIfDisplayChanged();
    }

    // The dialog is only redrawn when what it says has changed; engine progress arrives far faster than that.
    private void RaiseIfDisplayChanged()
    {
        var now = Display();
        if (now == shown) return;
        shown = now;
        Changed?.Invoke();
    }

    private string Display() => StatusText() + "\n" + DetailText() + "\n" + run.Warning;

    private void Conclude(bool beforeConfirm)
    {
        end = run.End ?? OperationEnd.Unknown(run.Kind);
        endedBeforeConfirm = beforeConfirm;
        Stage = CancelStage.Ended;
        Changed?.Invoke();
    }

    private CancelView Asking() => new(CancelStage.Confirming, CancelTone.Neutral, copy.Question, copy.Explanation, "", "", "", "",
        false, false, 0, copy.Confirm, copy.Keep, true, CancelLink.None, "");

    // A problem the cancel itself ran into (a callback that failed) is shown while the stop is still running.
    private CancelView Stopping() => new(CancelStage.Cancelling, CancelTone.Neutral, copy.Stopping, "", StatusText(), DetailText(),
        copy.CleansUp ? "Инструмент работы с образами никогда не останавливается принудительно, поэтому это может занять несколько минут. Не закрывайте приложение." :
            "Не закрывайте приложение до завершения.",
        run.Warning, true, true, 0, null, null, false, CancelLink.None, "");

    private string StatusText() => phase switch
    {
        CancelPhase.DiscardingMount => "Отмена рабочего монтирования",
        CancelPhase.SavingLogs => "Сохранение журналов",
        _ => percentFinished ? "Завершение текущей операции" : copy.Waiting
    };

    private string DetailText() =>
        phase != CancelPhase.Waiting || current.Length == 0 ? "" :
        percent is { } value && !percentFinished ? $"Текущая операция: {current} · {Math.Floor(value):0}%" : $"Текущая операция: {current}";

    private CancelView Ended(OperationEnd e)
    {
        var tone = e.NeedsAttention ? CancelTone.Error : e.Warning.Length > 0 ? CancelTone.Warning : CancelTone.Done;
        var title = e.Title.TrimEnd('.');
        string heading, body, status;
        if (e.RecoveryRequired)
        {
            heading = title + (e.Result == RunResult.Cancelled ? ", но требуется восстановление" : ", и требуется восстановление");
            (body, status) = (e.Message, "");
        }
        else if (e.Result == RunResult.Succeeded)
        {
            heading = endedBeforeConfirm ? $"{copy.Noun}: отменять уже нечего" : "Завершилось до отмены";
            body = copy.Finished + (endedBeforeConfirm ? " Всё завершилось до отмены, поэтому ничего не отменено." :
                " Отмена пришла слишком поздно, чтобы остановить операцию, поэтому ничего не отменено.");
            status = "Завершено.";
        }
        else
        {
            heading = title;
            body = e.Message;
            status = e.Result == RunResult.Cancelled ? copy.CleansUp ? "Очистка завершена." : "Остановлено." : "";
        }
        var link = tone != CancelTone.Done && e.WorkDirectory is not null ? CancelLink.OpenLogs :
            tone == CancelTone.Error && e.Details.Length > 0 ? CancelLink.CopyDetails : CancelLink.None;
        // A bar is only ever shown complete for a run that ended cleanly; failures and recovery never wear it.
        var complete = tone != CancelTone.Error;
        return new(CancelStage.Ended, tone, heading, body, complete ? status : "", "",
            tone != CancelTone.Done && e.WorkDirectory is { } directory ? "Папка задачи и журналы: " + directory : "",
            e.Warning, complete, false, complete ? 100 : 0, null,
            tone == CancelTone.Error ? "Закрыть" : "Готово", true, link,
            link == CancelLink.OpenLogs ? "Открыть журнал" : link == CancelLink.CopyDetails ? "Копировать детали" : "");
    }
}
