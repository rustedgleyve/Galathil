using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;

internal static class PluginFileAccess
{
    internal const string HiddenSuffix = ".mohidden";

    public static bool TryGetPluginName(string path, out string pluginName)
    {
        pluginName = Path.GetFileName(path);
        if (pluginName.EndsWith(HiddenSuffix, StringComparison.OrdinalIgnoreCase))
            pluginName = pluginName[..^HiddenSuffix.Length];
        return IsPluginName(pluginName);
    }

    public static bool IsPluginName(string pluginName) =>
        Path.GetFileName(pluginName).Equals(pluginName, StringComparison.OrdinalIgnoreCase) &&
        pluginName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        Path.GetExtension(pluginName).ToLowerInvariant() is ".esp" or ".esm" or ".esl";

    public static bool IsHiddenFile(string path) =>
        Path.GetFileName(path).EndsWith(HiddenSuffix, StringComparison.OrdinalIgnoreCase);

    public static string? FindInModFolder(string folder, string pluginName)
    {
        if (!IsPluginName(pluginName)) return null;
        var visiblePath = Path.Combine(folder, pluginName);
        if (File.Exists(visiblePath)) return visiblePath;
        var hiddenPath = visiblePath + HiddenSuffix;
        return File.Exists(hiddenPath) ? hiddenPath : null;
    }

    public static PluginFileLease Open(string pluginName, string path)
    {
        if (!IsPluginName(pluginName))
            throw new InvalidDataException($"'{pluginName}' is not a valid Skyrim plugin filename.");
        if (!IsHiddenFile(path))
            return new PluginFileLease(SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE), null);

        // Mutagen's overlay reader derives the ModKey from the filename. Read a
        // temporary copy with its canonical plugin filename; the MO2 source file
        // stays hidden and the temporary copy is removed when the lease closes.
        var tempFolder = Path.Combine(Path.GetTempPath(), "Galathil", "HiddenPluginReads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        var tempPath = Path.Combine(tempFolder, pluginName);
        try
        {
            File.Copy(path, tempPath);
            return new PluginFileLease(SkyrimMod.CreateFromBinaryOverlay(tempPath, SkyrimRelease.SkyrimSE), tempFolder);
        }
        catch
        {
            try { Directory.Delete(tempFolder, recursive: true); } catch { }
            throw;
        }
    }
}

internal sealed class PluginFileLease : IDisposable
{
    private readonly string? _temporaryFolder;

    public ISkyrimModDisposableGetter Mod { get; }

    public PluginFileLease(ISkyrimModDisposableGetter mod, string? temporaryFolder)
    {
        Mod = mod;
        _temporaryFolder = temporaryFolder;
    }

    public void Dispose()
    {
        try { Mod.Dispose(); }
        finally
        {
            if (_temporaryFolder is not null)
            {
                try { Directory.Delete(_temporaryFolder, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
