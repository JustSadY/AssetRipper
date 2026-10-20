#!/usr/bin/env python3
"""Build and execute only the source-owned Mono/IL2CPP method fixture."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
import zipfile

ANDROID_PACKAGE = "com.AssetRipper.OwnedMethodsFixture"
ANDROID_SERIAL_PORT = 5584
REPORT_MARKER = "ASSETRIPPER_METHOD_FIXTURE_REPORT "


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--pair-variant", action="store_true",
                        help="Insert one preserved method after PureMethods.Affine. The type and image counts stay the same, so "
                             "this variant's metadata is a near-identical wrong pair for the binary of an ordinary build.")
    parser.add_argument("--stripped", action="store_true",
                        help="Remove every [Preserve] attribute and link.xml entry from the copy and build with High managed "
                             "stripping. Only code reachable from the probe survives.")
    parser.add_argument("--target", choices=("linux64", "android-arm64"), default="linux64",
                        help="android-arm64 builds an IL2CPP-only ARM64 APK; Unity has no ARM64 Mono player.")
    parser.add_argument("--adb", type=Path, help="adb executable (android-arm64)")
    parser.add_argument("--emulator", type=Path, help="Android emulator executable (android-arm64)")
    parser.add_argument("--avd", help="AVD that can run arm64-v8a code, natively or through translation (android-arm64)")
    args = parser.parse_args()
    android = args.target == "android-arm64"
    if android and not (args.adb and args.emulator and args.avd):
        parser.error("android-arm64 needs --adb, --emulator and --avd to execute the player.")
    template = Path(__file__).resolve().parent / "Il2CppMethods"
    output = args.output.resolve()
    if output.is_relative_to(template) or (output.exists() and (not output.is_dir() or any(output.iterdir()))):
        parser.error("Output must be new or empty and outside the source project.")
    workspace = Path(tempfile.mkdtemp(prefix="assetripper-il2cpp-fixture-"))
    project = workspace / "Project"; shutil.copytree(template, project)
    corpus = project / "Assets" / "Scripts" / "Corpus.cs"
    if args.pair_variant:
        anchor = "public static int Affine(int x) { return unchecked(x * 7 - 11); }"
        text = corpus.read_text()
        if text.count(anchor) != 1:
            raise RuntimeError("The pair-variant anchor is missing from Corpus.cs.")
        extra = "\n\t\t[Preserve, MethodImpl(MethodImplOptions.NoInlining)] public static int PairVariant(int x) { return x + 1; }"
        corpus.write_text(text.replace(anchor, anchor + extra))
    if args.stripped:
        text = corpus.read_text()
        stripped = text.replace("[Preserve] ", "").replace("[Preserve, ", "[")
        if "Preserve" in stripped.replace("using UnityEngine.Scripting;", ""):
            raise RuntimeError("A [Preserve] attribute survived the stripped-variant rewrite.")
        corpus.write_text(stripped)
        (project / "Assets" / "link.xml").write_text("<linker>\n</linker>\n")
    print(f"Disposable project: {project}", flush=True)
    print(f"Logs: {workspace}", flush=True)
    env = dict(os.environ, ASSETRIPPER_UNITY_FIXTURE_OUTPUT=str(output),
               ASSETRIPPER_UNITY_FIXTURE_TARGET="Android" if android else "StandaloneLinux64",
               ASSETRIPPER_UNITY_FIXTURE_STRIPPING="High" if args.stripped else "Minimal")
    command = [str(args.unity.resolve()), "-batchmode", "-nographics", "-projectPath", str(project)]
    if android:
        command += ["-buildTarget", "Android"]
    command += ["-executeMethod", "Il2CppFixtureBuilder.Build", "-quit", "-logFile", str(workspace / "editor.log")]
    with (workspace / "stdout.log").open("w") as log:
        result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=3600)
    if result.returncode != 0 or not (output / "oracle.json").is_file():
        raise RuntimeError(f"Fixture build failed (exit {result.returncode}); inspect {workspace}")
    oracle = json.loads((output / "oracle.json").read_text())
    if android:
        apk = output / "IL2CPP" / "Methods.apk"
        # Only the native libraries and player data are needed; aapt2-shortened res/ names can differ only in case.
        with zipfile.ZipFile(apk) as archive:
            for name in archive.namelist():
                if name.startswith("/") or ".." in Path(name).parts:
                    raise RuntimeError(f"Unsafe APK entry: {name}")
            archive.extractall(output / "IL2CPP", [n for n in archive.namelist() if n.startswith(("lib/", "assets/")) and not n.endswith("/")])
        report = output / "il2cpp-runtime.json"
        report.write_text(json.dumps(run_android(args, apk, workspace), indent=2))
        backends = ("IL2CPP",)
    else:
        backends = ("Mono", "IL2CPP")
        for backend in backends:
            report = output / f"{backend.lower()}-runtime.json"
            env = dict(os.environ, ASSETRIPPER_METHOD_FIXTURE_REPORT=str(report))
            with (workspace / f"{backend.lower()}-stdout.log").open("w") as log:
                result = subprocess.run([str(output / backend / "Methods.x86_64"), "-batchmode", "-nographics", "-noaudio",
                                         "-logFile", str(workspace / f"{backend.lower()}-player.log")],
                                        env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180)
            if result.returncode != 0 or not report.is_file():
                raise RuntimeError(f"Owned {backend} player failed (exit {result.returncode}); inspect {workspace}")
    for backend in backends:
        actual = json.loads((output / f"{backend.lower()}-runtime.json").read_text())
        if actual["Backend"] != backend or actual["UnityVersion"] != oracle["UnityVersion"] or actual["Cases"] != oracle["Cases"]:
            raise RuntimeError(f"{backend} behavior differs from the managed oracle")
    files = [{"Path": str(p.relative_to(output)), "Sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
             for p in sorted(output.rglob("*")) if p.is_file()]
    manifest = {"SchemaVersion": 1, "UnityVersion": oracle["UnityVersion"], "BuildTarget": "Android" if android else "StandaloneLinux64", "Files": files}
    (output / "fixture-manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"Verified 20 behavior cases in {' and '.join(backends)}; {len(files)} hashed inputs: {output}", flush=True)
    variable = "ASSETRIPPER_UNITY_IL2CPP_ARM64_FIXTURE" if android else "ASSETRIPPER_UNITY_IL2CPP_STRIPPED_FIXTURE" if args.stripped else "ASSETRIPPER_UNITY_IL2CPP_FIXTURE"
    print(f"{variable}={output}")


def run_android(args, apk, workspace):
    """Runs the owned APK on a fresh emulator instance that this builder starts and always stops."""
    serial = f"emulator-{ANDROID_SERIAL_PORT}"
    adb = [str(args.adb.resolve()), "-s", serial]

    def call(*extra, timeout=120, check=True):
        result = subprocess.run(adb + list(extra), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=timeout)
        if check and result.returncode != 0:
            raise RuntimeError(f"adb {' '.join(extra)} failed: {result.stdout.strip()}")
        return result.stdout

    emulator_log = (workspace / "emulator.log").open("w")
    emulator = subprocess.Popen([str(args.emulator.resolve()), "-avd", args.avd, "-port", str(ANDROID_SERIAL_PORT), "-no-window",
                                 "-no-audio", "-no-boot-anim", "-no-snapshot", "-read-only", "-gpu", "swiftshader_indirect"],
                                stdout=emulator_log, stderr=subprocess.STDOUT)
    try:
        call("wait-for-device", timeout=300)
        deadline = time.monotonic() + 300
        while call("shell", "getprop", "sys.boot_completed", check=False).strip() != "1":
            if time.monotonic() > deadline or emulator.poll() is not None:
                raise RuntimeError(f"The emulator did not boot; inspect {workspace / 'emulator.log'}")
            time.sleep(2)
        abis = call("shell", "getprop", "ro.product.cpu.abilist").strip()
        if "arm64-v8a" not in abis.split(","):
            raise RuntimeError(f"The AVD cannot run arm64-v8a code (abilist: {abis}).")
        call("install", "-r", "-g", str(apk), timeout=600)
        # A focused, unlocked screen is required: Unity does not load the first scene while Android's one-time
        # full-screen confirmation holds focus, so mark that confirmation as already seen.
        call("shell", "settings", "put", "secure", "immersive_mode_confirmations", "confirmed")
        call("shell", "input", "keyevent", "KEYCODE_WAKEUP")
        call("shell", "wm", "dismiss-keyguard")
        call("shell", "svc", "power", "stayon", "true")
        call("logcat", "-c")
        call("shell", "am", "start", "-W", "-n", f"{ANDROID_PACKAGE}/com.unity3d.player.UnityPlayerGameActivity", timeout=180)
        chunks, count = {}, None
        deadline = time.monotonic() + 240
        while count is None or len(chunks) < count:
            if time.monotonic() > deadline:
                raise RuntimeError(f"No complete report in logcat; inspect {workspace / 'logcat.txt'}")
            time.sleep(2)
            text = call("logcat", "-d", "-v", "raw", "-s", "Unity")
            (workspace / "logcat.txt").write_text(text)
            for line in text.splitlines():
                match = re.search(re.escape(REPORT_MARKER) + r"(\d+)/(\d+) (.*)$", line)
                if match:
                    chunks[int(match.group(1))] = match.group(3)
                    count = int(match.group(2))
        report = json.loads("".join(chunks[i] for i in range(count)))
        call("uninstall", ANDROID_PACKAGE, check=False)
        return report
    finally:
        subprocess.run(adb + ["emu", "kill"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
        try:
            emulator.wait(timeout=120)
        except subprocess.TimeoutExpired:
            emulator.kill()
        emulator_log.close()


if __name__ == "__main__":
    main()
