# Tempo sample-basis diagnosis

The failed `fixture-2 / adaptive-hold-source` counted **4** sign changes in the old fixed-root hand projection. The actual hand-relative-forearm curve has **3** at both the old ±0.5° threshold and the independent ±0.15° threshold. The requested two cycles are intact.

The permitted actual forearm roll changes from 12.101603° to 12.816760°. Near the end, frame 5598 at game time 31.987564s reads 0.688551° in the old projection, but -0.00000445° in the actual relative joint. That parent-rotation offset crosses the old positive threshold and creates a spurious fourth count.

Across 137 same-frame comparisons, actual relative angle differs from the separately recorded command by at most 0.00002575°. The command is corroboration; cadence is still measured from actual bones. Arm direction error is0°, elbow error 0.00022888°, and shoulder/elbow residuals remain at micrometre scale.

Only Tempo completion now checks the independently measured relative-joint curve, with **exactly2×cycles−1** internal sign changes and unchanged amplitude tolerances. No runtime curve, geometry threshold, old Palm regression or legacy statistics were altered. Full failed evidence is preserved.

Raw fixed-root report: `Server/ARDY/runtime/semantic-tempo-v1/tempo-failed-v1/constraint-regression.json`  
SHA256: `be170d92011d8c1d26dd5fdc7aa3aafd03652e9e14b2297f14eec4255206de2c`

Raw independent report: `Server/ARDY/runtime/semantic-tempo-v1/tempo-failed-v1/tempo-regression.json`  
SHA256: `994b046b8dcfaafd0e113c2cdeaa7309a16886328409cccd828f24e82943845e`

The companion JSON contains timestamps, all crossing events, paired tail samples, forearm roll, source hashes and geometry measurements.
