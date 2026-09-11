#!/usr/bin/env python3
"""Build the machine-readable test catalog from existing repository sources.

Sources (all read-only; nothing is executed here):

  1. ``tools/*/*.csproj``                      .NET check projects
  2. ``tools/*/run.py`` / ``tools/*/presets.py``  Python contract harnesses
  3. ``docs/TEST_MATRIX.md`` Windows/Linux command blocks  documented unattended commands
  4. ``tools/*/package.json``                  Node service tests
  5. ``tools/{verify,test}-*.sh``/``.ps1``     L0 structure and runtime gates

Classification is derived from project content (target framework, assembly
references, package references), so a new tool project is catalogued without
editing this file.  The only explicit tables are the ones that cannot be read
from a project file: measurement-only probes and the checkpoint subcommand.
"""

from __future__ import annotations

import json
import re
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

SCHEMA_VERSION = 1

# Measurement-only projects: they assert inner kernels but also print benchmark
# payloads, and docs/TEST_MATRIX.md never counts them as pass/fail contracts.
PROBE_PROJECTS = frozenset({
    "tools/CompactCandidateChecks/CompactCandidateChecks.csproj",
    "tools/CompactStatePrototype/CompactStatePrototype.csproj",
})

# Contract invocation that needs an explicit subcommand in the project file.
EXTRA_ARGUMENTS = {
    "tools/CheckpointTool/CheckpointTool.csproj": ["--", "self-test"],
}

# Scopes map a name to the entry kinds a run is allowed to execute.
SCOPES = {
    "pure-contract": ("PureDotnet", "PythonContract"),
    "all": ("*",),
}

_INTERESTING_SUFFIXES = (".cs", ".py")
_SKIPPED_DIRECTORIES = frozenset({"bin", "obj", ".local", ".godot", ".git"})

# The ledger entry script itself is tooling, not a gate under test.
GATE_EXCLUSIONS = frozenset({"tools/test-ledger.sh"})


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _git(repo: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", "-C", str(repo), *arguments],
        capture_output=True, text=True, check=False)
    return completed.stdout.strip() if completed.returncode == 0 else ""


def source_revision(repo: Path) -> dict:
    """Record what the catalog was generated from; never used as test evidence."""
    porcelain = _git(repo, "status", "--porcelain")
    status_lines = [line for line in porcelain.splitlines() if line.strip()]
    tracked = [line for line in status_lines if not line.startswith("??")]
    commit = _git(repo, "rev-parse", "HEAD")
    return {
        "commit": commit,
        "commitShort": commit[:7],
        "branch": _git(repo, "rev-parse", "--abbrev-ref", "HEAD"),
        "worktree": str(repo),
        "trackedDirty": bool(tracked),
        "dirty": bool(status_lines),
        "dirtyPaths": status_lines[:64],
        "dirtyPathCount": len(status_lines),
    }


