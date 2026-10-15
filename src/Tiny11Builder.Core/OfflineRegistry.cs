using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Tiny11Builder.Core;

public interface IOfflineRegistry
{
    void Apply(string mountDirectory, BuildRecipe recipe, Action<string> log);
    void ApplySetupBypass(string mountDirectory, Action<string> log);
}

[SupportedOSPlatform("windows")]
public sealed class OfflineRegistry : IOfflineRegistry
{
    public void Apply(string mountDirectory, BuildRecipe recipe, Action<string> log)
    {
        ImageOperations.RequireOwnedMount(mountDirectory);
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        using var softwareHive = Load(Path.Combine(mountDirectory, @"Windows\System32\config\SOFTWARE"));
        using var userHive = Load(Path.Combine(mountDirectory, @"Users\Default\NTUSER.DAT"));
        using var systemHive = Load(Path.Combine(mountDirectory, @"Windows\System32\config\SYSTEM"));
        var software = softwareHive.Root;
        var user = userHive.Root;
        var system = systemHive.Root;
        int current;
        using (var select = system.OpenSubKey("Select"))
            current = select?.GetValue("Current") is int c && c is > 0 and < 1000 ? c : 1;
        var controlSet = $"ControlSet{current:D3}";
        foreach (var id in recipe.SelectedOptions)
            foreach (var value in PolicyCatalog.ForOption(id))
                Set(value.Hive switch { PolicyHive.Software => software, PolicyHive.DefaultUser => user, _ => system },
                    value.Key.Replace("{ControlSet}", controlSet, StringComparison.Ordinal), value.Name, value.Value, log);
        if (recipe.PreferLocalAccount)
            Set(software, @"Microsoft\Windows\CurrentVersion\OOBE", "BypassNRO", 1, log);
        if (recipe.Flavor == BuildFlavor.Core)
        {
            foreach (var control in system.GetSubKeyNames().Where(key =>
                         System.Text.RegularExpressions.Regex.IsMatch(key, @"^ControlSet\d{3}$")))
            {
                using var services = OpenWriteKey(system, control + @"\Services");
                var names = services.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "wuauserv", "UsoSvc", "WaaSMedicSvc", "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "Sense" })
                {
                    if (!names.Contains(name)) { log($"Отсутствует в {control}: служба {name}"); continue; }
                    Set(system, $@"{control}\Services\{name}", "Start", 4, log);
                    log($"Core: отключена служба {control}\\{name}");
                }
            }
            Set(software, @"Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1, log);
            Set(software, @"Policies\Microsoft\Windows\WindowsUpdate", "DisableWindowsUpdateAccess", 1, log);
            Set(software, @"Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1, log);
            Set(software, @"Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 1, log);
        }
    }

    public void ApplySetupBypass(string mountDirectory, Action<string> log)
    {
        ImageOperations.RequireOwnedMount(mountDirectory);
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        using var systemHive = Load(Path.Combine(mountDirectory, @"Windows\System32\config\SYSTEM"));
        var system = systemHive.Root;
        foreach (var value in PolicyCatalog.ForOption("hardware-bypass").Where(v => v.Hive == PolicyHive.System))
            Set(system, value.Key, value.Name, value.Value, log);
    }

    private static void Set(RegistryKey root, string path, string name, object value, Action<string> log)
    {
        try
        {
            using var key = OpenWriteKey(root, path);
            var bytes = value is int number ? BitConverter.GetBytes(number) :
                System.Text.Encoding.Unicode.GetBytes((string)value + "\0");
            var type = value is int ? 4 : 1;
            var code = RegSetValueEx(key.Handle, name, 0, type, bytes, bytes.Length);
            if (code != 0) throw new Win32Exception(code, $"Не удалось задать значение автономного реестра {path}\\{name}.");
            if (!Equals(key.GetValue(name), value)) throw new IOException($"Проверка автономного реестра не пройдена: {path}\\{name}");
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UnauthorizedAccessException($"Не удалось записать или проверить значение автономного реестра {root.Name}\\{path}\\{name}.", error);
        }
        log($"Политика записана: {path}\\{name} = {value}");
    }

    internal static OfflineHive Load(string path, bool writable = true) => new(path, writable);

    internal static RegistryKey OpenWriteKey(RegistryKey root, string path)
    {
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        var code = RegCreateKeyEx(root.Handle, path, 0, IntPtr.Zero, 4, 0xF003F, IntPtr.Zero, out var handle, out _);
        if (code != 0) throw new Win32Exception(code, $"Не удалось открыть защищённый ключ автономного реестра: {path}");
        return RegistryKey.FromHandle(handle, RegistryView.Default);
    }

    internal static void DeleteKeyTree(RegistryKey parent, string name)
    {
        using (var child = OpenWriteKey(parent, name))
        {
            var descriptor = new RawSecurityDescriptor("O:BAG:BAD:(A;CI;KA;;;BA)");
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            var code = RegSetKeySecurity(child.Handle, 5, bytes);
            if (code != 0) throw new Win32Exception(code, $"Не удалось получить контроль над защищённым автономным ключом: {name}");
            foreach (var subkey in child.GetSubKeyNames()) DeleteKeyTree(child, subkey);
        }
        parent.DeleteSubKey(name, throwOnMissingSubKey: true);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyEx(SafeRegistryHandle root, string subkey, int reserved,
        IntPtr keyClass, int options, int desired, IntPtr security, out SafeRegistryHandle result, out int disposition);
    [DllImport("advapi32.dll")]
    private static extern int RegSetKeySecurity(SafeRegistryHandle key, int info, byte[] descriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);
}

[SupportedOSPlatform("windows")]
internal sealed class OfflineHive : IDisposable
{
    private static readonly IntPtr LocalMachine = new(unchecked((int)0x80000002));
    private readonly string name = "Tiny11Builder_Offline_" + Guid.NewGuid().ToString("N");
    private readonly string journal;
    private bool unloaded;
    public RegistryKey Root { get; }

    public OfflineHive(string path, bool writable)
    {
        path = PathSafety.Full(path);
        PathSafety.RejectReparsePoints(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) throw new FileNotFoundException("Куст автономного реестра отсутствует.", path);
        var job = Path.GetDirectoryName(path);
        while (job is not null && !File.Exists(Path.Combine(job, "job.json"))) job = Path.GetDirectoryName(job);
        if (job is null || !Path.GetFileName(job).StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(job)[7..], "N", out _))
            throw new InvalidDataException("Автономный куст должен принадлежать задаче образа tiny11.");
        ImageFileAccess.ValidateFile(path);
        journal = Path.Combine(job, name + ".hive.json");
        File.WriteAllText(journal, JsonSerializer.Serialize(new HiveMount(name, path), JsonDefaults.Options));
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        var code = RegLoadKey(LocalMachine, name, path);
        if (code != 0)
        {
            File.Delete(journal);
            throw new Win32Exception(code, $"Не удалось загрузить автономный куст (ошибка {code}): {path}");
        }
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            Root = machine.OpenSubKey(name, writable) ?? throw new IOException("Не удалось открыть временное монтирование автономного куста.");
        }
        catch
        {
            var unload = RegUnLoadKey(LocalMachine, name);
            if (unload != 0) throw new Win32Exception(unload, $"Не удалось выгрузить временный автономный куст {name}.");
            File.Delete(journal);
            throw;
        }
    }

    public void Dispose()
    {
        if (unloaded) return;
        Unload();
        File.Delete(journal);
    }

    private void Unload()
    {
        if (unloaded) return;
        Root.Dispose();
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        var code = RegUnLoadKey(LocalMachine, name);
        if (code != 0) throw new Win32Exception(code, $"Не удалось выгрузить временный автономный куст {name} (ошибка {code}). Требуется восстановление.");
        unloaded = true;
    }

    public static void RecoverJob(string directory)
    {
        using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege", "SeRestorePrivilege");
        foreach (var file in Directory.EnumerateFiles(directory, "Tiny11Builder_Offline_*.hive.json"))
        {
            PathSafety.RejectReparsePoints(file);
            var state = JsonSerializer.Deserialize<HiveMount>(File.ReadAllText(file), JsonDefaults.Options) ??
                throw new InvalidDataException("Недопустимый журнал восстановления автономного куста.");
            const string prefix = "Tiny11Builder_Offline_";
            if (!state.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(state.Name[prefix.Length..], "N", out _) ||
                !PathSafety.IsInside(state.Path, directory) ||
                Path.GetFileName(file) != state.Name + ".hive.json")
                throw new InvalidDataException("Журнал восстановления автономного куста не принадлежит этой задаче.");
            var code = RegUnLoadKey(LocalMachine, state.Name);
            if (code is not (0 or 2 or 3))
                throw new Win32Exception(code, $"Не удалось выгрузить прерванный автономный куст {state.Name}.");
            File.Delete(file);
        }
    }

    private sealed record HiveMount(string Name, string Path);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegLoadKeyW")]
    private static extern int RegLoadKey(IntPtr key, string subkey, string file);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegUnLoadKeyW")]
    private static extern int RegUnLoadKey(IntPtr key, string subkey);
}
