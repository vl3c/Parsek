using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Parsek;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Keeps the enumerable clause catalogue honest against the constants it
    /// claims to enumerate. The catalogue exists so the GUI state gallery's
    /// completeness guard (design section 11) can walk the Logistics reason
    /// vocabulary mechanically; a constant that never reaches
    /// <c>All</c> would be a reason the guard cannot see, which is exactly the
    /// best-effort scrape the extraction replaced.
    ///
    /// Reflection over the declaring classes, not a hand-kept second list: a
    /// new clause constant reds here until it is catalogued.
    /// </summary>
    public class LogisticsClauseCatalogTests
    {
        // catches: a clause constant added to the class but never catalogued,
        // and a catalogue row naming a constant that does not exist.
        [Fact]
        public void EveryHoldConstantIsCataloguedExactlyOnce()
        {
            AssertCatalogueMatchesConstants(
                typeof(LogisticsHoldClauses), LogisticsHoldClauses.All, LogisticsClauseKind.Hold);
        }

        // catches: the same, for the reject half.
        [Fact]
        public void EveryRejectConstantIsCataloguedExactlyOnce()
        {
            AssertCatalogueMatchesConstants(
                typeof(LogisticsRejectClauses), LogisticsRejectClauses.All, LogisticsClauseKind.Reject);
        }

        // catches: a catalogue row whose Format drifted from the constant it
        // names (a copy-paste into the wrong row reads as coverage otherwise).
        [Fact]
        public void EveryCatalogueRowCarriesItsConstantsOwnText()
        {
            foreach (LogisticsClause clause in LogisticsClauseCatalog.All)
            {
                Type owner = clause.Kind == LogisticsClauseKind.Hold
                    ? typeof(LogisticsHoldClauses)
                    : typeof(LogisticsRejectClauses);
                Assert.Equal(DeclaredConstants(owner)[clause.Name], clause.Format);
            }
        }

        // catches: two clauses sharing a name, which would let one hide behind
        // the other in any name-keyed guard.
        [Fact]
        public void CatalogueNamesAreUniqueAcrossBothHalves()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (LogisticsClause clause in LogisticsClauseCatalog.All)
                Assert.True(seen.Add(clause.Name), "duplicate clause name: " + clause.Name);
        }

        // catches: a blank or whitespace clause, which would render as a hold
        // or reject with no reason at all.
        [Fact]
        public void NoClauseIsBlank()
        {
            foreach (LogisticsClause clause in LogisticsClauseCatalog.All)
                Assert.False(string.IsNullOrWhiteSpace(clause.Format), clause.Name + " is blank");
        }

        // catches: the two halves being wired to the same list, or one going
        // missing from the combined catalogue.
        [Fact]
        public void TheCombinedCatalogueIsTheTwoHalvesInOrder()
        {
            Assert.Equal(
                LogisticsHoldClauses.All.Count + LogisticsRejectClauses.All.Count,
                LogisticsClauseCatalog.All.Count);
            Assert.All(
                LogisticsClauseCatalog.All.Take(LogisticsHoldClauses.All.Count),
                c => Assert.Equal(LogisticsClauseKind.Hold, c.Kind));
            Assert.All(
                LogisticsClauseCatalog.All.Skip(LogisticsHoldClauses.All.Count),
                c => Assert.Equal(LogisticsClauseKind.Reject, c.Kind));
        }

        // catches: the reflection walk going vacuous (an empty constants class,
        // or a rename that makes DeclaredConstants find nothing, would pass
        // every cell above). The floors are the counts measured at extraction:
        // 29 long-form hold clauses + 29 compact + 6 frames, and 12 reject
        // clauses (11 RouteAnalysisStatus arms + the not-fully-sealed gate).
        [Fact]
        public void TheWalkIsNotVacuous()
        {
            Assert.Equal(64, LogisticsHoldClauses.All.Count);
            Assert.Equal(12, LogisticsRejectClauses.All.Count);
            Assert.Equal(64, DeclaredConstants(typeof(LogisticsHoldClauses)).Count);
            Assert.Equal(12, DeclaredConstants(typeof(LogisticsRejectClauses)).Count);
        }

        private static void AssertCatalogueMatchesConstants(
            Type owner, IReadOnlyList<LogisticsClause> catalogue, LogisticsClauseKind expectedKind)
        {
            Dictionary<string, string> declared = DeclaredConstants(owner);

            var catalogued = new List<string>();
            foreach (LogisticsClause clause in catalogue)
            {
                Assert.Equal(expectedKind, clause.Kind);
                Assert.True(declared.ContainsKey(clause.Name),
                    owner.Name + " catalogues '" + clause.Name + "', which it does not declare");
                catalogued.Add(clause.Name);
            }

            List<string> missing = declared.Keys.Where(k => !catalogued.Contains(k)).ToList();
            Assert.True(missing.Count == 0,
                owner.Name + " declares uncatalogued clauses: " + string.Join(", ", missing));

            List<string> duplicated = catalogued
                .GroupBy(n => n, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Assert.True(duplicated.Count == 0,
                owner.Name + " catalogues clauses twice: " + string.Join(", ", duplicated));
        }

        /// <summary>
        /// Every <c>const string</c> the class declares, by field name. Literal
        /// constants only (<see cref="FieldInfo.IsLiteral"/>), so the private
        /// backing field of the <c>All</c> list is not mistaken for a clause.
        /// </summary>
        private static Dictionary<string, string> DeclaredConstants(Type owner)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (FieldInfo f in owner.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!f.IsLiteral || f.IsInitOnly || f.FieldType != typeof(string))
                    continue;
                map[f.Name] = (string)f.GetRawConstantValue();
            }
            return map;
        }
    }
}
