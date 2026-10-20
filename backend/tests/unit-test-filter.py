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


def classes() -> list[str]:
    tests, excluded = set(), set()
    for path in sorted(ROOT.rglob("*.cs")):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        if NEEDS_ENVIRONMENT.search(text):
            excluded.update(ANY_CLASS.findall(text))
        else:
            tests.update(TEST_CLASS.findall(text))
    return sorted(tests - excluded)


if __name__ == "__main__":
    selected = classes()
    if not selected:
        sys.exit("No self-contained test classes found; the selector is out of date.")
    if "--list" in sys.argv:
        print("\n".join(selected))
    else:
        print("|".join(f"FullyQualifiedName~.{name}." for name in selected))
