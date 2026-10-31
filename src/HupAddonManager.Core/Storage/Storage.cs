using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HupAddonManager.Core.Storage;

public static class AppInfo
{
    public const string Name = "hup's AddOn Manager";
    public const string ShortName = "HupAddonManager";
    public const string Repo = "huptiq/wow-addon-manager";
    public const string RepoUrl = "https://github.com/" + Repo;

    public static Version Version =>
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}

public static class AppPaths
{
    private static string? _dataDir;

    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;
            var exeDir = AppContext.BaseDirectory;
            var portableMarker = Path.Combine(exeDir, "portable.txt");
            var overrideDir = Environment.GetEnvironmentVariable("HUPADDON_DATA");
            _dataDir = !string.IsNullOrWhiteSpace(overrideDir) ? overrideDir
                : File.Exists(portableMarker)
                ? Path.Combine(exeDir, "HupAddonManagerData")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.ShortName);
            Directory.CreateDirectory(_dataDir);
            return _dataDir;
        }
    }

    internal static void OverrideDataDir(string dir) { _dataDir = dir; Directory.CreateDirectory(dir); }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DatabaseFile => Path.Combine(DataDir, "addons.json");
    public static string LogsDir => Ensure(Path.Combine(DataDir, "logs"));
    public static string BackupsDir => Ensure(Path.Combine(DataDir, "backups"));
    public static string TempDir => Ensure(Path.Combine(Path.GetTempPath(), AppInfo.ShortName));

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {Path.GetFileName(path)}; a copy was kept as .broken", ex);
            try { File.Copy(path, path + ".broken", true); } catch { }
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}

public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("hup-addon-manager");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static event Action<string>? Line;

    public static string TodayFile => Path.Combine(AppPaths.LogsDir, $"{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            try { File.AppendAllText(TodayFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
        try { Line?.Invoke(line); } catch { }
    }

    public static void Prune()
    {
        try
        {
            foreach (var f in Directory.GetFiles(AppPaths.LogsDir, "*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }
}
