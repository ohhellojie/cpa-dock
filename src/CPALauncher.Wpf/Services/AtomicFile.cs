using System.Text;

namespace CPALauncher.Services;

internal static class AtomicFile
{
    internal static void WriteText(string path, string text, string? backup = null)
        => WriteBytes(path, Encoding.UTF8.GetBytes(text), backup);

    internal static void WriteBytes(string path, byte[] bytes, string? backup = null)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, backup);
            else File.Move(temporary, path);
        }
        catch
        {
            if (!File.Exists(path) && backup is not null && File.Exists(backup))
                File.Copy(backup, path);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
