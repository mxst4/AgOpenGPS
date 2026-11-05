# Farm telemetry sync

Farm sync sends a latest-state snapshot directly to the custom Farm AgShare host. It is disabled by default and independent of ordinary AgShare field upload/download. Enable it from **AgShare settings → Farm sync**, after saving the custom HTTPS server and shared API key in AgShare settings. Public `agshare.agopengps.com` is deliberately rejected.

Copy this tablet's telemetry ID into Farm's AgOpenGPS source settings and assign its terminal and tractor. Several tablets may use one terminal API key, but every tablet needs a different telemetry ID. After copying environment settings to another tablet, use **New ID for this tablet**, register that new ID in Farm, then enable synchronization. The ID persists across restarts, key changes and an environment reset; an environment reset disables synchronization.

The default interval is 300 seconds, configurable from 60 to 3600 seconds. **Sync now** saves these settings and uses the same worker. The settings screen displays the last successful upload and current errors. Upload errors do not open modal dialogs.

Each snapshot contains the current valid GPS observation, selected tool name/effective width, active field and all complete strips saved in that field's `Sections.txt`. Coverage can include older work and does not attribute treatment area to the current tool or day. A missing GPS fix becomes null after ten seconds without a valid observation. Simulator snapshots update normal Farm data with `simulator: true`. Easy Drive sends position and tool name, with null width and no field or coverage.

The sender never flushes active coverage or reads live OpenGL collections. A complete saved empty file clears that source's layer; an unreadable or incomplete first strip retains Farm's last good layer. Complete strips followed by a partial block are marked `trailingBlockDeferred`. Boundary files must contain complete valid rings and holes; an invalid boundary is omitted entirely. Coordinates use an independent `LocalPlane` with the copied origin and drift compensation.

Requests use `POST /api/telemetry/v1/snapshots`, `Authorization: ApiKey`, and no `Idempotency-Key`. Each immutable snapshot has a UUID and UTC capture time. There is one in-flight operation, one short retry for transient errors, no durable queue and no offline history replay. Keep tablet clocks synchronized. Initial limits are 10 MiB per JSON body, 200,000 coverage vertices, 50,000 strips and 20,000 boundary vertices; excess coverage sends `TOO_LARGE`, and oversized boundaries are omitted.

The UI timer only copies small state. File reads, conversion, serialization and HTTP run in the background. Field selection, origin, drift and AgShare identity changes invalidate prepared snapshots before sending/retrying. Atomic identity handling is shared with automatic uploads, batch ID replacement and downloads. Confirmed application shutdown cancels telemetry immediately and never waits for its network request. Ordinary AgShare's existing shutdown upload remains unchanged.

Fields become eligible for telemetry only after their exact active field instance has completed loading or creation, including KML/ISOXML import. A failed or canceled creation cannot export an existing directory with the same name: until a successful open, snapshots report `LOADING` without reading that directory. Loading or resetting environment settings cancels the old request and retry before subsequent snapshots use the new host, key, ID and enabled state.

## Validation

The Farm telemetry tests are part of `SourceCode/AgOpenGPS.Core.Tests` for the normal Windows solution build. A portable harness compiles the same production sources, coordinate conversion and test files:

```sh
dotnet test tests/FarmTelemetry.Portable.Tests/FarmTelemetry.Portable.Tests.csproj
```

The harness requires the .NET 8 runtime. The repository's normal `global.json` still selects .NET 9 for the application; to use an isolated .NET 8 SDK, run it from outside this checkout with an absolute project path. Setting `AOG_TELEMETRY_PAYLOAD_DIR` exports serialized production-model payloads for backend/schema interoperability checks. Contract examples and complete/truncated boundary fixtures are copied into the tests so they run independently of the planning workspace.

The tests cover shared-key wire shapes, exact retries, cancellation, host restrictions, fix freshness/provenance, saved-file completeness, topology/holes, origin/drift, identity races and request-size fallback. Windows tablet checks still need to exercise the WinForms controls, simulator/live fix transition, field switching while a slow server is connected, canceled exit, and real complete-field files at the configured limits.
