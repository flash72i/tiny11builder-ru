namespace Tiny11Builder.Core;

public static class ImageDefaults
{
    public static int SelectPosition(IReadOnlyList<WindowsImage> images, int? restoredIndex = null)
    {
        if (images.Count == 0) throw new InvalidDataException("Источник не содержит редакций Windows.");
        if (restoredIndex is int saved)
            for (var i = 0; i < images.Count; i++)
                if (images[i].Index == saved) return i;
        for (var i = 0; i < images.Count; i++)
            if (images[i].Edition.Equals("Professional", StringComparison.OrdinalIgnoreCase) ||
                images[i].Name.Equals("Windows 11 Pro", StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < images.Count; i++) if (images[i].Index == 1) return i;
        throw new InvalidDataException("В этом источнике нет ни Windows 11 Pro, ни образа с индексом 1.");
    }
}
