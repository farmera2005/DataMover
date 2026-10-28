"""The committed notebooks are generated from the package; they must be up to date and runnable."""
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("build_notebooks", ROOT / "tools" / "build_notebooks.py")
build_notebooks = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build_notebooks)


def cells(content: str) -> list[str]:
    chunks = content.split("# CELL ********************")[1:]
    return [c.split("# METADATA ********************")[0].strip() for c in chunks]


def test_committed_notebooks_match_the_package():
    for path, content in build_notebooks.build().items():
        assert path.exists(), f"{path} missing: run fabric/tools/build_notebooks.py"
        assert path.read_text(encoding="utf-8") == content, f"{path} is stale: run fabric/tools/build_notebooks.py"


def test_notebook_cells_run_in_one_namespace(spark):
    for path, content in build_notebooks.build().items():
        if path.name != "notebook-content.py":
            continue
        settings, library, entry = cells(content)
        namespace = {"spark": spark}
        exec(compile(settings, f"{path}:settings", "exec"), namespace)
        exec(compile(library, f"{path}:library", "exec"), namespace)
        for name in ("run", "process_batch", "purge_tombstones", "compact", "RECONCILIATION_TABLE"):
            assert name in namespace, f"{name} missing from {path}"
        compile(entry, f"{path}:entry", "exec")
