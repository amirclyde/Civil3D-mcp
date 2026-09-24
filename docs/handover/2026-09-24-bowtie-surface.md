# Handover 2026-09-24 - corridor surfaces that follow the valleys (bowtie_surface), live on GO-260809v6

Read `CLAUDE.md` first. Junctions stay out of scope.

## Goal (Amir)
Each drain's own corridor surface (`Drain FL-02 Top`, `Drain FL-03 Top`, `Drain FL-04 Top`) is one closed continuous surface
that follows every bowtie valley line exactly, with its `Corridor outline` boundary kept current after every fix / unfix /
refresh.

## Result (drawing QSAVEd 24 Sep ~07:40 via `civil3d_drawing save`)
Checked with `bowtie_surface dryRun` (a new read after each change) and `bowtie_check` on all three:
- **FL-03 Top: closed_and_following.** 8 valley breaklines (no duplicates), 0.000 m off the valleys (227 samples at 0.25 m),
  0 holes, area 9332.52 vs outline 9332.50, boundary reached everywhere. Channel point 38386.595, -57590.0758 still 41.30
  (no lid). bowtie_check verified; its 4 chord "join" notes dropped because the surface carries the valleys.
- **FL-04 Top: closed_and_following.** No repairs, nothing to add; its own `Extents` boundary left alone (area equal to the
  outline as built). bowtie_check verified.
- **FL-02 Top: closed, follows 5 of 6 valleys exactly; `not_following_valleys` only at BT-384Lb** - 0.078 m at
  38359.443, -57978.416 (station ~383.98, offset ~2.2 m left). That is the MD302 -> MD303 change at 384.17: two sections at
  one station with the drain top 0.3 m apart (design flag 2 of 23 Sep, junction topic). Not changed - highlighted for Amir.
  0 holes, area 13965.86 = outline, channel point on the CL at 384.17 reads 25.452 (invert). bowtie_check verified.

## What was built / fixed (commit on bc80d54)
- `bowtie_surface` (plugin `bowtieSurface`, `CorridorBowtieSurfaceCommands.cs`; MCP `civil3d_corridor action bowtie_surface`,
  dryRun = read-only). Wired into bowtie_fix / unfix / refresh / check (see the 23 Sep project handover).
- Found live and fixed:
  1. **Trim started at the drain's INNER wall top** (first Top link, 0.75 m on MD100): every valley began on the vertical
     inner wall, the dry run read 1.5 m off (the invert) - a real run would have lidded the channel. Now the valley starts
     5 mm outside the structure edge = end of the leading run of Top links that bound a shape (`StructureEdge`; 1.05 MD100,
     1.95 MD302, 2.1 MD303). Reported per valley as `valleyTrim`.
  2. **Outline corner at the last section end on the valley**, short of where the valley meets the ground (~1 m at BT-577):
     the tip fell outside the boundary. `ComputeOutline` takes `valleyTips` (end of each trimmed seam) and turns the corner
     there (`valleyTipsUsed`). surface_create's outline is unchanged (no tips passed).
  3. **In-run check reads the surface before Civil 3D rebuilds it** (on commit): it reported holes / a 5.63 m2 gap that a read
     right after showed were not there. The plugin row now says so (`checkNote`); the TS layer re-checks with a dryRun
     after bowtie_surface, bowtie_fix, bowtie_unfix and bowtie_refresh (`recheckSurfaces`, `withRecheck`,
     `summaryWithRecheck`; `checkedAfterCommit`).
  4. **Removing earlier breaklines skipped every second one** (operation handles are index-based): 3 duplicates left on FL-03.
     Now removed by index from the end, each fetched again, and the run refuses (transaction not committed) if any tagged
     breakline is left. Live: FL-03 swapped exactly 8.
  5. Check reports `boundaryNotReached` (walks the boundary 5 cm inside every 0.5 m) and `holesFrom`/`holesTo` per valley.
  6. Real `bowtie_surface` now needs approval (`safeForRetry: false`).
- Kernel tests: 49 pass (run with `dotnet build ... -nodeReuse:false --disable-build-servers` then the dll under a timeout -
  the earlier "hang" was the build server).

## TypeScript (approval gate, re-check)
Live-tested 07:45 after an app restart: real `bowtie_surface` on FL-03 asked for approval, found everything current (no
change, no rebuild) and returned `checkedAfterCommit: true`. The automatic re-check after an actual change runs the same
dry run that was checked by hand after every run today.

## Wiring test (08:04, drawing QSAVEd after)
Real `bowtie_refresh` on FL-03 with no design change (approval asked, background, 9 s): 8 of 8 `unchanged` (plan, level and
meet stations 0), 8 of 8 clean in the built corridor, and the `surfaces` block present with `checkedAfterCommit: true`:
Drain FL-03 Top closed_and_following, 8 breaklines, nothing changed. So fix / unfix / refresh hand over to bowtie_surface and
the re-read works; the change path inside them is the same `bowtie_surface` code proven above.

## Slope flags - decision (Amir, 24 Sep 08:03, on the engineer's behalf as a working assumption)
The engineer accepts the slopes where the repair matches the two sides' levels at the valley (steeper than the 1:2 design,
over short strips at the valley edge). No remedial change is made; the flags stay in the reports as a record:
- FL-03 BT-274R at 274.741: 1:1.99 (offsets 2.05-10.05 and 11.05-13.09 m).
- FL-03 BT-404L at 404.317: 1:1.78 (offsets 4.75-5.47 m).
- FL-02 BT-384La/b at 383.43: 1:1.76.
This does not close FL-02 at 384.17: there the drain top itself steps 0.3 m between two sections at one station (MD302 ->
MD303), which one surface cannot follow - the transition is the junction topic.

## Next
- Amir: decide the FL-02 384.17 drain-top step (junction topic) - then FL-02 Top should close fully on BT-384Lb.
- Pasted / composite surfaces built from the Top surfaces (`Core FL-0x`, composites) may be out of date: rebuild them.
