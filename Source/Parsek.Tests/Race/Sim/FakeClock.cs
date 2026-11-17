using System;

namespace Parsek.Tests.Race.Sim
{
    /// <summary>
    /// One simulated machine's clocks: real time in UTC and game UT, each set or advanced on
    /// its own. Two machines skew by holding different values; nothing here reads the host
    /// clock or the host time zone (a non-UTC <see cref="DateTime"/> is refused, because
    /// converting a Local or Unspecified value depends on the host's zone).
    /// It is the one clock fake of the race tests: when the race core declares its real-time
    /// clock port, this class implements it (the port's <c>UtcNow</c> is this property).
    /// </summary>
    internal sealed class FakeClock
    {
        private DateTime utcNow;
        private double ut;

        public FakeClock(DateTime utcNow, double ut = 0.0)
        {
            SetUtc(utcNow);
            SetUT(ut);
        }

        public DateTime UtcNow => utcNow;

        public double UT => ut;

        public void SetUtc(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("FakeClock takes UTC only (DateTimeKind.Utc)", nameof(value));
            utcNow = value;
        }

        /// <summary>Moves real time; a negative step models a clock set back.</summary>
        public void AdvanceReal(TimeSpan by)
        {
            utcNow = utcNow.Add(by);
        }

        public void SetUT(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "UT must be finite");
            ut = value;
        }

        public void AdvanceUT(double seconds)
        {
            SetUT(ut + seconds);
        }
    }
}
