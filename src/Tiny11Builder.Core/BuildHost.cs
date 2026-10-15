using System.Runtime.Versioning;

namespace Tiny11Builder.Core;

public interface IBuildHost
{
    void ValidateTools(BuildRecipe recipe);
    void ValidateImage(BuildRecipe recipe, WindowsImage image);

    /// <summary>
    /// Checks a host before a saved build is resumed. The default keeps older hosts working; <see cref="BuildHost"/>
    /// overrides it so a resume is not held to the free-space bar of a brand-new build.
    /// </summary>
    void ValidateResume(BuildRecipe recipe, WindowsImage image, long requiredWorkspaceBytes) => ValidateImage(recipe, image);
}

[SupportedOSPlatform("windows")]
public sealed class BuildHost : IBuildHost
{
    public void ValidateTools(BuildRecipe recipe)
    {
        if (!ToolLocator.IsAdministrator) throw new UnauthorizedAccessException("Обслуживание образов требует прав администратора.");
        ToolLocator.Validate(recipe.DismPath, "dism.exe");
        if (!string.IsNullOrEmpty(recipe.OscdimgPath)) ToolLocator.Validate(recipe.OscdimgPath, "oscdimg.exe");
    }

    public void ValidateImage(BuildRecipe recipe, WindowsImage image)
    {
        image.Validate();
        ToolLocator.EnsureServicingVersion(recipe.DismPath, image);
        var drive = new DriveInfo(Path.GetPathRoot(recipe.WorkspacePath)!);
        RequireNtfs(drive);
        var required = Math.Max(40L * 1024 * 1024 * 1024, image.Size * 3 + 10L * 1024 * 1024 * 1024);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"Рабочей области нужно минимум {required / (1024 * 1024 * 1024)} ГиБ свободно для этого образа.");
        ValidateOutputDrive(recipe, drive);
    }

    public void ValidateResume(BuildRecipe recipe, WindowsImage image, long requiredWorkspaceBytes)
    {
        image.Validate();
        ToolLocator.EnsureServicingVersion(recipe.DismPath, image);
        var drive = new DriveInfo(Path.GetPathRoot(recipe.WorkspacePath)!);
        RequireNtfs(drive);
        if (drive.AvailableFreeSpace < requiredWorkspaceBytes)
            throw new IOException($"Рабочей области нужно минимум {(requiredWorkspaceBytes + (1L << 30) - 1) >> 30} ГиБ свободного места для завершения сборки.");
        ValidateOutputDrive(recipe, drive);
    }

    private static void RequireNtfs(DriveInfo drive)
    {
        if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Рабочая область должна быть на NTFS. Монтирование WIM на ReFS, FAT32 и сетевых дисках не поддерживается.");
    }

    private static void ValidateOutputDrive(BuildRecipe recipe, DriveInfo workspace)
    {
        if (string.IsNullOrEmpty(recipe.OutputPath)) return;
        var output = new DriveInfo(Path.GetPathRoot(recipe.OutputPath)!);
        if (output.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Диск результата не может быть FAT32; ISO-образы Windows обычно превышают его лимит 4 ГиБ.");
        if (!output.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase) &&
            output.AvailableFreeSpace < 12L * 1024 * 1024 * 1024)
            throw new IOException("На диске результата нужно минимум 12 ГиБ свободно.");
    }
}
