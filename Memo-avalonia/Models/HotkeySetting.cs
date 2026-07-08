using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Memo.Models;

public class HotkeySetting {
    public string Key { get; set; } = string.Empty;
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Key);

    public override string ToString() {
        if (IsEmpty) return "未设置";

        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join(" + ", parts);
    }

    public HotkeySetting Clone() => new() {
        Key = Key,
        Ctrl = Ctrl,
        Alt = Alt,
        Shift = Shift,
        Win = Win,
    };
}
