"""Repository-relative entry point for single-process phase orchestration."""

from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

from mighty_ai_tools.phase_runner import main


if __name__ == "__main__":
    sys.exit(main())
