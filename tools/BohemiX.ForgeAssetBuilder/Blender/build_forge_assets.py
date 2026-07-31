"""Rebuild Forge weapon assets with embedded PBR textures and QA metadata.

This is intentionally a thin, deterministic entry point around the existing
weapon authoring script. Run with Blender 4.5 LTS in background mode:

    blender.exe --background --factory-startup --python build_forge_assets.py
"""

from pathlib import Path
import runpy


ROOT = Path(__file__).resolve().parents[3]
SOURCE_SCRIPT = ROOT / "src" / "BohemiX.Modules.Forge" / "Assets" / "Source" / "Scripts" / "build_weapons.py"

if not SOURCE_SCRIPT.exists():
    raise FileNotFoundError(f"Forge weapon source script was not found: {SOURCE_SCRIPT}")

# The source script owns the scene and export conventions used by the app.
runpy.run_path(str(SOURCE_SCRIPT), run_name="__main__")
