using System;
using System.Threading;
using System.Threading.Tasks;

namespace Memo.Services;

public sealed class DebouncedAction : IDisposable {
    private readonly TimeSpan _delay;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Func<Task>? _pending;

    public DebouncedAction(TimeSpan delay, SynchronizationContext? context = null) {
        _delay = delay;
        _context = context ?? SynchronizationContext.Current;
    }

    public void Schedule(Func<Task> action) {
        CancellationToken token;
        lock (_gate) {
            _pending = action;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            token = _cancellation.Token;
        }
        _ = RunAsync(action, token);
    }

    public async Task FlushAsync() {
        Func<Task>? action;
        lock (_gate) {
            action = _pending;
            _pending = null;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
        if (action != null) await InvokeAsync(action);
    }

    public void Cancel() {
        lock (_gate) {
            _pending = null;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void Dispose() => Cancel();

    private async Task RunAsync(Func<Task> action, CancellationToken token) {
        try {
            await Task.Delay(_delay, token);
            lock (_gate) {
                if (token.IsCancellationRequested || !ReferenceEquals(_pending, action)) return;
                _pending = null;
                _cancellation?.Dispose();
                _cancellation = null;
            }
            await InvokeAsync(action);
        }
        catch (OperationCanceledException) { }
    }

    private Task InvokeAsync(Func<Task> action) {
        if (_context == null || ReferenceEquals(SynchronizationContext.Current, _context))
            return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(async _ => {
            try {
                await action();
                completion.SetResult();
            }
            catch (Exception exception) {
                completion.SetException(exception);
            }
        }, null);
        return completion.Task;
    }
}
