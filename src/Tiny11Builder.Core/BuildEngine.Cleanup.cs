namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    /// <summary>CBS_E_PENDING: DISM will not clean the component store while operations are pending (https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options).</summary>
    internal const int PendingOperationsExitCode = unchecked((int)0x800F0806);

    private const string PendingSkipReason = "DISM reported pending operations (0x800F0806). They stay pending until first boot and nothing was reverted. " +
        "Superseded components remain, so the image may be larger.";

    private static bool RemovesComponentPayloads(BuildRecipe recipe) => SetupInstaller.RequiresLegacy(recipe);

    private static async Task CleanComponentsAsync(BuildSession s, CancellationToken token)
    {
        try
        {
            await s.Dism.RunAsync(token, s.Recipe.Flavor == BuildFlavor.Core ?
                [$"/Image:{s.Mount}", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"] :
                [$"/Image:{s.Mount}", "/Cleanup-Image", "/StartComponentCleanup"]).ConfigureAwait(false);
        }
        catch (DismCommandException error) when (error.ExitCode == PendingOperationsExitCode)
        {
            token.ThrowIfCancellationRequested();
            if (RemovesComponentPayloads(s.Recipe))
                throw Refuse(s, [$"Код выхода DISM {error.ExitCode} (0x{error.ExitCode:X8}) при очистке компонентов"], error);
            s.CleanupDeferredForPending = true;
            s.Warn("Component cleanup was skipped: " + PendingSkipReason);
            s.Tracking.SkipStage(PendingSkipReason);
        }
    }

    private static IReadOnlyList<string> OriginalFileOptions(BuildSession s)
    {
        var options = s.Recipe.SelectedOptions;
        if (!s.CleanupDeferredForPending || !options.Contains("edge-files")) return options;
        s.Tracking.Skip("retained:edge-files", "Особые компоненты", CleanupCatalog.Options.Single(option => option.Id == "edge-files").Title,
            "Сохранено: ожидающие операции обслуживания могут ссылаться на эти файлы компонентов");
        s.Warn("Edge and WebView2 removal was selected but not applied: pending operations (0x800F0806) may reference their component files, " +
            "so all Edge files were kept.");
        return options.Where(option => option != "edge-files").ToArray();
    }

    private static async Task EnsureNoPendingOperationsAsync(BuildSession s, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Проверка автономного образа на ожидающие операции требует Windows.");
        s.Log("Проверка образа на ожидающие операции перед удалением файлов компонентов.");
        var scan = await PendingOperations.ScanAsync(s.Dism, s.Mount, token).ConfigureAwait(false);
        if (scan.Pending.Count > 0) throw Refuse(s, scan.Pending, null);
        s.Log($"Ожидающих операций не найдено ({scan.Packages} пакетов и {scan.Features} компонентов проверено).");
        token.ThrowIfCancellationRequested();
    }

    private static PendingOperationsException Refuse(BuildSession s, IReadOnlyList<string> evidence, Exception? inner)
    {
        foreach (var item in evidence) s.Log("Ожидающая операция: " + item);
        return new(PendingOperations.Refusal(s.Recipe), evidence, inner);
    }
}

internal sealed class PendingOperationsException(string message, IReadOnlyList<string> evidence, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public IReadOnlyList<string> Evidence { get; } = evidence;
}
