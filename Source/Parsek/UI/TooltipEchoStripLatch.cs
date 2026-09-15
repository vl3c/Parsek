using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Observability latch for the hover-echo strip, and the one thing that made the GUI
    /// census's hover captures readable from <c>KSP.log</c> instead of only from a PNG.
    ///
    /// <para><b>WHY IT EXISTS.</b> The census's four hover captures
    /// (GUI-3's <c>ib-main-tooltip-logistics-advanced</c>, GUI-5's
    /// <c>cek-main-tooltip-career-advanced</c>, GUI-7's
    /// <c>b1-main-tooltip-timeline-advanced</c> and
    /// <c>b1-main-disabledecho-spawncontrol-advanced</c>) all photographed an UNHOVERED
    /// window, and nothing in the log said so: the only evidence was that the
    /// <c>TooltipEchoBox</c> strip node in the <c>.gui.json</c> was the same empty label as
    /// its non-hover sibling. Two mechanical dump / pixel comparisons had to be done by hand
    /// to establish it. A strip whose text CHANGES now says so on one Info line, so a lane
    /// can pin "the strip carried text" as a log contract and a reader of a red hover lane
    /// sees WHICH text (or none) rather than diffing images.</para>
    ///
    /// <para><b>NOT A UI SURFACE.</b> Nothing here draws, and nothing a player can reach
    /// changes: <see cref="TooltipEchoBox"/> calls <see cref="Observe"/> on its Repaint
    /// pass, after the text it was going to render either way. The one cost is a string
    /// compare per strip per Repaint.</para>
    ///
    /// <para><b>WHY THE LOG IS KEYED BY TEXT AND CAPPED.</b> A player sweeping the pointer
    /// across the Recordings table crosses dozens of tooltipped cells, so an unconditional
    /// per-change line would be per-frame-class noise in the primary debugging tool. The
    /// line fires once per DISTINCT text (<see cref="ShouldLogText"/>) and stops altogether
    /// after <see cref="MaxLoggedTexts"/> distinct texts, with one line saying so - the
    /// bound is on the SESSION, so the worst case is a fixed, small number of lines rather
    /// than a function of how long the window was open. A census lane crosses two or three
    /// controls, so it never approaches the cap.</para>
    ///
    /// <para><b>THE MOUSE-POSITION PROBE</b> (<see cref="ArmMousePositionProbe"/> /
    /// <see cref="SampleMousePositionProbe"/>) is the discriminator for
    /// GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT: it reports what
    /// <c>Event.current.mousePosition</c> reads INSIDE a Parsek <c>OnGUI</c> Repaint beside
    /// what <c>Input.mousePosition</c> reads in the same pass. The op's read-back already
    /// proves the second; only the first decides a hover. Armed by
    /// <c>UiAction op=pointer</c> and it fires exactly once per arm, so an unarmed game
    /// pays one bool test.</para>
    ///
    /// <para>Every value is passed IN: this type touches no Unity API, so its decisions are
    /// xUnit-covered headlessly (<c>TooltipEchoStripLatchTests</c>).</para>
    /// </summary>
    internal static class TooltipEchoStripLatch
    {
        /// <summary>Subsystem tag for every line this type writes. The same
        /// <c>[UI]</c> surface the windows themselves log under.</summary>
        internal const string Tag = "UI";

        /// <summary>Distinct strip texts that will be logged in one session, after which
        /// the latch goes quiet. See the class header for why the bound is per-session.</summary>
        internal const int MaxLoggedTexts = 200;

        /// <summary>Longest strip text carried on a log line. A tooltip is a sentence or
        /// two; a longer one is truncated with an ellipsis marker rather than wrapping a
        /// paragraph into the log.</summary>
        internal const int MaxLoggedTextLength = 240;

        /// <summary>What a truncated text ends with, so a reader can tell a clipped line
        /// from a short tooltip.</summary>
        internal const string TruncationMarker = "...";

        /// <summary>What an EMPTY strip is spelled as on the wire and in the log: the
        /// describe payload's absent-value sentinel, because a trailing <c>text=</c> reads
        /// as a truncated line.</summary>
        internal const string EmptyTextToken = "-";

        private static readonly HashSet<string> LoggedTexts =
            new HashSet<string>(StringComparer.Ordinal);

        private static string lastText = string.Empty;
        private static int lastFrame;
        private static bool capReported;
        private static bool probeArmed;

        /// <summary>The strip text as of the last Repaint that drew a strip, or the empty
        /// string when no strip has drawn (or the last one was empty). Read by
        /// <c>UiAction op=pointer</c>'s settle so the response and the log line carry what
        /// the capture beside them actually shows.</summary>
        internal static string LastText
        {
            get { return lastText ?? string.Empty; }
        }

        /// <summary>The frame <see cref="LastText"/> was observed in, so a reader can tell
        /// a live hover from a value left over from a strip that drew minutes ago.</summary>
        internal static int LastFrame
        {
            get { return lastFrame; }
        }

        /// <summary>Whether a mouse-position probe is waiting for the next strip
        /// Repaint.</summary>
        internal static bool ProbeArmed
        {
            get { return probeArmed; }
        }

        // ----- pure decisions -----

        /// <summary>
        /// Whether a strip text change is worth a log line: the text must have actually
        /// CHANGED, must be non-empty, must not have been logged already, and the
        /// per-session cap must not be spent.
        ///
        /// <para>An empty text is deliberately silent. The strip is permanently visible and
        /// empties every time the pointer leaves a control, so logging the empty state would
        /// double every line for no reader - and "the strip went blank" is exactly what the
        /// ABSENCE of a line after a hover step says.</para>
        /// </summary>
        internal static bool ShouldLogText(string text, string previousText,
                                           ICollection<string> alreadyLogged, int cap)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (string.Equals(text, previousText, StringComparison.Ordinal)) return false;
            if (alreadyLogged == null) return true;
            if (alreadyLogged.Contains(text)) return false;
            return alreadyLogged.Count < cap;
        }

        /// <summary>
        /// One strip text, rendered safe for a single log line: newlines and tabs collapsed
        /// to a literal <c>\n</c> / <c>\t</c> (a two-line tooltip must not split the line a
        /// log contract matches), truncated to <see cref="MaxLoggedTextLength"/>, and the
        /// empty string spelled <see cref="EmptyTextToken"/>.
        /// </summary>
        internal static string FormatForLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return EmptyTextToken;
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            string flat = sb.ToString();
            if (flat.Length <= MaxLoggedTextLength) return flat;
            return flat.Substring(0, MaxLoggedTextLength) + TruncationMarker;
        }

        // ----- live observation -----

        /// <summary>
        /// Records the text a strip is about to render. REPAINT ONLY - the caller owns that
        /// gate, because Layout reads a one-frame-stale value by the
        /// <see cref="TooltipEchoBox"/> contract and latching it would report a hover one
        /// frame after it ended.
        /// </summary>
        internal static void Observe(string text, int frame)
        {
            string incoming = text ?? string.Empty;
            bool log = ShouldLogText(incoming, lastText, LoggedTexts, MaxLoggedTexts);
            bool capJustSpent = !log && !capReported
                                && !string.IsNullOrEmpty(incoming)
                                && !string.Equals(incoming, lastText, StringComparison.Ordinal)
                                && !LoggedTexts.Contains(incoming)
                                && LoggedTexts.Count >= MaxLoggedTexts;

            lastText = incoming;
            if (!string.IsNullOrEmpty(incoming)) lastFrame = frame;

            if (log)
            {
                LoggedTexts.Add(incoming);
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "tooltip echo strip text={0} len={1} frame={2} distinct={3}",
                    FormatForLog(incoming), incoming.Length, frame, LoggedTexts.Count));
                return;
            }
            if (capJustSpent)
            {
                capReported = true;
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "tooltip echo strip log cap reached distinct={0}; further strip texts "
                    + "will not be logged this session", LoggedTexts.Count));
            }
        }

        /// <summary>Arms one mouse-position probe. Idempotent: a second arm before the
        /// next strip Repaint is the same one pending sample.</summary>
        internal static void ArmMousePositionProbe()
        {
            probeArmed = true;
        }

        /// <summary>
        /// Fires the armed probe, once, with the two positions a hover decision depends
        /// on. No-op when nothing is armed.
        ///
        /// <para>The IMGUI value is WINDOW-LOCAL inside a <c>GUI.Window</c> callback, which
        /// is the whole reason the caller also hands in its screen-space conversion: a
        /// window-local 133,109 and a screen-space 133,109 are different claims and only the
        /// second is comparable with <c>Input.mousePosition</c>.</para>
        /// </summary>
        /// <param name="eventX">Event.current.mousePosition.x, window-local, y DOWN.</param>
        /// <param name="eventY">Event.current.mousePosition.y, window-local, y DOWN.</param>
        /// <param name="eventScreenX">The same point through GUIUtility.GUIToScreenPoint.</param>
        /// <param name="eventScreenY">Ditto, y DOWN in screen space.</param>
        /// <param name="inputX">Input.mousePosition.x, screen, y UP.</param>
        /// <param name="inputY">Input.mousePosition.y, screen, y UP.</param>
        /// <param name="screenHeight">Screen.height, so the reader can flip either one.</param>
        /// <param name="frame">Time.frameCount.</param>
        internal static void SampleMousePositionProbe(
            float eventX, float eventY, float eventScreenX, float eventScreenY,
            float inputX, float inputY, int screenHeight, int frame)
        {
            if (!probeArmed) return;
            probeArmed = false;
            CultureInfo ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose(Tag, string.Format(ic,
                "tooltip echo strip pointer probe eventLocal={0},{1} eventScreen={2},{3} "
                + "input={4},{5} inputGuiY={6} screenH={7} frame={8} text={9}",
                eventX.ToString("F1", ic), eventY.ToString("F1", ic),
                eventScreenX.ToString("F1", ic), eventScreenY.ToString("F1", ic),
                inputX.ToString("F1", ic), inputY.ToString("F1", ic),
                (screenHeight - inputY).ToString("F1", ic),
                screenHeight.ToString(ic), frame.ToString(ic),
                FormatForLog(lastText)));
        }

        /// <summary>Drops every latched value. Static state, so the xUnit suite resets it
        /// between cells (<c>[Collection("Sequential")]</c>).</summary>
        internal static void ResetForTesting()
        {
            LoggedTexts.Clear();
            lastText = string.Empty;
            lastFrame = 0;
            capReported = false;
            probeArmed = false;
        }
    }
}
