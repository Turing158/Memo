using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Memo.Services;

public sealed class MemoEditCoordinator {
    private sealed record Lease(object Owner, Func<Task<bool>> RelinquishAsync);
    private readonly Dictionary<Guid, Lease> _leases = new();

    public static MemoEditCoordinator Shared { get; } = new();

    public async Task<bool> AcquireAsync(Guid memoId, object owner, Func<Task<bool>> relinquishAsync) {
        if (_leases.TryGetValue(memoId, out var existing)) {
            if (ReferenceEquals(existing.Owner, owner)) {
                _leases[memoId] = new Lease(owner, relinquishAsync);
                return true;
            }
            if (!await existing.RelinquishAsync()) return false;
        }
        _leases[memoId] = new Lease(owner, relinquishAsync);
        return true;
    }

    public void Release(Guid memoId, object owner) {
        if (_leases.TryGetValue(memoId, out var existing) && ReferenceEquals(existing.Owner, owner))
            _leases.Remove(memoId);
    }

    public bool IsOwner(Guid memoId, object owner) =>
        _leases.TryGetValue(memoId, out var existing) && ReferenceEquals(existing.Owner, owner);
}
