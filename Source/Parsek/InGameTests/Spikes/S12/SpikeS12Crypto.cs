using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Parsek.InGameTests.Spikes.S12
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S12.
    //
    // CryptoProbe: does System.Security.Cryptography hash and sign inside KSP 1.12.5's
    // Mono, and how fast. SHA-256 of 1 MB and 10 MB, RSA 2048 and ECDsa P-256 create /
    // sign / verify / refuse-tampered / export-import, a public key written to and read
    // back from the spike output folder, and a named CSP key container persisted,
    // reopened and deleted (the only write outside the spike folder; it goes to the
    // runtime's own default key store and is removed again, and the listing diff of that
    // store says what, if anything, stayed).
    //
    // Every probe body runs inside Run(), which times it with a Stopwatch and turns any
    // exception into a "fail" step with the exception's type and message, so one missing
    // algorithm never stops the rest. The members KSP's Mono may lack (ECDsa, ECCurve,
    // ECParameters, HashAlgorithmName, RSASignaturePadding) are named only inside
    // [NoInlining] probe bodies: a MissingMethodException / TypeLoadException is thrown
    // when THAT body is JIT-compiled, which is inside Run()'s try. No field, parameter or
    // return type here names them, and no lambda captures one, so the in-game test
    // discovery (which reflects over every type) never loads them.

    /// <summary>Mutable state shared by the probe bodies of one run.</summary>
    internal sealed class SpikeS12Context : IDisposable
    {
        internal string Run;
        /// <summary>Folder for the public-key file; null skips that probe.</summary>
        internal string OutputDir;
        internal bool IncludeContainer;
        internal List<string> KeyStoreDirs = new List<string>();
        internal int WarmRepeats = SpikeS12Plan.WarmRepeats;
        internal int RsaBits = SpikeS12Plan.RsaKeyBits;

        internal byte[] Blob;
        internal byte[] Buf1;
        internal byte[] Buf10;

        internal AsymmetricAlgorithm Rsa;
        internal byte[] RsaSig;
        internal byte[] RsaSigLegacy;
        internal string RsaPublicXml;

        // An ECDsa instance, kept as its mscorlib base type.
        internal AsymmetricAlgorithm Ec;
        internal byte[] EcSig;

        internal byte[] ContainerModulus;
        internal SortedSet<string> StoreBefore;

        public void Dispose()
        {
            DisposeQuietly(Rsa);
            DisposeQuietly(Ec);
            Rsa = null;
            Ec = null;
        }

        private static void DisposeQuietly(IDisposable d)
        {
            try { d?.Dispose(); }
            catch (Exception) { }
        }
    }

    /// <summary>The probe list, as one ordered sequence of steps.</summary>
    internal static class SpikeS12Probes
    {
        private const string HashName = "SHA256";

        /// <summary>
        /// Runs every probe in order, one step per item. Nothing escapes: each body's
        /// exception becomes that step's "fail".
        /// </summary>
        internal static IEnumerable<SpikeS12Step> RunAll(SpikeS12Context ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            yield return Run("env", s => Env(s));
            yield return Run("buffers.fill", s => FillBuffers(ctx, s));

            // ----- SHA-256 -----
            yield return Run("sha256.create", s => ShaCreate());
            yield return Run(SpikeS12Plan.StepShaKat, s => ShaKat(s));
            yield return Run("sha256.blob", s => ShaOnce(s, ctx.Blob, SpikeS12Plan.BlobSha256));
            yield return Run("sha256.1mb", s => ShaOnce(s, ctx.Buf1, SpikeS12Plan.OneMbSha256));
            yield return Run("sha256.1mb.warm", s => ShaWarm(s, ctx.Buf1, SpikeS12Plan.OneMbSha256, ctx.WarmRepeats));
            yield return Run(SpikeS12Plan.StepSha10, s => ShaOnce(s, ctx.Buf10, SpikeS12Plan.TenMbSha256));
            yield return Run(SpikeS12Plan.StepSha10Warm, s => ShaWarm(s, ctx.Buf10, SpikeS12Plan.TenMbSha256, ctx.WarmRepeats));
            yield return Run("sha256managed.10mb", s => ShaManaged(s, ctx.Buf10));

            // ----- RSA 2048 -----
            yield return Run(SpikeS12Plan.StepRsaCreate, s => RsaCreate(ctx, s));
            if (ctx.Rsa != null)
            {
                yield return Run(SpikeS12Plan.StepRsaSign, s => RsaSign(ctx, s));
                yield return Run(SpikeS12Plan.StepRsaVerify, s => RsaVerify(ctx, s));
                yield return Run(SpikeS12Plan.StepRsaSignLegacy, s => RsaSignLegacy(ctx, s));
                yield return Run(SpikeS12Plan.StepRsaVerifyLegacy, s => RsaVerifyLegacy(ctx, s));
                yield return Run(SpikeS12Plan.StepRsaTampered, s => RsaTampered(ctx, s));
                yield return Run("rsa.xml.export", s => RsaXmlExport(ctx, s));
                yield return Run(SpikeS12Plan.StepRsaXmlImport, s => RsaXmlImport(ctx, s));
                yield return Run(SpikeS12Plan.StepRsaParams, s => RsaParamsRoundTrip(ctx, s));
                if (!string.IsNullOrEmpty(ctx.OutputDir))
                    yield return Run("rsa.xml.file", s => RsaXmlFile(ctx, s));
                else
                    yield return Skip("rsa.xml.file", "no output folder");
            }
            else
            {
                foreach (string n in new[]
                {
                    SpikeS12Plan.StepRsaSign, SpikeS12Plan.StepRsaVerify, SpikeS12Plan.StepRsaSignLegacy,
                    SpikeS12Plan.StepRsaVerifyLegacy, SpikeS12Plan.StepRsaTampered, "rsa.xml.export",
                    SpikeS12Plan.StepRsaXmlImport, SpikeS12Plan.StepRsaParams, "rsa.xml.file",
                })
                    yield return Skip(n, "no RSA instance (rsa.create failed)");
            }

            // ----- the named key container -----
            if (ctx.IncludeContainer)
            {
                yield return Run("container.store.before", s => StoreBefore(ctx, s));
                SpikeS12Step persist = Run(SpikeS12Plan.StepContainerPersist, s => ContainerPersist(ctx, s));
                yield return persist;
                if (persist.IsOk)
                    yield return Run(SpikeS12Plan.StepContainerReopen, s => ContainerReopen(ctx, s));
                else
                    yield return Skip(SpikeS12Plan.StepContainerReopen, "persist failed");
                // Always attempt the delete, so a half-made container is removed.
                yield return Run(SpikeS12Plan.StepContainerDelete, s => ContainerDelete(ctx, s));
                yield return Run(SpikeS12Plan.StepContainerLeftover, s => ContainerLeftover(ctx, s));
            }
            else
            {
                foreach (string n in new[]
                {
                    "container.store.before", SpikeS12Plan.StepContainerPersist, SpikeS12Plan.StepContainerReopen,
                    SpikeS12Plan.StepContainerDelete, SpikeS12Plan.StepContainerLeftover,
                })
                    yield return Skip(n, "container probes off for this run");
            }

            // ----- ECDsa P-256 -----
            yield return Run(SpikeS12Plan.StepEcCreate, s => EcCreateNamed(ctx, s));
            if (ctx.Ec == null)
                yield return Run(SpikeS12Plan.StepEcCreateDefault, s => EcCreateDefault(ctx, s));
            else
                yield return Skip(SpikeS12Plan.StepEcCreateDefault, "not needed: the named-curve create worked");
            if (ctx.Ec != null)
            {
                yield return Run(SpikeS12Plan.StepEcSign, s => EcSign(ctx, s));
                yield return Run(SpikeS12Plan.StepEcVerify, s => EcVerify(ctx, s));
                yield return Run(SpikeS12Plan.StepEcTampered, s => EcTampered(ctx, s));
                yield return Run(SpikeS12Plan.StepEcParams, s => EcParamsRoundTrip(ctx, s));
                yield return Run("ecdsa.xml.export", s => EcXmlExport(ctx, s));
            }
            else
            {
                foreach (string n in new[]
                {
                    SpikeS12Plan.StepEcSign, SpikeS12Plan.StepEcVerify, SpikeS12Plan.StepEcTampered,
                    SpikeS12Plan.StepEcParams, "ecdsa.xml.export",
                })
                    yield return Skip(n, "no ECDsa instance (both creates failed)");
            }
        }

        // ----- the wrapper -----

        /// <summary>
        /// Times <paramref name="body"/> and records its outcome. The body returns the
        /// detail text; it may set Status = Fail for a wrong result, Bytes, and Ms (a body
        /// that measures only part of its work, such as the warm median). An exception is
        /// a Fail with its type and message.
        /// </summary>
        internal static SpikeS12Step Run(string name, Func<SpikeS12Step, string> body)
        {
            var step = new SpikeS12Step { Name = name, Status = SpikeS12Status.Ok, Ms = double.NaN };
            var sw = Stopwatch.StartNew();
            try
            {
                step.Detail = body(step);
            }
            catch (Exception ex)
            {
                step.Status = SpikeS12Status.Fail;
                SpikeS12Plan.Describe(ex, out step.ErrorType, out step.ErrorMessage);
            }
            sw.Stop();
            if (double.IsNaN(step.Ms)) step.Ms = sw.Elapsed.TotalMilliseconds;
            return step;
        }

        internal static SpikeS12Step Skip(string name, string why)
            => new SpikeS12Step { Name = name, Status = SpikeS12Status.Skip, Ms = 0, Detail = "skipped: " + why };

        private static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

        private static string Fail(SpikeS12Step s, string detail)
        {
            s.Status = SpikeS12Status.Fail;
            return detail;
        }

        // ----- environment -----

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Env(SpikeS12Step s)
        {
            var sb = new StringBuilder();
            sb.Append("runtime=").Append(MonoDisplayName());
            sb.Append(" clr=").Append(Environment.Version.ToString());
            sb.Append(" os=").Append(Environment.OSVersion.ToString());
            sb.Append(" ptr=").Append(I(IntPtr.Size));
            sb.Append(" mscorlib=").Append(typeof(object).Assembly.GetName().Version.ToString());
            sb.Append(" systemCore=").Append(AssemblyVersionOf("System.Linq.Enumerable, System.Core"));
            foreach (string t in new[]
            {
                "System.Security.Cryptography.HashAlgorithmName, mscorlib",
                "System.Security.Cryptography.RSASignaturePadding, mscorlib",
                "System.Security.Cryptography.ECDsa, System.Core",
                "System.Security.Cryptography.ECCurve, System.Core",
                "System.Security.Cryptography.ECParameters, System.Core",
                "System.Security.Cryptography.ECDsaCng, System.Core",
            })
            {
                string shortName = t.Substring(0, t.IndexOf(',')).Substring("System.Security.Cryptography.".Length);
                sb.Append(' ').Append(shortName).Append('=').Append(TypePresent(t) ? "present" : "absent");
            }
            return sb.ToString();
        }

        private static string MonoDisplayName()
        {
            try
            {
                Type mono = Type.GetType("Mono.Runtime");
                if (mono == null) return "not-mono";
                MethodInfo m = mono.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                object v = m?.Invoke(null, null);
                return "mono " + (v as string ?? "?");
            }
            catch (Exception ex)
            {
                return "mono <" + ex.GetType().Name + ">";
            }
        }

        private static bool TypePresent(string assemblyQualified)
        {
            try { return Type.GetType(assemblyQualified, false) != null; }
            catch (Exception) { return false; }
        }

        private static string AssemblyVersionOf(string assemblyQualified)
        {
            try
            {
                Type t = Type.GetType(assemblyQualified, false);
                return t == null ? "absent" : t.Assembly.GetName().Version.ToString();
            }
            catch (Exception ex)
            {
                return "<" + ex.GetType().Name + ">";
            }
        }

        private static string FillBuffers(SpikeS12Context ctx, SpikeS12Step s)
        {
            ctx.Blob = SpikeS12Plan.FillBuffer(SpikeS12Plan.BlobBytes);
            ctx.Buf1 = SpikeS12Plan.FillBuffer(SpikeS12Plan.OneMb);
            ctx.Buf10 = SpikeS12Plan.FillBuffer(SpikeS12Plan.TenMb);
            s.Bytes = ctx.Blob.Length + (long)ctx.Buf1.Length + ctx.Buf10.Length;
            return "blob=" + I(ctx.Blob.Length) + " oneMb=" + I(ctx.Buf1.Length) + " tenMb=" + I(ctx.Buf10.Length);
        }

        // ----- SHA-256 -----

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ShaCreate()
        {
            using (SHA256 h = SHA256.Create())
            {
                if (h == null) throw new InvalidOperationException("SHA256.Create() returned null");
                return "impl=" + h.GetType().FullName + " hashSize=" + I(h.HashSize);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ShaKat(SpikeS12Step s)
        {
            byte[] abc = Encoding.ASCII.GetBytes("abc");
            string hex;
            using (SHA256 h = SHA256.Create()) hex = SpikeS12Plan.ToHex(h.ComputeHash(abc));
            s.Bytes = abc.Length;
            bool match = hex == SpikeS12Plan.AbcSha256;
            string detail = "digest=" + hex + " match=" + (match ? "true" : "false");
            return match ? detail : Fail(s, detail);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ShaOnce(SpikeS12Step s, byte[] data, string expected)
        {
            if (data == null) throw new InvalidOperationException("input buffer missing (buffers.fill failed)");
            byte[] digest;
            using (SHA256 h = SHA256.Create()) digest = h.ComputeHash(data);
            s.Bytes = data.Length;
            string hex = SpikeS12Plan.ToHex(digest);
            bool match = hex == expected;
            string detail = "digest=" + hex + " match=" + (match ? "true" : "false")
                + " firstRun=true (ms includes SHA256.Create and the first JIT of the hash)";
            return match ? detail : Fail(s, detail);
        }

        // MB/s of a step whose body set Ms itself; NaN otherwise.
        private static double MbPerSecond(long bytes, SpikeS12Step s)
            => double.IsNaN(s.Ms) || s.Ms <= 0 ? double.NaN : bytes / 1048576.0 / (s.Ms / 1000.0);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ShaWarm(SpikeS12Step s, byte[] data, string expected, int repeats)
        {
            if (data == null) throw new InvalidOperationException("input buffer missing (buffers.fill failed)");
            if (repeats < 1) repeats = 1;
            var times = new List<double>(repeats);
            bool allMatch = true;
            using (SHA256 h = SHA256.Create())
            {
                for (int i = 0; i < repeats; i++)
                {
                    var sw = Stopwatch.StartNew();
                    byte[] digest = h.ComputeHash(data);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    if (SpikeS12Plan.ToHex(digest) != expected) allMatch = false;
                }
            }
            s.Bytes = data.Length;
            s.Ms = SpikeS12Plan.Median(times);
            var parts = new string[times.Count];
            for (int i = 0; i < times.Count; i++) parts[i] = F3(times[i]);
            string detail = "repeats=" + I(repeats) + " runsMs=" + string.Join("/", parts)
                + " medianMs=" + F3(s.Ms) + " mbPerS=" + F3(MbPerSecond(data.Length, s))
                + " match=" + (allMatch ? "true" : "false");
            return allMatch ? detail : Fail(s, detail);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ShaManaged(SpikeS12Step s, byte[] data)
        {
            if (data == null) throw new InvalidOperationException("input buffer missing (buffers.fill failed)");
            string hex;
            using (var h = new SHA256Managed()) hex = SpikeS12Plan.ToHex(h.ComputeHash(data));
            s.Bytes = data.Length;
            bool match = hex == SpikeS12Plan.TenMbSha256;
            string detail = "impl=SHA256Managed match=" + (match ? "true" : "false");
            return match ? detail : Fail(s, detail);
        }

        // ----- RSA -----

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string RsaCreate(SpikeS12Context ctx, SpikeS12Step s)
        {
            var rsa = new RSACryptoServiceProvider(ctx.RsaBits);
            try
            {
                // Mono generates the key pair lazily; exporting forces it inside this step.
                RSAParameters p = rsa.ExportParameters(false);
                ctx.Rsa = rsa;
                s.Bytes = p.Modulus == null ? 0 : p.Modulus.Length;
                return "impl=" + rsa.GetType().FullName + " keySize=" + I(rsa.KeySize)
                    + " modulusBytes=" + I(s.Bytes) + " persistKeyInCsp=" + (rsa.PersistKeyInCsp ? "true" : "false")
                    + " publicOnly=" + (rsa.PublicOnly ? "true" : "false")
                    + " rsaCreateDefault=" + DefaultRsaImpl();
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
        }

        private static string DefaultRsaImpl()
        {
            try
            {
                using (RSA r = RSA.Create()) return r == null ? "null" : r.GetType().FullName;
            }
            catch (Exception ex)
            {
                return "<" + ex.GetType().Name + ">";
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] SignModern(AsymmetricAlgorithm key, byte[] data)
            => ((RSA)key).SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool VerifyModern(AsymmetricAlgorithm key, byte[] data, byte[] sig)
            => ((RSA)key).VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // The string-named overload KSP's own DLC check uses (with MD5 there).
        private static byte[] SignLegacy(AsymmetricAlgorithm key, byte[] data)
            => ((RSACryptoServiceProvider)key).SignData(data, HashName);

        private static bool VerifyLegacy(AsymmetricAlgorithm key, byte[] data, byte[] sig)
            => ((RSACryptoServiceProvider)key).VerifyData(data, HashName, sig);

        private static string RsaSign(SpikeS12Context ctx, SpikeS12Step s)
        {
            ctx.RsaSig = SignModern(ctx.Rsa, ctx.Blob);
            s.Bytes = ctx.RsaSig.Length;
            return "api=SignData(byte[],HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1) sigBytes=" + I(ctx.RsaSig.Length);
        }

        private static string RsaVerify(SpikeS12Context ctx, SpikeS12Step s)
        {
            if (ctx.RsaSig == null) return SkipInPlace(s, "no signature (rsa.sign failed)");
            bool ok = VerifyModern(ctx.Rsa, ctx.Blob, ctx.RsaSig);
            string detail = "verified=" + (ok ? "true" : "false");
            return ok ? detail : Fail(s, detail);
        }

        private static string RsaSignLegacy(SpikeS12Context ctx, SpikeS12Step s)
        {
            ctx.RsaSigLegacy = SignLegacy(ctx.Rsa, ctx.Blob);
            s.Bytes = ctx.RsaSigLegacy.Length;
            bool same = ctx.RsaSig != null && SpikeS12Plan.ToHex(ctx.RsaSig) == SpikeS12Plan.ToHex(ctx.RsaSigLegacy);
            return "api=SignData(byte[],\"SHA256\") sigBytes=" + I(ctx.RsaSigLegacy.Length)
                + " sameAsModern=" + (same ? "true" : "false");
        }

        private static string RsaVerifyLegacy(SpikeS12Context ctx, SpikeS12Step s)
        {
            if (ctx.RsaSigLegacy == null) return SkipInPlace(s, "no signature (rsa.sign.legacy failed)");
            bool ok = VerifyLegacy(ctx.Rsa, ctx.Blob, ctx.RsaSigLegacy);
            string detail = "verified=" + (ok ? "true" : "false");
            return ok ? detail : Fail(s, detail);
        }

        private static string SkipInPlace(SpikeS12Step s, string why)
        {
            s.Status = SpikeS12Status.Skip;
            s.Ms = 0;
            return "skipped: " + why;
        }

        private static byte[] Tamper(byte[] data)
        {
            var copy = (byte[])data.Clone();
            copy[0] ^= 0xFF;
            return copy;
        }

        private static string RsaTampered(SpikeS12Context ctx, SpikeS12Step s)
        {
            byte[] bad = Tamper(ctx.Blob);
            bool accepted;
            string api;
            if (ctx.RsaSig != null)
            {
                api = "modern";
                accepted = VerifyModern(ctx.Rsa, bad, ctx.RsaSig);
            }
            else if (ctx.RsaSigLegacy != null)
            {
                api = "legacy";
                accepted = VerifyLegacy(ctx.Rsa, bad, ctx.RsaSigLegacy);
            }
            else return SkipInPlace(s, "no signature to test");
            string detail = "api=" + api + " acceptedTampered=" + (accepted ? "true" : "false");
            return accepted ? Fail(s, detail) : detail;
        }

        private static string RsaXmlExport(SpikeS12Context ctx, SpikeS12Step s)
        {
            string priv = ctx.Rsa.ToXmlString(true);
            ctx.RsaPublicXml = ctx.Rsa.ToXmlString(false);
            s.Bytes = ctx.RsaPublicXml.Length;
            return "privateXmlChars=" + I(priv.Length) + " publicXmlChars=" + I(ctx.RsaPublicXml.Length);
        }

        // Verifies the run's signature with a key that holds only what was imported.
        private static bool VerifyWithEither(SpikeS12Context ctx, AsymmetricAlgorithm key, out string api)
        {
            if (ctx.RsaSig != null)
            {
                api = "modern";
                return VerifyModern(key, ctx.Blob, ctx.RsaSig);
            }
            if (ctx.RsaSigLegacy != null)
            {
                api = "legacy";
                return VerifyLegacy(key, ctx.Blob, ctx.RsaSigLegacy);
            }
            throw new InvalidOperationException("no RSA signature to verify (both sign steps failed)");
        }

        private static string RsaXmlImport(SpikeS12Context ctx, SpikeS12Step s)
        {
            if (ctx.RsaPublicXml == null) return SkipInPlace(s, "no exported XML (rsa.xml.export failed)");
            using (var pub = new RSACryptoServiceProvider())
            {
                pub.FromXmlString(ctx.RsaPublicXml);
                bool ok = VerifyWithEither(ctx, pub, out string api);
                string detail = "publicOnly=" + (pub.PublicOnly ? "true" : "false") + " api=" + api
                    + " verified=" + (ok ? "true" : "false");
                return ok ? detail : Fail(s, detail);
            }
        }

        private static string RsaParamsRoundTrip(SpikeS12Context ctx, SpikeS12Step s)
        {
            RSAParameters full = ((RSA)ctx.Rsa).ExportParameters(true);
            using (var copy = new RSACryptoServiceProvider())
            {
                copy.ImportParameters(full);
                // The imported private key signs; the original verifies.
                byte[] sig;
                string api;
                bool ok;
                if (ctx.RsaSig != null)
                {
                    api = "modern";
                    sig = SignModern(copy, ctx.Blob);
                    ok = VerifyModern(ctx.Rsa, ctx.Blob, sig);
                }
                else
                {
                    api = "legacy";
                    sig = SignLegacy(copy, ctx.Blob);
                    ok = VerifyLegacy(ctx.Rsa, ctx.Blob, sig);
                }
                RSAParameters back = copy.ExportParameters(false);
                bool sameModulus = SpikeS12Plan.ToHex(back.Modulus) == SpikeS12Plan.ToHex(full.Modulus);
                ok = ok && sameModulus;
                string detail = "api=" + api + " sameModulus=" + (sameModulus ? "true" : "false")
                    + " crossVerified=" + (ok ? "true" : "false");
                return ok ? detail : Fail(s, detail);
            }
        }

        private static string RsaXmlFile(SpikeS12Context ctx, SpikeS12Step s)
        {
            if (ctx.RsaPublicXml == null) return SkipInPlace(s, "no exported XML (rsa.xml.export failed)");
            Directory.CreateDirectory(ctx.OutputDir);
            string path = Path.Combine(ctx.OutputDir, SpikeS12Plan.PublicKeyFileName(ctx.Run));
            File.WriteAllText(path, ctx.RsaPublicXml, new UTF8Encoding(false));
            string back = File.ReadAllText(path, Encoding.UTF8);
            s.Bytes = new FileInfo(path).Length;
            using (var pub = new RSACryptoServiceProvider())
            {
                pub.FromXmlString(back);
                bool ok = back == ctx.RsaPublicXml && VerifyWithEither(ctx, pub, out string api);
                string detail = "file=" + Path.GetFileName(path) + " sameText=" + (back == ctx.RsaPublicXml ? "true" : "false")
                    + " verified=" + (ok ? "true" : "false") + " (public key only; kept in the spike folder)";
                return ok ? detail : Fail(s, detail);
            }
        }

        // ----- the named key container -----

        private static string StoreBefore(SpikeS12Context ctx, SpikeS12Step s)
        {
            ctx.StoreBefore = SpikeS12Plan.Snapshot(ctx.KeyStoreDirs);
            return "container=" + SpikeS12Plan.ContainerName(ctx.Run) + " " + DescribeStores(ctx.StoreBefore);
        }

        private static string DescribeStores(SortedSet<string> listing)
        {
            var sb = new StringBuilder();
            int files = 0;
            foreach (string e in listing)
            {
                if (e.StartsWith("dir|", StringComparison.Ordinal))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(e.Substring(4).Replace('|', '='));
                }
                else if (e.StartsWith("file|", StringComparison.Ordinal)) files++;
            }
            return sb.Append(" files=").Append(I(files)).ToString();
        }

        private static CspParameters ContainerParams(SpikeS12Context ctx, bool existingOnly)
        {
            var p = new CspParameters { KeyContainerName = SpikeS12Plan.ContainerName(ctx.Run) };
            if (existingOnly) p.Flags = CspProviderFlags.UseExistingKey;
            return p;
        }

        private static string ContainerPersist(SpikeS12Context ctx, SpikeS12Step s)
        {
            using (var r = new RSACryptoServiceProvider(ctx.RsaBits, ContainerParams(ctx, false)))
            {
                r.PersistKeyInCsp = true;
                RSAParameters p = r.ExportParameters(false);
                ctx.ContainerModulus = p.Modulus;
                s.Bytes = p.Modulus == null ? 0 : p.Modulus.Length;
            }
            var after = SpikeS12Plan.Snapshot(ctx.KeyStoreDirs);
            var diff = SpikeS12Plan.Diff(ctx.StoreBefore ?? new SortedSet<string>(), after);
            return "persisted=" + (diff.Added.Count + diff.Changed.Count > 0 ? "file-seen" : "no-file-seen")
                + " " + diff.Describe();
        }

        private static string ContainerReopen(SpikeS12Context ctx, SpikeS12Step s)
        {
            using (var r = new RSACryptoServiceProvider(ContainerParams(ctx, true)))
            {
                RSAParameters p = r.ExportParameters(false);
                bool same = ctx.ContainerModulus != null
                    && SpikeS12Plan.ToHex(p.Modulus) == SpikeS12Plan.ToHex(ctx.ContainerModulus);
                string detail = "sameKey=" + (same ? "true" : "false");
                return same ? detail : Fail(s, detail);
            }
        }

        private static string ContainerDelete(SpikeS12Context ctx, SpikeS12Step s)
        {
            // PersistKeyInCsp = false then Clear() removes the container from the store
            // (CryptoAPI and Mono's KeyPairPersistence both delete on that pair).
            var r = new RSACryptoServiceProvider(ContainerParams(ctx, false));
            r.PersistKeyInCsp = false;
            r.Clear();
            return "cleared container=" + SpikeS12Plan.ContainerName(ctx.Run);
        }

        private static string ContainerLeftover(SpikeS12Context ctx, SpikeS12Step s)
        {
            var after = SpikeS12Plan.Snapshot(ctx.KeyStoreDirs);
            var diff = SpikeS12Plan.Diff(ctx.StoreBefore ?? new SortedSet<string>(), after);
            bool clean = diff.Added.Count == 0 && diff.Changed.Count == 0 && diff.Removed.Count == 0;
            string detail = (clean ? "nothing left" : "LEFT BEHIND") + " " + diff.Describe();
            return clean ? detail : Fail(s, detail);
        }

        // ----- ECDsa P-256 -----

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string EcCreateNamed(SpikeS12Context ctx, SpikeS12Step s)
        {
            ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (ec == null) throw new InvalidOperationException("ECDsa.Create(nistP256) returned null");
            ctx.Ec = ec;
            return "api=ECDsa.Create(ECCurve.NamedCurves.nistP256) impl=" + ec.GetType().FullName + " keySize=" + I(ec.KeySize);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string EcCreateDefault(SpikeS12Context ctx, SpikeS12Step s)
        {
            ECDsa ec = ECDsa.Create();
            if (ec == null) throw new InvalidOperationException("ECDsa.Create() returned null");
            try
            {
                ec.KeySize = SpikeS12Plan.EcKeyBits;
            }
            catch
            {
                ec.Dispose();
                throw;
            }
            ctx.Ec = ec;
            return "api=ECDsa.Create()+KeySize=256 impl=" + ec.GetType().FullName + " keySize=" + I(ec.KeySize);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] EcSignCore(AsymmetricAlgorithm key, byte[] data)
            => ((ECDsa)key).SignData(data, HashAlgorithmName.SHA256);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool EcVerifyCore(AsymmetricAlgorithm key, byte[] data, byte[] sig)
            => ((ECDsa)key).VerifyData(data, sig, HashAlgorithmName.SHA256);

        private static string EcSign(SpikeS12Context ctx, SpikeS12Step s)
        {
            ctx.EcSig = EcSignCore(ctx.Ec, ctx.Blob);
            s.Bytes = ctx.EcSig.Length;
            return "api=SignData(byte[],HashAlgorithmName.SHA256) sigBytes=" + I(ctx.EcSig.Length);
        }

        private static string EcVerify(SpikeS12Context ctx, SpikeS12Step s)
        {
            if (ctx.EcSig == null) return SkipInPlace(s, "no signature (ecdsa.sign failed)");
            bool ok = EcVerifyCore(ctx.Ec, ctx.Blob, ctx.EcSig);
            string detail = "verified=" + (ok ? "true" : "false");
            return ok ? detail : Fail(s, detail);
        }

        private static string EcTampered(SpikeS12Context ctx, SpikeS12Step s)
        {
            if (ctx.EcSig == null) return SkipInPlace(s, "no signature (ecdsa.sign failed)");
            bool accepted = EcVerifyCore(ctx.Ec, Tamper(ctx.Blob), ctx.EcSig);
            string detail = "acceptedTampered=" + (accepted ? "true" : "false");
            return accepted ? Fail(s, detail) : detail;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string EcParamsRoundTrip(SpikeS12Context ctx, SpikeS12Step s)
        {
            if (ctx.EcSig == null) return SkipInPlace(s, "no signature (ecdsa.sign failed)");
            var ec = (ECDsa)ctx.Ec;
            ECParameters full = ec.ExportParameters(true);
            ECParameters pub = ec.ExportParameters(false);
            bool signedByCopy, verifiedByPublic;
            using (ECDsa copy = ECDsa.Create(full))
            {
                if (copy == null) throw new InvalidOperationException("ECDsa.Create(ECParameters) returned null");
                byte[] sig = copy.SignData(ctx.Blob, HashAlgorithmName.SHA256);
                signedByCopy = ec.VerifyData(ctx.Blob, sig, HashAlgorithmName.SHA256);
            }
            using (ECDsa pubOnly = ECDsa.Create(pub))
            {
                if (pubOnly == null) throw new InvalidOperationException("ECDsa.Create(public ECParameters) returned null");
                verifiedByPublic = pubOnly.VerifyData(ctx.Blob, ctx.EcSig, HashAlgorithmName.SHA256);
            }
            int qBytes = (pub.Q.X == null ? 0 : pub.Q.X.Length) + (pub.Q.Y == null ? 0 : pub.Q.Y.Length);
            s.Bytes = qBytes;
            bool ok = signedByCopy && verifiedByPublic;
            string detail = "publicPointBytes=" + I(qBytes) + " privateBytes=" + I(full.D == null ? 0 : full.D.Length)
                + " copySignsOriginalVerifies=" + (signedByCopy ? "true" : "false")
                + " publicImportVerifies=" + (verifiedByPublic ? "true" : "false");
            return ok ? detail : Fail(s, detail);
        }

        // Informational: the .NET Framework base ECDsa has no XML form (ECDsaCng only).
        private static string EcXmlExport(SpikeS12Context ctx, SpikeS12Step s)
        {
            string xml = ctx.Ec.ToXmlString(false);
            s.Bytes = xml == null ? 0 : xml.Length;
            return "publicXmlChars=" + I(s.Bytes);
        }
    }

    /// <summary>The in-game entry point. HAND: Ctrl+Shift+T, category SpikeS12, Run on CryptoProbe.</summary>
    public class SpikeS12Crypto
    {
        private static void Info(string msg) => ParsekLog.Info(SpikeS12Plan.Tag, msg);
        private static void Warn(string msg) => ParsekLog.Warn(SpikeS12Plan.Tag, msg);

        private static void Csv(string name, string[] header, object[] fields)
        {
            try { SpikeCsv.AppendRow(SpikeS12Plan.SpikeId, name, header, fields); }
            catch (Exception ex) { Warn("csv write failed name=" + name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }

        [InGameTest(Category = "SpikeS12", Scene = InGameTestAttribute.AnyScene,
            AllowBatchExecution = false,
            BatchSkipReason = "R0 spike S12: needs PARSEK_SPIKE=S12; run alone from Ctrl+Shift+T",
            Description = "R0 spike S12: SHA-256 1/10 MB, RSA 2048 and ECDsa P-256 sign/verify/export/import, key container write, timed")]
        public IEnumerator CryptoProbe()
        {
            if (!SpikeHost.IsArmed(SpikeS12Plan.SpikeId))
                InGameAssert.Skip("PARSEK_SPIKE does not name S12");

            string run = SpikeS12Plan.RunLabel(DateTime.UtcNow);
            var ctx = new SpikeS12Context
            {
                Run = run,
                OutputDir = ResolveOutputDir(),
                IncludeContainer = true,
                KeyStoreDirs = ResolveKeyStoreDirs(),
            };
            Info("probe start run=" + run + " repeats=" + ctx.WarmRepeats.ToString(CultureInfo.InvariantCulture)
                + " rsaBits=" + ctx.RsaBits.ToString(CultureInfo.InvariantCulture)
                + " container=" + SpikeS12Plan.ContainerName(run)
                + " outputDir=" + (ctx.OutputDir ?? "<none>")
                + " keyStores=" + string.Join(";", ctx.KeyStoreDirs.ToArray()));

            var steps = new List<SpikeS12Step>();
            try
            {
                foreach (SpikeS12Step step in SpikeS12Probes.RunAll(ctx))
                {
                    steps.Add(step);
                    Info(SpikeS12Plan.StepLine(run, step));
                    Csv(SpikeS12Plan.ProbeCsvName, SpikeS12Plan.ProbeCsvHeader, SpikeS12Plan.StepRow(run, step));
                    // One frame between steps keeps the window and the log responsive; a
                    // single step (RSA key generation) may still hold the frame for seconds.
                    yield return null;
                }
            }
            finally
            {
                ctx.Dispose();
            }

            SpikeS12Plan.Verdict v = SpikeS12Plan.Judge(steps);
            Info(SpikeS12Plan.VerdictLine(run, v));
            Csv(SpikeS12Plan.VerdictCsvName, SpikeS12Plan.VerdictCsvHeader, SpikeS12Plan.VerdictRow(run, v));
            if (!v.Pass)
                InGameAssert.Fail("S12 verdict FAIL: shaOk=" + v.ShaOk + " rsaChain=" + v.RsaChain
                    + " ecdsaChain=" + v.EcdsaChain + " (see the [Spike.S12] step lines)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ResolveOutputDir()
        {
            try { return Path.GetDirectoryName(SpikeCsv.PathFor(SpikeS12Plan.SpikeId, SpikeS12Plan.ProbeCsvName)); }
            catch (Exception ex)
            {
                Warn("output folder unresolved: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static List<string> ResolveKeyStoreDirs()
            => SpikeS12Plan.KeyStoreDirs(
                SafeFolder(Environment.SpecialFolder.ApplicationData),
                SafeFolder(Environment.SpecialFolder.CommonApplicationData));

        private static string SafeFolder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); }
            catch (Exception) { return null; }
        }
    }
}
