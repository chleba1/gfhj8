# MMOCore tick-loop tests — status and plan

## Shipped now: `Tests/GameServer.Tests/`

`GameServer.Tests.csproj` + `SpatialGridTests.cs` — 13 tests, verified compiling and
passing against `SpatialGrid.cs` **unmodified** (bucket introspection is by reflection,
matching `AbilityCatalogTests`' use of reflection for weapon lines).

Covers the broad-phase grid that backs **both** the per-client AoI snapshot pass and the
area-effect damage tick — rebuilt once per tick under `_simLock`:

| Test | Invariant |
|---|---|
| `Query_NeverMissesAnEntityInsideTheRadius` | no false negatives, 5 seeds x 400 entities x 60 queries vs brute force |
| `NegativeCoordinates_BucketAndQueryCorrectly` | `MathF.Floor` vs truncation across the origin |
| `EntityOnCellBoundary_IsFoundFromBothSides` | cell seams |
| `Clear_RemovesAllEntitiesButGridStaysUsable` | per-tick rebuild can't leak last tick's positions |
| `Query_AppendsAndDoesNotClearCallerList` | the documented contract the scratch-list reuse depends on |
| `Query_ResultOrder_IsDeterministicForIdenticalInsertionOrder` | identical inputs → identical order, not just set |
| `NaNPosition_IsSilentlyBucketedOutOfReach_NotRejected` | see defect below |
| `InfinitePosition_CollapsesToTheSameUnreachableCell` | ±Inf and NaN are indistinguishable after bucketing |
| `BucketDictionary_NeverShrinks_AcrossRebuilds` | see finding below |

Install: drop the folder at `Tests\GameServer.Tests\`, add it to `MMOCore.sln`, and it joins
`dotnet test` in `verify.ps1` automatically.

---

## Findings

### 1. NaN/Inf position makes an entity invisible, silently — LATENT DEFECT

`CellOf` is `(int)MathF.Floor(v * _invCellSize)`. Measured in .NET 8:

```
NaN  -> -2147483648
+Inf -> -2147483648
-Inf -> -2147483648
```

Not 0 — `int.MinValue`. A NaN-coordinate entity is bucketed at
`(-2147483648, -2147483648)`, a cell **no realistic query range can span**. Verified: it is
not found from the origin, from ±500, from 1e6, or with radius 10,000.

Consequence: the entity stays in `_sessions`, stays alive, keeps being simulated — and is
invisible to every AoI snapshot and every area-effect query, for every player. Exactly the
"player vanished but the server thinks they're fine" desync, with no exception anywhere.

Not currently reachable as far as the dumped code shows — `CarryWeightSpeedFactor` guards
`capacity <= 0`, and the direction normalise is gated behind `if (len > 1f)` so there's no
divide-by-zero. This is a **guard against future change**, and the test locks the behaviour
so it can't be introduced silently.

Suggested guard at the integration site in `SimulateTick`, after the walkability check:

```csharp
if (!float.IsFinite(s.X) || !float.IsFinite(s.Z))
{
    Console.WriteLine($"[SIM] non-finite position for {s.Id} - resetting to spawn");
    // reset to spawn / last known good rather than letting it reach the grid
}
```

### 2. `_cells` never shrinks — unbounded growth

`Clear()` empties buckets but keeps them, by design, for GC. The *dictionary* also never
drops empty entries, so every cell ever visited is retained for the process lifetime. Test
`BucketDictionary_NeverShrinks_AcrossRebuilds` demonstrates 500 retained buckets behind one
live entity.

Bounded by world area / cell size (cell 32), so it plateaus rather than leaking forever —
low severity on a fixed-size map, worth knowing before any move to streamed or procedural
zones.

### 3. Determinism risk the grid can't fix — `ConcurrentDictionary` iteration order

`SimulateTick` iterates `_sessions.Values` (`ConcurrentDictionary<ulong, PlayerSession>`),
whose enumeration order is **not guaranteed stable** across runs or bucket layouts. For
independent per-player movement integration this is harmless. For anything order-sensitive
(resolving two same-tick actions against one target, insertion order into the grid feeding a
"first candidate" decision) it means identical inputs can produce different outcomes.

The grid is deterministic given a fixed insertion order — the test proves that — so the
ordering risk lives upstream, in the iteration, not in the grid.

To make the golden-hash test below meaningful, the tick needs a stable iteration order:
sort by session id once per tick into a reusable scratch list, or keep an explicit ordered
player list alongside the dictionary.

---

## Blocked: the golden tick-hash test

The original target — run N ticks with scripted inputs, hash sim state, compare to a
committed golden in the `AbilityDumpTests` / `MMOCORE_REGEN=1` style — is blocked on three
things, in order:

1. **No test project referenced GameServer.** `Shared.Tests` references only `Shared`.
   Fixed by `GameServer.Tests.csproj` above.
2. **`SimulateTick` is `private`.** Needs `InternalsVisibleTo("GameServer.Tests")` plus
   `internal`, or a deliberate test seam. Reflection would work but is brittle for the
   primary sim entry point.
3. **`Server` construction is unknown.** `TickLoopAsync` is clean — `SimulateTick(dt, nowMs)`
   already takes time as parameters and `dt` is a fixed timestep, so the sim step is nearly
   a pure function of `(dt, nowMs, state)`. That is better than expected. The open question
   is whether a `Server` can be constructed without binding TCP/UDP listeners or a
   Postgres/Redis store. `SendFramed` is fire-and-forget inside `Task.Run` with a bare
   `catch {}`, so a session with no live socket should no-op rather than throw — but that is
   inference from the dumped body, not something executed.

### Next dump (small)

```powershell
$root='C:\Users\Easy\Desktop\MMOCore'; Push-Location $root
$o=@()
$o+="### Server.cs 1-140"; $o+=(gc GameServer\Server.cs|select -First 140)
$o+="### Program.cs";      $o+=(gc GameServer\Program.cs)
$o+="### PlayerSession";   $o+=(gc GameServer\World\PlayerSession.cs)
$o+="### RebuildSpatialGrids + SendPerClientSnapshots"
$o+=(gc GameServer\Server.NetTick.cs|select -Skip 400)
Pop-Location; $t=($o|Out-String); $t|Set-Clipboard; "OK ($($t.Length) chars)"
```

With those four, the golden-hash test can be written against real signatures and verified
the same way these were.

---

## UPDATE — tick-loop tests shipped (`TickLoopTests.cs`)

All three blockers resolved from the second dump:

| Blocker | Resolution |
|---|---|
| No test project referencing GameServer | `GameServer.Tests.csproj` (shipped earlier) |
| `SimulateTick` is private | Reflection harness; every member resolved once, throws **by name** if missing |
| Headless `Server` construction unknown | `new Server()` takes no args and binds nothing — `_tcpListener`/`_udpClient` stay null until `RunAsync()` |

The socket question is settled by `SendPerClientSnapshots`: it does
`if (viewer.UdpEndPoint == null) continue;` **before** touching `_udpClient!`. Test sessions
leave `UdpEndPoint` null, so no send is ever reached. `PlayerSession` gets an unconnected
`new TcpClient()` — never dialled, nothing opened.

`TickLoopAsync`'s body is mirrored in `ServerTickHarness.Tick` in the same order
(`SimulateTick` → `RebuildSpatialGrids` → `TickAreaEffects` → `TickStatusEffects` →
`TickProjectiles`). `SendPerClientSnapshots` is deliberately excluded — no sim-state change
worth hashing, and it would dereference `_udpClient` if a test ever set `UdpEndPoint`.

### Tests

| Test | Locks |
|---|---|
| `Harness_ResolvesEveryTickMethod` | a rename can't leave the suite silently testing nothing |
| `Server_ConstructsHeadlessly_WithoutBindingListeners` | headless testability itself |
| `IdenticalInputs_ProduceIdenticalStateHash` | determinism — unseeded RNG, lost mutations |
| `Simulation_AdvancesOnTickCount_NotWallClock` | fixed timestep; same state with real delays injected |
| `ExaggeratedInputVector_DoesNotOutrunAUnitVector` | the anti-cheat speed clamp |
| `GoldenTickState_MatchesCommittedSnapshot` | behaviour lock, `MMOCORE_REGEN=1` convention |

`StateHash` sorts sessions by `PlayerId` before hashing, deliberately: `_sessions` is a
`ConcurrentDictionary` with no guaranteed enumeration order, and hashing in traversal order
would flake on something that isn't the sim's behaviour. The ordering risk is real but lives
upstream (finding 3), not in this test.

### Verification

Mechanics verified against a structural stub mirroring the real shapes (private tick methods,
`ConcurrentDictionary _sessions`, `PlayerSession(int, string, ulong, TcpClient)`), then
**mutation-tested** — passing tests prove nothing about detection:

| Mutant injected into the stub sim | Result |
|---|---|
| Unseeded `Random.Shared` in movement | 4 tests fail |
| Speed clamp removed | `ExaggeratedInputVector` fails |
| `DateTime.UtcNow` leaked into position | 3 tests fail |
| `TickProjectiles` renamed | 5 tests fail, named explicitly |

All four killed; clean stub back to 6/6.

### Not verified, and what to do about it

These were **not compiled against the real GameServer assembly** — that needs `GameWorld`,
`NetworkConfig`, `Inventory`, `TerrainHeight`, `ClassCatalog` and the rest of the tree, which
isn't available here. Compile risk is deliberately minimised: the file references only
`Server` and `PlayerSession` by type, everything else through reflection.

First run:

```powershell
dotnet test Tests\GameServer.Tests                       # expect: golden-missing failure only
$env:MMOCORE_REGEN=1; dotnet test --filter GoldenTickState
Remove-Item Env:\MMOCORE_REGEN
copy Tests\GameServer.Tests\bin\Debug\net8.0\golden-tick-state.txt Tests\GameServer.Tests\
dotnet test Tests\GameServer.Tests                       # expect: 6/6
```

Two things to watch on that first real run, both plausible and neither fatal:

1. **`IdenticalInputs_ProduceIdenticalStateHash` fails immediately.** That is a *finding*, not
   a broken test — it means real nondeterminism exists in the sim today. Most likely source:
   `SimulateMobs` / `RespawnNodes` / `MobRetargetMinMs..MaxMs` randomised wander. If so, the
   mob RNG needs seeding before the golden hash is meaningful.
2. **A reflected method is missing** — the failure names it; update the harness.

Report either back rather than deleting the test.
