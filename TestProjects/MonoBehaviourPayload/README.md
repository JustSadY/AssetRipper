# Editor-generated MonoBehaviour payload corpus

Source-owned Unity **6000.4.0f1** project. `PayloadFixtureBuilder.Build` saves a
scene/prefab, reopens the scene, reads fields through `SerializedObject`, and
builds `payload` / `external` bundles for **StandaloneLinux64**, with and without
type trees. No Unity binary or generated bundle is checked in.

Generate from the repository root with an installed, usable Editor:

```sh
python TestProjects/build_payload_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /path/to/new-or-empty/fixture

ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/6000.4.0f1/Editor/Data \
ASSETRIPPER_UNITY_PAYLOAD_FIXTURE=/path/to/fixture \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=UnityEditorPayload --logger trx
```

The runner copies this template to a new temporary project and preserves its
logs; it does not clear an existing output or run the Editor in this source tree.
Editor licensing and the Linux build module must already be available. The
manifest hashes all generated inputs. Unity GUIDs/local IDs may vary between
builds; tests compare known values and resolved target identity rather than
expecting identical bundle bytes. Source asset local IDs are not bundle PathIDs.

Without a configured fixture, four integration checks are explicitly skipped;
five loader/integrity checks still run. With a configured fixture, missing files,
changed hashes, wrong versions or missing pinned Editor references fail. These
checks do not import an AssetRipper-exported project back into the Editor.

See [the detailed contract](../../docs/UNITY_EDITOR_PAYLOAD.tr.md).

To additionally import an AssetRipper-exported project, set
`ASSETRIPPER_UNITY_EDITOR_EXECUTABLE` to the same Editor executable and run
`--filter TestCategory=UnityEditorRoundTrip` with the corpus/reference variables.
Its five cases include three deliberately broken exports. See
[the roundtrip contract](../../docs/UNITY_EDITOR_ROUNDTRIP.tr.md).

Use `--managed-references` when generating a separate graph corpus with
`GraphFixtureBuilder.Build`. Set
`ASSETRIPPER_UNITY_MANAGED_REFERENCE_FIXTURE` to that output and run
`--filter TestCategory=UnityManagedReference` with the Editor/reference variables.
The 14 cases cover shared/cyclic polymorphic SerializeReference data, nulls,
64-bit IDs, registry dependencies, deep-clone preservation, and actual exported
project reimport. Three clone regressions run without Unity; the other 11 cases
need the corpus and, for five of them, the Editor executable. See
[the graph contract](../../docs/UNITY_MANAGED_REFERENCES.tr.md).

Use `--scenes` to generate a separate scene-bundle corpus with five GameObjects,
two script components, inactive children, transforms and 13 object-reference
fields. Set `ASSETRIPPER_UNITY_SCENE_FIXTURE` and the same Editor/reference
variables, then run `--filter TestCategory=UnityScene`. Eight naming regressions
run without Unity; six bundle checks need the corpus and seven reimport checks
also need the Editor executable. The pinned scene corpus retains MonoBehaviour
type trees in both build modes, including `DisableWriteTypeTree`. A separately
reported controlled case withholds that metadata from the parser to test the
assembly fallback, without changing any input bytes. See
[the scene contract](../../docs/UNITY_SCENE_ROUNDTRIP.tr.md).

New corpus builds archive all runtime and builder C# sources in this template.
Use `--prefabs` to generate base, variant and nested prefabs with effective
overrides and cross-prefab object references. Set `ASSETRIPPER_UNITY_PREFAB_FIXTURE`
and the Editor/reference variables, then run `--filter TestCategory=UnityPrefab`.
Four report regressions always run; four reader and two primary-export cases
need the corpus; six reimport cases also need the Editor executable. The checker
reports effective data and authoring relations separately: flattened regular
prefabs with correct values are a **partial** reconstruction. See
[the prefab graph contract](../../docs/UNITY_PREFAB_GRAPH.tr.md).

The prefab graph corpus has 27 hashed files; new scene builds have 22 and new
scalar/graph builds have 23. Previously generated 18-file scalar, 20-file
scalar/graph and 21-file scene corpora remain valid.
