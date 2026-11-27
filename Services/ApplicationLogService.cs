using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvataRunway.Models;

namespace AvataRunway.Services
{
    public sealed class ApplicationLogService
    {
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private string currentVrmFilePath = string.Empty;
        private AppLogLevel logLevel;

        public ApplicationLogService(AppLogLevel initialLogLevel = AppLogLevel.All)
        {
            logLevel = initialLogLevel;
        }

        public string LogDirectoryPath => ApplicationPaths.LogDirectoryPath;

        public string? LastWriteFailureType { get; private set; }

        public void SetCurrentVrmFilePath(string? filePath)
        {
            Volatile.Write(
                ref currentVrmFilePath,
                filePath ?? string.Empty);
        }

        public void SetLogLevel(AppLogLevel value)
        {
            logLevel = value;
        }

        public async Task LogErrorAsync(
            string? filePath,
            string? className,
            string? methodName,
            string? message)
        {
            await WriteAsync("ERROR", filePath, className, methodName, message);
        }

        public async Task LogInfoAsync(
            string? className,
            string? methodName,
            string? message)
        {
            if (logLevel != AppLogLevel.All)
            {
                return;
            }

            await WriteAsync("INFO", string.Empty, className, methodName, message);
        }

        public async Task LogWarningAsync(
            string? className,
            string? methodName,
            string? message)
        {
            if (logLevel == AppLogLevel.Error)
            {
                return;
            }

            await WriteAsync("WARN", string.Empty, className, methodName, message);
        }

        private async Task WriteAsync(
            string level,
            string? filePath,
            string? className,
            string? methodName,
            string? message)
        {
            try
            {
                await writeLock.WaitAsync();
                try
                {
                    var logDirectory = LogDirectoryPath;
                    Directory.CreateDirectory(logDirectory);

                    var now = DateTime.Now;
                    var logPath = Path.Combine(logDirectory, $"{now:yyyyMMdd}.log");
                    var effectiveFilePath = string.IsNullOrWhiteSpace(filePath)
                        ? Volatile.Read(ref currentVrmFilePath)
                        : filePath;
                    var line = string.Format(
                        "{0:yyyy/MM/dd HH:mm:ss} [{1}] FilePath=\"{2}\" Class=\"{3}\" Method=\"{4}\" Message=\"{5}\"{6}",
                        now,
                        level,
                        EscapeFilePath(effectiveFilePath),
                        Escape(className),
                        Escape(methodName),
                        Escape(message),
                        Environment.NewLine);

                    await File.AppendAllTextAsync(logPath, line, new UTF8Encoding(false));
                    LastWriteFailureType = null;
                }
                finally
                {
                    writeLock.Release();
                }
            }
            catch (Exception exception)
            {
                // Logging must never terminate the application.
                LastWriteFailureType = exception.GetType().Name;
                System.Diagnostics.Debug.WriteLine(
                    $"Application logging failed ({LastWriteFailureType}): {exception.Message}");
            }
        }

        private static string Escape(string? value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        }

        private static string EscapeFilePath(string? value)
        {
            return (value ?? string.Empty)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        }
    }
}
