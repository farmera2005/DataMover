"""
Builds the Fabric notebooks (Git-integration format) from the tested package, so what runs in Fabric is exactly the
code the tests ran. Run after changing anything under src/:

    python fabric/tools/build_notebooks.py

Each notebook carries the whole library in its first cell (no %run or wheel to keep in step) and its entry point after.
"""
import json
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "src" / "lakeflow_fabric"
OUT = ROOT / "notebooks"
MODULES = ("names", "checksum", "bronze", "silver", "job", "maintenance")

HEADER = """# Fabric notebook source

# METADATA ********************

# META {
# META   "kernel_info": {
# META     "name": "synapse_pyspark"
# META   },
# META   "dependencies": {}
# META }
"""

CELL = """
# CELL ********************

{code}

# METADATA ********************

# META {{
# META   "language": "python",
# META   "language_group": "synapse_pyspark"
# META }}
"""

NOTEBOOKS = {
    "lakeflow_bronze_to_silver": (
        "Every 5 minutes: raw Eventstream events -> bronze change tables -> silver current state, and reconciliation.",
        '''# Settings. Attach the "bronze" lakehouse (default) and the "silver" lakehouse to this notebook.
DIVISIONS = ["grain", "feed", "agronomy", "erp", "other"]
CHECKPOINT_ROOT = "Files/lakeflow/checkpoints"   # in the default (bronze) lakehouse
BRONZE = "bronze"
SILVER = "silver"''',
        '''summary = run(spark, CHECKPOINT_ROOT, divisions=DIVISIONS, bronze=BRONZE, silver=SILVER)
print(summary)
mismatches = sum(v["mismatches"] for v in summary.values() if isinstance(v, dict))
if mismatches:
    print(f"{mismatches} reconciliation mismatch(es); see {SILVER}.{RECONCILIATION_TABLE}")''',
    ),
    "lakeflow_maintenance": (
        "Monthly: purge silver tombstones past the replay window, OPTIMIZE and VACUUM bronze and silver.",
        '''# Settings. Attach the "bronze" lakehouse (default) and the "silver" lakehouse to this notebook.
BRONZE = "bronze"
SILVER = "silver"''',
        '''print("tombstones purged:", purge_tombstones(spark, SILVER))
print("compacted:", compact(spark, BRONZE, SILVER))''',
    ),
}


def library_code() -> str:
    parts = []
    for module in MODULES:
        lines = (PACKAGE / f"{module}.py").read_text(encoding="utf-8").splitlines()
        kept = [l for l in lines if not l.startswith(("from .", "from lakeflow_fabric"))]
        parts.append(f"# ---- lakeflow_fabric/{module}.py ----\n" + "\n".join(kept).strip())
    return "# Generated from fabric/src/lakeflow_fabric by fabric/tools/build_notebooks.py: edit the package, not this cell.\n\n" + "\n\n".join(parts)


def notebook(settings: str, entry: str) -> str:
    return HEADER + CELL.format(code=settings) + CELL.format(code=library_code()) + CELL.format(code=entry)


def platform(name: str, description: str) -> str:
    return json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/gitIntegration/platformProperties/2.0.0/schema.json",
        "metadata": {"type": "Notebook", "displayName": name, "description": description},
        "config": {"version": "2.0", "logicalId": str(uuid.uuid5(uuid.NAMESPACE_URL, f"lakeflow/{name}"))},
    }, indent=2) + "\n"


def build() -> dict[Path, str]:
    files = {}
    for name, (description, settings, entry) in NOTEBOOKS.items():
        folder = OUT / f"{name}.Notebook"
        files[folder / "notebook-content.py"] = notebook(settings, entry)
        files[folder / ".platform"] = platform(name, description)
    return files


if __name__ == "__main__":
    check = "--check" in sys.argv
    stale = []
    for path, content in build().items():
        if check:
            if not path.exists() or path.read_text(encoding="utf-8") != content:
                stale.append(str(path))
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
    if stale:
        sys.exit("Out of date (run fabric/tools/build_notebooks.py): " + ", ".join(stale))
