#define _USE_CUSTOM_THREADPOOL
using System;
using System.Threading;

namespace Best.HTTP.Shared.PlatformSupport.Threading
{
    [IL2CPP.Il2CppEagerStaticClassConstruction]
    public static class ThreadedRunner
    {
        public static int ShortLivingThreads { get => _shortLivingThreads; }
        private static int _shortLivingThreads;

        public static int LongLivingThreads { get => _LongLivingThreads; }
        private static int _LongLivingThreads;

        public static void SetThreadName(string name)
        {
            try
            {
                System.Threading.Thread.CurrentThread.Name = name;
            }
            catch(Exception ex)
            {
                if (HTTPManager.Logger.IsDiagnostic)
                    HTTPManager.Logger.Exception(nameof(ThreadedRunner), nameof(SetThreadName), ex);
            }
        }

        public static void RunShortLiving<T>(Action<T> job, T param)
        {
            #if USE_CUSTOM_THREADPOOL
            CustomThreadPool.QueueUserWorkItem(() =>
            #else
            ThreadPool.QueueUserWorkItem((state) =>
            #endif
            {
                using var __ = new IncDecShortLiving(true);
                job(param);
            });
        }

        public static void RunShortLiving<T1, T2>(Action<T1, T2> job, T1 param1, T2 param2)
        {
#if USE_CUSTOM_THREADPOOL
            CustomThreadPool.QueueUserWorkItem(() =>
#else
            ThreadPool.QueueUserWorkItem((state) =>
#endif
            {
                using var __ = new IncDecShortLiving(true);
                job(param1, param2);
            });
        }

        public static void RunShortLiving<T1, T2, T3>(Action<T1, T2, T3> job, T1 param1, T2 param2, T3 param3)
        {            
#if USE_CUSTOM_THREADPOOL
            CustomThreadPool.QueueUserWorkItem(() =>
#else
            ThreadPool.QueueUserWorkItem((state) =>
#endif
            {
                using var __ = new IncDecShortLiving(true);
                job(param1, param2, param3);
            });
        }

        public static void RunShortLiving<T1, T2, T3, T4>(Action<T1, T2, T3, T4> job, T1 param1, T2 param2, T3 param3, T4 param4)
        {
#if USE_CUSTOM_THREADPOOL
            CustomThreadPool.QueueUserWorkItem(() =>
#else
            ThreadPool.QueueUserWorkItem((state) =>
#endif
            {
                using var __ = new IncDecShortLiving(true);
                job(param1, param2, param3, param4);
            });
        }

        public static void RunShortLiving(Action job)
        {
#if USE_CUSTOM_THREADPOOL
            CustomThreadPool.QueueUserWorkItem(() =>
#else
            ThreadPool.QueueUserWorkItem((state) =>
#endif
            {
                using var __ = new IncDecShortLiving(true);
                job();
            });
        }

        public static Thread RunLongLiving(Action job)
        {
            var thread = new Thread(() =>
            {
                using var __ = new IncDecLongLiving(true);
                job();
            });
            thread.IsBackground = true;
            thread.Start();

            return thread;
        }
        
        private static int maxLongLiving;
        private static int maxShortLiving;
        
        public static void StoreLongLivingThreadUsage(int count)
        {
            int current;
            do
            {
                current = maxLongLiving;
            } while (current < count && Interlocked.CompareExchange(ref maxLongLiving, count, current) != current);
        }

        public static int GetAndZeroLongLivingThreadUsage()
        {
            int current = maxLongLiving;
            Interlocked.Exchange(ref maxLongLiving, 0);
            return current;
        }

        public static int GetAndZeroShortLivingThreadUsage()
        {
            int current = maxShortLiving;
            Interlocked.Exchange(ref maxShortLiving, 0);
            return current;
        }


        public static void StoreShortLivingThreadUsage(int count)
        {
            int current;
            do
            {
                current = maxShortLiving;
            } while (current < count && Interlocked.CompareExchange(ref maxShortLiving, count, current) != current);
        }

        public struct IncDecShortLiving : IDisposable
        {
            public IncDecShortLiving(bool dummy) => Interlocked.Increment(ref ThreadedRunner._shortLivingThreads);

            public void Dispose()
            {
                StoreShortLivingThreadUsage(ThreadedRunner._shortLivingThreads);
                Interlocked.Decrement(ref ThreadedRunner._shortLivingThreads);
            }
        }

        public struct IncDecLongLiving : IDisposable
        {
            public IncDecLongLiving(bool dummy) => Interlocked.Increment(ref ThreadedRunner._LongLivingThreads);

            public void Dispose()
            {
                StoreLongLivingThreadUsage(ThreadedRunner._LongLivingThreads);
                Interlocked.Decrement(ref ThreadedRunner._LongLivingThreads);
            }
        }
    }
}
