#!/usr/bin/env python3
"""Real-SDK import coherence controls; preserve synthetic samples for diagnosis."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--sdk-install", action="append", type=Path)
parser.add_argument("--helper", type=Path)
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
helper = args.helper or repo / "src/FsHotWatch.AnalyzerEvaluationHost/bin/Debug/net8.0/FsHotWatch.AnalyzerEvaluationHost.dll"
assert helper.is_file(), "Compile through mise run compile-evaluation-helper first"
installs = args.sdk_install or [
    Path.home() / ".local/share/mise/installs/dotnet" / version
    for version in ("8.0.408", "9.0.311", "10.0.301")
]
for install in installs:
    sdk = (install / "sdk" / install.name).resolve()
    deps = json.loads((sdk / "MSBuild.deps.json").read_text())
    msbuild = next(key.split("/", 1)[1] for key in deps["libraries"] if key.startswith("Microsoft.Build/"))
    root = Path(tempfile.mkdtemp(prefix="a564-import-coherence-")).resolve()
    os.chmod(root, 0o700)
    print("SDK %s: preserved synthetic fixture %s" % (install.name, root), flush=True)
    imported = root / "Rules.props"
    replacement = root / "Replacement.props"
    project = root / "Mini.fsproj"
    (root / "Rules.fs").write_text("module Rules\nlet value = 1\n")
    original = b'<Project><PropertyGroup><DefineConstants>FIRST</DefineConstants></PropertyGroup></Project>'
    altered = original.replace(b"FIRST", b"OTHER")
    project_xml = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project_xml, "PropertyGroup")
    ET.SubElement(properties, "TargetFramework").text = "net%s.0" % install.name.split('.')[0]
    ET.SubElement(project_xml, "Import", Project="Rules.props")
    mutation = ET.SubElement(project_xml, "PropertyGroup")
    # Execute only after the import was consumed by this very Project evaluation.
    ET.SubElement(mutation, "_ReplaceImport").text = "$([System.IO.File]::Copy('Replacement.props', 'Rules.props', true))"
    ET.SubElement(mutation, "_RestoreTime").text = "$([System.IO.File]::SetLastWriteTimeUtc('Rules.props', $([System.DateTime]::Parse('2023-11-14T22:13:20Z').ToUniversalTime())))"
    items = ET.SubElement(project_xml, "ItemGroup")
    ET.SubElement(items, "Compile", Include="Rules.fs")
    project.write_bytes(ET.tostring(project_xml))
    (root / "global.json").write_text(json.dumps({"sdk": {"version": install.name, "rollForward": "disable"}}))
    request = ET.Element("AnalyzerEvaluationRequest", version="1", project=str(project),
                         sdkRoot=str(sdk), sdkVersion=install.name, msbuildVersion=msbuild)
    ET.SubElement(request, "Globals")
    request_path = root / "request.xml"
    request_path.write_bytes(ET.tostring(request))
    os.chmod(request_path, 0o600)
    environment = os.environ.copy()
    # This capability is enabled only in the disposable synthetic evaluation child.
    environment["MSBUILDENABLEALLPROPERTYFUNCTIONS"] = "1"
    for changed in (False, True):
        imported.write_bytes(original)
        fixed_time = 1_700_000_000_000_000_000
        os.utime(imported, ns=(fixed_time, fixed_time))
        expected = altered if changed else original
        replacement.write_bytes(expected)
        result = subprocess.run([
            str(install / "dotnet"), "exec", "--runtimeconfig", str(sdk / "MSBuild.runtimeconfig.json"),
            "--depsfile", str(helper.with_suffix('.deps.json')), str(helper), str(request_path)
        ], cwd=root, env=environment, capture_output=True, text=True, timeout=45)
        label = "changed" if changed else "unchanged"
        (root / (label + '.stdout')).write_text(result.stdout)
        (root / (label + '.stderr')).write_text(result.stderr)
        # Establish the mutation and preserved timestamp before judging helper status.
        assert imported.read_bytes() == expected, "Evaluation did not perform the intended import replacement"
        assert imported.stat().st_mtime_ns == fixed_time, "Mutation did not preserve import mtime"
        if changed:
            assert result.returncode == 2, "Helper accepted evaluated FIRST XML with a hash of OTHER XML"
            assert not result.stdout, "Refusal must not publish a mixed projection"
        else:
            assert result.returncode == 0, "Unchanged imported XML must remain evaluable"
            projection = json.loads(result.stdout)
            witness = next(item for item in projection['Imports'] if item['FullPath'] == str(imported))
            assert witness['Hash'] == hashlib.sha256(original).hexdigest().upper()
            assert [Path(item['FullPath']).name for item in projection['Items']['Compile']] == ['Rules.fs']
    print("SDK %s: unchanged and replaced-import coherence controls passed" % install.name, flush=True)
