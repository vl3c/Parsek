using System.Globalization;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The LOOPED-INTERPLANETARY synthetic corpus: ONE looped recording whose
    /// OrbitSegment chain is the REAL FLOWN duna-direct geometry, byte-pinned.
    ///
    /// <para>
    /// PROVENANCE. Every segment below is extracted VERBATIM from the committed
    /// <c>harness/fixtures/saves/duna-direct-recorded</c> fixture's main
    /// recording (id 311d98e32547491e8dd37aec2526d25d, the B17 green flight,
    /// run 2026-08-06_1527): the DD1 Duna Direct Probe's pad-aligned direct
    /// Kerbin -> Sun -> Duna transfer + Ike-clear capture. Real flown elements
    /// rather than invented ones, deliberately: the SOI-seam continuity the
    /// Tier-1 cells assert must be a property the AUTHORED data actually has
    /// (KSP recorded both sides of each handoff from one physical vessel), so
    /// the assertion measures the PLAYBACK path's segment dispatch, never the
    /// fixture author's orbital mechanics.
    /// </para>
    ///
    /// <para>
    /// ABSOLUTE UTs, deliberately NOT rebased to the target save's clock (the
    /// RewindB9 fixture rebases; this one must not): every segment's world
    /// position is body-position(UT) + orbit-offset(UT), so the cross-SOI seam
    /// continuity only holds at the UTs the flight actually flew -- rebasing
    /// the recording would move the vessel-frame geometry but not the planets,
    /// tearing both seams open by the bodies' own displacement. The consuming
    /// in-game cells evaluate positions at these recorded UTs; nothing needs
    /// "now" to be inside the window.
    /// </para>
    ///
    /// <para>
    /// The two SOI seams this fixture exists to cover (exact, adjacent
    /// endUT == startUT pairs in the flown data):
    ///   Kerbin -> Sun at UT 4742953.2210230371 (escape hyperbola handoff)
    ///   Sun -> Duna  at UT 9128108.75539902   (arrival hyperbola handoff)
    /// </para>
    /// </summary>
    internal static class LoopedInterplanetaryFixture
    {
        internal const string RecordingId = "loopedinterp0000000000000000dd17";
        internal const string VesselName = "Looped Interplanetary DD1";
        internal const string GroupName = "Synthetic-Interplanetary";

        // The two seam UTs, exported for the headless generator tests (the
        // in-game cells re-derive them from the committed segments through the
        // same pure enumerator they gate on, never from these constants).
        internal const double KerbinToSunSeamUT = 4742953.2210230371;
        internal const double SunToDunaSeamUT = 9128108.75539902;

        // body, startUT, endUT, inc, ecc, sma, lan, argPe, mna, epoch --
        // verbatim from the flown recording's 16 non-predicted ORBIT_SEGMENTs,
        // in recorded order. (The flown ofr* orientation quaternions are
        // deliberately dropped: the Tier-1 cells assert POSITION resolution,
        // and a default orientation keeps the fixture minimal per the
        // recording design principle.)
        private static readonly object[][] Segments = new object[][]
        {
            new object[] { "Kerbin", 4653692.5977440914, 4653784.4578612577, 0.096995657629491583, 0.47071196053591829, 476031.51912352763, 92.4846085136254, 117.78011560304961, 2.442971906375619, 4653692.5977440914 },
            new object[] { "Kerbin", 4653851.2978597637, 4654658.7574508507, 0.085734295510697681, 0.0098739379304471318, 699739.0692184224, 119.66469337081321, 177.09307073015253, 1.7291578909493466, 4653851.2978597637 },
            new object[] { "Kerbin", 4654664.9774507117, 4655034.96745071, 0.085734295466069782, 0.00987393802802108, 699739.06913629186, 119.66469331528702, 177.09307129244297, 4.3415297158090542, 4654664.9774507117 },
            new object[] { "Kerbin", 4654664.9774507117, 4655255.6953437859, 0.085734295466069782, 0.00987393802802108, 699739.06913629186, 119.66469331528702, 177.09307129244297, 4.3415297158090542, 4654664.9774507117 },
            new object[] { "Kerbin", 4655339.1153419213, 4742921.4116212241, 0.085734126521499851, 1.1293300064853573, -5357267.2527378509, 119.66742461454163, 182.07292894894894, 0.0064436591616006469, 4655339.1153419213 },
            new object[] { "Kerbin", 4742923.2316211835, 4742953.2210230371, 0.085734126528027588, 1.129330006485276, -5357267.2527406393, 119.66742461241643, 182.07292895106787, 13.280270202871776, 4742923.2316211835 },
            new object[] { "Sun", 4742953.2210230371, 4835963.7697974527, 0.0031630134224745906, 0.19487127731253925, 16894163161.981085, 181.3485308808977, 181.51899028366046, 0.033657216352832295, 4742953.2058721324 },
            new object[] { "Sun", 4836036.2668779157, 8636191.9577849023, 0.019906738876677793, 0.219752677408991, 17451458915.501793, 11.088794871422806, 0.7130499972048957, 6.2609337280003814, 4836036.2668779157 },
            new object[] { "Sun", 8636192.47778489, 8636228.498037478, 0.019906738876673352, 0.21975267740900084, 17451458915.501869, 11.088794871430878, 0.71304999719893747, 1.7625062575197221, 8636192.47778489 },
            new object[] { "Sun", 8636249.7388268374, 9127699.2386266328, 0.026216554346662403, 0.21614432887100377, 17681801085.266754, 354.35935434056995, 20.899438443699157, 1.6979590226018364, 8636249.7388268374 },
            new object[] { "Sun", 8636249.7388268374, 9128076.80623646, 0.026216554346662403, 0.21614432887100377, 17681801085.266754, 354.35935434056995, 20.899438443699157, 1.6979590226018364, 8636249.7388268374 },
            new object[] { "Sun", 9128078.766236417, 9128108.75539902, 0.026216554346668086, 0.21614432887100446, 17681801085.266529, 354.35935434056091, 20.8994384437053, 1.9244494561467962, 9128078.766236417 },
            new object[] { "Duna", 9128108.75539902, 9128151.7463560514, 1.9668948574153007, 8.5362034650755945, -137763.92092870534, 283.66710078345614, 108.9127532447616, -344.34779380007234, 9128108.7070987541 },
            new object[] { "Duna", 9128151.766356051, 9128152.84791367, 1.9668948573998524, 8.5362034650545056, -137763.92092870918, 283.66710078335882, 108.91275324487442, -343.88551028091081, 9128151.766356051 },
            new object[] { "Duna", 9128153.3479136582, 9159555.6416856013, 1.966894857378761, 8.5362034650406162, -137763.92092871643, 283.66710078322666, 108.9127532450165, -343.86853070286156, 9128153.3479136582 },
            new object[] { "Duna", 9159565.4416853823, 9160152.7036970723, 1.9668948575415708, 8.5362034654090824, -137763.92092991679, 283.66710079277959, 108.91275322451303, -6.6287789089162423, 9159565.4416853823 },
        };

        internal static double RecordingStartUT
        {
            get { return (double)Segments[0][1]; }
        }

        internal static double RecordingEndUT
        {
            get { return (double)Segments[Segments.Length - 1][2]; }
        }

        /// <summary>
        /// Build the looped interplanetary recording. Loop interval is left at
        /// the builder's duration fallback (the ~4.5M-second flown span): the
        /// Tier-1 cells gate the loop-clock mapping and segment dispatch, and
        /// mission-level (re-aim) cadence is deliberately out of this
        /// fixture's scope -- re-aim engagement is a Mission property the
        /// Tier-2 lane arms on the committed REAL fixture instead.
        /// </summary>
        internal static RecordingBuilder BuildRecording()
        {
            var builder = new RecordingBuilder(VesselName)
                .WithRecordingId(RecordingId)
                .WithRecordingGroup(GroupName)
                .WithSegmentBodyName("Kerbin")
                .WithLoopPlayback(true, 0.0)
                .WithTerminalState((int)global::Parsek.TerminalState.Orbiting);
            foreach (object[] row in Segments)
            {
                builder.AddOrbitSegment(
                    startUT: (double)row[1], endUT: (double)row[2],
                    inc: (double)row[3], ecc: (double)row[4],
                    sma: (double)row[5], lan: (double)row[6],
                    argPe: (double)row[7], mna: (double)row[8],
                    epoch: (double)row[9], body: (string)row[0]);
            }
            // Two boundary state-vector points so bounds resolution and the
            // points-presence sweeps have honest endpoints (a segment-only
            // recording is not a shape the recorder ever writes). Values from
            // the flown endpoints: launch pad-side sample and the parked
            // terminal sample (lat/lon/alt approximated to the segment frame;
            // the Tier-1 cells never read these points).
            builder.AddPoint(RecordingStartUT, -0.0972, -74.5577, 75.0, "Kerbin");
            builder.AddPoint(RecordingEndUT, 0.0, 0.0, 300000.0, "Duna");
            return builder;
        }

        /// <summary>Populate <paramref name="writer"/> with the fixture tree.</summary>
        internal static void PopulateWriter(ScenarioWriter writer)
        {
            writer.AddRecordingAsTree(BuildRecording());
        }
    }
}
