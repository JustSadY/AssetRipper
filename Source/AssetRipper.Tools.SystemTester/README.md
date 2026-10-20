# AssetRipper.Tools.SystemTester

This is an automated system testing program.

## Running and reports

```bash
dotnet run --project Source/AssetRipper.Tools.SystemTester -- --tests /path/to/Corpus --output /path/to/Results --report /path/to/baseline.json
dotnet run --project Source/AssetRipper.Tools.SystemTester -- /path/to/Game --output /path/to/Results --report /path/to/game.json
dotnet run --project Source/AssetRipper.Tools.SystemTester -- /path/to/Game --output /path/to/Results --raw-output /path/to/Recovery --report /path/to/recovery.json
dotnet run --project Source/AssetRipper.Tools.SystemTester -- /path/to/Game --il2cpp-worker /absolute/AssetRipper.Tools.Il2CppWorker.dll --il2cpp-worker-dir /path/to/Jobs --il2cpp-worker-timeout 300 --il2cpp-worker-max-heap 4096 --output /path/to/Results --report /path/to/worker.json
```

With no input arguments, the runner uses the existing `../../Tests` corpus layout.
`--tests` cannot be combined with individual input paths. Cases run in ordinal
version/name order. The runner exits without waiting for keyboard input.

Exit codes:

- `0`: at least one case ran and every case completed without logged errors or failed export collections.
- `1`: a case failed, the corpus was missing, or no cases were found.
- `2`: invalid arguments or a runner/report-writing failure.

The JSON report records runtime/build identity, input and actual output paths,
Unity version, loaded and processed object counts, assembly count, elapsed time,
warnings, errors and per-case outcomes. Loaded counts can include injected engine
resources; they are not raw serialized object-table coverage. Timestamps, durations and unique output
paths vary between runs; compare case outcomes and counts rather than the entire
report byte for byte. A successful run does not validate Unity Editor import or
recovered method behavior.

`--raw-output DIR` enables opt-in raw serialized object preservation before asset
factory parsing. Each load creates a separate `Recovery_<GUID>` directory containing
SHA-256-addressed object bytes and an append-only `objects.jsonl` journal. Keep this
directory outside the input tree. Unknown, unreadable, null and partial results are
recorded; deferred MonoBehaviour reads update the same object record. Unresolved or
failed raw objects make the export unsuccessful (exit `1`). Raw storage/journal IO
errors propagate. Omitting the option leaves capture disabled.

The export's `raw-recovery.json` and the case report's nullable `RawRecovery` field
contain counters and paths to the persistent recovery job; payloads remain there.
If loading itself throws, use the job path logged before import. Offsets refer to
the decoded serialized-file stream. This does not archive whole bundles, external
resource streams, or files that fail before asset collection creation. IDs are
job-local; there is no automatic resume. Per-event disk flush adds IO overhead.
See [the recovery contract](../../docs/RAW_RECOVERY.tr.md) for details and limits.

Output directories are never cleared. A new or empty directory is reserved for
the job; a populated or already reserved directory receives a unique
`AssetRipper_...` subfolder. `.assetripper-export` reserves the directory so a
second export cannot mistake an otherwise empty job for an unused target.

Script exports write `script-export-report.json` inside `ExportedProject` (Unity
project export) or the export root (primary content). It lists per-assembly
decompiler diagnostics and placeholder script paths. ILSpy's recorded errors are
checked even when no exception escapes. Placeholder generation and failed
decompilation make the collection unsuccessful. Cancellation and filesystem
failures propagate rather than being treated as recoverable decompiler errors.

`LanguageSelection` records the requested and effective C# version and the target
Unity version. Automatic selection uses the same Unity-version policy in both
export modes. Primary-content also records `ProjectScaffoldFramework` (`net10.0`):
this is the SDK scaffold with implicit framework references disabled, not the
game's API profile. Neither field means that the exported code has been compiled.

Both export modes also write `collection-export-report.json` in the export root.
It records import failures and collection preparation/export failures, attempted
and successful export counts, and a `Completed` flag. Recoverable collection errors
allow independent collections to continue; they still make the export fail.
Half-loaded source collections are removed before dependency binding. Membership
is committed only after preparation succeeds. The SystemTester case report embeds
this report as `CollectionExport`. Its success applies to the collection loop;
the case's `Succeeded` includes the overall export and logged errors.

Cancellation, out-of-memory, access and filesystem errors, including wrapped ones,
escape the collection isolation boundaries. Source reads may isolate unexpected
end-of-input as malformed data. Partial files and cross-collection references are
not rolled back. Global processors and post-exporters can still abort the case.
See [the isolation contract](../../docs/COLLECTION_ISOLATION.tr.md).

