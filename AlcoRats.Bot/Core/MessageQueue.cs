using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Bot.Core;

public class MessageQueue<T>
{
    private ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();

    private ConcurrentQueue<TaskCompletionSource<T>> _waitingQueue =  new ConcurrentQueue<TaskCompletionSource<T>>();

    private Lock _queueSyncLock = new Lock();

    public void Enqueue(T item)
    {
        _queue.Enqueue(item);
        ProcessQueues();
    }

    public async Task<T> Dequeue()
    {
        TaskCompletionSource<T> tcs = new TaskCompletionSource<T>();
        _waitingQueue.Enqueue(tcs);
        ProcessQueues();
        return tcs.Task.IsCompleted ? tcs.Task.Result : await tcs.Task;
    }

    private void ProcessQueues()
    {
        TaskCompletionSource<T> tcs = null;
        T firstItem = default;
        while (true)
        {
            bool ok;
            lock (_queueSyncLock)
            {
                ok = _waitingQueue.TryPeek(out tcs) && _queue.TryPeek(out firstItem);
                if (ok)
                {
                    _waitingQueue.TryDequeue(out tcs);
                    _queue.TryDequeue(out firstItem);
                }
            }
            if (!ok) break;
            tcs.SetResult(firstItem);
        }
    }
}

