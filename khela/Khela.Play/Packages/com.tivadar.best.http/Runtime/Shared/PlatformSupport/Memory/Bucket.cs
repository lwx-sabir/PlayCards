using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Best.HTTP.Shared.PlatformSupport.Memory
{
    internal struct Bucket
    {
#if UNITY_EDITOR
        /// <summary>
        /// Size the Bucket is associated with. Serves mostly debug purposes.
        /// </summary>
        public readonly int Size;
#endif

        /// <summary>
        /// What was Items' minimum Count between two checks.
        /// </summary>
        public int MinCount;

        /// <summary>
        /// Direct access to a buffer, without going throug ConcurrentStack's pop/push logic.
        /// </summary>
        public byte[] FastItem;

        private int _count;
        
        private readonly ConcurrentStack<byte[]> _items;

        public Bucket(int size)
        {
#if UNITY_EDITOR
            this.Size = size;
#endif
            this.FastItem = null;
            this._items = new ConcurrentStack<byte[]>();
            this.MinCount = int.MaxValue;
            this._count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPop(out byte[] item)
        {
            if (this._items.TryPop(out item))
            {
                Interlocked.Decrement(ref this._count);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(byte[] item)
        {
            this._items.Push(item);
            Interlocked.Increment(ref this._count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            this._items.Clear();
            Interlocked.Exchange(ref this._count, 0);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateMinCount(int fastItem)
        {
            int newMinCount = _count + fastItem;
            int oldMinCount = MinCount;
            while (newMinCount < oldMinCount && Interlocked.CompareExchange(ref MinCount, newMinCount, oldMinCount) != oldMinCount)
                oldMinCount = MinCount;
        }

#if UNITY_EDITOR
        public override string ToString() => $"[{Size}, {(FastItem != null ? "y" : "n")}, {_count}, {MinCount}]";
#endif
    }
}