`assembly-resolution-report.json` records loaded managed assembly candidates,
source hashes/paths, CLR identities, MVIDs, DLL output paths and shared-context
limitations. The case report embeds it as `AssemblyResolution`. Same-name DLLs
are retained with separate output names. ILSpy selects by name/version/culture/
public-key-token; ambiguous references are rejected and unique compatible version
fallbacks are recorded in the script report's `ResolutionDiagnostics`.

AsmResolver's shared context cannot safely represent multiple simple-name candidates.
Such jobs skip marked assembly processors and assembly-based MonoBehaviour field
recovery, keep all DLL candidates, and exit `1`. Unity-project export uses reported
placeholders for ambiguous MonoScript bindings; primary-content can decompile each
candidate separately. Byte-identical copies are also conservatively treated as
collisions. See [the identity contract](../../docs/ASSEMBLY_IDENTITY.tr.md).

The source-owned managed fixture is in
`Source/AssetRipper.Tests/Fixtures/DecompilationSample.cs`; its tests also inject
decompiler failures and exercise missing assemblies/types. It complements the
external Unity corpus and does not replace Editor-generated fixtures.

The separate `ManagedRoundTrip` NUnit category compiles the embedded
`Fixtures/ManagedRoundTrip.cs` corpus with pinned .NET 10 reference images, decompiles
and recompiles it under C# 7.3/9 and Debug/Release, then checks public contracts and
20 known behavior cases. Reports, generated sources and DLLs are attached to test
evidence in CI. This is a controlled .NET fixture profile, not Unity API or Editor
validation. The SystemTester does not execute recovered game code.
See [the managed roundtrip contract](../../docs/MANAGED_ROUNDTRIP.tr.md).

The `UnityManagedReference` test category adds a source-owned Unity 6000.4.0f1
SerializeReference graph corpus: polymorphic nodes, shared identities, a two-node
cycle, nulls, 64-bit RIDs, and Unity object targets inside registry data. It also
covers the dynamic registry-data deep-clone fix without requiring Unity. The
optional Editor checks reimport actual exported C# and prefab data and reject
three independently damaged graphs. See
[the managed-reference contract](../../docs/UNITY_MANAGED_REFERENCES.tr.md) for
generation, environment variables, evidence, and the remaining format/version
limits. SystemTester does not run arbitrary recovered game code.

The `ManagedReferenceValidation` category verifies format-2 RID uniqueness,
host-local reference targets, and required type names. It adds 20 always-on
controlled cases and 12 optional cases that mutate copied objects from the real
Editor corpus. Invalid graphs retain raw script bytes, report incomplete
dependencies, and fail both export modes while independent assets continue.
See [the registry validation contract](../../docs/MANAGED_REFERENCE_VALIDATION.tr.md).

`ScriptLanguageProfile` tests exercise both export paths and their generated
language settings. The optional `UnityReferenceProfile` tests recompile a trusted
MonoBehaviour fixture against 20 hash-pinned reference images from Unity
6000.4.0f1 / .NET Standard 2.1. Set `ASSETRIPPER_UNITY_EDITOR_DATA` to the pinned
installation's `Editor/Data` directory. Without it, the five installation-dependent
checks are explicitly skipped; missing or mismatched files with a configured path
fail the check. The fixture does not execute native Unity calls or validate Editor
import. See [the language/profile contract](../../docs/SCRIPT_LANGUAGE_PROFILE.tr.md).

When ILSpy replaces a shared C# file entirely with its error comment, the script
exporter retries each original top-level type independently, keeping nested types
with their parent. Successfully emitted types use the original path or distinct
token-based filenames. The original error file is retained as a hash-reported
`.txt` sidecar under `_DecompilationEvidence`. Existing partial source is not
replaced, and resource-backed companion maps are conservatively skipped.

Each assembly's `TypeRecoveries` records attempts, diagnostics and relative output
paths; `RecoveredTypeCount` counts types emitted without decompiler errors. The
original errors remain, so partial recovery still fails the export. This count
does not imply compilation or behavior validation. `ScriptTypeRecovery` tests
verify both exporters using deliberately damaged source-owned metadata, C#
recompilation and five known behavior observations. Their evidence is uploaded
in CI. See [the type recovery contract](../../docs/TYPE_RECOVERY.tr.md).

Failed MonoBehaviour field reads now retain an `UnreadableStructure` with the
original byte segment and incomplete dependency coverage. Before collection
preparation, both exporters resolve deferred reads and record failures in
`collection-export-report.json` under `MonoBehaviourFailures`. These failures
make the export unsuccessful even when opt-in raw capture is disabled. The
failed segment is saved to `MonoBehaviourRecovery/<sha256>.bin` under the export
root; `PayloadScope` distinguishes script fields from an object whose header
could not be read. This is not a whole-input archive. Existing conflicting
recovery output is not overwritten.

