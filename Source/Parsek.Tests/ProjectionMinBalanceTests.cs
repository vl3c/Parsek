using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RESERVATION-OVERLAY-GAPS (b): the funds / science modules keep the projection's
    /// unclamped minimum alongside the floored availability, so the currency tooltip can
    /// name a deficit instead of reporting "Reserved: 0" while one eats the pool.
    /// </summary>
    [Collection("Sequential")]
    public class ProjectionMinBalanceTests : IDisposable
    {
        public ProjectionMinBalanceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void Funds_ProjectionMinBalance_IsTheUnclampedMinimum_WhileAvailableIsFloored()
        {
            var funds = new FundsModule();
            funds.Reset();

            funds.SetProjectedAvailable(
                available: -12000, currentBalance: 50000, minProjectedBalance: -12000,
                finalProjectedBalance: 3000, futureActions: 2, deltaActions: 2);

            Assert.Equal(0.0, funds.GetAvailableFunds());
            Assert.Equal(-12000.0, funds.GetProjectionMinBalance());
        }

        [Fact]
        public void Funds_Reset_DropsTheProjectedMinimum()
        {
            var funds = new FundsModule();
            funds.Reset();
            funds.SetProjectedAvailable(
                available: -12000, currentBalance: 50000, minProjectedBalance: -12000,
                finalProjectedBalance: 3000, futureActions: 2, deltaActions: 2);

            funds.Reset();

            // No projection installed: the legacy unclamped availability (all zero after a reset).
            Assert.Equal(0.0, funds.GetProjectionMinBalance());
        }

        [Fact]
        public void Science_ProjectionMinBalance_IsTheUnclampedMinimum_WhileAvailableIsFloored()
        {
            var science = new ScienceModule();
            science.Reset();

            science.SetProjectedAvailable(
                available: -4.5, currentBalance: 20.0, minProjectedBalance: -4.5,
                finalProjectedBalance: 1.0, futureActions: 1, deltaActions: 1);

            Assert.Equal(0.0, science.GetAvailableScience());
            Assert.Equal(-4.5, science.GetProjectionMinBalance());
        }
    }
}
