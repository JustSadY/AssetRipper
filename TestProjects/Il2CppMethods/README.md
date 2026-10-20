# Source-owned IL2CPP method capability fixture

Unity **6000.4.0f1 / StandaloneLinux64 / x86_64**. The builder creates an empty
scene and two players from the same preserved source: Mono and IL2CPP, minimal
managed stripping, release native compilation. Both players execute 20 known
observations; their reports must exactly match the independently checked Editor
oracle before the hash manifest is written. Native metadata version is 39.

From the repository root, with both Linux build backends installed:

```sh
python3 TestProjects/build_il2cpp_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /absolute/new-or-empty/il2cpp-fixture

ASSETRIPPER_UNITY_IL2CPP_FIXTURE=/absolute/il2cpp-fixture \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter 'TestCategory=Il2CppCapability|TestCategory=Il2CppNative' --logger trx
```

The builder copies this template to a disposable project. It retains failed
builds and logs and never deletes an existing output. It executes only the two
players it just built. Tests never execute a configured native player: they
verify its recorded observations and all input hashes, analyze the binary, then
run the source-owned managed assembly and generated CIL in separate bounded
processes. Configure only a fixture you built from this source. The capability
trial still analyzes CIL in the test process. The separate `Il2CppWorkerNative`
category feeds the same fixture to the opt-in worker process instead; see
[the worker contract](../../docs/IL2CPP_WORKER.tr.md).

```sh
python3 TestProjects/build_il2cpp_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /absolute/new-or-empty/pair-variant --pair-variant

ASSETRIPPER_UNITY_IL2CPP_FIXTURE=/absolute/il2cpp-fixture \
ASSETRIPPER_UNITY_IL2CPP_PAIR_VARIANT_FIXTURE=/absolute/pair-variant \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter 'TestCategory=Il2CppWorker|TestCategory=Il2CppWorkerNative' --logger trx
```

Two more variants share the same 20 checks:

- `--stripped` removes every `[Preserve]` attribute and `link.xml` entry from the copy
  and builds with High managed stripping (`ASSETRIPPER_UNITY_IL2CPP_STRIPPED_FIXTURE`).
- `--target android-arm64 --adb ADB --emulator EMULATOR --avd AVD` builds an IL2CPP-only
  ARM64 APK (Unity has no ARM64 Mono player). It runs the APK on a fresh emulator
  instance the builder starts and stops; the AVD must run `arm64-v8a`, natively or
  through translation. The player reports through logcat. Only `lib/` and `assets/`
  are extracted (`ASSETRIPPER_UNITY_IL2CPP_ARM64_FIXTURE`).

`--pair-variant` inserts one preserved method after `PureMethods.Affine` in the
disposable copy. Types and images stay the same, and both players still pass the
same 20 checks. Its metadata paired with an ordinary binary is the near-identical
wrong pair that the worker must reject. Without the variant variable, the pair
tests are skipped.

Twenty-eight provider/report/integrity checks run without Unity; the one native
trial is explicitly skipped without the fixture. Invalid configured inputs fail.
No Editor executable or target reference profile is substituted from the host.
No binary, generated Unity project or Editor log is checked in.

The trial retains untouched provider output and separately measures a checked
`MaxStack` reconstruction. Passing a few observations does not establish full
native method recovery. See [the capability contract](../../docs/IL2CPP_CAPABILITY.tr.md).
