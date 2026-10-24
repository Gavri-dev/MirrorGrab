using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace MirrorGrab.Services;

public static class ArchiveService
{
    private static readonly string[] ArchiveExts =
        [".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz"];

    private static readonly string[] CompressedTarExts =
        [".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz"];

    private static readonly Regex MultipartRe =
        new(@"\.(r\d{2,3}|7z\.\d{3}|zip\.\d{3})$", RegexOptions.IgnoreCase);

    private static readonly Regex RarPartRe =
        new(@"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);

    public static bool IsArchive(string filepath)
    {
        var lower = filepath.ToLower();
        return ArchiveExts.Any(ext => lower.EndsWith(ext)) || MultipartRe.IsMatch(lower);
    }

    private static bool IsCompressedTar(string filepath) =>
        CompressedTarExts.Any(ext => filepath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static List<(string First, List<string> Parts)> GroupArchives(IEnumerable<string> filepaths)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fp in filepaths)
        {
            var name = Path.GetFileName(fp);
            string key;

            // Modern RAR: name.part01.rar → group by base
            var rarMatch = RarPartRe.Match(name);
            if (rarMatch.Success)
            {
                key = RarPartRe.Replace(name, "");
            }
            // Multipart split: name.7z.001
            else if (MultipartRe.IsMatch(name))
            {
                key = MultipartRe.Replace(name, "");
            }
            else
            {
                key = name;
            }

            if (!groups.ContainsKey(key))
                groups[key] = [];
            groups[key].Add(fp);
        }

        var result = new List<(string, List<string>)>();
        foreach (var (_, parts) in groups)
        {
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            result.Add((parts[0], parts));
        }

        return result;
    }

    public static string Extract(string filepath, string? destDir = null)
    {
        destDir ??= StripAllExt(filepath);
        Directory.CreateDirectory(destDir);

        try
        {
            var options = new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            };

            // Compressed tarballs only open through the streaming reader;
            // ArchiveFactory can't see past the outer gzip/bzip2/xz layer.
            if (IsCompressedTar(filepath))
            {
                using var reader = ReaderFactory.OpenReader(filepath);
                while (reader.MoveToNextEntry())
                {
                    if (!reader.Entry.IsDirectory)
                        reader.WriteEntryToDirectory(destDir, options);
                }
                return destDir;
            }

            using var archive = ArchiveFactory.OpenArchive(filepath);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                entry.WriteToDirectory(destDir, options);
            }
            return destDir;
        }
        catch (Exception ex)
        {
            // Try 7z command as fallback
            var sevenZip = Find7z();
            if (sevenZip != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = sevenZip,
                    Arguments = $"x \"{filepath}\" -o\"{destDir}\" -y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var proc = System.Diagnostics.Process.Start(psi)!;
                if (!proc.WaitForExit(120_000))
                {
                    try { proc.Kill(true); } catch { }
                    throw new InvalidOperationException(
                        $"7z timed out extracting '{Path.GetFileName(filepath)}'.");
                }
                if (proc.ExitCode == 0)
                    return destDir;
            }

            throw new InvalidOperationException(
                $"Failed to extract '{Path.GetFileName(filepath)}': {ex.Message}", ex);
        }
    }

    private static string StripAllExt(string filepath)
    {
        var dir = Path.GetDirectoryName(filepath) ?? ".";
        var name = Path.GetFileName(filepath);

        // Strip multi-extensions (.tar.gz etc)
        foreach (var ext in ArchiveExts.OrderByDescending(e => e.Length))
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }

        // Also strip multipart extensions
        name = MultipartRe.Replace(name, "");
        name = RarPartRe.Replace(name, "");

        if (string.IsNullOrWhiteSpace(name))
            name = "extracted";

        return Path.Combine(dir, name);
    }

    private static string? Find7z()
    {
        // Check PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            foreach (var exe in new[] { "7z", "7z.exe", "7za", "7za.exe" })
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full)) return full;
            }
        }

        // Common Windows paths
        if (OperatingSystem.IsWindows())
        {
            var paths = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            };
            foreach (var p in paths)
                if (File.Exists(p)) return p;
        }

        return null;
    }
}