Script PPtr values now participate directly in dependency traversal, including
pointer arrays and nested fields. The `MonoBehaviourPayload` tests use independent
hand-written bytes and TypeTree/assembly schemas to check field values, pointer
paths and selected targets. They cover controlled Unity-name shims, not an
Editor-generated scene or runtime. See [the payload contract](../../docs/MONOBEHAVIOUR_PAYLOAD.tr.md).

`TestProjects/build_payload_fixture.py` additionally generates an actual Unity
6000.4.0f1 scene/prefab and Linux64 AssetBundles with and without type trees in a
disposable project. Set `ASSETRIPPER_UNITY_PAYLOAD_FIXTURE` to that corpus and
`ASSETRIPPER_UNITY_EDITOR_DATA` to the pinned installation to enable four
`UnityEditorPayload` integration checks. They compare fields and resolved PPtr
targets with the Editor's `SerializedObject` oracle; omitting the external bundle
must preserve the original pointer IDs. Five loader/integrity checks run without
the corpus. Missing configuration skips the four integration checks; invalid
configured inputs fail. This does not import an AssetRipper-exported project into
the Editor. See [the Editor payload contract](../../docs/UNITY_EDITOR_PAYLOAD.tr.md).

The separate optional `UnityEditorRoundTrip` category runs actual processing and
Unity-project export, then opens the generated project in Unity 6000.4.0f1.
Set `ASSETRIPPER_UNITY_EDITOR_EXECUTABLE` as well as the two corpus/reference
variables above. Both schema paths must recompile with no missing scripts and
preserve the known serialized fields and reference targets. Three negative cases
alter a script GUID, a value and an external reference GUID; each must produce
its specific Editor validation failure. Without the executable option all five
checks are explicitly skipped. Curated source/input snapshots and JSON reports
are retained without publishing Editor caches or full logs. This is a trusted
fixture test, not a general game-code execution path in SystemTester.
See [the Editor roundtrip contract](../../docs/UNITY_EDITOR_ROUNDTRIP.tr.md).

The `UnityScene` category adds a dedicated scene-bundle corpus generated with
`TestProjects/build_payload_fixture.py --scenes`. Configure
`ASSETRIPPER_UNITY_SCENE_FIXTURE`, the pinned Editor data and, for actual reimport,
the Editor executable. Six reader checks compare five objects, two components
and 13 pointer fields; seven Editor cases open the exported `.unity` scene and
reject broken script GUIDs, values, external references and hierarchy. Eight
scene-name regressions always run. This particular scene corpus retains type
trees even in the `DisableWriteTypeTree` build; assembly fallback is exercised
by explicitly withholding MonoBehaviour type metadata in a separate controlled
case. Reports distinguish build flags from the schema actually used.

Serialized collections preserve `OriginalName` independently of normalized
dependency identifiers. When scene path metadata is unavailable, scene entries
named `BuildPlayer-<SceneName>` recover that scene name with its original case.
Explicit scene paths still take precedence. This does not reconstruct missing
Assets directory paths or infer names from CAB hashes. See
[the scene roundtrip contract](../../docs/UNITY_SCENE_ROUNDTRIP.tr.md).

`collection-export-report.json` now includes `PrefabReconstructions`, one row per
processed prefab hierarchy. `PrefabOrigin` distinguishes a
`SourceSerializedObject` from a `GeneratedFromHierarchy` prefab. Generation is
recorded at creation, not inferred from a processed collection; otherwise
unproven origins are `Unknown`. Root/prefab identities are job-local. The independent
`AuthoringRelationsValidation` field remains `NotRun`: neither an existing object
nor a successfully generated prefab proves recovery of original variants, nested
source links or property override lists. These rows describe the input hierarchy,
including ones whose export later fails; `Succeeded` retains its collection-loop
meaning. Both export modes and the embedded SystemTester report carry the rows.

The optional `UnityPrefab` category uses a source-owned base/variant/nested prefab
corpus generated with `--prefabs` and configured through
`ASSETRIPPER_UNITY_PREFAB_FIXTURE`. It verifies effective overrides, nine objects,
four components and 25 pointer fields, plus actual Editor reimport and four
damaged exports. Source authoring links are compared independently: the pinned
runtime bundle is flattened, so a healthy effective-data result is explicitly
`FullRoundTripStatus=Partial`. See
[the prefab graph contract](../../docs/UNITY_PREFAB_GRAPH.tr.md).

Script reports also carry optional `Il2CppRecovery` provider/body provenance.
The exact default Cpp2IL output is reported as known stubs, even when Level3 was
requested. Custom providers remain `Unknown`; `Validation=NotRun` is independent
of successful C# emission. Level3 by-reference stub repair now follows the actual
selected provider rather than assuming all Level3 bodies were recovered.

