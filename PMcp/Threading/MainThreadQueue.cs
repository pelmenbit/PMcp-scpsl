using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using LabApi.Features.Console;
using MEC;

namespace PMcp.Threading;

public sealed class MainThreadUnavailableException : Exception
{
    public MainThreadUnavailableException(string message)
        : base(message) { }
}

public sealed class MainThreadQueue
{
    private const int Pending = 0;
    private const int Taken = 1;
    private const int Abandoned = 2;

    private sealed class Job
    {
        public Func<object?>? Work;
        public Action? Fire;
        public readonly ManualResetEventSlim Done = new(false);
        public object? Result;
        public Exception? Error;
        public int State;
    }

    private readonly ConcurrentQueue<Job> _jobs = new();
    private readonly int _maxPerFrame;
    private CoroutineHandle _handle;
    private volatile bool _running;

    public MainThreadQueue(int maxPerFrame)
    {
        _maxPerFrame = Math.Max(1, maxPerFrame);
    }

    public void Start()
    {
        _running = true;
        _handle = Timing.RunCoroutine(Drain());
    }

    public void Stop()
    {
        _running = false;
        if (_handle.IsRunning)
            Timing.KillCoroutines(_handle);

        while (_jobs.TryDequeue(out Job job))
        {
            if (Interlocked.CompareExchange(ref job.State, Taken, Pending) != Pending)
                continue;
            job.Error = new MainThreadUnavailableException("PMcp is shutting down");
            job.Done.Set();
        }
    }

    public void Post(Action action)
    {
        if (_running)
            _jobs.Enqueue(new Job { Fire = action });
    }

    public object? Run(Func<object?> work, int timeoutMs)
    {
        if (!_running)
            throw new MainThreadUnavailableException("PMcp is disabled");

        var job = new Job { Work = work };
        _jobs.Enqueue(job);

        if (!job.Done.Wait(timeoutMs))
        {
            if (Interlocked.CompareExchange(ref job.State, Abandoned, Pending) == Pending)
                throw new TimeoutException(
                    $"The server main thread did not pick up the request within {timeoutMs} ms (scene change, round restart or freeze)."
                );

            if (!job.Done.Wait(timeoutMs))
                throw new TimeoutException($"The request has been running on the main thread for more than {timeoutMs * 2} ms.");
        }

        if (job.Error != null)
            ExceptionDispatchInfo.Capture(job.Error).Throw();
        return job.Result;
    }

    private IEnumerator<float> Drain()
    {
        while (true)
        {
            int handled = 0;
            while (handled < _maxPerFrame && _jobs.TryDequeue(out Job job))
            {
                if (job.Fire != null)
                {
                    try
                    {
                        job.Fire();
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"PMcp: отложенная задача главного потока упала: {e}");
                    }
                    continue;
                }

                if (Interlocked.CompareExchange(ref job.State, Taken, Pending) != Pending)
                    continue;

                handled++;
                try
                {
                    job.Result = job.Work!();
                }
                catch (Exception e)
                {
                    job.Error = e;
                }
                job.Done.Set();
            }

            yield return Timing.WaitForOneFrame;
        }
    }
}
