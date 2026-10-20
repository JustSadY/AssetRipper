# Three-assembly Unity fixture

This source-owned Unity **6000.4.0f1 / Linux64** fixture tests:

- `AssetRipper.GraphModel`: serializable data, an inherited component field and an interface.
- `AssetRipper.GraphComponents`: a component using those types, a private serialized field and object pointers.
- `Assembly-CSharp`: a component pointing to the custom component and sharing model types.

The builder saves/reopens a scene and prefab and checks fixed field values, Unity object
identity, null slots, and arithmetic/interface results before writing the oracle. It builds
both TypeTree modes, copies the three actual Editor assemblies and hashes all 36 corpus inputs.
The source prefab is evidence, never an AssetRipper import input.

```sh
python3 TestProjects/build_assembly_graph_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /new/empty/corpus/directory
```

Set `ASSETRIPPER_UNITY_ASSEMBLY_GRAPH_FIXTURE` to that directory, and
`ASSETRIPPER_UNITY_EDITOR_DATA` to the matching `Editor/Data` directory. Set
`ASSETRIPPER_UNITY_EDITOR_EXECUTABLE` to enable reimport of exported projects.

```sh
dotnet test --no-restore -p:AllowMissingPrunePackageData=true \
  --filter TestCategory=UnityAssemblyGraph --logger trx
```

Tests cover fully decompiled sources and Hybrid DLL/source outputs. The Editor checker
uses reflection and `SerializedObject`; the original `Assembly-CSharp.dll` is absent from
the exported project's Assets. Hybrid deliberately uses saved custom DLLs. Compiler
failure controls have their own diagnostic report and never claim that the semantic
checker ran. Full Editor logs stay outside CI artifacts.

This fixture does not validate IL2CPP recovery, general game execution, Play Mode,
package version reconstruction or original asmdef platform/define constraints.
