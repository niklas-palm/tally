namespace Tally;

/// <summary>Files written whole, so nothing is ever read half-written: to a temporary file beside it, on the disk
/// itself, then moved into place. Without the flush, a power cut just after can leave the file its right length but
/// all zeros: NTFS keeps the rename before the data.</summary>
internal static class Disk
{
    public static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);  // a full disk, or a file another program holds: no half-written copy left beside it
            throw;
        }
    }
}
