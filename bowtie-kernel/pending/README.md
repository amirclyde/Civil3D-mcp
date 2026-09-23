# bowtie-kernel: work in progress for benched parts (21 Sep 2026) - NOT applied

`bench-part-wip.patch` is a diff against `bowtie-kernel/` as it stood on 20 Sep (31 tests). It is parked, not finished:
30 of 31 tests pass with it ("coverage: angle point" fails), so it must not be applied as it is.

What it contains
- Sections that do not end on the ground ("open ended": they meet the ground on the way out and carry on with a terminal
  bench) are recognised; they are not extended past their last point, a design/ground crossing along them is not taken as
  the daylight, and the valley may end by separation (`OpenEnded` in the result).
- Where both sides are on flat links beyond the first slope (benches), the sides are divided in plan and the valley takes
  the mean level (bench candidates merged into the tracked path, a true level meeting preferred where there is one).
- A steep link shorter than `MinSlopeRun` (0.5 m: drain walls, kerb faces) does not count as a slope.
- Level points are added between march steps until the valley level is straight to 10 mm (drains crossed by the valley).
- `solve` accepts `--level` (LevelFromTarget) and `--raw`.

Results: original UTNMBench snapshots `zzA-right` (curve 1) 109 -> 0 crossings, Ok, 0.07 m; `zzA-left` (curve 2) 19 -> 0, Ok, 0.19 m.
With UTNMBench v0.2 every section ends on the ground, so the first item matters much less; the unmodified kernel in the
plugin already solves curve 1 on v0.2 sections (104 -> 0, Ok). Curve 2 on v0.2 still fails in the plugin (the valley's
ground end falls inside the toe-drain notch: 0.52 m adjustment); the short-link rule and the level refinement are the parts
of this patch aimed at that, and they still need work (whole-corridor snapshot `zzV02c-left`: 2 crossings left).

Next: make "coverage: angle point" pass again (suspect: the MinSlopeRun rule or the level refinement changing an apex leg),
add fixture tests from `zzV02c-right.json` / `zzV02c-left.json`, then apply.

Note (21 Sep, later): `SeamSolver.cs` in the repo has since gained `OvershootReach` / `OvershootMax` / `OvershootLength` (the
valley's level run-on past its end, so the sections in the region margin find the target) and `Program.cs` the `--level`
flag. The patch was cut before that: its hunk around the "overshoot" point and the `solve` line will need merging by hand.
