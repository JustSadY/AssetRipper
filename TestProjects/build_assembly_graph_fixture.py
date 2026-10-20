#!/usr/bin/env python3
"""Build the source-owned three-assembly Unity 6000.4 corpus in a disposable project."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    templates = Path(__file__).resolve().parent
    output = args.output.resolve()
    if output.is_relative_to(templates):
        parser.error("Output must be outside TestProjects.")
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        parser.error("Output must be new or empty.")
    workspace = Path(tempfile.mkdtemp(prefix="assetripper-assembly-graph-"))
    project = workspace / "Project"
    shutil.copytree(templates / "AssemblyGraph/Assets", project / "Assets")
    for name in ("Packages", "ProjectSettings"):
        shutil.copytree(templates / "MonoBehaviourPayload" / name, project / name)
    log = workspace / "editor.log"
    command = [str(args.unity.resolve()), "-batchmode", "-nographics", "-projectPath", str(project),
               "-executeMethod", "AssemblyGraphFixtureBuilder.Build", "-quit", "-logFile", str(log)]
    print(f"Disposable project: {project}\nEditor log: {log}", flush=True)
    with (workspace / "stdout.log").open("w") as stdout:
        result = subprocess.run(command, env=dict(os.environ, ASSETRIPPER_UNITY_FIXTURE_OUTPUT=str(output)),
                                stdout=stdout, stderr=subprocess.STDOUT, timeout=600)
    manifest_path = output / "fixture-manifest.json"
    if result.returncode != 0 or not manifest_path.is_file():
        raise RuntimeError(f"Unity fixture generation failed (exit {result.returncode}); inspect {log}")
    manifest = json.loads(manifest_path.read_text())
    if manifest["UnityVersion"] != "6000.4.0f1":
        raise RuntimeError("Unexpected Editor version")
    for entry in manifest["Files"]:
        path = Path(entry["Path"])
        if path.is_absolute() or ".." in path.parts:
            raise RuntimeError("Unsafe fixture path")
        if hashlib.sha256((output / path).read_bytes()).hexdigest() != entry["Sha256"]:
            raise RuntimeError(f"Fixture hash mismatch: {path}")
    print(f"Verified {len(manifest['Files'])} fixture files: {output}")
    print(f"ASSETRIPPER_UNITY_ASSEMBLY_GRAPH_FIXTURE={output}")


if __name__ == "__main__":
    main()
