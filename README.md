# StratumParity

Differential behavior test suite for [Stratum](https://github.com/StratumServer/Stratum),
the performance-oriented server fork of Vintage Story. Built on
[Atlas](https://github.com/Pixnop/Atlas), the in-process integration test harness.

**Dashboard**: [stratumserver.github.io/StratumParity](https://stratumserver.github.io/StratumParity/):
run history, every Stratum build ever tested (pinned stables and scouted indevs),
per-flavor and per-scenario duration trends, and the latest parity verdict, republished
by CI on every main-branch run. Adoption into Stratum's own CI is tracked in
[StratumServer/Stratum#147](https://github.com/StratumServer/Stratum/issues/147).

Stratum promises performance gains without changing vanilla gameplay behavior. This suite
puts that promise under test: the same scenarios run against a vanilla install and against
a Stratum install, and the results must line up. Scenarios fall into two families:

- **Parity scenarios**: behavior that must be identical on both servers. Any divergence is
  a regression (in Stratum, or an incorrect assumption in the scenario).
- **Probe scenarios**: behavior that Stratum intentionally changes (entity tick throttling,
  simulation distance). Probes measure the delta, assert the vanilla baseline on vanilla,
  assert the documented Stratum behavior on Stratum, and verify that disabling the feature
  through `stratum-performance.json` restores parity.

## Coverage

Ninety-three scenarios in thirty-two classes (plus thirty world free unit tests of the
known-divergence policy, the golden file formats, the ore count bands, the registry and
tree digests, the settle helper, the lighting closed forms and the mechanical power speed
model): parity scenarios, probes, and one informational perf measurement, green on
both flavors against the pinned pair (fourteen confirmed Stratum bugs are pinned as known
divergences, listed below), re-run
by CI on every PR and weekly against the pinned Stratum release. A separate daily **indev scout**
workflow resolves the latest Stratum pre-release and runs the same suite against it:
non-blocking early warning for the next stable. Scout runs are recorded in their own
history and shown in the dashboard's Builds section, never mixed into the stable trends.

| Surface | Scenarios | What is pinned down |
|---|---|---|
| Boot, block placement, join | `SmokeParityScenarios` | Fundamentals identical on both flavors; a joined player survives Stratum's packet limiter; the CI leg's expected flavor is asserted against the loaded server (the flavor guard, `PARITY_EXPECTED_FLAVOR`) |
| Entity tick throttling | `EntityTickingProbes`, `EntityTickingDisabledScenarios` | Exact counts against the engine entity-simulation tick counter: near entities tick once per sim tick on both flavors, entities in Stratum's very-far band (beyond 96 blocks; the probe puts one 200 blocks out) exactly 1 in 10 on Stratum and once per sim tick on vanilla; the mid (1 in 2) and far (1 in 5) bands are not probed; `EntityTicking.Enabled: false` restores exact parity; the probes are run with both a straw dummy and an AI creature (a raccoon) that stays put |
| Command surface | `CommandParityScenarios` | Vanilla commands behave identically (incl. unknown-command error codes); `/stratum` and `/sethome` exist on Stratum only |
| Block tick listeners | `BlockTickListenerProbes`, `BlockTickListenerDisabledScenarios` | Far listeners are skipped entirely on Stratum (128-block radius), force-loaded columns stay exempt, toggle restores parity |
| Chunk persistence | `ChunkPersistenceScenarios` | Blocks + chunk moddata survive save/unload/reload cycles identically through Stratum's incremental autosave (its opt-in pooled chunk reads are off by default and not exercised here); sunlight and block light around a torch are re-read after the reload. A column nobody keeps loaded persists its blocks, chunk moddata and map chunk moddata when the engine unloads it by itself, with no save requested. One chunk holding 300 distinct plain block types (a 512 entry palette, stored compressed) reads back exactly after the engine packed it idle, after a save, unload and reload with a torch beside them reading 14 and 13, and after a palette shrink (brought on by filling the palette to 512 values with most of the first 300 gone) and a second round trip. A straw dummy, a named wounded raccoon and a stack of 64 flint come back from an unload and reload with the same id, code, position, health, nametag, stack and no duplicate |
| Incremental flush | `IncrementalFlushProbes` | Probe of Stratum's incremental dirty flush (`Performance.AutoSave.IncrementalDirtyFlush`) against vanilla's save policy. A pattern, chunk moddata and map chunk moddata written after `World.SaveNow` and left alone for 1500 ticks are lost on an API unload on vanilla and kept on Stratum, whose chunk thread writes dirty columns every 10 s (the `DirtyForSaving` flags of the chunk and the map chunk back the timing with world state, and a baseline saved first proves the stored copy was applied); with the flush switched off through `/stratum set` Stratum loses them as vanilla does; a column the engine unloads by itself still persists everything with the flush off, on both flavors (save on unload is then the only writer). A documented probe, not a bug |
| Restart persistence | `RestartPersistenceScenarios` | A graceful restart keeps the savegame store and the chunk, map chunk and map region moddata of a staged probe mod (one boot record appended per boot, so all four read `boot1, boot2`), plus one marker block per boot. The restart is the practical round trip for the map region and the map chunk, which do not unload with their column |
| Chunk read pool | `ChunkReadPoolScenarios` | With `Performance.ChunkIo` switched on through a seeded fixture (four workers, vanilla ignores it): a column, then 25 columns reloaded in one request, come back with their pattern, per column moddata and one marker block per chunk level. On Stratum the pooled path is proven to have done the reading (the prepared statements of the pool run exactly 8 times for one column and 200 for 25, read by reflection) and the boot diagnostics hold no read path error. Covers recycled connection slots, not contention: reads of different columns never overlap on the single chunk thread |
| Random ticks | `RandomTickProbes`, `RandomTickDisabledScenarios` | Vanilla gates random ticks to 5 chunks around Playing clients; Stratum clamps to 3; probed at chunk distance 4 via a staged source mod whose block converts on every random tick. A full chunk of counter blocks gets bursts of 16 random ticks per pass on vanilla and at most 8 on Stratum (`MaxRandomTicksPerChunk`), and `BlockTicks.Enabled: false` brings the 16 back; the burst size is read off the probe log |
| Climate queries | `ClimateQueryScenarios` | `GetClimateAt` full modes return independent objects, the int overload ignores the previous query, the temperature modes give stable values per column and match a fresh computation; Stratum diverges on all four (known divergences below). Probe: `AllLoadedMapRegions` is a fresh copy per read on vanilla and one shared snapshot per tick on Stratum, which caches it on purpose (Stratum commit `035a42d`; a contract difference with no practical effect, not reported) |
| Block simulation contracts | `BlockTickContractScenarios` | A handler that keeps its random tick position never sees it rewritten (a port of Stratum's own check for StratumServer/Stratum#353), a neighbour gets exactly one `OnNeighbourBlockChange` per change, 2000 changes in one tick all drain in passes of at most 500, a bulk accessor commit notifies every modified block; read through counter blocks of the staged probe mod |
| Chiseled blocks | `MicroblockEditScenarios` | Two blocks open for editing at once keep their own shapes (the WorldEdit chisel brush path); a rebuild from cuboids preserves the voxel set, the volume and the face solidity for stairs, checkerboard, single-voxel and tunnel shapes, with expectations derived in the test |
| Pathfinding | `PathfindingScenarios` | Probe: the nodes of a found path survive the next search on vanilla and are rewritten by it on Stratum, which pools its A* nodes on purpose (Stratum commit `ad381da`: callers are expected to convert a found path to waypoints at once, not a bug). Parity: a synchronous search follows the only corridor of a walled maze and returns no path to an unreachable target; a raccoon walks the maze through the asynchronous workers without getting stuck |
| Physics activation queue | `PhysicsActivationQueueScenarios` | 120 entities spawned and 70 despawned in one step leave no physics tickable behind and none of the despawned ids in the observer's server-side tracked set (what the client keeps of them is logged on the affected builds, asserted elsewhere); with the activation cap switched off through `/stratum set` there is no leak |
| Inventory resync | `InventoryResyncScenarios` | A survival player killed with stacks in the hotbar and the bag has the emptied slots flushed to the client within 10 ticks on a quiet server (a main-thread probe first proves the drop dirtied them); the dropped item entities equal the former inventory |
| Asset matching and registry | `AssetMatchingScenarios` | Two truth tables on `WildcardUtil.Match`: 26 common rows that both flavors agree on, and 12 edge rows (10 `Match` calls and 2 through `SatisfiesAsIngredient`) that form a probe, because the fastMatch rewrite of Stratum answers every one differently from vanilla (empty `allowedVariants`, a leading `@`, a `*` domain with a literal path, letter case, a null wildcard); that difference is under discussion and not reported yet; a per-code digest of every block, item and entity type and the grid recipes against a golden captured from the vanilla leg (`fixtures/assetmatching-registry/`, written with `PARITY_REGISTRY_CAPTURE`, re-pinned on a game bump, `PARITY_REGISTRY_DUMP` writes the canonical text behind each hash for a diff); a shaped, a shapeless, a wildcard and a tool recipe crafted through the player's grid consume exactly their inputs |
| Despawn clock | `DespawnClockScenarios` | Dropped items 16, 80 and 200 blocks from the nearest player age at the same rate over 60 s (the clock is the `deathTime` attribute plus the behavior's accumulator); with `EntityTicking.Enabled: false` all three keep pace on both flavors |
| Body temperature | `BodyTemperatureProbes` | Updates of a survival player's body temperature counted over 600 entity ticks: about every second on vanilla, about every three seconds on Stratum with its jitter (5 to 9 updates over 20 s); `BodyTemperature.Enabled: false` is expected to bring the vanilla cadence back |
| Terrain generation | `WorldgenTerrainScenarios`, `WorldgenSplitDisabledScenarios` | On the standard world of seed 7351, in a golden rectangle of 16 columns about 2000 blocks from spawn: height map, rock strata and cave air against a golden from the vanilla leg (`WorldgenGoldens`, re-pinned on a game bump), also with Stratum's split terrain pass switched off through a seeded `stratum.json`; the ore blocks of each column and the water rivulet sources of 36 peeked columns against goldens captured from the vanilla leg (`fixtures/worldgenterrain-goldens/`, written with `PARITY_WORLDGEN_CAPTURE`; the ore blocks are a probe, see below); the prospecting pick's strata simulation against the generated column; a peek up to each worldgen pass (Terrain, TerrainFeatures, Vegetation) tells the passes apart and never loads the column; 200 columns requested at once all reach Done and the request queues drain back; `/stratum pregen` over the rectangle completes and gives the golden terrain. Stratum diverges on caves, rivulets and the Terrain peek (known divergences below). Probe: the ore blocks. Vanilla equals the ore golden, while Stratum's optimized `DiscGenerator` (pull request StratumServer/Stratum#33, commit `3f621d7`: it hashes the disc edge per block column instead of sampling a smooth noise, and `GenDeposit` takes 9.1 s instead of 32.6 s over 10,200 chunks) places other ore blocks for the same seed. The deposits are the same: in the 16 golden columns Stratum has 17431 ore blocks of 21 ores against vanilla's 17115 of 20 (+1.8%), a count per column between -13.5% and +19.8% of vanilla's, and the same most plentiful ore in every column. A deliberate speed trade-off that Stratum accepted, so not reported; the scenarios hold Stratum to bands (total within 10%, each column within 30%, same most plentiful ore) and never pin a block |
| Worldgen pass contract | `WorldgenPassContractScenarios`, `WorldgenPassContractSplitScenarios` | A staged probe mod registers a chunk column generation handler at each of the five passes: each one runs once per column in pass order, while the column's own map chunk is at that pass, with the eight neighbours ready after Terrain and the column Done afterwards, and the rock, granite and air counts of the column at the Terrain pass equal the vanilla golden in the stock topology (Stratum diverges on caves and tall grass, known divergences below). With the split kept next to the foreign handler (`AutoDisableSplitForMods` off through a seeded `stratum.json`) the contract holds too, and a Terrain handler sees the pre-strata shape on Stratum by design (granite equals rock): a documented probe, not a bug |
| Story structures | `WorldgenStoryStructureScenarios` | The six story structures of seed 7351 sit where vanilla puts them (centres taken from the vanilla leg, next to the configured distances and directions) at the vanilla Y, nothing shifted on a 256 high world; the ruins of a 12x12 area of columns never overlap each other, with at least 50 of them (vugs and lakes are left out, vanilla overlaps those); runs on the survive and build playstyle, because the creative one switches lore content off |
| Tree generation | `TreeGenerationScenarios` | Stratum's `TreeGen` rewrite (block positions from a manual stack indexed by branch depth, a scratch position for vines, reordered moss conditions) must not change a tree: every `TreeGen` generator (35 in the base game) grows two sapling shaped and two worldgen shaped trees (vines and moss on, either hemisphere) at one spot of a flat world, from an `LCGRandom` seeded per generator and sample, and the digest of every block each one staged (through a bulk block accessor that is never committed, so the world stays untouched) equals a golden captured from the vanilla leg (`fixtures/treegeneration-goldens/`, written with `PARITY_TREEGEN_CAPTURE`, re-pinned on a game bump, `PARITY_TREEGEN_DUMP` writes the block list behind each digest for a diff); the whole set grown a second time in reverse order must give the same digests |
| Lighting | `LightingScenarios` | Block light, sun light and relight against Stratum's rewrite of the `ChunkIlluminator`, read through full cube lamps of a staged assets mod (exact `lightHsv`, no absorption) and never by counting ticks: every wait is a positive reaction followed by identical reads. Closed forms, exact on both flavors: the block light of a torch or a lamp over a 15 block cube is the maximum over the sources of V minus the Manhattan distance, a brighter second source added and removed gives the maximum and then the first source alone, and a lamp on the edge of its chunk along each axis lights the next chunk by the same form. A 1 wide corridor through glass, leaves, water and granite reads the same levels on both flavors (16, 15, 14, 12, 10, 9, 0, 0, 0); the sun light of a hollow reads 22 open, 0 sealed and 22 reopened on both; the rain height map follows two stacked roofs placed and removed in both orders. Three per flavor goldens that are documented differences of the rewrite, not bugs: a slab, two stairs and a chiseled block in a corridor (vanilla stops the light behind each one, Stratum's face aware absorption passes about half behind the slab and the stairs open along the ray, and gives the stairs seen on their solid face and the chiseled block no light in their own cell); the mix of five overlapping lamp colours (hue 21, saturation 3 on vanilla, hue 18, saturation 5 on Stratum, whose Top-4 colour tracking drops the weakest source); and `FullRelight` over a cluster of 12 lamps and one lamp 150 blocks away (the sun light is unchanged on both; vanilla re-places the sources at a doubled chunk offset, off the map, so the region reads dark afterwards, Stratum restores the cluster and leaves the far lamp dark, outside the 3 x 3 x 3 chunks around the centroid of the sources). The cluster stays at 12 lamps because vanilla keeps at most 15 light sources per cell |
| Block mechanics | `BlockMechanicsScenarios` | Four block world mechanics that Stratum's patches touch, each against an exact expectation derived in the test (a closed form or a brute force model over a fixed scene, never a golden or a block id): a water source on a granite plate spreads to level 7 minus the Manhattan distance and drains to nothing once the source is removed; a lattice of walled one block shafts holds 100 sand blocks on supports and collapses into the bottom four layers when they go, at 12, 48 and 76 blocks from the anchor (a falling block is only simulated within 96 blocks of a player), with no stray sand and no falling entity left (the far band takes about 3 times as many ticks on Stratum, inside the timeout); 120 lone sand blocks released in one step all land, past the activation cap of Stratum's physics manager (50 per tick by default); `GetNearestEntity` (ties, ranges, filters) and `RayTraceForSelection` over a fixed scene of inactive dummies and one granite block match the model on both flavors, the ray straight down never selecting a dummy on either one |
| Block entity time | `BlockEntityTimeProbes` | Probe of what Stratum's listener limit (`Performance.SimulationDistance.LimitBlockGameTickListeners`, 128 blocks) and `BlockEntityInit` stagger do to the time of block entities, in columns that are never force loaded. After a 49 hour calendar jump four far torches are all burned out with the near ones on vanilla; on Stratum they are still lit while nobody is near and burn out within a few checks of the anchor teleporting in (a torch keeps its own calendar stamp, so it catches up). A far fire burns out with the near one on vanilla; on Stratum it is intact after the hold and burns its full 5 s after the anchor arrives (a fire counts the delta time of its own listener, so the time it was not ticked is lost, no catch-up). With the limit switched off through `/stratum set` Stratum gives the vanilla result for both. Two listeners registered at one position fire together after 1000 ms on vanilla; on Stratum the one with no initial delay waits for the position hash offset of `BlockEntityInit` as well (about 1270 ms against 1003 ms for the control at the position picked), and with that feature off both fire as on vanilla. Documented probes of intended behavior, not bugs |
| Mechanical power | `MechanicalPowerScenarios` | A creative rotor with its axles, an angled gear and a quern against Stratum's patch of `MechanicalPowerMod` (a lock around the network registry, a reused list for the per tick copy): two rotor lines of different length and speed setting each build exactly one network with exactly their members, settle at the speed derived from the network update rule (0.2972 and 0.5975, equal to the model on both flavors) and gain angle at the rate the mod's tick time implies (ratio 1.000, so a network that nobody ticks or that is ticked twice fails); a rotor, three axles, an angled gear and a quern grind 8 grain into exactly 8 flour in about 1050 ticks at the modelled speed (this scenario takes about 40 s per flavor and the class about 65 s, and stays on every pull request); the same chain with power 5 and speed setting 6 (so a fresh rotor reading the defaults fails) reforms with the same members, settings and speed after its column is saved, unloaded and loaded again. Members are compared as positions relative to the rotor, never as ids |
| Tick cost (perf) | `TickCostProbes` | Server work-ms per tick under a fixed active-entity load, emitted to the dashboard as a trend; informational, not a pass/fail gate (see below) |

### Known divergences

Fourteen Stratum behaviours that differ from vanilla are pinned rather than hidden. On the two
builds where each was confirmed (1.22.7-stratum.2 and 1.22.7-stratum.2-indev.1) the Stratum
side of the scenario asserts the observed bug shape, so a fix turns it red and the entry is
deleted; vanilla and every other build stay strict parity. Each one is an issue on
StratumServer/Stratum.

- [StratumServer/Stratum#360](https://github.com/StratumServer/Stratum/issues/360) (`ClimateQueryScenarios`, two scenarios): the full `GetClimateAt` modes return one shared per-thread object that a later query overwrites, and the int overload returns it with stale world gen fields from the previous full query.
- [StratumServer/Stratum#361](https://github.com/StratumServer/Stratum/issues/361) (`ClimateQueryScenarios`): on a cache miss the temperature and rainfall mode returns the stored cache entry, which the `OnGetClimate` handlers then rewrite, so the second query at the same position differs.
- [StratumServer/Stratum#362](https://github.com/StratumServer/Stratum/issues/362) (`ClimateQueryScenarios`): the temperature only cache key is chunk granular and wraps every 1024 chunks, so every column of a chunk gets the first queried column's values and a chunk 1024 chunks away shares the entry.
- [StratumServer/Stratum#353](https://github.com/StratumServer/Stratum/pull/353) (`BlockTickContractScenarios`): the random tick position pool hands a handler an object that a later pass rewrites in place; fixed upstream, in neither build yet.
- [StratumServer/Stratum#356](https://github.com/StratumServer/Stratum/issues/356) (`MicroblockEditScenarios`): `BeginEdit` hands every block on the game thread the same voxel and material grids, so editing a second block rebuilds the first from its shape.
- [StratumServer/Stratum#358](https://github.com/StratumServer/Stratum/issues/358) (`PhysicsActivationQueueScenarios`, two scenarios): with the default activation cap, an entity that despawns while its physics add is still deferred leaves a permanent tickable and stays in the observers' server-side tracked set; in two of four full-suite legs the client also kept 20 of the 70 despawned entities.
- [StratumServer/Stratum#357](https://github.com/StratumServer/Stratum/issues/357) (`InventoryResyncScenarios`): the slots emptied by a death drop stay dirty and unsent on a quiet server, because `InventoryBasePlayer.DropAll` and the backpack override add to `dirtySlots` directly while the `StratumAnySlotDirty` fast path of `SendDirtySlots` only wakes on `MarkSlotDirty`.
- [StratumServer/Stratum#363](https://github.com/StratumServer/Stratum/issues/363) (`AssetMatchingScenarios`): the lazy `RegistryObjectType.Resolve` resolves nested children before merging the picked `xByType` values, so `game:clothes-face-surgeonhood` ends with warmth 1 instead of 0.25; it is the only registry object that differs across the digest.
- [StratumServer/Stratum#359](https://github.com/StratumServer/Stratum/issues/359) (`DespawnClockScenarios`): an item more than 96 blocks from every player ages at about 0.6 of real time, because the very-far tick band hands the despawn behavior 0.33 s per call and it clamps each call at 0.2 s; switching entity ticking off restores it.
- [StratumServer/Stratum#364](https://github.com/StratumServer/Stratum/issues/364) (`BodyTemperatureProbes`): `Performance.BodyTemperature.Enabled: false` keeps the field defaults of the patched behavior (2 s update, 5 s heat source pass) instead of vanilla's 1 s and 3 s, so the update cadence stays at half the vanilla rate.
- [StratumServer/Stratum#366](https://github.com/StratumServer/Stratum/issues/366) (`WorldgenTerrainScenarios`, `WorldgenSplitDisabledScenarios`, `WorldgenPassContractScenarios`): the rewritten `GenCaves.SetBlocks` keeps the rock in lava cells, cuts step floors deeper, checks liquids per column and clips domes, so the rock layer (the lava band at y 1 to 7) of five of the 16 golden columns differs, and the Terrain pass rock count of two columns of the pass contract area moves by 25 and 1 blocks.
- [StratumServer/Stratum#368](https://github.com/StratumServer/Stratum/issues/368) (`WorldgenTerrainScenarios`, `WorldgenSplitDisabledScenarios`): the guard in `GenRivulets.tryGenRivulet` draws the height from a bound one smaller than vanilla, so the same seed gets other rivulet sources: one extra in the golden rectangle, and 13 sources against vanilla's 19 with none in common in the peeked wet area.
- [StratumServer/Stratum#367](https://github.com/StratumServer/Stratum/issues/367) (`WorldgenTerrainScenarios`): `PeekChunkColumn` up to Terrain stops after the first half of Stratum's split Terrain pass, so the peeked column is bare granite, with no other rock, no soil and no caves.
- [StratumServer/Stratum#369](https://github.com/StratumServer/Stratum/issues/369) (`WorldgenPassContractScenarios`): in the stock Terrain topology `GenBlockLayers` places tall grass from a worker generator that is not seeded per column, so the tall grass differs from vanilla and from one run to the next.

One scenario is a perf measurement rather than a parity check: `TickCostProbes` records the
server's per-tick work time under a fixed load of active entities and emits it to the dashboard.
It is **informational, never a pass/fail gate**: vanilla and Stratum run on different CI
machines (no same-machine pairing) and the engine's tick-cost counter has 1ms integer
resolution, so a reliable threshold is impossible. The scenario asserts only structural facts:
the server ticked, the number is finite, and the load did not make ticks cheaper than idle
(within the counter's 1ms floor);
the two trend lines on the dashboard are read by a human.
Locally it shows Stratum's core tick loop at roughly a third of vanilla's cost (its per-tick
allocation and LINQ reductions). Caveats: headless single-process superflat world, not a
realistic multiplayer benchmark; numbers near the 1ms floor are trend, not value; the load size
is a runner-speed-tuned constant. The figure is the mean per-pass busy time that Atlas's
`MeasureTicks` reports (`PassTimingStats.MeanMs`), median of five 90-tick windows; see
`scenarios/StratumParity.Scenarios/TickCostProbes.cs`.

Field results so far: Stratum's chunk persistence is byte-faithful, its throttles behave
as documented and their toggles genuinely restore vanilla behavior, and no behavioral
drift appeared between stratum.14 and stratum.15, nor on 1.22.7 between stratum.2 and its
indev prerelease. The suite also surfaced
[StratumServer/Stratum#146](https://github.com/StratumServer/Stratum/issues/146)
(shutdown never completes when the game thread is blocked) and
[#151](https://github.com/StratumServer/Stratum/issues/151) (process-killing race in the
background assets packet build), and drove two Atlas features (Playing-state test
players in 0.9.0, assets-build wait on join in 0.9.1).

One lesson from the scout lane: from 26 August to 13 September every daily run was red,
not because gameplay had drifted but because Stratum's new first-packet gate rejected the
harness's synthetic join, which opened with the identification packet. Atlas 0.13.1 opens
with the login token query, as a real client does, and the lane went green again the same
day it was adopted. A red streak is worth reading before it is worth trusting.

## Requirements

- .NET 10 SDK
- A vanilla Vintage Story server install (1.22.x)
- A bootstrapped Stratum install (run `StratumServer` once so the vanilla assets and the
  Stratum overlay are in place)

## Running

One install at a time (the standard Atlas workflow):

```bash
VINTAGE_STORY=/path/to/vanilla-server dotnet test -c Release
VINTAGE_STORY=/path/to/stratum-server dotnet test -c Release
```

Both installs plus a comparison report (requires the Atlas CLI at the same version as the
package the project references: `dotnet tool install -g Pixnop.Atlas.Cli --version 0.16.1`):

```bash
scripts/run-parity.sh /path/to/vanilla-server /path/to/stratum-server
```

Both paths must be server installs. The vanilla one is the unpacked
`vs_server_linux-x64_<version>.tar.gz`; the Stratum one is a release zip unpacked and
launched once, which downloads the vanilla assets and applies the overlay (the workflows
do exactly this, and stop the server as soon as it reports ready).

The script builds once, then `atlas stage` swaps the install's `VintagestoryAPI.dll`
into the test output before each run: the copy must match the install the run targets
(mixing a fork's `VintagestoryLib` with the vanilla API produces `MissingFieldException`
at boot), and staging replaces the full rebuild per install that guaranteed this before
Atlas 0.11.

### CI shards and the extended tier

Each CI lane runs one leg per flavor and shard (`vanilla 1of3`, `stratum 2of3`, ...), so a
leg stays around ten minutes as the suite grows. `scripts/shard_filter.py` lists the tests
of the built assembly (`dotnet test --no-build --list-tests`) and gives a class to shard
`crc32(class name) % count`: nothing is registered anywhere, a new test class lands in a
shard on its own and never moves another class. Every shard uploads its TRX as
`<flavor>-<index>of<count>.trx`; the compare and publish jobs run `scripts/merge_trx.py`
to merge them into one `<flavor>.trx` before `atlas diff`, the job summary and the
dashboard history, which therefore still see a single run with every scenario. A missing,
empty or overlapping shard fails the compare job by name, and a shard that ran fewer tests
than it was assigned fails its own leg.

To change the number of shards, edit the `shard` list of the matrix in both workflows
(`[1of4, 2of4, 3of4, 4of4]`). To reproduce one leg locally, after a Release build:

```bash
VINTAGE_STORY=/path/to/server dotnet test scenarios/StratumParity.Scenarios -c Release --no-build \
  --filter "$(python3 scripts/shard_filter.py --shard 2of3 --project scenarios/StratumParity.Scenarios)"
```

Tests that are too slow for every pull request can be tagged
`[Trait("Category", "Extended")]` (on a class or a method). Pull requests and pushes to
`main` skip them; the weekly Parity run, a manual Parity run with the `extended` input
checked, and the daily indev scout run them. `scripts/run-parity.sh` and a plain
`dotnet test` run everything. `python3 scripts/test_ci_scripts.py` self-checks the two
scripts without a server, and the compare job runs it.

## Layout

- `scenarios/StratumParity.Scenarios/`: the xUnit scenario project (consumes the
  `Pixnop.Atlas.XUnit` NuGet package)
- `scenarios/StratumParity.Scenarios/mods/randomtickprobe/`: test-only source mod
  (compiled by the game's ModLoader): probe blocks that turn to granite or andesite on a
  random tick, so random tick coverage becomes countable world state, and counter blocks
  that log neighbour updates and sampled random ticks for the scenarios to read
- `scenarios/StratumParity.Scenarios/mods/worldgenprobe/`: test-only source mod with a
  handler at each worldgen pass of the standard world, recording in chunk moddata what the
  handler saw (pass, the column's own pass, rock and air counts, how many neighbours were ready)
- `scenarios/StratumParity.Scenarios/mods/persistprobe/`: test-only source mod that appends a
  boot record to the savegame and to the chunk, map chunk and map region of the world centre
  column at every boot, plus one marker block per boot, for the restart scenarios
- `scenarios/StratumParity.Scenarios/mods/lightprobe/`: test-only assets mod (no code):
  full-cube lamps in seven colors with an exact `lightHsv` and no light absorption, for the
  lighting scenarios
- `scenarios/StratumParity.Scenarios/mods/entityprobe/`: test-only assets mod (no code): a copy
  of the straw dummy whose code path starts with `chicken-`, so Stratum's ambient fauna tick
  tier applies to it
- `scenarios/StratumParity.Scenarios/mods/aiprobe/`: test-only source mod with an AI task that
  never executes and counts how often the task manager asks it, so a scenario can read the
  cadence of the AI task start scan
- `scenarios/StratumParity.Scenarios/fixtures/`: seeded Stratum configs for the toggle
  scenarios (each includes `stratum.json` next to the performance file: before
  [Stratum#159](https://github.com/StratumServer/Stratum/pull/159), shipped in
  v1.22.5-stratum.1, the performance file was only read when the main config existed,
  and the pair keeps the fixtures valid on any release)
- `scripts/run-parity.sh`: differential runner (one build, two staged runs, one diff)
- `scripts/render_summary.py`: renders the CI job summary from `atlas diff --json-tests`
  output and gates on parity (symmetric divergence check)
- `scripts/shard_filter.py`, `scripts/merge_trx.py`, `scripts/test_ci_scripts.py`: split
  the suite across CI shards by class, merge the shard reports back into one per flavor,
  and the server-free self-check of both
- `scripts/history_append.py`: appends one run to the dashboard history on the `gh-pages`
  branch, `data/runs.json` for the pinned lane and `data/scout.json` for the indev lane
- `site/index.html`: the dashboard, one static page with no build step, copied to
  `gh-pages` by the publish job and reading the two JSON files with the cache bypassed
- `.github/workflows/parity.yml` (pull requests, pushes to main, weekly) and
  `indev-scout.yml` (daily, latest Stratum prerelease): the two lanes

## Versions

Pinned expectations: Vintage Story 1.22.7, Stratum v1.22.7-stratum.2, Atlas 0.16.1.
Stratum moves fast (releases every few days); when a scenario starts failing on a new
Stratum release, that is the suite doing its job.
