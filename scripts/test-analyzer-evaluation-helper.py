#!/usr/bin/env python3
"""Bounded SDK evaluation smoke; no targets, restore, builds, or environment replay."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--sdk-install", action="append", type=Path)
args = parser.parse_args()
installs = args.sdk_install or [
    Path.home() / ".local/share/mise/installs/dotnet" / version
    for version in ("8.0.408", "9.0.311", "10.0.301")
]
repo = Path(__file__).resolve().parent.parent
helper = repo / "src/FsHotWatch.AnalyzerEvaluationHost/bin/Debug/net8.0/FsHotWatch.AnalyzerEvaluationHost.dll"
assert helper.is_file(), "Compile the helper through mise run compile-evaluation-helper first"
for install in installs:
    sdk = install / "sdk" / install.name
    deps = json.loads((sdk / "MSBuild.deps.json").read_text())
    msbuild_version = next(key.split("/", 1)[1] for key in deps["libraries"] if key.startswith("Microsoft.Build/"))
    with tempfile.TemporaryDirectory(prefix="helper-") as temporary:
        root = Path(temporary).resolve()
        os.chmod(root, 0o700)
        producer = root / "Producer"
        producer.mkdir()
        project = producer / "Mini.fsproj"
        # Actual literal XML, quotes and line breaks, not percent-encoded CLI text.
        xml_global = '<Configuration>\n  <Project Name="quoted">value</Project>\n</Configuration>'
        project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net%s.0</TargetFramework></PropertyGroup><ItemGroup>
<Compile Include="Rules.fs"/><Compile Include="Selected.fs" Condition="'$(RuleFlavor)' == 'semi%%3Bcolon'"/>
<Compile Include="XmlSelected.fs" Condition="'$(RuleXml.Length)' == '%s'"/>
</ItemGroup><Import Project="../Rules.props" Condition="Exists('../Rules.props')"/></Project>''' % (install.name.split('.')[0], len(xml_global)))
        (producer / "global.json").write_text(json.dumps({"sdk": {"version": install.name, "rollForward": "disable"}}))
        (producer / "Rules.fs").write_text("module Rules\nlet value = 1\n")
        (producer / "Selected.fs").write_text("module Selected\nlet value = 2\n")
        (producer / "XmlSelected.fs").write_text("module XmlSelected\nlet value = 3\n")
        request = root / "request.xml"
        def save_request(version=install.name):
            node = ET.Element("AnalyzerEvaluationRequest", version="1", project=str(project),
                              sdkRoot=str(sdk.resolve()), sdkVersion=version, msbuildVersion=msbuild_version)
            globals_node = ET.SubElement(node, "Globals")
            ET.SubElement(globals_node, "Property", name="RuleFlavor", value="semi%3Bcolon")
            ET.SubElement(globals_node, "Property", name="RuleXml", value=xml_global)
            request.write_bytes(ET.tostring(node))
            os.chmod(request, 0o600)
        def run(success=True):
            result = subprocess.run([str(install / "dotnet"), "exec", "--runtimeconfig",
                str(sdk / "MSBuild.runtimeconfig.json"), "--depsfile", str(helper.with_suffix('.deps.json')),
                str(helper), str(request)], cwd=producer, capture_output=True, text=True, timeout=45)
            assert (result.returncode == 0) == success, "SDK helper returned an unexpected status (output withheld)"
            if not success:
                assert "semi" not in result.stderr and not result.stdout, "Refusal exposed private input"
                return None
            return json.loads(result.stdout)
        save_request()
        before = run()
        assert before == run(), "Unchanged evaluation differs"
        assert before["Properties"]["NETCoreSdkVersion"] == install.name
        assert before["Properties"]["MSBuildVersion"] == msbuild_version
        assert Path(before["Sdk"]["AssemblyPath"]).parent == sdk.resolve()
        assert [Path(item["FullPath"]).name for item in before["Items"]["Compile"]] == ["Rules.fs", "Selected.fs", "XmlSelected.fs"]
        imported = root / "Rules.props"
        imported.write_text("<Project><PropertyGroup><DefineConstants>FIRST</DefineConstants></PropertyGroup></Project>")
        after = run()
        assert after["Items"] == before["Items"]
        assert str(imported) not in [item["FullPath"] for item in before["Imports"]]
        added = next(item for item in after["Imports"] if item["FullPath"] == str(imported))
        imported.write_text("<Project><PropertyGroup><DefineConstants>SECOND</DefineConstants></PropertyGroup></Project>")
        changed = next(item for item in run()["Imports"] if item["FullPath"] == str(imported))
        assert added["Hash"] != changed["Hash"], "Resolved import content was not bound"
        save_request("0.0.0")
        run(False)
        request.write_text("<invalid/>")
        os.chmod(request, 0o600)
        run(False)
        print("SDK %s: evaluation/import/hash/escaped-global/refusal controls passed" % install.name)
