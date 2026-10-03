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

Twenty scenarios in ten classes: parity scenarios, probes, and one informational perf
measurement, green on both flavors against the pinned pair, re-run by CI on every PR and
weekly against the pinned Stratum release. A separate daily **indev scout**
workflow resolves the latest Stratum pre-release and runs the same suite against it:
non-blocking early warning for the next stable. Scout runs are recorded in their own
history and shown in the dashboard's Builds section, never mixed into the stable trends.

| Surface | Scenarios | What is pinned down |
|---|---|---|
| Boot, block placement, join | `SmokeParityScenarios` | Fundamentals identical on both flavors; a joined player survives Stratum's packet limiter; the CI leg's expected flavor is asserted against the loaded server (the flavor guard, `PARITY_EXPECTED_FLAVOR`) |
| Entity tick throttling | `EntityTickingProbes`, `EntityTickingDisabledScenarios` | Exact counts against the engine entity-simulation tick counter: near entities tick once per sim tick on both flavors, entities in Stratum's very-far band (beyond 96 blocks; the probe puts one 200 blocks out) exactly 1 in 10 on Stratum and once per sim tick on vanilla; the mid (1 in 2) and far (1 in 5) bands are not probed; `EntityTicking.Enabled: false` restores exact parity; the probes are run with both a straw dummy and an AI creature (a raccoon) that stays put |
| Command surface | `CommandParityScenarios` | Vanilla commands behave identically (incl. unknown-command error codes); `/stratum` and `/sethome` exist on Stratum only |
| Block tick listeners | `BlockTickListenerProbes`, `BlockTickListenerDisabledScenarios` | Far listeners are skipped entirely on Stratum (128-block radius), force-loaded columns stay exempt, toggle restores parity |
| Chunk persistence | `ChunkPersistenceScenarios` | Blocks + chunk moddata survive save/unload/reload cycles identically through Stratum's incremental autosave (its opt-in pooled chunk reads are off by default and not exercised here); sunlight and block light around a torch are re-read after the reload |
| Random ticks | `RandomTickProbes`, `RandomTickDisabledScenarios` | Vanilla gates random ticks to 5 chunks around Playing clients; Stratum clamps to 3; probed at chunk distance 4 via a staged source mod whose block converts on every random tick |
| Tick cost (perf) | `TickCostProbes` | Server work-ms per tick under a fixed active-entity load, emitted to the dashboard as a trend; informational, not a pass/fail gate (see below) |

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
package the project references: `dotnet tool install -g Pixnop.Atlas.Cli --version 0.16.0`):

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

## Layout

- `scenarios/StratumParity.Scenarios/`: the xUnit scenario project (consumes the
  `Pixnop.Atlas.XUnit` NuGet package)
- `scenarios/StratumParity.Scenarios/mods/randomtickprobe/`: test-only source mod
  (compiled by the game's ModLoader): probe blocks that turn to granite or andesite on a
  random tick, so random tick coverage becomes countable world state, and counter blocks
  that log neighbour updates and sampled random ticks for the scenarios to read
- `scenarios/StratumParity.Scenarios/fixtures/`: seeded Stratum configs for the toggle
  scenarios (each includes `stratum.json` next to the performance file: before
  [Stratum#159](https://github.com/StratumServer/Stratum/pull/159), shipped in
  v1.22.5-stratum.1, the performance file was only read when the main config existed,
  and the pair keeps the fixtures valid on any release)
- `scripts/run-parity.sh`: differential runner (one build, two staged runs, one diff)
- `scripts/render_summary.py`: renders the CI job summary from `atlas diff --json-tests`
  output and gates on parity (symmetric divergence check)
- `scripts/history_append.py`: appends one run to the dashboard history on the `gh-pages`
  branch, `data/runs.json` for the pinned lane and `data/scout.json` for the indev lane
- `site/index.html`: the dashboard, one static page with no build step, copied to
  `gh-pages` by the publish job and reading the two JSON files with the cache bypassed
- `.github/workflows/parity.yml` (pull requests, pushes to main, weekly) and
  `indev-scout.yml` (daily, latest Stratum prerelease): the two lanes

## Versions

Pinned expectations: Vintage Story 1.22.7, Stratum v1.22.7-stratum.2, Atlas 0.16.0.
Stratum moves fast (releases every few days); when a scenario starts failing on a new
Stratum release, that is the suite doing its job.
