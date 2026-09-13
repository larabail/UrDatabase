namespace UrDatabase.Services
{
    /// <summary>One visit to a series, sharing a single load between initial open and refresh.</summary>
    public sealed class SeriesRefreshSession : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime;
        private Task? _pending;
        private bool _closed;

        public SeriesRefreshSession(CancellationToken lifetime = default)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Token = _lifetime.Token;
        }

        public CancellationToken Token { get; }
        public bool IsCurrent
        {
            get { lock (_gate) return !_closed && !Token.IsCancellationRequested; }
        }
        public bool IsBusy
        {
            get { lock (_gate) return _pending is { IsCompleted: false }; }
        }

        public Task RunAsync(Func<CancellationToken, Task> load)
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (!IsCurrent) return Task.CompletedTask;
                if (_pending is { IsCompleted: false }) return _pending;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = completion.Task;
            }

            _ = CompleteAsync(load, completion);
            return completion.Task;
        }

        private async Task CompleteAsync(Func<CancellationToken, Task> load, TaskCompletionSource completion)
        {
            try
            {
                Token.ThrowIfCancellationRequested();
                await load(Token);
                Token.ThrowIfCancellationRequested();
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
            }

            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
