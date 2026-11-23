using System.Collections.Concurrent;
using System.IO;

namespace taisan11.TaskManager;

internal enum PluginGroupKind
{
    Plugin,
    Host,
    Runtime,
    Native,
}

internal readonly record struct PluginGroup(string Name, PluginGroupKind Kind);

internal sealed class PluginCatalog
{
    public static readonly PluginGroup Host = new("YMM4本体", PluginGroupKind.Host);
    public static readonly PluginGroup Runtime = new(".NET ランタイム・ライブラリ", PluginGroupKind.Runtime);
    public static readonly PluginGroup Native = new("ネイティブ・待機中", PluginGroupKind.Native);

    private readonly ConcurrentDictionary<string, PluginGroup> cache = new(StringComparer.OrdinalIgnoreCase);

    public string PluginRoot { get; }

    public PluginCatalog()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "user", "plugin");
        if (!Directory.Exists(root))
        {
            var self = Path.GetDirectoryName(typeof(PluginCatalog).Assembly.Location);
            root = string.IsNullOrEmpty(self) ? root : Path.GetFullPath(Path.Combine(self, ".."));
        }
        PluginRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    }

    public IEnumerable<PluginGroup> GetInstalledPlugins()
    {
        if (!Directory.Exists(PluginRoot))
            return [];
        return Directory.EnumerateDirectories(PluginRoot)
            .Select(x => new PluginGroup(Path.GetFileName(x), PluginGroupKind.Plugin));
    }

    public PluginGroup Classify(string? modulePath)
    {
        if (string.IsNullOrEmpty(modulePath))
            return Runtime;
        return cache.GetOrAdd(modulePath, path =>
        {
            if (path.StartsWith(PluginRoot, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[PluginRoot.Length..];
                var separator = rest.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                if (separator > 0)
                    return new PluginGroup(rest[..separator], PluginGroupKind.Plugin);
            }
            return Path.GetFileName(path).StartsWith("YukkuriMovieMaker", StringComparison.OrdinalIgnoreCase) ? Host : Runtime;
        });
    }

    public Dictionary<PluginGroup, int> CountLoadedAssemblies()
    {
        var result = new Dictionary<PluginGroup, int>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;
            var group = Classify(assembly.Location);
            result[group] = result.GetValueOrDefault(group) + 1;
        }
        return result;
    }
}
