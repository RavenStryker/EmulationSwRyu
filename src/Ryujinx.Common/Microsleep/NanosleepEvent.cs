using System;
using System.Threading;

namespace Ryujinx.Common.Microsleep
{
    public class NanosleepEvent : IMicrosleepEvent
    {
        private AutoResetEvent _waitEvent = new(false);
        private NanosleepPool _pool;

        public NanosleepEvent()
        {
            _pool = new NanosleepPool(_waitEvent);
        }

        public bool CanSleepTil(long timePoint)
        {
            // Delay to timePoint is more than the bias
            long now = PerformanceCounter.ElapsedTicks;
            long delta = (timePoint - now);
            long ns = (delta * 1_000_000) / PerformanceCounter.TicksPerMillisecond;

            return ns - Nanosleep.Bias > 0;
        }

        public long AdjustTimePoint(long timePoint)
        {
            // No adjustment
            return timePoint;
        }

        public bool SleepUntil(long timePoint, bool strictlyBefore = false)
        {
            long now = PerformanceCounter.ElapsedTicks;
            long delta = (timePoint - now);
            long ms = Math.Min(delta / PerformanceCounter.TicksPerMillisecond, int.MaxValue);
            long ns = (delta * 1_000_000) / PerformanceCounter.TicksPerMillisecond;

            if (ms > 0)
            {
                _waitEvent.WaitOne((int)ms);

                return true;
            }
            else if (ns - Nanosleep.Bias > 0)
            {
                // Don't bother starting a sleep if there's already a signal active.
                if (_waitEvent.WaitOne(0))
                {
                    return true;
                }

                // The 1ms wait will be interrupted by the nanosleep timeout if it completes.
                if (!_pool.SleepAndSignal(ns, timePoint))
                {
                    // Too many threads on the pool.
                    return false;
                }
                _waitEvent.WaitOne(1);
                _pool.IgnoreSignal();

                return true;
            }
            
            return false;
        }

        public void Sleep()
        {
            _waitEvent.WaitOne();
        }

        public void Signal()
        {
            _waitEvent.Set();
        }

        public void Dispose()
        {
            _pool.Dispose();
            _waitEvent.Dispose();
        }
    }
}