def _tag(element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def _text(element) -> str:
    return (element.text or "").strip()


class Project:
    """The subset of an MSBuild project that decides how a check runs."""

    def __init__(self, path: Path, repo: Path):
        self.path = path
        self.rel = path.relative_to(repo).as_posix()
        tree = ET.parse(path)
        self.target_framework = ""
        self.output_type = ""
        self.hint_paths: list[str] = []
        self.package_references: list[tuple[str, str]] = []
        for group in tree.getroot():
            if _tag(group) != "PropertyGroup":
                continue
            for child in group:
                if _tag(child) == "TargetFramework":
                    self.target_framework = _text(child)
                elif _tag(child) == "OutputType":
                    self.output_type = _text(child)
        for group in tree.getroot():
            if _tag(group) != "ItemGroup":
                continue
            for child in group:
                if _tag(child) == "Reference":
                    hint = child.get("HintPath")
                    if hint:
                        self.hint_paths.append(hint)
                elif _tag(child) == "PackageReference":
                    include = child.get("Include") or ""
                    version = child.get("Version") or ""
                    self.package_references.append((include, version))

    @property
    def uses_game_assemblies(self) -> bool:
        markers = ("sts2.dll", "GodotSharp", "0Harmony", "STS2-RitsuLib")
        return any(any(marker in hint for marker in markers) for hint in self.hint_paths)


def _scan_markers(paths: list[Path], repo: Path) -> tuple[dict[str, str], dict[str, str]]:
    ok_patterns: dict[str, str] = {}
    pass_patterns: dict[str, str] = {}
    literal_pattern = re.compile(r'"((?:[^"\\\n]|\\.){3,300})"')
    ok_token = re.compile(r"[A-Z][A-Z0-9_]{2,}_OK")
    sentence = re.compile(r"^(?:PASS|Passed)\b")
    failure_state = re.compile(r"(?:^|[_\s])(?:fail|failed|failure|error)\b")
    placeholder = re.compile(r"\{[^{}]*\}")
    for path in paths:
        if not path.is_file() or path.suffix not in _INTERESTING_SUFFIXES:
            continue
        for number, line in enumerate(path.read_text(errors="replace").splitlines(), 1):
            for literal in literal_pattern.findall(line):
                if "\\" in literal:
                    continue
                if failure_state.search(literal.lower()):
                    continue
                location = f"{path.relative_to(repo).as_posix()}:{number}"
                found = ok_token.search(literal)
                if found:
                    ok_patterns.setdefault(found.group(0), location)
                    continue
                if sentence.match(literal):
                    parts = placeholder.split(literal)
                    pass_patterns.setdefault(".*".join(re.escape(part) for part in parts), location)
    return ok_patterns, pass_patterns


def marker_patterns(tool_directory: Path, repo: Path,
                    entry_file: str | None = None) -> tuple[list[str], str | None]:
    """Derive the success-marker regexes the default invocation prints.

    Only the entry file and the tool's own ``Program.cs`` are trusted: markers
    that live in sibling contract files may belong to a non-default CLI flag, and
    a required-but-absent marker would be recorded as a false failure.
    ``_OK`` tokens win over ``PASS``/``Passed`` sentences; placeholders such as
    ``{checks}`` become ``.*``.
    """
    entry_paths: list[Path] = []
    if entry_file:
        entry_paths.append(tool_directory / entry_file)
    entry_paths.append(tool_directory / "Program.cs")
    ok_patterns, pass_patterns = _scan_markers(entry_paths, repo)
    chosen = ok_patterns if ok_patterns else pass_patterns
    if not chosen:
        return [], None
    patterns = sorted(chosen)
    return patterns[:6], chosen[patterns[0]]


def _library_entries(repo: Path) -> tuple[list[dict], list[dict]]:
    entries: list[dict] = []
    source_lines: list[dict] = []
    for project_path in sorted(repo.glob("tools/*/*.csproj")):
        project = Project(project_path, repo)
        tool_directory = project_path.parent
        patterns, marker_source = marker_patterns(tool_directory, repo, entry_file="Program.cs")
        base = {
            "entryId": f"dotnet:{project.rel}",
            "targetPath": project.rel,
            "platform": "any",
            "cwd": ".",
            "scenarioId": None,
            "matrixIndex": None,
            "documentedCommand": None,
            "markerPatterns": patterns,
            "markerSource": marker_source,
            "verification": "exit_code_and_marker" if patterns else "exit_code",
            "fixturePaths": [],
            "mutates": [],
            "source": {"id": "tools-csproj", "path": project.rel, "line": None},
        }
        arguments = ["dotnet", "run", "--project", project.rel, "-c", "Release"]
        arguments += EXTRA_ARGUMENTS.get(project.rel, [])
        requires = ["dotnet", "platform:any"]
        if project.target_framework == "net48" or project.output_type == "WinExe":
            base.update(
                kind="DotnetWindows",
                command=None,
                commandText=" ".join(arguments),
                requires=["dotnet", "platform:windows"] + [
                    f"nuget:{name.lower()}/{version}"
                    for name, version in project.package_references if version],
                notes="net48 desktop target; not runnable on Linux.",
            )
        elif project.rel in PROBE_PROJECTS:
            base.update(
                kind="DotnetProbe",
                command=arguments,
                commandText=" ".join(arguments),
                requires=requires,
                notes="measurement/benchmark entry; never counted as a pass/fail contract.",
            )
        elif project.uses_game_assemblies:
            kind = "DotnetCoverage" if "CoverageCatalog" in project.rel else "DotnetGame"
            notes = ("rewrites coverage/*.json and docs/COMBAT_HOOK_COVERAGE.md; "
                     "refused by default policy."
                     if kind == "DotnetCoverage" else
                     "references game/RitsuLib assemblies; not a pure contract entry.")
            if kind == "DotnetCoverage":
                arguments = arguments + ["--", "."]
            base.update(
                kind=kind,
                command=arguments,
                commandText=" ".join(arguments),
                requires=["dotnet", "platform:any", "local_props", "game_assemblies", "ritsu_lib"],
                mutates=([] if kind != "DotnetCoverage" else
                         ["coverage/*.json", "docs/COMBAT_HOOK_COVERAGE.md"]),
                notes=notes,
            )
        elif project.package_references:
            base.update(
                kind="DotnetTool",
                command=arguments,
                commandText=" ".join(arguments),
                requires=requires + ["external_input"] + [
                    f"nuget:{name.lower()}/{version}"
                    for name, version in project.package_references if version],
                notes="needs an external trace/artifact input path; not a pass/fail contract.",
            )
        else:
            base.update(kind="PureDotnet", command=arguments,
                        commandText=" ".join(arguments), requires=requires)
        entries.append(base)
        source_lines.append({
            "entryId": base["entryId"], "kind": base["kind"],
            "markerPatterns": patterns, "markerSource": marker_source,
        })
    source = {
        "id": "tools-csproj",
        "path": "tools/*/*.csproj",
        "entryCount": len(entries),
        "notes": "classification from TargetFramework/OutputType/Reference/PackageReference",
    }
    return entries, [source]


def _python_entries(repo: Path) -> tuple[list[dict], dict]:
    entries: list[dict] = []
    for script in sorted(list(repo.glob("tools/*/run.py")) + list(repo.glob("tools/*/presets.py"))):
        rel = script.relative_to(repo).as_posix()
        patterns, marker_source = marker_patterns(script.parent, repo, entry_file=script.name)
        entries.append({
            "entryId": f"python:{rel}",
            "targetPath": rel,
            "kind": "PythonContract",
            "platform": "any",
            "cwd": ".",
            "command": ["python3", rel],
            "commandText": f"python3 {rel}",
            "scenarioId": None,
            "matrixIndex": None,
            "documentedCommand": None,
            "markerPatterns": patterns,
            "markerSource": marker_source,
            "verification": "exit_code_and_marker" if patterns else "exit_code",
            "requires": ["python3", "platform:any"],
            "fixturePaths": [],
            "mutates": [f".local/{script.parent.name.lower()}/**"],
            "source": {"id": "python-contract-runners", "path": rel, "line": None},
        })
    source = {"id": "python-contract-runners", "path": "tools/*/{run,presets}.py",
              "entryCount": len(entries),
              "notes": "harnesses compile production slices into .local/<name>/ (gitignored)"}
    return entries, source


def _node_entries(repo: Path) -> tuple[list[dict], dict]:
    entries: list[dict] = []
    for package_path in sorted(repo.glob("tools/*/package.json")):
        try:
            package = json.loads(package_path.read_text())
        except json.JSONDecodeError:
            continue
        scripts = package.get("scripts") or {}
        directory = package_path.parent.relative_to(repo).as_posix()
        for script_name, kind, extra in (("test", "NodeTest", []),
                                         ("test:browser", "NodeTestBrowser", ["browser_environment"])):
            if script_name not in scripts:
                continue
            arguments = (["npm", "test"] if script_name == "test"
                         else ["npm", "run", script_name])
            entries.append({
                "entryId": f"node:{directory}:{script_name}",
                "targetPath": f"{directory}/package.json",
                "kind": kind,
                "platform": "any",
                "cwd": directory,
                "command": arguments,
                "commandText": f"cd {directory} && {' '.join(arguments)}",
                "scenarioId": None,
                "matrixIndex": None,
                "documentedCommand": None,
                "markerPatterns": [],
                "markerSource": None,
                "verification": "exit_code",
                "requires": ["node", "npm", f"node_modules:{directory}/node_modules"] + extra,
                "fixturePaths": [],
                "mutates": [],
                "source": {"id": "node-service-tests", "path": f"{directory}/package.json", "line": None},
            })
    source = {"id": "node-service-tests", "path": "tools/*/package.json",
              "entryCount": len(entries),
              "notes": "npm test / npm run test:browser scripts"}
    return entries, source


_MATRIX_COMMANDS = (
    re.compile(r"^pwsh -NoProfile -File tools\\(run-unattended-test|run-visible-steam-benchmark)\.ps1(?: |$)"),
    re.compile(r"^(?:\./)?tools/(run-unattended-test|run-visible-steam-benchmark)\.sh(?: |$)"),
)
_SCENARIO_ID = re.compile(r"--scenario-id[= ]+('([^']*)'|\"([^\"]*)\"|([^\s]+))")
_SCENARIO_ID_WINDOWS = re.compile(r"-ScenarioId\s+('([^']*)'|\"([^\"]*)\"|([^\s]+))")
_FIXTURE_PATH = re.compile(r"[^\s'\"]*coverage[\\/][^\s'\"]+")
_ENVIRONMENT_VARIABLE = re.compile(r"\$\{([A-Z_][A-Z0-9_]*)(?::\?|:[-=])|(?:\$\{)?\$env:([A-Z_][A-Z0-9_]*)")


def _matrix_block(text: str, heading_pattern: str) -> tuple[list[str], int] | None:
    heading = re.search(heading_pattern, text, re.MULTILINE)
    if heading is None:
        return None
    fence = re.search(r"^```[a-z]*\n(.*?)^```", text[heading.end():], re.MULTILINE | re.DOTALL)
    if fence is None:
        return None
    first_line = text.count("\n", 0, heading.end() + fence.start(1)) + 1
    return fence.group(1).splitlines(), first_line


def _matrix_entries(repo: Path) -> tuple[list[dict], dict]:
    matrix_path = repo / "docs/TEST_MATRIX.md"
    text = matrix_path.read_text()
    entries: list[dict] = []
    sources: list[dict] = []
    unparsed: list[str] = []
    for platform, heading in (("windows", r"^### Windows（PowerShell 7）\s*$"),
                              ("linux", r"^### Linux（Bash）\s*$")):
        block = _matrix_block(text, heading)
        if block is None:
            unparsed.append(f"missing {platform} block")
            continue
        lines, first_line = block
        pattern = _MATRIX_COMMANDS[0 if platform == "windows" else 1]
        index = 0
        for offset, raw in enumerate(lines):
            line = raw.strip()
            if not line:
                continue
            match = pattern.match(line)
            if match is None:
                unparsed.append(f"{platform}:{first_line + offset}: {line[:120]}")
                continue
            index += 1
            runner = match.group(1)
            steam = runner == "run-visible-steam-benchmark"
            scenario_match = (_SCENARIO_ID_WINDOWS if platform == "windows" else _SCENARIO_ID).search(line)
            scenario_id = None
            if scenario_match:
                scenario_id = next((group for group in scenario_match.groups()[1:] if group), None)
            if scenario_id is None:
                scenario_id = f"{platform}-steam-benchmark-{index}" if steam else f"{platform}-command-{index}"
            fixtures = sorted({path.replace("\\", "/").strip("\"'")
                               for path in _FIXTURE_PATH.findall(line)})
            environment = sorted({left or right
                                  for left, right in _ENVIRONMENT_VARIABLE.findall(line)})
            arguments = ["bash", "-c", line] if platform == "linux" else None
            kind = ("SteamBenchmark" if steam else "Matrix") + platform.capitalize()
            requires = ["platform:" + platform]
            if platform == "linux":
                requires += ["game_executable", "game_assemblies", "ritsu_lib", "combat_solver_build",
                             "bash", "jq", "flock"]
            if steam:
                requires.append("display_session")
            requires += [f"env:{name}" for name in environment]
            requires += [f"fixture:{path}" for path in fixtures]
            entries.append({
                "entryId": f"matrix-{platform}:{scenario_id}",
                "targetPath": f"tools/{runner}." + ("ps1" if platform == "windows" else "sh"),
                "kind": kind,
                "platform": platform,
                "cwd": ".",
                "command": arguments,
                "commandText": line,
                "documentedCommand": line,
                "scenarioId": scenario_id,
                "matrixIndex": index,
                "markerPatterns": [],
                "markerSource": None,
                "verification": "exit_code",
                "requires": requires,
                "fixturePaths": fixtures,
                "mutates": [],
                "source": {"id": f"test-matrix-{platform}",
                           "path": f"docs/TEST_MATRIX.md:{first_line}-{first_line + len(lines) - 1}",
                           "line": first_line + offset},
            })
        sources.append({
            "id": f"test-matrix-{platform}",
            "path": f"docs/TEST_MATRIX.md:{first_line}-{first_line + len(lines) - 1}",
            "entryCount": index,
            "notes": "verbatim documented command lines; the matrix runner parses the same block",
        })
    if unparsed:
        sources.append({"id": "test-matrix-unparsed", "path": "docs/TEST_MATRIX.md",
                        "entryCount": len(unparsed), "lines": unparsed})
    return entries, sources


def _disambiguate(entries: list[dict]) -> None:
    """Two documented commands may share a scenario id; keep entry ids unique."""
    seen: dict[str, int] = {}
    for entry in entries:
        entry_id = entry["entryId"]
        seen[entry_id] = seen.get(entry_id, 0) + 1
        if seen[entry_id] > 1:
            entry["entryId"] = f"{entry_id}#{seen[entry_id]}"
            if entry.get("scenarioId"):
                entry["scenarioId"] = f"{entry['scenarioId']}#{seen[entry_id]}"


def _gate_entries(repo: Path) -> tuple[list[dict], dict]:
    entries: list[dict] = []
    for script in sorted(repo.glob("tools/[vt]*")):
        if script.suffix not in (".sh", ".ps1") or not script.is_file():
            continue
        name = script.name
        if not (name.startswith("verify-") or name.startswith("test-")):
            continue
        rel = script.relative_to(repo).as_posix()
        if rel in GATE_EXCLUSIONS:
            continue
        if name.endswith(".sh"):
            arguments = ["bash", rel]
            requires = ["bash", "platform:linux"]
            kind, platform = "GateLinux", "linux"
        else:
            arguments = ["pwsh", "-NoProfile", "-File", rel]
            requires = ["pwsh", "platform:windows"]
            kind, platform = "GateWindows", "windows"
        entries.append({
            "entryId": f"gate:{rel}",
            "targetPath": rel,
            "kind": kind,
            "platform": platform,
            "cwd": ".",
            "command": arguments,
            "commandText": " ".join(arguments),
            "documentedCommand": None,
            "scenarioId": None,
            "matrixIndex": None,
            "markerPatterns": [],
            "markerSource": None,
            "verification": "exit_code",
            "requires": requires,
            "fixturePaths": [],
            "mutates": [],
            "source": {"id": "tool-gates", "path": rel, "line": None},
        })
    source = {"id": "tool-gates", "path": "tools/{verify,test}-*.{sh,ps1}",
              "entryCount": len(entries), "notes": "L0 structure/runtime gates (AGENTS.md section 8)"}
    return entries, source


def build(repo: Path) -> dict:
    entries: list[dict] = []
    sources: list[dict] = []
    for builder in (_library_entries, _python_entries, _node_entries, _matrix_entries, _gate_entries):
        produced, produced_sources = builder(repo)
        entries.extend(produced)
        sources.extend(produced_sources if isinstance(produced_sources, list) else [produced_sources])
    _disambiguate(entries)
    for entry in entries:
        entry.setdefault("notes", None)
        entry.setdefault("targetPath", None)
        entry.setdefault("mutates", [])
    kinds: dict[str, int] = {}
    for entry in entries:
        kinds[entry["kind"]] = kinds.get(entry["kind"], 0) + 1
    for source in sources:
        source["entryCount"] = sum(
            1 for entry in entries if entry["source"]["id"] == source["id"]) or source.get("entryCount", 0)
    return {
        "schemaVersion": SCHEMA_VERSION,
        "kind": "combat-solver-test-catalog",
        "generatedAtUtc": utc_now(),
        "generator": "tools/TestLedger/catalog.py",
        "repoRoot": str(repo),
        "sourceRevision": source_revision(repo),
        "scopes": {name: list(kinds_list) for name, kinds_list in SCOPES.items()},
        "entryCountsByKind": dict(sorted(kinds.items())),
        "sources": sources,
        "entries": entries,
    }


def write(catalog: dict, destination: Path) -> Path:
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(catalog, indent=2, ensure_ascii=False) + "\n")
    return destination


def main() -> int:  # pragma: no cover - exercised through the CLI wrapper
    import argparse
    parser = argparse.ArgumentParser(description="Generate the CombatSolver test catalog.")
    parser.add_argument("--repo", default=".", help="repository root (default: cwd)")
    parser.add_argument("--output", default=".local/test-ledger/test-catalog.json")
    arguments = parser.parse_args()
    repo = Path(arguments.repo).resolve()
    catalog = build(repo)
    destination = write(catalog, Path(arguments.output) if Path(arguments.output).is_absolute()
                        else repo / arguments.output)
    print(f"TEST_LEDGER_CATALOG entries={catalog['entryCountsByKind']} output={destination}")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
