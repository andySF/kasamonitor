namespace KasaMonitor.Workers;

/// <summary>
/// A secret mounted as a file (Docker secret, bind mount). Rule shared by every secret that can come from a file or
/// from an environment variable: the file wins when it exists and is not empty, otherwise the variable.
/// </summary>
public static class SecretFile
{
    /// <summary>The trimmed content, or null with the reason (missing, empty, unreadable) for the UI.
    /// <paramref name="readFile"/> is a test hook (null result = the file does not exist). <paramref name="lineEndOnly"/>:
    /// only the trailing new line is removed (a password may begin or end with a space).</summary>
    public static (string? Text, string? Problem) Read(string path, Func<string, string?>? readFile = null, bool lineEndOnly = false)
    {
        readFile ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        try
        {
            var text = readFile(path);
            if (text is null) return (null, "fișierul " + path + " nu există");
            var value = lineEndOnly ? text.TrimEnd('\r', '\n') : text.Trim();
            return value.Length == 0 ? (null, "fișierul " + path + " e gol") : (value, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "fișierul " + path + " nu poate fi citit (" + ex.GetType().Name + ")");
        }
    }
}
