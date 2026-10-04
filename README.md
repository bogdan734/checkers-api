# Checkers REST API (Chinook-ready)

ASP.NET Core (.NET 8) Web API that takes a checkers position in PDN/FEN notation and returns the best move.
English/American checkers (8x8, 32 squares, forced captures, Black moves first).

## Honest status: Chinook is NOT included

Chinook / KingsRow and their 2–8 piece endgame databases are proprietary Windows binaries. They are not publicly
downloadable and cannot run on the Mac/Linux environment this was built in. So the repo contains:

| Piece | Status |
|---|---|
| `ChinookEngineAdapter` | Real integration code: long-lived worker **processes** driven over a line protocol. Untested against real Chinook (no binaries). Tested against the reference process below. |
| `StubEngineAdapter` | **Built-in replacement engine**: own iterative-deepening alpha-beta (TT, capture extensions, killer/history ordering) + a real **3-piece** tablebase generated at startup. Much weaker than Chinook. |
| `CheckersApi.EngineCli` | Console process speaking the same line protocol using the built-in engine. Proves the process pool end-to-end and is the template for a Chinook shim. |

Switch with `Engine:Type` (`chinook` or `stub`). Chinook's tablebases cover 2–8 pieces; the stub's cover ≤3
(`Engine:TablebasePieces`, 4 is possible but large). Positions with 4–8 pieces are searched, so `tablebaseHit` is
`false` for them with the stub.

## API

`POST /v1/move/suggest`
```json
{ "gameId": "g1", "state": { "notation": "PDN", "position": "B:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12" },
  "level": "strong", "limits": { "maxDepth": 16, "softTimeMs": 550, "hardTimeMs": 1200 } }
```
→ `{ engine, bestMove, pv[], scoreOrWDL, depth, nodes, positionKey, info: { tablebaseHit, timeMs, cached } }`

* `scoreOrWDL` is a number (centipawns, side to move) or `"WIN"|"DRAW"|"LOSS"` for tablebase hits.
* Moves use landing squares: `11-15`, `22x15`, `9x18x27`.
* `level` (optional): `weak` depth 7 / 100 ms, `medium` depth 11 / 250 ms, `strong` depth 16 / 550 ms. No randomness.
  Without a level, `Limits:DefaultSoftTimeMs` and depth 14 apply. `limits` override the level.
* Position formats: `B:W21,22,K30:B1-12`, `[FEN "..."]`, or `start`. Canonical form: `B:W<sorted>:B<sorted>`.

`POST /v1/move/validate` `{ position, move }` → `{ legal }`

`GET /healthz` → `{ ok, workers, configured, engine }` (503 until all workers are warm)

`POST /v1/position/moves` `{ position }` → all legal moves with resulting positions (used by the web board).

Errors: `{ code, message, requestId }` — **422** invalid PDN / level / limits / no legal moves, **504** `hardTimeMs`
exceeded, **500** engine returned an illegal move.

## Flow

parse + validate PDN → generate legal moves → cache lookup (key: canonical PDN + level + limits, LRU 20000 / 15 min) →
worker pool (round robin, async lock per worker, 2 workers warmed at startup) → engine probes tablebase first, else
searches until `maxDepth` or `softTimeMs` → root-legality check of `bestMove` (then remaining PV entries, else 500).
`hardTimeMs` is a `CancellationToken` created in the controller; on expiry the search is cancelled (a Chinook worker
process is killed and respawned lazily) and the API answers 504. One JSON log line per request on stdout:
`requestId, timeMs, status, level, depth, nodes, tablebaseHit, cached`.

## Run locally

```bash
dotnet run --project src/CheckersApi          # Development => stub engine, http://localhost:5000-ish, board at /
dotnet test                                   # needs the .NET 9 SDK (test host); app itself targets net8.0
```
Open `/` for the board: play against the engine, or "Engine vs engine"; the right panel shows depth, nodes, tablebase hit, raw JSON.

## Docker

```bash
docker build -t checkers-api .
docker run -p 8080:8080 checkers-api          # stub engine; honours $PORT
```
Works on any Docker host (Render / Railway / Fly.io / Azure Container Apps). Free tiers sleep when idle.

## Windows Server + IIS + real Chinook

1. Install the **.NET 8 Hosting Bundle**, then restart IIS. `dotnet publish src/CheckersApi -c Release -o C:\inetpub\checkers-api`.
2. Put the engine at `C:\engines\chinook\chinook.exe` and the databases at `D:\tb\chinook` (paths in `appsettings.json`).
3. **The engine executable must speak the line protocol** (Chinook has no CLI of its own; write a thin shim around the
   CheckerBoard-style engine DLL `getmove`, or around KingsRow, and print these lines):
   ```
   isready                                  -> readyok
   position <canonical pdn>                 (no output)
   go depth <d> time <ms> tb <0|1>          -> info depth <d> nodes <n> [time <ms>] score cp <x>|wdl <WIN|DRAW|LOSS> tb <0|1> pv <m1> <m2> ...
                                               bestmove <move>      (or: bestmove none)
   quit
   ```
   `CheckersApi.EngineCli/Program.cs` is a ~80-line reference implementation. The databases path is passed as
   `--db "<path>"`; extra args go in `Engine:Args`.
4. IIS site: no managed code, in-process hosting (`web.config` included). App pool: **Start Mode = AlwaysRunning, Idle
   Time-out = 0**, site **Preload Enabled = true** (install the *Application Initialization* feature) so workers are
   warm and not killed by idle recycling. Check `GET /healthz`.
5. No Windows Service is used. Worker processes are children of `w3wp.exe`.

## Layout

`src/Checkers.Core` rules, PDN, search, tablebase · `src/CheckersApi` web app, adapters, pool, board UI (`wwwroot`) ·
`src/CheckersApi.EngineCli` reference engine process · `tests` 50 tests: movegen perft vs known counts, tablebase,
all acceptance criteria (healthz, tablebase <50 ms, strong <600 ms, 422, 504), pool/lock/LRU/TTL, and the process
adapter against the CLI.
