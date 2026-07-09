using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using Memo.Utils;

namespace Memo.Models;

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

    /// <summary>关联的弹出窗口引用计数（新建新窗体 +1，关闭 -1），不持久化。</summary>
    [JsonIgnore]
    public int PopoutRefCount { get; set; }

    // —— 派生显示字段 ——

    /// <summary>相对时间文本（今天/昨天/周内/同年/跨年）。</summary>
    public string RelativeTime {
        get => CreatedAt.ToRelativeTimeString();
    }

    /// <summary>完整时间文本（今天/昨天/周内/同年/跨年）。</summary>
    public string FullTime {
        get => CreatedAt.ToFullTimeString();
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
