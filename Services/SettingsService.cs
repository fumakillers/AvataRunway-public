using AvataRunway.Models;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvataRunway.Services
{
    public sealed class SettingsService
    {
        private readonly SemaphoreSlim saveLock = new(1, 1);
        private long nextSaveVersion;
        private long lastWrittenSaveVersion;
        private int isShutdownSaveRequested;

        public SettingsService()
        {
            Current = Load();
        }

        public AppSettings Current { get; }

        public string SettingsFilePath => ApplicationPaths.SettingsFilePath;

        public Exception? LoadFailure { get; private set; }

        public async Task SaveAsync()
        {
            if (Volatile.Read(ref isShutdownSaveRequested) != 0)
            {
                return;
            }

            var json = SerializeCurrentSettings();
            var saveVersion = Interlocked.Increment(ref nextSaveVersion);
            await saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref isShutdownSaveRequested) != 0 ||
                    saveVersion < Volatile.Read(ref lastWrittenSaveVersion))
                {
                    return;
                }

                await WriteSettingsFileAsync(json).ConfigureAwait(false);
                Volatile.Write(ref lastWrittenSaveVersion, saveVersion);
            }
            finally
            {
                saveLock.Release();
            }
        }

        public void SaveForShutdown()
        {
            Interlocked.Exchange(ref isShutdownSaveRequested, 1);
            saveLock.Wait();
            try
            {
                WriteSettingsFile(SerializeCurrentSettings());
            }
            finally
            {
                saveLock.Release();
            }
        }

        private AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(SettingsFilePath);
                var document = JsonSerializer.Deserialize(
                    json,
                    AppSettingsJsonContext.Default.AppSettingsDocument);
                var loadedSettings = document?.Settings;
                if (document?.Version is < 1 or > AppSettingsDocument.CurrentVersion || loadedSettings is null)
                {
                    throw new InvalidDataException("The settings file version is unsupported.");
                }

                if (!Enum.IsDefined(loadedSettings.LogLevel) ||
                    !Enum.IsDefined(loadedSettings.Language) ||
                    !Enum.IsDefined(loadedSettings.PreviewColorScheme))
                {
                    throw new InvalidDataException("The settings file contains an unsupported value.");
                }

                return loadedSettings;
            }
            catch (Exception exception)
            {
                LoadFailure = exception;
                return new AppSettings();
            }
        }

        private string SerializeCurrentSettings()
        {
            var document = new AppSettingsDocument { Settings = Current };
            return JsonSerializer.Serialize(
                document,
                AppSettingsJsonContext.Default.AppSettingsDocument);
        }

        private async Task WriteSettingsFileAsync(string json)
        {
            PrepareSettingsWrite();
            try
            {
                await File.WriteAllTextAsync(
                    TemporarySettingsFilePath,
                    json,
                    new UTF8Encoding(false)).ConfigureAwait(false);
                CommitTemporarySettingsFile();
            }
            finally
            {
                DeleteTemporarySettingsFileIfPresent();
            }
        }

        private void WriteSettingsFile(string json)
        {
            PrepareSettingsWrite();
            try
            {
                File.WriteAllText(
                    TemporarySettingsFilePath,
                    json,
                    new UTF8Encoding(false));
                CommitTemporarySettingsFile();
            }
            finally
            {
                DeleteTemporarySettingsFileIfPresent();
            }
        }

        private string TemporarySettingsFilePath => SettingsFilePath + ".tmp";

        private void PrepareSettingsWrite()
        {
            Directory.CreateDirectory(ApplicationPaths.SettingsDirectoryPath);
            DeleteTemporarySettingsFileIfPresent();
        }

        private void CommitTemporarySettingsFile()
        {
            File.Move(TemporarySettingsFilePath, SettingsFilePath, true);
        }

        private void DeleteTemporarySettingsFileIfPresent()
        {
            if (File.Exists(TemporarySettingsFilePath))
            {
                File.Delete(TemporarySettingsFilePath);
            }
        }
    }
}
