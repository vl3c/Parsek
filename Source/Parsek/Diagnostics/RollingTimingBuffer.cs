namespace Parsek
{
    /// <summary>
    /// Pre-allocated ring buffer for frame timing history.
    /// Stores (wallTimestamp, microseconds) pairs. No allocations after construction.
    /// Capacity: 1024 entries (enough for 4s at 240fps).
    /// </summary>
    internal class RollingTimingBuffer
    {
        private const int Capacity = 1024;

        private readonly double[] timestamps;
        private readonly long[] microseconds;
        private int head;
        private int tail;
        private int count;

        public RollingTimingBuffer()
        {
            timestamps = new double[Capacity];
            microseconds = new long[Capacity];
            head = 0;
            tail = 0;
            count = 0;
        }

        public int Count => count;

        public bool IsEmpty => count == 0;

        /// <summary>
        /// Append a timing entry. If the buffer is full, overwrites the oldest entry.
        /// </summary>
        public void Append(double wallTimestamp, long microseconds)
        {
            this.timestamps[tail] = wallTimestamp;
            this.microseconds[tail] = microseconds;
            tail = (tail + 1) % Capacity;

            if (count < Capacity)
            {
                count++;
            }
            else
            {
                // Buffer full — oldest entry overwritten, advance head
                head = (head + 1) % Capacity;
            }
        }

        /// <summary>
        /// Compute rolling average and peak over entries within windowSeconds of currentTimestamp.
        /// Scans from newest to oldest, stops when entry is older than the window.
        /// Output values are in milliseconds (converted from stored microseconds).
        /// Empty buffer or no entries in window: avgMs=0, peakMs=0, actualWindowDuration=0.
        /// </summary>
        public void ComputeStats(double currentTimestamp, double windowSeconds,
            out double avgMs, out double peakMs, out double actualWindowDuration)
        {
            avgMs = 0.0;
            peakMs = 0.0;
            actualWindowDuration = 0.0;

            if (count == 0)
                return;

            double windowStart = currentTimestamp - windowSeconds;
            long sumMicroseconds = 0;
            long maxMicroseconds = 0;
            int entriesInWindow = 0;
            double oldestInWindow = currentTimestamp;
            double newestInWindow = 0.0;

            // Scan from newest to oldest
            for (int i = 0; i < count; i++)
            {
                int idx = (tail - 1 - i + Capacity) % Capacity;
                double ts = timestamps[idx];

                if (ts < windowStart)
                    break;

                long us = this.microseconds[idx];
                sumMicroseconds += us;
                if (us > maxMicroseconds)
                    maxMicroseconds = us;

                if (ts < oldestInWindow)
                    oldestInWindow = ts;
                if (ts > newestInWindow)
                    newestInWindow = ts;

                entriesInWindow++;
            }

            if (entriesInWindow == 0)
                return;

            avgMs = (sumMicroseconds / (double)entriesInWindow) / 1000.0;
            peakMs = maxMicroseconds / 1000.0;
            actualWindowDuration = newestInWindow - oldestInWindow;
        }

        /// <summary>
        /// Reset the buffer to empty state.
        /// </summary>
        public void Reset()
        {
            head = 0;
            tail = 0;
            count = 0;
        }
    }
}
