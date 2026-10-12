using System.Runtime.Versioning;
using Microsoft.Win32;
using FontA.Core.Fonts;

namespace FontA.Core.Activation;

/// <summary>Where an installed font lives and the registry value that registers it.</summary>
public sealed record InstallRecord(string DestPath, string ValueName);

/// <summary>
/// Installs fonts for the current user only, the same way Windows' own "Install for me" does:
/// the file is copied to %LOCALAPPDATA%\Microsoft\Windows\Fonts and a value pointing at the copy is added under
/// HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts. No administrator rights are needed, and the font
/// then survives sign-out and restart without FontA running. Requires Windows 10 1809 or later.
/// The registry key and folder can be overridden so tests never touch the real ones.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FontInstaller
{
    public const string DefaultRegistryKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    private readonly string _registryKey;
    private readonly string _fontsDir;
    private readonly bool _notifySystem;

    public FontInstaller(string? registryKey = null, string? fontsDir = null, bool notifySystem = true)
    {
        _registryKey = registryKey ?? DefaultRegistryKey;
        _fontsDir = fontsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        _notifySystem = notifySystem;
    }

    public InstallRecord Install(FontInfo font)
    {
        var source = font.Path;
        if (!File.Exists(source)) throw new FileNotFoundException("Font file not found.", source);

        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is not (".ttf" or ".otf" or ".ttc" or ".otc"))
            throw new InvalidOperationException($"Unsupported font file type '{ext}'.");

        // Collections hold several faces under one file; register them under the file's own name.
        bool collection = ext is ".ttc" or ".otc";
        var valueName = SafeValueName(collection ? Path.GetFileNameWithoutExtension(source) : font.FullName)
                        + (ext is ".otf" or ".otc" ? " (OpenType)" : " (TrueType)");

        Directory.CreateDirectory(_fontsDir);
        var dest = ChooseDestination(source);
        if (!IsOurFile(dest)) throw new InvalidOperationException("Refusing to install outside the fonts folder.");

        using var key = Registry.CurrentUser.CreateSubKey(_registryKey, writable: true);
        if (key.GetValue(valueName) is string existing && !SamePath(existing, dest))
            throw new InvalidOperationException($"A font named '{valueName}' is already installed from a different file.");

        if (!File.Exists(dest)) File.Copy(source, dest);
        key.SetValue(valueName, dest, RegistryValueKind.String);

        if (_notifySystem)
        {
            // Makes it usable in this session immediately; at the next sign-in Windows loads it from the registry.
            FontNative.AddFontResourceEx(dest, 0, IntPtr.Zero);
            FontNative.BroadcastFontChange();
        }
        return new InstallRecord(dest, valueName);
    }

    /// <summary>Removes the registration and the copied file. Returns false if the file couldn't be deleted (still in use).</summary>
    public bool Uninstall(InstallRecord record)
    {
        // The record comes from FontA's database. Never act on a path that isn't a font file directly inside the fonts
        // folder, so a damaged or tampered database can't make an uninstall delete or unregister anything else.
        if (!IsOurFile(record.DestPath)) return false;

        if (_notifySystem) FontNative.RemoveFontResourceEx(record.DestPath, 0, IntPtr.Zero);

        using (var key = Registry.CurrentUser.OpenSubKey(_registryKey, writable: true))
            if (key?.GetValue(record.ValueName) is string current && SamePath(current, record.DestPath))
                key.DeleteValue(record.ValueName, throwOnMissingValue: false);

        if (_notifySystem) FontNative.BroadcastFontChange();

        try
        {
            if (File.Exists(record.DestPath)) File.Delete(record.DestPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // Windows keeps a font file open until apps using it close; it is no longer registered.
        }
    }

    /// <summary>True only for a font file that sits directly in the fonts folder (installs are flat, never in subfolders).</summary>
    private bool IsOurFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(_fontsDir).TrimEnd('\\', '/');
            return string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)
                   && (Path.GetExtension(full).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// Font names come from inside the font file, so treat them as untrusted: drop control characters, collapse
    /// whitespace and keep it to a sensible length (registry value names are limited, and a huge or odd name helps no one).
    /// </summary>
    internal static string SafeValueName(string name)
    {
        var cleaned = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length > 200) cleaned = cleaned[..200].TrimEnd();
        return cleaned.Length == 0 ? "Font" : cleaned;
    }

    /// <summary>Reuses an identical existing copy, otherwise picks a free name so nothing is overwritten.</summary>
    private string ChooseDestination(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        var ext = Path.GetExtension(source);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(_fontsDir, i == 1 ? name + ext : $"{name} ({i}){ext}");
            if (!File.Exists(candidate) || SameContent(source, candidate)) return candidate;
        }
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
