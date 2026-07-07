using System;
using System.ComponentModel;
using System.Linq;

namespace note_avalonia.Models;

/// <summary>
/// 单条备忘录模型。Title / Subtitle 由 Content 自动派生（首行=标题，第二行起=副标题）。
/// </summary>
public class MemoItem : INotifyPropertyChanged {
    private string _content = string.Empty;
    private DateTime _createdAt;
    private DateTime _updatedAt;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Content {
        get => _content;
        set {
            if (_content != value) {
                _content = value;
                OnPropertyChanged(nameof(Content));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public DateTime CreatedAt {
        get => _createdAt;
        set {
            if (_createdAt != value) {
                _createdAt = value;
                OnPropertyChanged(nameof(CreatedAt));
            }
        }
    }

    public DateTime UpdatedAt {
        get => _updatedAt;
        set {
            if (_updatedAt != value) {
                _updatedAt = value;
                OnPropertyChanged(nameof(UpdatedAt));
            }
        }
    }

    // —— 派生显示字段 ——

    /// <summary>相对时间：今天显示「HH:mm」，昨天显示「昨天 HH:mm」，更早显示「yyyy/M/d」。</summary>
    public string RelativeTime {
        get {
            var local = CreatedAt.ToLocalTime();
            var now = DateTime.Now;
            if (local.Date == now.Date)
                return local.ToString("HH:mm");
            if (local.Date == now.Date.AddDays(-1))
                return "昨天 " + local.ToString("HH:mm");
            if (local.Year == now.Year)
                return local.ToString("M/d");
            return local.ToString("yyyy/M/d");
        }
    }

    public string Title =>
        (_content ?? string.Empty)
            .Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()
        ?? (_content ?? string.Empty).Trim();

    /// <summary>副标题：仅取内容的第二行（trim 后），为空则返回空字符串。</summary>
    public string Subtitle {
        get {
            var second = (_content ?? string.Empty).Split('\n').ElementAtOrDefault(1);
            return second?.Trim() ?? string.Empty;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
