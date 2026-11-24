using RbxSwitcher.Core.Security;

namespace RbxSwitcher.Core.Logging;

/// <summary>
/// Minimal local log. Every line passes through <see cref="Redact.Text"/>, so a session that slips
/// into an exception message still never reaches the disk. Rotates at 1 MB, keeps one old file.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private const long MaxBytes = 1024 * 1024;

    public static void Init(string path) => _path = path;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {Redact.Text(message)}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
