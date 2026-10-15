namespace Tiny11Builder.Core;

public static class CleanupCatalog
{
    public static IReadOnlyList<CleanupOption> Options { get; } =
    [
        App("copilot-app", "Приложения Copilot", "Удалить предустановленные приложения Copilot. Общие среды выполнения и компоненты оболочки сохраняются.", true, true,
            "Microsoft.Copilot", "Microsoft.Windows.Copilot", "Microsoft.549981C3F5F10"),
        App("outlook", "Новые Outlook и Почта", "Удалить Outlook и устаревшие приложения «Почта» и «Календарь».", true, true,
            "Microsoft.OutlookForWindows", "microsoft.windowscommunicationsapps"),
        App("teams", "Teams", "Удалить личные клиенты Teams.", true, true, "MSTeams", "MicrosoftTeams", "Microsoft.Windows.Teams"),
        App("xbox", "Xbox и игровые оверлеи", "Удалить приложения Xbox и Game Bar. Игровой вход и запись могут стать недоступны.", true, true,
            "Microsoft.GamingApp", "Microsoft.XboxApp", "Microsoft.Xbox.TCUI", "Microsoft.XboxGameOverlay",
            "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay"),
        App("news-weather", "Приложения новостей, погоды и Bing", "Удалить приложения новостных лент; локальный поиск Windows сохраняется.", true, true,
            "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch"),
        App("office-promos", "Реклама Microsoft 365", "Удалить Office Hub и рекламные приложения push-уведомлений Office.", true, true,
            "Microsoft.MicrosoftOfficeHub", "Microsoft.OfficePushNotificationUtility"),
        App("clipchamp", "Clipchamp", "Удалить встроенный видеоредактор.", true, true, "Clipchamp.Clipchamp"),
        App("solitaire", "Solitaire", "Удалить встроенные игры.", true, true, "Microsoft.MicrosoftSolitaireCollection"),
        App("phone", "Phone Link и кросс-устройства", "Удалить приложения интеграции с телефоном. Кросс-платформенные функции могут перестать работать.", true, true,
            "Microsoft.YourPhone", "Microsoft.Windows.CrossDevice"),
        App("feedback", "Отзывы, советы и справка", "Удалить Feedback Hub, «Советы» и ознакомительные приложения.", true, true,
            "Microsoft.WindowsFeedbackHub", "Microsoft.GetHelp", "Microsoft.Getstarted"),
        App("devhome", "Dev Home и Power Automate", "Удалить Dev Home и Power Automate Desktop.", true, true,
            "Microsoft.Windows.DevHome", "Microsoft.PowerAutomateDesktop"),
        App("social", "People, To Do, Family и Skype", "Удалить необязательные приложения-органайзеры и средства связи.", true, true,
            "Microsoft.People", "Microsoft.Todos", "Microsoft.SkypeApp", "MicrosoftCorporationII.MicrosoftFamily"),
        App("maps", "Карты и Mixed Reality", "Удалить устаревшие клиенты «Карты» и Mixed Reality.", true, true,
            "Microsoft.WindowsMaps", "Microsoft.MixedReality.Portal"),
        App("media", "Media Player и «Фильмы»", "Удалить встроенные приложения для воспроизведения; кодеки и общие платформы остаются.", false, true,
            "Microsoft.ZuneMusic", "Microsoft.ZuneVideo"),
        App("utilities", "Часы, Recorder и Записки", "Удалить необязательные личные утилиты.", false, true,
            "Microsoft.WindowsAlarms", "Microsoft.WindowsSoundRecorder", "Microsoft.MicrosoftStickyNotes"),
        App("quickassist", "Быстрая помощь", "Удалить клиент удалённой помощи Microsoft.", false, true, "MicrosoftCorporationII.QuickAssist"),
        App("paint", "Paint, 3D Viewer и OneNote", "Удалить Paint, 3D Viewer и OneNote. Оставьте неотмеченным, чтобы сохранить их.", false, false,
            "Microsoft.Paint", "Microsoft.MSPaint", "Microsoft.Microsoft3DViewer", "Microsoft.Office.OneNote"),
        App("camera", "Камера", "Удалить приложение «Камера», но не драйверы камер.", false, false, "Microsoft.WindowsCamera"),
        App("terminal", "Windows Terminal", "Удалить Terminal. PowerShell и командная строка остаются.", false, false, "Microsoft.WindowsTerminal"),
        App("oem", "Рекламные приложения OEM", "Удалить статусные и рекламные приложения Intel и Dolby, но не драйверы.", false, false,
            "AppUp.IntelManagementandSecurityStatus", "DolbyLaboratories.DolbyAccess", "DolbyLaboratories.DolbyDigitalPlusDecoderOEM"),
        new("edge-files", "Удалить Edge и WebView2", "Удалить файлы Edge и WebView2. Приложения, которым нужен WebView2, могут перестать работать. Обновления могут переустановить Edge.",
            "Расширенные / оригинальный сборщик", OptionKind.File, false, false, []),
        new("onedrive-files", "Удалить установщики OneDrive", "Удалить оба установщика OneDrive (для каждой архитектуры) из автономного образа.",
            "Расширенные / оригинальный сборщик", OptionKind.File, false, false, []),
        new("language-extras", "Компоненты речи, OCR и рукописного ввода", "Удалить необязательные языковые компоненты для всех языков образа; базовая поддержка языков сохраняется.",
            "Дополнительные компоненты", OptionKind.Capability, false, false,
            ["Language.Handwriting", "Language.OCR", "Language.Speech", "Language.TextToSpeech"]),
        new("legacy-capabilities", "Устаревшие аксессуары и обои", "Удалить WordPad, «Запись действий», Math Recognizer, старый Media Player и расширенные обои, если они установлены.",
            "Дополнительные компоненты", OptionKind.Capability, false, true,
            ["Microsoft.Windows.WordPad", "App.StepsRecorder", "MathRecognizer", "Media.WindowsMediaPlayer", "Microsoft.Windows.Wallpaper.Extended"]),
        Feature("xps", "Печать XPS", "Отключить необязательную печать XPS и удалить её компоненты.", false, true, "Printing-XPSServices-Features"),
        Feature("workfolders", "Рабочие папки", "Отключить корпоративную синхронизацию «Рабочие папки» и удалить её компоненты.", false, true, "WorkFolders-Client"),
        Feature("legacy", "Устаревший Internet Explorer", "Отключить устаревший дополнительный компонент Internet Explorer, если он присутствует.", true, true,
            "Internet-Explorer-Optional-amd64", "Internet-Explorer-Optional-arm64"),
        Feature("recall-feature", "Дополнительный компонент Recall", "Отключить и удалить компоненты Recall, только если этот образ предоставляет их DISM.", true, true, "Recall"),
        Policy("ads", "Реклама, рекомендации и спонсорские приложения", "Подавить потребительский контент, подсказки в параметрах, рекламу в проводнике и приветственные советы.", true, true),
        Policy("privacy", "Необязательная диагностика и персонализация", "Сохранить обязательные диагностические данные. Отключить рекламный идентификатор, персональные рекомендации и сбор данных ввода.", true, true),
        Policy("copilot", "Отключить Copilot и Recall", "Применить политики отключения Copilot и Recall. Поддержка зависит от сборки и редакции.", true, true),
        Policy("widgets", "Виджеты и ленты на панели задач", "Отключить виджеты политикой и скрыть их значок на панели задач.", true, true),
        Policy("search", "Веб-поиск и подсветка результатов", "Отключить веб-подсказки, облачный поиск и динамическую подсветку результатов; локальный поиск сохраняется.", true, true),
        Policy("onedrive", "Синхронизация OneDrive и предложения резервного копирования", "Отключить синхронизацию OneDrive политикой. Файлы OneDrive сохраняются; используйте, только если синхронизация не нужна.", true, true),
        Policy("edge", "Первый запуск Edge и рекламный интерфейс", "Подавить первый запуск, боковую панель и рекомендации. Edge и WebView2 сохраняются для совместимости приложений.", true, true),
        Policy("oobe", "Приватность при установке и напоминания о настройке", "Подавить настройку конфиденциальности и предложения настройки после входа, без создания учётных записей и принятия EULA.", true, true),
        Policy("cleanup", "Очистка компонентов", "Обычный режим сохраняет обслуживание. Core дополнительно сбрасывает базу обновлений перед необратимым усечением компонентов.", true, true),
        Policy("hardware-bypass", "Обойти требования к оборудованию при установке", "Установить значения обхода требований к ЦП, ОЗУ, TPM, хранилищу и Secure Boot в установщике и Windows. Неподдерживаемое оборудование может не работать.", false, false),
        Policy("reserved-storage", "Отключить зарезервированное хранилище", "Не резервировать место на диске для обновлений. Это может уменьшить свободное место при обслуживании.", false, false),
        Policy("device-encryption", "Отключить автоматическое шифрование устройства", "Предотвратить автоматическое шифрование устройства BitLocker; не используйте, если шифрование требуется по умолчанию.", false, false)
    ];

