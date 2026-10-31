"""Print a `dotnet test --filter` expression for the self-contained backend tests.

A test class qualifies when no file declaring it needs a database or local tool: no Mongo fixture or collection, no
hosted application factory, no environment-provided runtime (OCR, video, browser, PDF raster) and nothing
Windows-only. A partial class is judged by all of its files, since one part may carry [Collection("Mongo")].
Everything else runs in the Mongo-backed batches (tmp/production/run-full-migration-checks.ps1) and in the
manually dispatched Release validation workflow. Used by .github/workflows/quality.yml.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent / "UnitToko.Tests"
NEEDS_ENVIRONMENT = re.compile(
    r'MongoFixture|Collection\("Mongo"\)|StoreTestData|WebApplicationFactory|IMongoDatabase|MongoClient|'
    r'UNITTOKO_|GetEnvironmentVariable|27028|OperatingSystem\.IsWindows|SupportedOSPlatform|Windows'
)
TEST_CLASS = re.compile(r'^\s*public\s+(?:sealed\s+|static\s+)?(?:partial\s+)?class\s+(\w+Tests)\b', re.MULTILINE)
ANY_CLASS = re.compile(r'\bclass\s+(\w+)\b')
# Retired tests (docs/quality/retired-tests.md) are deselected so CI can still require passed == total; a Pending skip stays visible.
RETIRED_TEST = re.compile(
    r'\[\w*(?:Fact|Theory)\(Skip\s*=\s*Retired\.\w+\)\](?:\s*\[[^\]]*\])*\s*public\s+(?:async\s+)?[\w<>]+\s+(\w+)\s*\(')


def classes() -> tuple[list[str], list[str]]:
    tests, excluded, retired = set(), set(), set()
    for path in sorted(ROOT.rglob("*.cs")):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        if NEEDS_ENVIRONMENT.search(text):
            excluded.update(ANY_CLASS.findall(text))
            continue
        declared = list(TEST_CLASS.finditer(text))
        tests.update(match.group(1) for match in declared)
        for match in RETIRED_TEST.finditer(text):
            owner = [c.group(1) for c in declared if c.start() < match.start()]
            if owner:
                retired.add(f"{owner[-1]}.{match.group(1)}")
    selected = sorted(tests - excluded)
    return selected, sorted(r for r in retired if r.split(".")[0] in selected)


if __name__ == "__main__":
    selected, retired = classes()
    if not selected:
        sys.exit("No self-contained test classes found; the selector is out of date.")
    if "--list" in sys.argv:
        print("\n".join(selected))
    else:
        expression = "|".join(f"FullyQualifiedName~.{name}." for name in selected)
        print(f"({expression})" + "".join(f"&FullyQualifiedName!~.{test}" for test in retired) if retired else expression)
