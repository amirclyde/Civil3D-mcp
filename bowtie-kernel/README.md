# bowtie-kernel

The geometry behind the corridor bowtie repair, as plain C# with no Autodesk references. It builds and tests anywhere
(`net8.0`, no NuGet packages). The plugin's job is to read a snapshot out of Civil 3D and to write the result back; the
decisions about where each section stops are made here, where they can be tested.

Stage 3 of `bowtie-fix-plan-2026-09-19.md`. The plugin compiles these sources in and calls them from `bowtie_seam`.

## Run the tests

```
cd bowtie-kernel/BowtieKernel.Tests
dotnet run -c Release
```

The exit code is the number of failed tests. To solve a snapshot written by the plugin instead:

```
dotnet run -c Release -- solve snapshot.json result.json
```

## What it computes

For one bend, on the inside: the **seam** (valley line), which is the line where the design surface swept by the sections
before the apex and the surface swept by the sections after it are at the same level. Every inside section stops where it
meets the seam. The seam runs from the apex out to the point where it meets the ground; sections further away never reach
it and daylight normally.

- **Angle point:** the apex is the PI. This is the existing `bowtie_valley` construction (same march, same root rules, same
  tracking from the outer end inward) with one change: each side's level comes from the real section at the real station,
  not from one flat plane per leg.
- **Curve:** the apex is the centre of curvature at the tightest part of the curve. Every section on the constant-radius
  arc passes through that one point, so they are all stopped there: the second clip line is a short straight **apex bar**
  through the apex, square to the seam direction, and each arc section meets it at the apex itself. Nothing is left open
  around the point (the first version used a small arc 50 mm short of the centre, which left a hole). The bar is only as
  long as the converging sections need. A section stops at whichever line it meets first, seam or bar.

The result carries both clip lines, the meet stations, a per-station table (role, natural reach, clip offset, every crossing
with the clip lines, level closure against the other side) and the checks below.

## Cut changing to fill

Each side's level is read from the real section at the real station, so a bend where the inside daylight changes between
cut and fill needs no special input. What the geometry does:

- **The change is beyond the clip range** (the usual case: a bend in cut that runs out into fill further along). The seam is
  the same line as for the all-cut bend and simply meets the falling ground sooner. Sections past that point daylight on
  their own.
- **The change is at the bend.** A cut slope rises from its hinge and a fill slope falls from its hinge, so they never reach
  the same level and, over one ground surface, never cover the same ground. At an angle point only the lanes still
  overlap: the seam is a short line from the PI out to about the hinge, and it ends where the overlap ends
  (`seamEnd: separated`) or where a last short run of matching slopes meets the ground. On a curve the sections near the change are shallow, shorter than the radius, and the result is
  usually `NoBowtie`.
- **The legs differ in level** (a climbing corner, lower leg in cut, upper leg in fill). Just past the hinges the fill slope
  can be above the cut slope with the ground between them, so both do cover the same ground. No level joins them. The
  sides are divided in plan (equal offsets), those stations get the role `conflict`, and the step between the two slopes
  is reported as `LevelStep` (about 2 x grade x offset x tan(D/2)). Up to `MaxLevelStep` (0.30 m) it is a warning; above it
  the status is `DesignConflict`: the legs are too close for their level difference at these slopes.

`CutFillChanges` lists the stations where the daylight changes type. A seam may only end by separation when there is such a
change in range (or on a lane-only seam, or with no ground given); otherwise a seam that misses the ground still blocks, since
that means the wrong surface or templates that stop short.

## Inputs (`Snapshot`, schema 1)

- `samples`: the baseline as dense samples `s, x, y, dir` (0.05 m is what the tests use). An angle point is two samples at
  one station with different directions.
- `sections`: per applied station `station`, `z0` (baseline level) and `template`, the connected chain of inside links as
  `[offset, level relative to z0]`, ending at the **natural daylight**. Sections must be unclipped. A section marked
  `clipped` inside the search range makes the solver refuse.
- `ground`: optional grid for the daylight surface. Without it the seam is computed but not where it meets the ground.
- `bendFrom`, `bendTo`: the station range of the bend (the curve, or a metre either side of the PI). `BendFinder.Find`
  splits a baseline into single bends; a reverse curve gives two, on opposite sides.

## Statuses

`Ok`, `NoBend`, `NoBowtie` (no inside section crosses another at its natural daylight, so there is nothing to write),
`NoSeam`, `NeverMeetsGround`, `DesignConflict` (above), and `Unsupported` with the reason: the baseline turns both ways in the range, a string of
small angle points (tessellated curve), the curve radius is inside the road itself, the first-point or hinge offsets differ
either side of the bend, or clipped sections in range.

## Checks in every result

- `LinkCrossingsBefore` / `LinkCrossingsAfter`: plan crossings between sections at natural reach and at the clip.
- `FirstCrossingIsClip` per station: the first clip line a section meets is the one it is meant to stop at. The subassembly
  stops at the first crossing, so this is the check the first curve attempt failed.
- `MaxClosure`: largest level difference between the two sides where a section stops on the true seam. Millimetres.
- `ApexMismatch`, `ApexSpread`: the levels do not agree in a small patch at the apex. At an angle point that is lane level
  when there is a grade break; on a graded curve it is the last metre or two before the centre, where the arc sections
  arrive at levels `grade x arc length` apart. Above 0.10 m the result carries a warning. This patch is real, not a
  numerical artefact: it needs a look in the drawing.

## What the tests prove

Closed forms: angle point on a level profile (seam = bisector, ground point at reach / cos(D/2), meet stations at
PI +/- reach tan(D/2)); graded legs (seam = the intersection line of the two slope planes, to 2 mm); arc R = 12 (tangent
sections stop at R + x cot(D/2): 20.52 m at 6 m before the curve, 16.26 m at 3 m).

Independent brute force (Python, outside this code): spiral curve on a level profile, 17.88 / 13.77 / 12.57 m; the same
curve on a 3 % grade, 18.658 / 15.662 / 15.152 / 18.828 m. The kernel agrees to 4 mm.

Coverage: on a plan grid, using only the per-station clip offsets, every piece of ground inside the bend is covered by
exactly one section after the clip (0.00 m2 covered twice, 0.00-0.04 m2 uncovered, from 46-186 m2 covered twice before).
A companion test shows the check is not blind: clips 1 m short leave 25 m2 uncovered, 1 m long leave 24 m2 covered twice.

Cut to fill: a curve and an angle point in cut that run out into fill give the all-cut clip offsets to 5 mm and end
sooner; an angle point exactly at the change is clipped at lane level only; a climbing corner reports a 0.24 m step at 4 %
and is refused at 25 %; all with nothing covered twice and nothing left uncovered.

Also: a parabola where sections cross at 4.47 m although their own radius is 5.59 m; cut as well as fill; left and right
mirror each other; the refusals above; halving the sampling and the march step moves no clip by more than 5 mm; a snapshot
saved, loaded and solved gives the same seam.

## Not proven here

- The ten recorded FL-02 / FL-03 valleys. That regression needs snapshots from the drawing (plan stage 2).
- Anything about how Civil 3D carries two clip lines on one `ClipTarget` (plan stage 4).
- Benches: a template is extended past its daylight along its last slope, as the existing code does, so a bench that would
  have started just beyond the daylight is not modelled.
