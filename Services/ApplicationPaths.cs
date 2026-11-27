using System;
using System.IO;

namespace AvataRunway.Services
{
    public static class ApplicationPaths
    {
        public static string ApplicationDirectoryPath { get; } = ResolveApplicationDirectoryPath();

        public static string SettingsDirectoryPath { get; } = Path.Combine(
            ApplicationDirectoryPath,
            "app",
            "config");

        public static string SettingsFilePath { get; } = Path.Combine(
            SettingsDirectoryPath,
            "settings.json");

        public static string LogDirectoryPath { get; } = Path.Combine(
            ApplicationDirectoryPath,
            "app",
            "logs");

        public static string WebView2UserDataPath { get; } = Path.Combine(
            ApplicationDirectoryPath,
            "app",
            "temp",
            "webview2");

        public static void EnsurePortableStorageWritable()
        {
            EnsureDirectoryWritable(SettingsDirectoryPath);
            EnsureDirectoryWritable(LogDirectoryPath);
            EnsureDirectoryWritable(WebView2UserDataPath);
        }

        private static string ResolveApplicationDirectoryPath()
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                throw new InvalidOperationException("実行ファイルの配置先を特定できませんでした。");
            }

            var executableDirectory = Path.GetDirectoryName(processPath);
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                throw new InvalidOperationException("実行ファイルの配置ディレクトリを特定できませんでした。");
            }

            return executableDirectory;
        }

        private static void EnsureDirectoryWritable(string directoryPath)
        {
            Directory.CreateDirectory(directoryPath);

            var probePath = Path.Combine(directoryPath, $".write-test-{Guid.NewGuid():N}.tmp");
            using var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
    }
}
