using Memo.Models;
using Memo.Services;
using Memo.Utils;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Memo.ViewModels;

/// <summary>
/// 持有备忘录集合、编辑态 ID 以及持久化服务。
/// </summary>
public class MainViewModel {
    private readonly JsonMemoStorage _storage;

    public ObservableCollection<MemoItem> Memos { get; } = new();

    /// <summary>当前正在编辑的备忘录 Id；null 表示非编辑态（提交会视为新增）。</summary>
    public Guid? EditingId { get; private set; }

    public MainViewModel() {
        _storage = new JsonMemoStorage();
    }

    public async Task LoadAsync() {
        var items = await _storage.LoadAsync();
        foreach (var item in items)
        {
            Memos.Add(item);
        }
    }

    /// <summary>
    /// 快速添加：若已有相同内容的备忘录则移到首位，否则新增。
    /// 返回被影响（新建或提升）的备忘录。
    /// </summary>
    public MemoItem? FindByContent(string content) =>
        Memos.FirstOrDefault(m => m.Content == content);

    /// <summary>
    /// 快速添加：若已有相同内容的备忘录则移到首位，否则新增。
    public MemoItem? AddOrPromoteItem(string content) {
        var existing = FindByContent(content);
        if (existing != null) {
            MoveToFront(existing.Id);
            return existing;
        }
        return AddItem(content);
    }

    public MemoItem? AddItem(string content) {
        var now = DateTimeUtils.Now;
        var item = new MemoItem { Content = content, CreatedAt = now, UpdatedAt = now };
        Memos.Insert(0, item);
        _ = _storage.SaveAsync(Memos);
        return item;
    }

    public void UpdateItem(Guid id, string content) {
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        item.Content = content;
        item.UpdatedAt = DateTimeUtils.Now;
    }

    public void UpdateItemAndSave(Guid id, string content) {
        UpdateItem(id, content);
        _ = _storage.SaveAsync(Memos);
    }

    public void MoveToFront(Guid id) {
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        var idx = Memos.IndexOf(item);
        if (idx > 0)
        {
            Memos.Move(idx, 0);
            _ = _storage.SaveAsync(Memos);
        }
    }

    /// <summary>
    /// 通用重排：把指定 id 的项移动到新索引位置，并异步持久化。
    /// 由长按拖拽交互调用。
    /// </summary>
    public void MoveItem(Guid id, int newIndex) {
        if (newIndex < 0 || newIndex >= Memos.Count) return;
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        var oldIndex = Memos.IndexOf(item);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        Memos.Move(oldIndex, newIndex);
        _ = _storage.SaveAsync(Memos);
    }

    public void DeleteItem(Guid id) {
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        Memos.Remove(item);
        if (EditingId == id) EditingId = null;
        _ = _storage.SaveAsync(Memos);
    }

    // —— 编辑态控制（UI 可直接调用） ——
    public void BeginEdit(Guid id) => EditingId = id;
    public void EndEdit() => EditingId = null;
}
