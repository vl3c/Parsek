using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The committed-future inputs the stock-screen decoration postfixes read, memoized per
    /// Unity frame and per index instance. Stock calls the decorated methods once per row
    /// or per node (a tech tree refresh runs <c>RDNode.UpdateGraphics</c> for every node),
    /// so the index, the clock and the Astronaut Complex lookups are fetched once per frame
    /// rather than once per row. The index itself is cached by
    /// <see cref="CommittedFutureIndexCache"/>; a ledger change inside the frame produces a
    /// new index instance, which drops the memo.
    /// </summary>
    internal sealed class StockUiLiveSnapshot
    {
        private static StockUiLiveSnapshot cached;
        private static int cachedFrame = int.MinValue;

        internal readonly CommittedFutureIndex Index;
        internal readonly double UT;
        private AstronautComplexContext astronautContext;
        private readonly Dictionary<string, StockUiDecoration> techById =
            new Dictionary<string, StockUiDecoration>(StringComparer.Ordinal);
        private readonly Dictionary<string, StockUiDecoration> kerbalByName =
            new Dictionary<string, StockUiDecoration>(StringComparer.Ordinal);

        private StockUiLiveSnapshot(CommittedFutureIndex index, double ut)
        {
            Index = index;
            UT = ut;
        }

        /// <summary>The snapshot for this frame. Live-only (reads Unity's frame counter).</summary>
        internal static StockUiLiveSnapshot Current
        {
            get
            {
                var index = CommittedFutureIndexCache.Current;
                int frame = ReadFrameCount();
                var snapshot = cached;
                if (snapshot == null || frame != cachedFrame || !ReferenceEquals(snapshot.Index, index))
                {
                    snapshot = new StockUiLiveSnapshot(index, CommittedFutureIndexCache.CurrentUT());
                    cached = snapshot;
                    cachedFrame = frame;
                }
                return snapshot;
            }
        }

        /// <summary>Drops the memo so the next read rebuilds (a timeline change re-pass).</summary>
        internal static void Invalidate()
        {
            cached = null;
            cachedFrame = int.MinValue;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadFrameCount()
        {
            return Time.frameCount;
        }

        internal AstronautComplexContext AstronautContext
        {
            get
            {
                if (astronautContext == null)
                    astronautContext = StockUiOverlayController.BuildLiveAstronautContext(
                        StockUiOverlayController.CollectActiveCrewOrTouristNames());
                return astronautContext;
            }
        }

        /// <summary>The R&amp;D decision for one tech node (<see cref="StockUiDecorationQuery.ForRnD"/>).</summary>
        internal StockUiDecoration Tech(string techId)
        {
            if (string.IsNullOrEmpty(techId))
                return default(StockUiDecoration);
            StockUiDecoration d;
            if (techById.TryGetValue(techId, out d))
                return d;
            var list = StockUiDecorationQuery.ForRnD(Index, UT, new[] { techId },
                ReservationExplanation.DefaultDateFormatter);
            d = list.Count > 0 ? list[0] : default(StockUiDecoration);
            techById[techId] = d;
            return d;
        }

        /// <summary>
        /// The Astronaut Complex decision for one kerbal
        /// (<see cref="StockUiDecorationQuery.ForAstronautComplex"/>). The tab only labels
        /// the record: the decision is by name, so a cloned row (Enhanced Astronaut
        /// Complex) gets the same answer as the stock one.
        /// </summary>
        internal StockUiDecoration Kerbal(string kerbalName, string tab)
        {
            if (string.IsNullOrEmpty(kerbalName))
                return default(StockUiDecoration);
            StockUiDecoration d;
            if (!kerbalByName.TryGetValue(kerbalName, out d))
            {
                var list = StockUiDecorationQuery.ForAstronautComplex(Index, UT,
                    new[] { new StockUiItem(kerbalName, tab) },
                    AstronautContext,
                    ReservationExplanation.DefaultDateFormatter);
                d = list.Count > 0 ? list[0] : default(StockUiDecoration);
                kerbalByName[kerbalName] = d;
            }
            d.Tab = string.IsNullOrEmpty(tab) ? d.Tab : tab;
            return d;
        }
    }

    /// <summary>
    /// Reads and writes the text of a stock TextMeshPro label without a compile-time
    /// reference to TextMeshPro (Parsek does not reference Unity.TextMeshPro): the
    /// <c>text</c> property is resolved by reflection per runtime type.
    /// </summary>
    internal static class StockUiText
    {
        private static readonly Dictionary<Type, PropertyInfo> textProperties = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<string, FieldInfo> labelFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        /// <summary>
        /// The TextMeshPro label held in <paramref name="fieldName"/> of
        /// <paramref name="declaringType"/> (public or private), or null.
        /// </summary>
        internal static object LabelField(object owner, Type declaringType, string fieldName)
        {
            if (owner == null || declaringType == null || string.IsNullOrEmpty(fieldName)) return null;
            string key = declaringType.FullName + "." + fieldName;
            FieldInfo field;
            if (!labelFields.TryGetValue(key, out field))
            {
                field = declaringType.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                labelFields[key] = field;
                if (field == null)
                    ParsekLog.Warn("StockUiOverlay",
                        declaringType.FullName + "." + fieldName + " not found - that stock label is not annotated");
            }
            return field != null ? field.GetValue(owner) : null;
        }

        private static PropertyInfo TextProperty(object label)
        {
            if (label == null) return null;
            Type t = label.GetType();
            PropertyInfo prop;
            if (!textProperties.TryGetValue(t, out prop))
            {
                prop = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && (prop.PropertyType != typeof(string) || !prop.CanRead || !prop.CanWrite))
                    prop = null;
                textProperties[t] = prop;
            }
            return prop;
        }

        internal static string Get(object label)
        {
            var prop = TextProperty(label);
            return prop != null ? prop.GetValue(label, null) as string : null;
        }

        internal static bool Set(object label, string text)
        {
            var prop = TextProperty(label);
            if (prop == null) return false;
            prop.SetValue(label, text, null);
            return true;
        }
    }
}