The optional `Il2CppNative` trial uses the separate source-owned
`TestProjects/Il2CppMethods` project and `ASSETRIPPER_UNITY_IL2CPP_FIXTURE`.
Twenty-eight `Il2CppCapability` provider/export/integrity tests run without Unity.
The native trial compares 20 observations from matching Mono/IL2CPP players,
retains original IL candidates, and separately measures checked max-stack repair
in bounded managed replay processes. The pinned provider still produces wrong
values in several methods; it is not enabled by default. This adds no general
game-code execution option to SystemTester. See
[the capability decision and limits](../../docs/IL2CPP_CAPABILITY.tr.md).

`--il2cpp-worker FILE` opts IL2CPP inputs into the separate
`AssetRipper.Tools.Il2CppWorker` process. Without it, import is unchanged and
in-process. `--il2cpp-worker-dir` and `--il2cpp-worker-timeout` (1–86400 seconds,
default 300) require the worker option. Each analysis gets a fresh process and a
job directory with hashed input copies, `run.json` and bounded logs; job
directories are kept as evidence and are not cleaned automatically. A `.dll`
worker is started through `dotnet` from `PATH`. IL2CPP cases imported through the
worker carry an `Il2CppWorker` record, including failed ones; Mono and unknown
backends are unaffected by the option. A configured worker that fails,
times out or returns an invalid response fails the case: it does not fall back
to the `Unknown` scripting backend. The worker only carries the default
metadata/stub output. `Il2CppRecovery.Isolation` records process and protocol
checks, not method behavior. This is crash isolation, not a security sandbox. See
[the worker contract](../../docs/IL2CPP_WORKER.tr.md).

`--il2cpp-worker-max-heap MIB` (64–1048576) passes `DOTNET_GCHeapHardLimit` to a
.NET worker. It caps the managed heap only; native memory is not limited.
`--il2cpp-worker-discard-inputs` deletes the hashed input copies of completed jobs;
failed jobs always keep them. Both require `--il2cpp-worker`. Before analysis the
worker rejects a structurally mismatched binary/metadata pair (`RejectedPair`):
tested with a one-method variant build whose metadata LibCpp2IL would otherwise map
to shifted method pointers. Reports carry `PairValidation` and `RecoveryCapability`.

## Selective export

Repeat `--select-class-id INT`, `--select-name NAME`, `--select-path-id INT64`, or
`--select-collection NAME` to select assets. Names and source collection names are
exact, case-sensitive matches. Values within one filter are alternatives; different
filters must all match. A PathID is local to a collection; combine it with
`--select-collection` when disambiguation is needed.

```sh
dotnet AssetRipper.Tools.SystemTester.dll /path/to/input \
  --select-class-id 114 --select-path-id 12345 --select-collection sharedassets0.assets \
  --output /path/to/output --report /path/to/results.json
```

Selection expands to whole export collections and their forward object references,
including prefab/scene members and the shared script export. The report's `Selection`
lists requested and included objects, owning collections, and unresolved references.
No matches, a non-exportable root, unknown script fields, or missing dependencies
cannot produce a successful selection. `KnownObjectReferencesOnly` is deliberately
limited to references understood by the current readers.

Import, processing, and collection preparation still cover the entire input.
StreamingAssets bulk copying is skipped, while the path-ID map contains only selected
objects. Project scaffolding, original assembly archives and recovery evidence retain
their existing global scope. Use a fresh output directory; old output is not cleaned.
The CLI returns 0 on success, 1 for an incomplete export, and 2 for invalid arguments.
See [the P14 scope and evidence](../../docs/SELECTIVE_EXPORT.tr.md).

## File Structure

```
Bins
 |--Tests
 |   |--2019.4.3 //just an example
 |   |   |--Test1Name
 |   |   |   |--Input
 |   |   |   |   |-- any files or folders to be processed
 |   |   |   |--Output
 |   |   |   |   |-- the test output
 |   |   |--Test2Name
 |   |   |   |--Input
 |   |   |   |   |-- any files or folders to be processed
 |   |   |   |--Output
 |   |   |   |   |-- the test output
 |   |--2020.2.0 //just an example
 |   |   |--Test3Name
 |   |   |   |--Input
 |   |   |   |   |-- any files or folders to be processed
 |   |   |   |--Output
 |   |   |   |   |-- the test output
 |   |   |--Test4Name
 |   |   |   |--Input
 |   |   |   |   |-- any files or folders to be processed
 |   |   |   |--Output
 |   |   |   |   |-- the test output
 |   |   |--Test5Name
 |   |   |   |--Input
 |   |   |   |   |-- any files or folders to be processed
 |   |   |   |--Output
 |   |   |   |   |-- the test output
 ```
