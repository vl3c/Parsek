# Spike S12: hashing and signing inside KSP's Mono (<YYYY-MM-DD>)

Verdict: <PASS | FAIL | PARTIAL>. <One sentence: e.g. "SHA-256 hashes 10 MB in <n> ms and RSA 2048 signs and verifies inside KSP's Mono; ECDsa <works | throws <type>>.">

## Question
Does `System.Security.Cryptography` SHA-256, `RSACryptoServiceProvider` and `ECDsa` work in
KSP 1.12.5's Mono, how fast, and is the key container writable. Only R7 (signed standings)
waits on it.

## Decides
Design section 12, last row ("signed standings", still to prove) and 7.15 (signed
standings); task R7 (which algorithm signs a standing, whether a key is kept in a CSP
container or in a file the mod writes, and whether the fallback "a managed implementation
shipped in the mod" is needed).

## Build
spike/mp-r0 at <sha>; deployed DLL sha256 <hash> (`deployed-dll-sha256.txt`); instance
spike-r0; KSP 1.12.5, DLCs yes, mods <list from the instance manifest>.
Runtime from the `env` step: <runtime=mono ...> clr=<...> mscorlib=<...> systemCore=<...>.

## Procedure as run
1. `spikes/r0/S12/PC-RUN.md` steps 0-1: build `-p:SkipKspDeploy=true`, copy into
   `spike-r0`, marker check `SpikeS12Crypto` / `SpikeS12Probes` / `SpikeS12Plan` PASS.
2. Step 2 (HAND): KSP with `PARSEK_SPIKE=S12`, career `spike-s0` at the Space Center,
   Ctrl+Shift+T, `SpikeS12` -> `CryptoProbe [single]` play button. Row turned <green | red>.
3. Step 3: `KSP.log`, `parsek-test-results.txt` and `parsek-spike\S12\` moved to the raw
   folder; 32 step lines and one verdict line.
<Any change from the plan, and why.>

## Measurements
| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| SHA-256 "abc" vector (`sha256.kat`) | <match=true/false> | the FIPS 180-2 digest | <ok/fail> |
| SHA-256 implementation (`sha256.create`) | <impl=...> | - | info |
| SHA-256 1 MB, cold (`sha256.1mb` ms) | <ms> | - | info |
| SHA-256 1 MB, warm median of 3 (`sha256.1mb.warm` ms) | <ms> (<MB/s>) | - | info |
| SHA-256 10 MB, cold (`sha256.10mb` ms) | <ms> | - | info |
| SHA-256 10 MB, warm median of 3 (`sha256.10mb.warm` ms) | <ms> (<MB/s>) | under 100 ms, digest pinned | <PASS/FAIL> |
| SHA256Managed 10 MB (`sha256managed.10mb` ms) | <ms> | - | info |
| RSA 2048 key generation (`rsa.create` ms) | <ms> | - | info (frame freeze length) |
| RSA sign 1 KB, HashAlgorithmName API (`rsa.sign` ms) | <ms or error type> | no exception | <ok/fail> |
| RSA verify (`rsa.verify` ms) | <ms> | true | <ok/fail> |
| RSA sign / verify, string-named API (`rsa.sign.legacy`, `rsa.verify.legacy`) | <ms / ms> | true | <ok/fail> |
| RSA tampered blob refused (`rsa.verify.tampered`) | <acceptedTampered=false> | refused | <ok/fail> |
| RSA XML export / import (`rsa.xml.export`, `rsa.xml.import`) | <chars, verified> | verifies | <ok/fail> |
| RSA parameters round trip (`rsa.params.roundtrip`) | <crossVerified> | verifies | <ok/fail> |
| RSA public key file in the spike folder (`rsa.xml.file`) | <verified> | verifies | <ok/fail> |
| Key container persist / reopen / delete (`container.*`) | <file-seen, sameKey, cleared> | writable, nothing left | <writable / not-writable / left-behind / partial> |
| Key store left behind (`container.leftover`) | <nothing left / path> | nothing left | <ok/fail> |
| ECDsa P-256 create, named curve (`ecdsa.create`) | <impl or error type: message> | - | <ok/fail> |
| ECDsa create, default + KeySize 256 (`ecdsa.create.default`) | <impl, error, or skipped> | - | <ok/fail/skip> |
| ECDsa sign / verify / tampered (`ecdsa.sign`, `ecdsa.verify`, `ecdsa.verify.tampered`) | <ms / result> | verifies, refuses | <ok/fail/skip> |
| ECDsa parameters round trip (`ecdsa.params.roundtrip`) | <result> | verifies | <ok/fail/skip> |
| ECDsa XML export (`ecdsa.xml.export`, informational) | <result> | - | info |
| Signing chain complete (`verdict` rsaChain / ecdsaChain) | <rsaChain=..., ecdsaChain=...> | at least one | <PASS/FAIL> |
| Overall (`verdict ... result=`) | <PASS/FAIL> | both of the above | <PASS/FAIL> |

Host-runtime smoke for comparison (NOT the KSP answer; cloud box, Mono 6.8.0.105, xUnit
`SpikeS12Tests.Smoke_HostRuntimeNotKspMono_ProbeListRunsToTheEnd`, 2026-11-17): SHA-256
`SHA256Managed`, 10 MB cold 105.7 ms / warm 110.3 ms (about 90 MB/s on a shared slow box);
RSA 2048 keygen 860 ms, sign 28 ms, verify 1.2 ms, both sign APIs, XML and parameters round
trips all ok; `ECDsa.Create(nistP256)` and `ECDsa.Create()` both throw
`System.NotImplementedException`. Container probes not run there.

## What the design must change
<7.15 / 12: name the signing algorithm R7 uses (RSA 2048 PKCS#1 v1.5 over SHA-256 if ECDsa
throws in KSP's Mono), the key storage (CSP container vs a key file the mod writes), and the
hashing cost per MB for the standings payload; or "nothing".>

## Fallback taken
<none, or: a managed implementation shipped in the mod (R7 sizes it) because ...>

## Raw artifacts
<umbrella>/logs/spikes/S12/<YYYY-MM-DD>/: `spike-commit.txt`, `deployed-dll.txt`,
`deployed-dll-sha256.txt`, `dev-dll-before.txt`, `dev-dll-after.txt`, `KSP.log`,
`parsek-test-results.txt`, `s12-lines.txt`, `parsek-spike-S12/` (`probe.csv`,
`verdict.csv`, `rsa-public-<run>.xml`), `keypairs-before.txt`, `keypairs-after.txt`;
<umbrella>/logs/console/<YYYY-MM-DD>/spike-S12-build.log.

## Follow-ups
<new todo or owner question, if any>

## What cannot be known without the game
- Whether KSP's Unity Mono (not the cloud's Mono 6.8) has ECDsa at all, and which exception
  it throws when it does not; whether `HashAlgorithmName` / `RSASignaturePadding` exist in
  KSP's mscorlib (the `env` step and `rsa.sign` answer both).
- SHA-256 and RSA speed on the owner's CPU under KSP's JIT (the cloud numbers above are a
  slow shared box under a different Mono).
- Where KSP's Mono persists a named CSP container on Windows, whether it can write there,
  and whether the delete removes it (`container.*`).
- How long RSA 2048 key generation freezes the main thread in KSP.

Limits of the measure: one run, main thread, one frame between steps; the cold numbers
include the first JIT of each body; the warm median is of 3 repeats; the 10 MB buffer is in
memory (no file I/O is timed).
