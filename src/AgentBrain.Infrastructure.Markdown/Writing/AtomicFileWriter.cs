using System.Text;
using AgentBrain.Application.Common;

namespace AgentBrain.Infrastructure.Markdown.Writing;

/// <summary>
/// Single-file atomic writes (plan §5.12): write a temp file in the same directory, flush it to disk, then
/// rename it over the target. A reader never sees a partial file. On Windows a rename fails while another
/// process holds the target open (ADR 0001), so it is retried briefly and then reported, never replaced by
/// an in-place write.
/// </summary>
public static class AtomicFileWriter
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Test hook: called between the flushed temp write and the rename, to simulate a crash.</summary>
    internal static Action<string>? BeforeRename { get; set; }

    public static async ValueTask<Result> WriteAsync(string path, string content, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            BeforeRename?.Invoke(temp);
            var deadline = DateTime.UtcNow + RetryWindow;
            while (true)
            {
                try
                {
                    File.Move(temp, path, overwrite);
                    return Result.Success;
                }
                catch (IOException) when (!overwrite && File.Exists(path))
                {
                    return new Error(ErrorCode.ValidationFailed, $"'{path}' already exists.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new Error(ErrorCode.LockTimeout, $"'{path}' is held open by another program; close it and retry. ({ex.Message})");
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
