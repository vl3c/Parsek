using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Parsek.InGameTests.Spikes.S12
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S12.
    //
    // The pure half of the crypto probe: the fixed inputs and their pinned digests, the
    // step record, the log line and CSV row, the verdict, and the key-store listing diff
    // the container probe uses to say what it left behind. Nothing here names a
    // System.Core crypto type (ECDsa, ECCurve, HashAlgorithmName live in the probe
    // bodies only), so this file loads on any Mono.

    internal enum SpikeS12Status
    {
        Ok,
        Fail,
        Skip,
    }

    /// <summary>One probe step: what ran, how long, and what it threw.</summary>
    internal sealed class SpikeS12Step
    {
        internal string Name;
        internal SpikeS12Status Status;
        internal double Ms;
        internal long Bytes;
        internal string Detail;
        internal string ErrorType;
        internal string ErrorMessage;

        internal bool IsOk => Status == SpikeS12Status.Ok;
    }

    internal static class SpikeS12Plan
    {
        internal const string SpikeId = "S12";
        internal static readonly string Tag = SpikeArming.SpikeTag(SpikeId);

        internal const int OneMb = 1 << 20;
        internal const int TenMb = 10 << 20;
        internal const int BlobBytes = 1024;
        internal const int WarmRepeats = 3;
        internal const int RsaKeyBits = 2048;
        internal const int EcKeyBits = 256;

        /// <summary>Plan "Pass": SHA-256 over 10 MB under this many milliseconds.</summary>
        internal const double ShaPassMs = 100.0;

        internal const string ProbeCsvName = "probe";
        internal const string VerdictCsvName = "verdict";
        internal const string ContainerPrefix = "parsek-spike-s12-";
        internal const string PublicKeyFilePrefix = "rsa-public-";

        // Pinned with Python hashlib over the same FillBuffer bytes (and FIPS 180-2 "abc").
        internal const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        internal const string BlobSha256 = "40e6fe33469db77988e8d2e4094112fdbfdb3da5b03b788e1cdce3908f88ec57";
        internal const string OneMbSha256 = "ca6073392ee71dbd1a2d356c3caa233f8f828ae17f8f8ba8570ee3491be128ab";
        internal const string TenMbSha256 = "e970d0c37592ab4541e5e3522b9eb7874822a0d8b763a6626c62a7a1b8d6023d";

        // Step names the verdict reads.
        internal const string StepShaKat = "sha256.kat";
        internal const string StepSha10 = "sha256.10mb";
        internal const string StepSha10Warm = "sha256.10mb.warm";
        internal const string StepRsaCreate = "rsa.create";
        internal const string StepRsaSign = "rsa.sign";
        internal const string StepRsaVerify = "rsa.verify";
        internal const string StepRsaSignLegacy = "rsa.sign.legacy";
        internal const string StepRsaVerifyLegacy = "rsa.verify.legacy";
        internal const string StepRsaTampered = "rsa.verify.tampered";
        internal const string StepRsaXmlImport = "rsa.xml.import";
        internal const string StepRsaParams = "rsa.params.roundtrip";
        internal const string StepEcCreate = "ecdsa.create";
        internal const string StepEcCreateDefault = "ecdsa.create.default";
        internal const string StepEcSign = "ecdsa.sign";
        internal const string StepEcVerify = "ecdsa.verify";
        internal const string StepEcTampered = "ecdsa.verify.tampered";
        internal const string StepEcParams = "ecdsa.params.roundtrip";
        internal const string StepContainerPersist = "container.persist";
        internal const string StepContainerReopen = "container.reopen";
        internal const string StepContainerDelete = "container.delete";
        internal const string StepContainerLeftover = "container.leftover";

        internal static readonly string[] ProbeCsvHeader =
            { "run", "name", "status", "ms", "bytes", "detail", "errorType", "errorMessage" };

        internal static readonly string[] VerdictCsvHeader =
            { "run", "sha10mbFirstMs", "sha10mbWarmMs", "shaOk", "rsaChain", "ecdsaChain", "container", "fails", "skips", "result" };

        /// <summary>Deterministic filler: byte i = top byte of (i * 2654435761) mod 2^32.</summary>
        internal static byte[] FillBuffer(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            var buf = new byte[length];
            for (int i = 0; i < length; i++)
                buf[i] = (byte)(unchecked((uint)i * 2654435761u) >> 24);
            return buf;
        }

        /// <summary>Lower-case hex; null -> "".</summary>
        internal static string ToHex(byte[] bytes)
        {
            if (bytes == null) return "";
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>"s12-20261117T093000Z".</summary>
        internal static string RunLabel(DateTime utc)
            => "s12-" + utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        /// <summary>The CSP container name of one run (unique, so no earlier run's key is reused).</summary>
        internal static string ContainerName(string run) => ContainerPrefix + (run ?? "");

        internal static string PublicKeyFileName(string run) => PublicKeyFilePrefix + (run ?? "") + ".xml";

        /// <summary>Median of the values (mean of the middle two for an even count); NaN when empty.</summary>
        internal static double Median(IList<double> values)
        {
            if (values == null || values.Count == 0) return double.NaN;
            var sorted = new List<double>(values);
            sorted.Sort();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        /// <summary>One line of text: CR / LF / tab to spaces, double quotes to single.</summary>
        internal static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else if (c == '"') sb.Append('\'');
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        internal static string StatusToken(SpikeS12Status s)
            => s == SpikeS12Status.Ok ? "ok" : s == SpikeS12Status.Skip ? "skip" : "fail";

        internal static string Ms(double ms)
            => double.IsNaN(ms) ? "NaN" : ms.ToString("F3", CultureInfo.InvariantCulture);

        /// <summary>
        /// The "Type: message" of an exception, unwrapping TargetInvocationException and
        /// TypeInitializationException to the inner one (the type that actually failed).
        /// </summary>
        internal static void Describe(Exception ex, out string type, out string message)
        {
            Exception e = ex;
            while (e != null && e.InnerException != null
                && (e is System.Reflection.TargetInvocationException || e is TypeInitializationException))
                e = e.InnerException;
            if (e == null)
            {
                type = "";
                message = "";
                return;
            }
            type = e.GetType().FullName ?? e.GetType().Name;
            message = OneLine(e.Message);
            if (!ReferenceEquals(e, ex))
                message = message + " (wrapped in " + ex.GetType().Name + ")";
        }

        /// <summary>The body of the [Parsek][INFO][Spike.S12] step line.</summary>
        internal static string StepLine(string run, SpikeS12Step s)
        {
            var sb = new StringBuilder();
            sb.Append("step run=").Append(run)
              .Append(" name=").Append(s.Name)
              .Append(" status=").Append(StatusToken(s.Status))
              .Append(" ms=").Append(Ms(s.Ms))
              .Append(" bytes=").Append(s.Bytes.ToString(CultureInfo.InvariantCulture))
              .Append(" detail=\"").Append(OneLine(s.Detail)).Append('"');
            if (!string.IsNullOrEmpty(s.ErrorType))
                sb.Append(" error=").Append(s.ErrorType)
                  .Append(" message=\"").Append(OneLine(s.ErrorMessage)).Append('"');
            return sb.ToString();
        }

        internal static object[] StepRow(string run, SpikeS12Step s)
            => new object[]
            {
                run, s.Name, StatusToken(s.Status), s.Ms, s.Bytes, OneLine(s.Detail),
                s.ErrorType ?? "", OneLine(s.ErrorMessage),
            };

        /// <summary>The run's verdict, read from the step list.</summary>
        internal sealed class Verdict
        {
            internal double Sha10FirstMs = double.NaN;
            internal double Sha10WarmMs = double.NaN;
            internal bool ShaOk;
            internal bool RsaChain;
            internal bool EcdsaChain;
            internal string Container = "not-run";
            internal int Fails;
            internal int Skips;

            internal bool Pass => ShaOk && (RsaChain || EcdsaChain);
        }

        internal static SpikeS12Step Find(IList<SpikeS12Step> steps, string name)
        {
            if (steps == null) return null;
            foreach (var s in steps)
                if (s != null && s.Name == name) return s;
            return null;
        }

        private static bool Ok(IList<SpikeS12Step> steps, string name)
        {
            var s = Find(steps, name);
            return s != null && s.IsOk;
        }

        /// <summary>
        /// Plan "Pass": SHA-256 over 10 MB under 100 ms (the warm median, with the pinned
        /// digest and the "abc" vector right); at least one signing algorithm creates,
        /// signs, verifies, refuses a tampered blob and round-trips its key. RSA counts
        /// with either the HashAlgorithmName API or the legacy string-named one, and with
        /// either the XML or the parameters round trip.
        /// </summary>
        internal static Verdict Judge(IList<SpikeS12Step> steps)
        {
            var v = new Verdict();
            if (steps == null) return v;
            foreach (var s in steps)
            {
                if (s == null) continue;
                if (s.Status == SpikeS12Status.Fail) v.Fails++;
                else if (s.Status == SpikeS12Status.Skip) v.Skips++;
            }

            var first = Find(steps, StepSha10);
            var warm = Find(steps, StepSha10Warm);
            if (first != null) v.Sha10FirstMs = first.Ms;
            if (warm != null) v.Sha10WarmMs = warm.Ms;
            v.ShaOk = Ok(steps, StepShaKat) && first != null && first.IsOk
                && warm != null && warm.IsOk && !double.IsNaN(warm.Ms) && warm.Ms < ShaPassMs;

            bool rsaSigned = (Ok(steps, StepRsaSign) && Ok(steps, StepRsaVerify))
                || (Ok(steps, StepRsaSignLegacy) && Ok(steps, StepRsaVerifyLegacy));
            v.RsaChain = Ok(steps, StepRsaCreate) && rsaSigned && Ok(steps, StepRsaTampered)
                && (Ok(steps, StepRsaXmlImport) || Ok(steps, StepRsaParams));

            v.EcdsaChain = (Ok(steps, StepEcCreate) || Ok(steps, StepEcCreateDefault))
                && Ok(steps, StepEcSign) && Ok(steps, StepEcVerify) && Ok(steps, StepEcTampered)
                && Ok(steps, StepEcParams);

            v.Container = ContainerVerdict(steps);
            return v;
        }

        /// <summary>
        /// not-run (no container step), writable (persisted, reopened with the same key,
        /// deleted, nothing left), left-behind (persisted but a file stayed), not-writable
        /// (the persist step failed).
        /// </summary>
        internal static string ContainerVerdict(IList<SpikeS12Step> steps)
        {
            var persist = Find(steps, StepContainerPersist);
            if (persist == null || persist.Status == SpikeS12Status.Skip) return "not-run";
            if (!persist.IsOk) return "not-writable";
            var leftover = Find(steps, StepContainerLeftover);
            if (leftover != null && !leftover.IsOk) return "left-behind";
            if (Ok(steps, StepContainerReopen) && Ok(steps, StepContainerDelete)) return "writable";
            return "partial";
        }

        internal static string VerdictLine(string run, Verdict v)
            => "verdict run=" + run
                + " sha10mbFirstMs=" + Ms(v.Sha10FirstMs)
                + " sha10mbWarmMs=" + Ms(v.Sha10WarmMs)
                + " shaOk=" + (v.ShaOk ? "true" : "false")
                + " rsaChain=" + (v.RsaChain ? "true" : "false")
                + " ecdsaChain=" + (v.EcdsaChain ? "true" : "false")
                + " container=" + v.Container
                + " fails=" + v.Fails.ToString(CultureInfo.InvariantCulture)
                + " skips=" + v.Skips.ToString(CultureInfo.InvariantCulture)
                + " result=" + (v.Pass ? "PASS" : "FAIL");

        internal static object[] VerdictRow(string run, Verdict v)
            => new object[]
            {
                run, v.Sha10FirstMs, v.Sha10WarmMs, v.ShaOk, v.RsaChain, v.EcdsaChain,
                v.Container, v.Fails, v.Skips, v.Pass ? "PASS" : "FAIL",
            };

        // ----- key store listing (what the container probe left behind) -----

        /// <summary>
        /// Where a persisted CSP key can land on the owner's PC, in order: Mono's user and
        /// machine key-pair stores (Mono.Security KeyPairPersistence:
        /// &lt;ApplicationData&gt;/.mono/keypairs and &lt;CommonApplicationData&gt;/.mono/keypairs)
        /// and the Windows CryptoAPI user stores (&lt;ApplicationData&gt;/Microsoft/Crypto/RSA
        /// and .../Keys) a .NET Framework runtime would use. Empty roots are left out.
        /// </summary>
        internal static List<string> KeyStoreDirs(string applicationData, string commonApplicationData)
        {
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(applicationData))
            {
                dirs.Add(Path.Combine(Path.Combine(applicationData, ".mono"), "keypairs"));
            }
            if (!string.IsNullOrEmpty(commonApplicationData))
            {
                dirs.Add(Path.Combine(Path.Combine(commonApplicationData, ".mono"), "keypairs"));
            }
            if (!string.IsNullOrEmpty(applicationData))
            {
                string crypto = Path.Combine(Path.Combine(applicationData, "Microsoft"), "Crypto");
                dirs.Add(Path.Combine(crypto, "RSA"));
                dirs.Add(Path.Combine(crypto, "Keys"));
            }
            return dirs;
        }

        /// <summary>
        /// A listing of the given directories: one "dir|&lt;path&gt;|missing" or
        /// "dir|&lt;path&gt;|present" entry per directory, then one
        /// "file|&lt;full path&gt;|&lt;length&gt;|&lt;last write ticks&gt;" per file under it
        /// (recursive). Unreadable parts become "error|&lt;path&gt;|&lt;type&gt;". Reads only.
        /// </summary>
        internal static SortedSet<string> Snapshot(IEnumerable<string> dirs)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (dirs == null) return set;
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        set.Add("dir|" + dir + "|missing");
                        continue;
                    }
                    set.Add("dir|" + dir + "|present");
                    foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var info = new FileInfo(f);
                            set.Add("file|" + f + "|" + info.Length.ToString(CultureInfo.InvariantCulture)
                                + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                        }
                        catch (Exception ex)
                        {
                            set.Add("error|" + f + "|" + ex.GetType().Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    set.Add("error|" + dir + "|" + ex.GetType().Name);
                }
            }
            return set;
        }

        /// <summary>The difference of two listings, by path.</summary>
        internal sealed class ListingDiff
        {
            internal readonly List<string> Added = new List<string>();
            internal readonly List<string> Removed = new List<string>();
            internal readonly List<string> Changed = new List<string>();
            internal readonly List<string> DirsCreated = new List<string>();

            internal bool Empty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0 && DirsCreated.Count == 0;

            internal string Describe()
                => "added=" + Join(Added) + " removed=" + Join(Removed) + " changed=" + Join(Changed)
                    + " dirsCreated=" + Join(DirsCreated);

            private static string Join(List<string> list)
                => list.Count == 0 ? "none" : string.Join(";", list.ToArray());
        }

        internal static ListingDiff Diff(SortedSet<string> before, SortedSet<string> after)
        {
            var d = new ListingDiff();
            var b = Index(before);
            var a = Index(after);
            foreach (var kv in a)
            {
                string old;
                if (!b.TryGetValue(kv.Key, out old))
                {
                    if (kv.Key.StartsWith("dir|", StringComparison.Ordinal)) continue;
                    d.Added.Add(PathOf(kv.Key));
                }
                else if (old != kv.Value)
                {
                    if (kv.Key.StartsWith("dir|", StringComparison.Ordinal))
                    {
                        if (old == "missing" && kv.Value == "present") d.DirsCreated.Add(PathOf(kv.Key));
                    }
                    else d.Changed.Add(PathOf(kv.Key));
                }
            }
            foreach (var kv in b)
            {
                if (kv.Key.StartsWith("dir|", StringComparison.Ordinal)) continue;
                if (!a.ContainsKey(kv.Key)) d.Removed.Add(PathOf(kv.Key));
            }
            return d;
        }

        // "kind|path|rest" -> key "kind|path", value "rest".
        private static Dictionary<string, string> Index(SortedSet<string> listing)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (listing == null) return map;
            foreach (string entry in listing)
            {
                int first = entry.IndexOf('|');
                int second = first < 0 ? -1 : entry.IndexOf('|', first + 1);
                if (second < 0)
                {
                    map[entry] = "";
                    continue;
                }
                map[entry.Substring(0, second)] = entry.Substring(second + 1);
            }
            return map;
        }

        private static string PathOf(string key)
        {
            int bar = key.IndexOf('|');
            return bar < 0 ? key : key.Substring(bar + 1);
        }
    }
}
