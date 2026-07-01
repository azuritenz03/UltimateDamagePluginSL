using System;
using System.Collections.Generic;
using System.Threading;

namespace MEC
{
    public enum Segment
    {
        Update,
        LateUpdate,
        FixedUpdate,
    }

    public struct CoroutineHandle : IEquatable<CoroutineHandle>
    {
        private readonly int id;
        private readonly bool valid;

        public CoroutineHandle(int id, bool valid = true)
        {
            this.id = id;
            this.valid = valid;
        }

        public bool IsValid => valid && id >= 0;
        public int Id => id;

        public static CoroutineHandle Invalid => new CoroutineHandle(-1, false);

        public override bool Equals(object obj) => obj is CoroutineHandle other && Equals(other);

        public bool Equals(CoroutineHandle other) => id == other.id && valid == other.valid;

        public override int GetHashCode() => (id * 397) ^ (valid ? 1 : 0);

        public static bool operator ==(CoroutineHandle left, CoroutineHandle right) => left.Equals(right);

        public static bool operator !=(CoroutineHandle left, CoroutineHandle right) => !left.Equals(right);
    }

    public static class Timing
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<int, CoroutineState> ActiveCoroutines = new Dictionary<int, CoroutineState>();
        private static int nextId;

        public static CoroutineHandle RunCoroutine(IEnumerator<float> coroutine, Segment segment)
        {
            if (coroutine == null)
                return CoroutineHandle.Invalid;

            var handle = new CoroutineHandle(NextId());
            var state = new CoroutineState(handle.Id, coroutine, segment);
            lock (SyncRoot)
            {
                ActiveCoroutines[state.Id] = state;
            }

            ThreadPool.QueueUserWorkItem(_ => Run(state));
            return handle;
        }

        public static float WaitForSeconds(float seconds)
        {
            return Math.Max(0f, seconds);
        }

        public static void KillCoroutines(CoroutineHandle handle)
        {
            if (!handle.IsValid)
                return;

            lock (SyncRoot)
            {
                ActiveCoroutines.Remove(handle.Id);
            }
        }

        private static int NextId()
        {
            lock (SyncRoot)
            {
                nextId++;
                if (nextId < 0)
                    nextId = 1;
                return nextId;
            }
        }

        private static void Run(CoroutineState state)
        {
            try
            {
                while (true)
                {
                    bool active;
                    lock (SyncRoot)
                    {
                        active = ActiveCoroutines.ContainsKey(state.Id);
                    }

                    if (!active)
                        break;

                    try
                    {
                        if (!state.Enumerator.MoveNext())
                            break;
                    }
                    catch
                    {
                        break;
                    }

                    if (state.Enumerator.Current > 0f)
                        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(state.Enumerator.Current, 0.25f)));
                }
            }
            finally
            {
                lock (SyncRoot)
                {
                    ActiveCoroutines.Remove(state.Id);
                }
            }
        }

        private sealed class CoroutineState
        {
            public CoroutineState(int id, IEnumerator<float> enumerator, Segment segment)
            {
                Id = id;
                Enumerator = enumerator;
                Segment = segment;
            }

            public int Id { get; }
            public IEnumerator<float> Enumerator { get; }
            public Segment Segment { get; }
        }
    }
}
