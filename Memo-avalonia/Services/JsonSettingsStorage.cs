using note_avalonia.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace note_avalonia.Services;

public class JsonSettingsStorage {
    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JsonSettingsStorage() {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "note_avalonia");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
    }

    public async Task<AppSettings> LoadAsync() {
        try {
            if (!File.Exists(_filePath)) return AppSettings.CreateDefault();
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[SettingsStorage] Load failed: {ex.Message}");
            return AppSettings.CreateDefault();
        }
    }

    public async Task SaveAsync(AppSettings settings) {
        await _semaphore.WaitAsync();
        try {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[SettingsStorage] Save failed: {ex.Message}");
        }
        finally {
            _semaphore.Release();
        }
    }
}
