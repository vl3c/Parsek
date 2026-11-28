# P9 Y2 / Y3 / Y4 / Y7 flights (throwaway branch p9-y-flights, never merged)

One lane, `harness/scenarios/local/P9-burst-dust.toml` (force-added here; copy it into the
worktree that flies). It runs every X4 burst case in air (explode, vessel-explode,
burst-custom, decouple; label `pb`) and then the X4 dust wave on the ground (label `pd`),
stock-minimal, report-only. It is also X4's first live run.

Fly it once per build, on a copy lease (2 runner slots max while Vlad is at the PC):

```
# BEFORE = origin/main (b16341141 or later main)
git -C <repo> fetch origin
git -C <repo> worktree add ../Parsek-p9-before origin/main --detach
cd ../Parsek-p9-before
git checkout origin/p9-y-flights -- harness/scenarios/local/P9-burst-dust.toml
cd Source/Parsek && dotnet build -p:SkipKspDeploy=true && cd ../..
cd harness && python missions/bootstrap_venv.py
python tools/ksp_lease.py claim --owner p9-thread
python provision/provision.py --profile stock-minimal --lease
# verify the automation copy's DLL hash equals bin/Debug/Parsek.dll
mkdir -p ../../logs/console/$(date +%F)
python run.py --lease --spec scenarios/local/P9-burst-dust.toml > ../../logs/console/$(date +%F)/P9-burst-dust-before.log 2>&1
python tools/ksp_lease.py release
```

AFTER runs use the branch under test instead of origin/main (p9-y4 first; later p9-y2, p9-y3).
After p9-y4, check the automation DLL carries the UTF-16 literal `Joint break draws nothing: `.

Collect per run and commit to this branch under
`docs/dev/research/p9-y-flights/<build>/` (build = before, y4, y2, y3):
- `results/<runId>_partparity/pb/*.burst.png` and `pd/*.png` (the atlases), `manifest.json`,
  `*.part.json`
- the run's result JSON and `<runId>_result*.json` verdict line
- `grep -E "\[DecoupleFx\]|burst case .*: captured|PartParity step|\[ERR\]|Exception" KSP.log`
  into `log-key-lines.txt`
Then `git push origin p9-y-flights` and report the runId, verdict, case count, and anything odd
(a case that never triggered, stray objects, a crash, a missing picture).