    private static CleanupOption App(string id, string title, string description, bool balanced, bool minimal, params string[] targets)
        => new(id, title, description, "Встроенные приложения", OptionKind.App, balanced, minimal, targets);
    private static CleanupOption Feature(string id, string title, string description, bool balanced, bool minimal, params string[] targets)
        => new(id, title, description, "Дополнительные компоненты", OptionKind.Feature, balanced, minimal, targets);
    private static CleanupOption Policy(string id, string title, string description, bool balanced, bool minimal)
        => new(id, title, description, "Параметры Windows", OptionKind.Policy, balanced, minimal, []);

    public static string[] ForPreset(CleanupPreset preset) => Options
        .Where(o => preset switch
        {
            CleanupPreset.Balanced => o.Balanced,
            CleanupPreset.Minimal => o.Minimal,
            CleanupPreset.Original => o.Kind == OptionKind.App || o.Id is not ("language-extras" or "legacy-capabilities"),
            _ => false
        })
        .Select(o => o.Id).ToArray();

    public static string[] ForMode(BuildMode mode) => mode switch
    {
        BuildMode.Tiny11 => ForPreset(CleanupPreset.Original),
        BuildMode.Tiny11Core => Options.Where(o => o.Minimal || o.Id is
            "language-extras" or "edge-files" or "onedrive-files" or
            "hardware-bypass" or "reserved-storage" or "device-encryption")
            .Select(o => o.Id).ToArray(),
        BuildMode.Custom => [],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static BuildMode ModeForRecipe(BuildRecipe recipe)
    {
        if (recipe.Mode is { } mode) return mode;
        var preset = recipe.Flavor == BuildFlavor.Core ? BuildMode.Tiny11Core : BuildMode.Tiny11;
        return recipe.ExpertActions.Length == 0 &&
            recipe.SelectedOptions.ToHashSet(StringComparer.Ordinal).SetEquals(ForMode(preset))
            ? preset : BuildMode.Custom;
    }

    public static bool MatchesApp(CleanupOption option, ProvisionedApp app) =>
        option.Kind == OptionKind.App && option.Targets.Any(target =>
            app.DisplayName.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            app.PackageName.StartsWith(target + "_", StringComparison.OrdinalIgnoreCase));
}
