#!/usr/bin/env python3
"""Generate the source-owned Unity payload corpus in a new disposable project."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import os


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path, help="Unity 6000.4.0f1 Editor executable")
    parser.add_argument("--output", required=True, type=Path, help="New or empty fixture output directory")
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--managed-references", action="store_true", help="Generate the shared/cyclic SerializeReference graph corpus")
    modes.add_argument("--scenes", action="store_true", help="Generate the hierarchy and cross-root scene-reference corpus")
    modes.add_argument("--prefabs", action="store_true", help="Generate the base, variant and nested prefab corpus")
    args = parser.parse_args()
    template = Path(__file__).resolve().parent / "MonoBehaviourPayload"
    output = args.output.resolve()
    if output.is_relative_to(template) or (output.exists() and (not output.is_dir() or any(output.iterdir()))):
        parser.error("Output must be new or empty and outside the source project.")
    workspace = Path(tempfile.mkdtemp(prefix="assetripper-editor-payload-"))
    project = workspace / "Project"
    shutil.copytree(template, project)
    log = workspace / "editor.log"
    env = dict(os.environ, ASSETRIPPER_UNITY_FIXTURE_OUTPUT=str(output))
    command = [str(args.unity.resolve()), "-batchmode", "-nographics", "-projectPath", str(project),
               "-executeMethod", "PrefabGraphFixtureBuilder.Build" if args.prefabs else "SceneFixtureBuilder.Build" if args.scenes else "GraphFixtureBuilder.Build" if args.managed_references else "PayloadFixtureBuilder.Build", "-quit", "-logFile", str(log)]
    print(f"Disposable project: {project}", flush=True)
    print(f"Editor log: {log}", flush=True)
    with (workspace / "stdout.log").open("w") as stdout:
        result = subprocess.run(command, env=env, stdout=stdout, stderr=subprocess.STDOUT, timeout=600)
    manifest_path = output / "fixture-manifest.json"
    if result.returncode != 0 or not manifest_path.is_file():
        raise RuntimeError(f"Unity fixture generation failed (exit {result.returncode}); inspect {log}")
    manifest = json.loads(manifest_path.read_text())
    if manifest["UnityVersion"] != "6000.4.0f1":
        raise RuntimeError("Unexpected Editor version in fixture manifest")
    for entry in manifest["Files"]:
        path = Path(entry["Path"])
        if path.is_absolute() or ".." in path.parts:
            raise RuntimeError("Unsafe fixture manifest path")
        if hashlib.sha256((output / path).read_bytes()).hexdigest() != entry["Sha256"]:
            raise RuntimeError(f"Fixture hash mismatch: {path}")
    print(f"Verified {len(manifest['Files'])} fixture files: {output}")
    variable = "ASSETRIPPER_UNITY_PREFAB_FIXTURE" if args.prefabs else "ASSETRIPPER_UNITY_SCENE_FIXTURE" if args.scenes else "ASSETRIPPER_UNITY_MANAGED_REFERENCE_FIXTURE" if args.managed_references else "ASSETRIPPER_UNITY_PAYLOAD_FIXTURE"
    print(f"{variable}={output}")


if __name__ == "__main__":
    main()
