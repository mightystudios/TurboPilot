"""Repository-relative entry point for phase-manifest generation."""

from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "src"))

from mighty_ai_tools.phase_suite import manifest_cli


if __name__ == "__main__":
    sys.exit(manifest_cli())
