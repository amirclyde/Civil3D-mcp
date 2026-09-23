# UTNMBenchClip

UTNMBench v0.2 (`../UTNMBench/UTNMBench_v0.2.pkt`: benches with berm drains, terminal bench with toe drain, fitted to the
ground, optional cascade drain links) with the two bowtie clip targets added. Generated, not hand-written:

    python make_pkt.py            # reads ../UTNMBench/UTNMBench_v0.2.pkt, writes UTNMBenchClip.pkt (v0.3)

The generator reads the source flowchart, keeps every activity verbatim, flattens it into one flowchart (branches that
join again stay joined), and puts a clip decision in front of each of the 135 link-making points (see the header of
`make_pkt.py`). If UTNMBench changes, regenerate it first, then run this generator again.

Added: targets `ClipTarget` (offset) and `ClipElev` (elevation), parameters `LaneWidth` (0 = none) and `LaneSlope`.
Clipped ends are coded `Valley`; the clipped link keeps its own codes plus `Clip`; a clipped section ends there (no later
bench, drain, marker or cascade link). Attachment point is `P900` (no codes); the source's `P1` is the lane end (= P900
when there is no lane).

Composer limits met on the way: a point named `P0`, or names with four digits (`AP1000`, `P1142`), load but make every
expression that mentions them fail, and the part then draws nothing. New names are P501-P635, L601-L735, AP401-, P900-P902
(UTNMBench v0.2 itself uses up to P380, L331, AP320).

v0.3 (21 Sep 2026), from UTNMBench v0.2: checked offline only so far - with no clip target, 605 simulated sections (cut and
fill to 30 m, cross-fall to 20 %) are identical to UTNMBench v0.2 in points, codes and links; the part loads in Civil 3D
2026 and its layout equals v0.2. NOT yet run in a corridor, clipped or unclipped.

v0.2 (20 Sep 2026), from the ORIGINAL UTNMBench: kept as `UTNMBenchClip_v0.2.pkt` / `make_pkt_v0.2_from_original.py`.
Checked in Civil 3D then (corridors `ZZ A` = original part, `ZZ C` = clip part on BTC Road, BenchHeight / MaxDaylightHeight
4): unclipped, 345 sections identical to the original to the last digit; with the BT-C1 valley mapped, station 96 stops on
slope 2 at offset 15.775, Z 48.558 (the line's level) and station 106 at 15.000, Z 48.170, coded Valley, link Top + Clip.
