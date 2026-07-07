using note_avalonia.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;

namespace note_avalonia.Services;

/// <summary>
/// JSON 文件持久化：%AppData%\note_avalonia\memos.json。
/// 每次增/改/删/移后异步保存，失败时静默吞错并记录。
/// </summary>
public class JsonMemoStorage {
    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JsonMemoStorage() {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "note_avalonia");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "memos.json");
    }

    public async Task<List<MemoItem>> LoadAsync() {
        try {
            if (!File.Exists(_filePath)) return new List<MemoItem>();
            var json = await File.ReadAllTextAsync(_filePath);
            var list = JsonSerializer.Deserialize<List<MemoItem>>(json, JsonOptions);
            return list ?? new List<MemoItem>();
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[MemoStorage] Load failed: {ex.Message}");
            return new List<MemoItem>();
        }
    }

    public async Task SaveAsync(IEnumerable<MemoItem> items) {
        await _semaphore.WaitAsync();
        try {
            var json = JsonSerializer.Serialize(items.ToList(), JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[MemoStorage] Save failed: {ex.Message}");
        }
        finally {
            _semaphore.Release();
        }
    }
}
