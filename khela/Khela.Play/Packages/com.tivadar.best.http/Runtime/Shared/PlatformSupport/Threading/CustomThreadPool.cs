using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Best.HTTP.Shared.PlatformSupport.Threading
{
    public static class CustomThreadPool
    {
        public static int BackgroundThreads => activeThreads;
        
        private static int minThreads = 1; 
        private static int maxThreads = 128;
        
        private static int activeThreads = 0;
        private static int workingThreads = 0;

        private static ConcurrentQueue<Action> jobQueue = new();
        private static AutoResetEvent are = new (false);

        //private static List<Thread> threads = new();
        
        public static void SetMinMaxThreads(int min, int max)
        {
            minThreads = min;
            maxThreads = max;
        }
        
        public static void QueueUserWorkItem(Action job)
        {
            jobQueue.Enqueue(job);
            are.Set();

            if (workingThreads >= (activeThreads * 0.7f) && activeThreads < maxThreads)
            {
                var id = Interlocked.Increment(ref activeThreads);
                
                var thread = new Thread(ThreadFunc);
                thread.Name = $"{nameof(CustomThreadPool)}({id})";
                
                thread.Priority = ThreadPriority.AboveNormal;
                thread.IsBackground = true;
                thread.Start();
            }
        }

        private static void ThreadFunc()
        {
            DateTime lastJobProcessed = DateTime.UtcNow;

            using var _ = new ThreadedRunner.IncDecLongLiving(true);
            
            try
            {
                var hasWork = false;
                do
                {
                    while (jobQueue.TryDequeue(out var job))
                    {
                        try
                        {
                            Interlocked.Increment(ref workingThreads);
                            lastJobProcessed = DateTime.UtcNow;
                            job?.Invoke();
                        }
                        catch (Exception e)
                        {
                            UnityEngine.Debug.LogException(e);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref workingThreads);
                        }
                    }

                    hasWork = are.WaitOne(TimeSpan.FromSeconds(5));
                } while (hasWork && DateTime.UtcNow - lastJobProcessed < TimeSpan.FromSeconds(60));
            }
            finally
            {
                Interlocked.Decrement(ref activeThreads);
            }
        }
    }
}