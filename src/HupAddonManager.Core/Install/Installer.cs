using System.IO.Compression;
using HupAddonManager.Core.Providers;
using HupAddonManager.Core.Storage;

namespace HupAddonManager.Core.Install;

public static class Installer
{
    public const string WorkDirName = "_hup_tmp";

    public static async Task DownloadAsync(string url, IDictionary<string, string>? headers, string destination,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers != null)
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);

        using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await Http.EnsureOk(resp, url, ct).ConfigureAwait(false);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("The download link returned a web page instead of a zip file.");

        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var output = File.Create(destination))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        try { using var _ = ZipFile.OpenRead(destination); }
        catch (InvalidDataException)
        {
            File.Delete(destination);
            throw new ProviderException("The downloaded file is not a valid zip archive.");
        }
    }

    public static List<ZipAddonFolder> Inspect(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return ZipLayout.Analyze(zip.Entries.Select(e => e.FullName));
    }

    public static List<string> InstallZip(string zipPath, string addOnsDir, IEnumerable<string> oldFolders)
    {
        Directory.CreateDirectory(addOnsDir);
        var interfaceDir = Path.GetDirectoryName(Path.GetFullPath(addOnsDir).TrimEnd('\\'))!;
        var work = Path.Combine(interfaceDir, WorkDirName, Guid.NewGuid().ToString("N")[..8]);
        var newDir = Path.Combine(work, "new");
        var oldDir = Path.Combine(work, "old");
        Directory.CreateDirectory(newDir);
        Directory.CreateDirectory(oldDir);

        try
        {
            List<ZipAddonFolder> layout;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                layout = ZipLayout.Analyze(zip.Entries.Select(e => e.FullName));
                if (layout.Count == 0)
                    throw new InvalidDataException("No addon found in the download (it contains no .toc file).");
                Extract(zip, layout, newDir);
            }

            var targets = layout.Select(l => l.TargetName).ToList();
            var extra = oldFolders.Where(f => !targets.Contains(f, StringComparer.OrdinalIgnoreCase))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Swap(addOnsDir, newDir, oldDir, targets, extra);
            return targets;
        }
        finally
        {
            DeleteDirectory(work);
            TryDeleteEmpty(Path.Combine(interfaceDir, WorkDirName));
        }
    }

    public static void RemoveFolders(string addOnsDir, IEnumerable<string> folders)
    {
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f) || f.Contains("..") || f.Contains('/') || f.Contains('\\')) continue;
            DeleteDirectory(Path.Combine(addOnsDir, f));
        }
    }

    public static void CleanupWorkDir(string addOnsDir)
    {
        try
        {
            var interfaceDir = Path.GetDirectoryName(Path.GetFullPath(addOnsDir).TrimEnd('\\'))!;
            var work = Path.Combine(interfaceDir, WorkDirName);
            if (!Directory.Exists(work)) return;
            foreach (var job in Directory.GetDirectories(work))
            {
                var old = Path.Combine(job, "old");
                if (!Directory.Exists(old)) continue;
                foreach (var d in Directory.GetDirectories(old))
                {
                    var target = Path.Combine(addOnsDir, Path.GetFileName(d));
                    if (!Directory.Exists(target)) MoveWithRetry(d, target);
                }
            }
            DeleteDirectory(work);
        }
        catch (Exception ex) { Log.Warn("Cleanup of temporary install folder failed: " + ex.Message); }
    }

    private static void Extract(ZipArchive zip, List<ZipAddonFolder> layout, string newDir)
    {
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (ZipLayout.IsJunk(path)) continue;
            foreach (var folder in layout)
            {
                string rel;
                if (folder.ZipPrefix.Length == 0) rel = path;
                else if (path.StartsWith(folder.ZipPrefix + "/", StringComparison.OrdinalIgnoreCase)) rel = path[(folder.ZipPrefix.Length + 1)..];
                else continue;

                var targetRoot = Path.GetFullPath(Path.Combine(newDir, folder.TargetName));
                if (rel.Length == 0 || rel.EndsWith('/'))
                {
                    Directory.CreateDirectory(Path.Combine(targetRoot, rel));
                    break;
                }
                var dest = Path.GetFullPath(Path.Combine(targetRoot, rel));
                if (!dest.StartsWith(targetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The zip contains an unsafe path ({path}).");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                break;
            }
        }
    }

    private static void Swap(string addOnsDir, string newDir, string oldDir, List<string> targets, List<string> extra)
    {
        var moved = new List<string>();
        var placed = new List<string>();
        try
        {
            foreach (var name in targets.Concat(extra))
            {
                var current = Path.Combine(addOnsDir, name);
                if (!Directory.Exists(current)) continue;
                MoveWithRetry(current, Path.Combine(oldDir, name));
                moved.Add(name);
            }
            foreach (var name in targets)
            {
                MoveWithRetry(Path.Combine(newDir, name), Path.Combine(addOnsDir, name));
                placed.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Install failed, restoring previous files", ex);
            foreach (var name in placed) DeleteDirectory(Path.Combine(addOnsDir, name));
            foreach (var name in moved)
            {
                try { MoveWithRetry(Path.Combine(oldDir, name), Path.Combine(addOnsDir, name)); }
                catch (Exception e2) { Log.Error($"Could not restore {name}", e2); }
            }
            if (ex is IOException or UnauthorizedAccessException)
                throw new IOException("A file in the addon folder is in use (is it open in Explorer or an editor?). Nothing was changed.", ex);
            throw;
        }
    }

    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception) when (attempt < 5) { Thread.Sleep(250 * attempt); }
        }
    }

    public static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(dir, true);
                return;
            }
            catch (Exception) when (attempt < 4) { Thread.Sleep(250 * attempt); }
            catch (Exception ex) { Log.Warn($"Could not delete {dir}: {ex.Message}"); return; }
        }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }
}
