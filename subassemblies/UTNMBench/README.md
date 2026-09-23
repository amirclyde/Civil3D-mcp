# UTNMBench v0.2

`UTNMBench_v0.2.pkt` is generated from the original `UTNMBench-C3D25-DrainConnect.pkt` (kept here, untouched):

    python make_pkt.py            # reads the original .pkt beside it, writes UTNMBench_v0.2.pkt

The generator reads the original flowchart, keeps every activity verbatim (same point and link numbers, codes and
expressions) and makes only the changes below. Same parameter names as the original, so an assembly can be swapped over.
This part draws the benching only; in a full typical section in cut the roadside drain sits in front of it, at P1.

## What changed

Repairs, always on:

1. The eight decisions that choose "partial slope + terminal bench" tested the FILL width of the terminal bench in cut and
   the CUT width in fill (the geometry behind them used the right one). With 6 m / 1 m the wrong ending was built whenever
   the remaining height was between 0.04 and 0.24 m. The test now agrees with the geometry.
2. One dead decision used `Math.Abs` on the bench rises while the live ones use them signed; signed is right (a bench that
   falls back towards the slope gives height back). Made signed.
3. `BenchDecision`, `TerminalBenchWidth`, `TerminalBenchHeight`: defined, never read (two were not bound to a variable).
   Removed.
4. `P290` had no From point; it starts at `P233` like its siblings.
5. The last daylight link always ran the way the ground lay at P1. Where the terminal drain ended on the other side of the
   ground the link pointed away from it, found nothing, and the section stopped in the air. It now runs up or down,
   whichever way the ground is from the end of the terminal drain. Where the drain already ends on the ground (1 mm) a
   marker point coded `Daylight` is put there instead, so every section carries a `Daylight` point.
6. Codes: every last link `Top, Daylight` (were none on `L135`, `L279`; `Top` only on `L182`, `L183`, `L195`), every last
   point `Daylight`; terminal bench links `Top` (`L156`, `L174` were `Top, Drain`); the uncoded terminal drain of the last
   ending (`L276`-`L278`, `P271`, `P274`) coded like its twenty siblings; slope link `L238` had no code (now `Top`);
   `P247` gets `TBenchDrainOut`.

`Fit To Ground` (Yes / No, default Yes):

The original takes the height of every stage from the ground straight above (below) the point where the stage starts, and
builds the terminal bench to that level 6 to 60 m further out, where the ground is somewhere else. It also takes
`Math.Abs` of that height: once a fill bench has overshot below rising ground the original keeps benching downwards under
the ground (BTC Road station 178: six benches, 33 m below the ground, 64 m out). With `Fit To Ground` the height is the one
that puts the END of the terminal bench on the ground it stands over: five passes (lay the stage out for the height so
far, read the ground at the end of the terminal bench, correct; secant steps from the third pass), the closest pass
decides. The same fitted height drives every decision of the stage. On even ground the result is the original to the last
digit. With `No` the part behaves as the original plus the repairs.

`Proposed Tolerance` (default 0.05 m): what the part proposes beyond the given parameters is marked, not hidden. A marker
point coded `Proposed` is put at the terminal drain where fitting changed the height of the last slope by more than this,
and on the daylight point where the last link runs the other way than the original would have sent it. Give the code a
point/feature-line style to see them in plan and section.

`Cascade Drain` (Yes / No, default No):

The berm drains run along the benches to the ends of the slope. Where the engineer adds a cascade drain down the slope,
use an assembly with this set to Yes for that region. Links coded `CascadeDrain` then join the drains down the slope:
berm drain to berm drain to the terminal drain (the toe drain, lowest point in fill); in cut the chain also runs from berm
drain 1 (or, without benches, from the terminal drain) down to P1. The original drew only the last piece (last berm drain
to terminal drain), at every station, uncoded.

## Checked

Offline (`simulate.py` runs a .pkt's flowchart over a ground line; `compare.py`, `replay.py`):

- 112 ground lines (cut/fill 0.1-30 m, cross-fall 0-25 %, wavy): the original leaves 50 sections not ending on the ground
  (up to 3.6 m), v0.2 none. With repair 1 switched off, v0.2 equals the original on all 14 even grounds, and
  `Fit To Ground = No` equals the original on all 112 (last link aside): every difference comes from a listed change.
- 1305 sections: every link coded, one `Daylight` point each.

In Civil 3D 2026 (21 Sep 2026, fixture drawing, corridors `ZZ orig` / `ZZ v02c` on BTC Road, bench height 4, both sides,
356 sections): the part loads, the Yes/No parameters are read (10 = Yes, 11 = No; how they look in the Properties palette was not looked at), layout mode equals the original. Every section
ends on the ground (within the 1 m check grid). With the original, the coded link chain stops short of the ground at 203 of
345 sections (last link uncoded or not found), 24 of them by more than 2 m, worst 32.7 m. The evaluator predicts
Civil 3D's reach at 350 of 356 stations (the rest: grid against TIN). Cascade links come out as described (station 60:
P1 - drain 1 - terminal drain on the left; P1 - drain 1 - drain 2 - terminal drain on the right).

Not fitted: where the ground is steeper than the slope and kinked, the five passes can fail to settle (1 of 333 fitted
endings on BTC Road: station 170 left, bench end 1.1 m under the ground); the last link still closes the section.
Beyond six benches the part runs out of stages, as the original does.

## Names

New names stay within three digits (Composer does not resolve `P0` or four-digit names in expressions): aux points
`AP100`-`AP229` (fit), `AP300`-`AP320` (last link); marker points `P300`-`P320` (Proposed, fitted), `P330`-`P350` (Proposed,
link reversed), `P360`-`P380` (Daylight on the ground); cascade links `L321`-`L331`; variables `RawH*`, `FitH*`, `FitY*`, `FitF*`.
