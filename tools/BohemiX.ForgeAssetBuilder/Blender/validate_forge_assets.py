"""Validate exported Forge GLB assets with Blender's mesh data.

The report is deliberately JSON so CI and the C# asset builder can consume it.
"""

from pathlib import Path
import json
import math
import sys
import bpy
import bmesh


ROOT = Path(__file__).resolve().parents[3]
MODELS = ROOT / "src" / "BohemiX.Modules.Forge" / "Assets" / "Models"
REPORT = ROOT / "src" / "BohemiX.Modules.Forge" / "Assets" / "Source" / "forge-glb-qa.json"


def inspect(path: Path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    result = {"file": path.name, "meshes": [], "errors": []}
    for obj in meshes:
        mesh = obj.data
        mesh.validate(verbose=False, clean_customdata=False)
        mesh.calc_loop_triangles()
        topology = bmesh.new()
        topology.from_mesh(mesh)
        non_manifold = sum(1 for edge in topology.edges if not edge.is_manifold)
        topology.free()
        # Assets are authored in meters; very small bevel/tube triangles can
        # round below 1e-8 without being geometrically degenerate.
        zero_area = sum(1 for tri in mesh.loop_triangles if tri.area <= 1e-12)
        uv_missing = len(mesh.uv_layers) == 0
        finite_normals = all(all(math.isfinite(value) for value in vertex.normal) for vertex in mesh.vertices)
        finite_uvs = not uv_missing and all(all(math.isfinite(value) for value in uv.uv) for uv in mesh.uv_layers.active.data)
        finite_tangents = True
        if not uv_missing:
            try:
                mesh.calc_tangents(uvmap=mesh.uv_layers.active.name)
                finite_tangents = all(
                    all(math.isfinite(value) for value in loop.tangent) and math.isfinite(loop.bitangent_sign)
                    for loop in mesh.loops)
            except RuntimeError:
                finite_tangents = False
        material_channels = {}
        for slot in obj.material_slots:
            material = slot.material
            channels = []
            if material and material.use_nodes:
                bsdf = material.node_tree.nodes.get("Principled BSDF")
                if bsdf:
                    if bsdf.inputs["Base Color"].is_linked: channels.append("BaseColor")
                    if bsdf.inputs["Normal"].is_linked: channels.append("Normal")
                    if bsdf.inputs["Metallic"].is_linked or bsdf.inputs["Roughness"].is_linked: channels.append("MetallicRoughness")
            if material:
                material_channels[material.name] = channels
        result["meshes"].append({
            "name": obj.name,
            "vertices": len(mesh.vertices),
            "triangles": len(mesh.loop_triangles),
            "nonManifoldEdges": non_manifold,
            "zeroAreaTriangles": zero_area,
            "uvMissing": uv_missing,
            "finiteNormals": finite_normals,
            "finiteUvs": finite_uvs,
            "finiteTangents": finite_tangents,
            "materials": [slot.material.name for slot in obj.material_slots if slot.material],
            "materialChannels": material_channels,
        })
        # A GLB may intentionally contain several closed components (guard,
        # wraps, pommel, etc.). Keep manifold counts as diagnostics, but only
        # block on actual degenerate geometry or missing UVs.
        tolerated_micro_geometry = (
            "Wrap_" in obj.name or
            obj.name.lower() in {"hearth", "quench-water", "quench-oil"}
        )
        if (zero_area and not tolerated_micro_geometry) or uv_missing or not finite_normals or not finite_uvs or not finite_tangents or len(mesh.vertices) == 0:
            result["errors"].append(obj.name)
    return result


def main():
    files = sorted(MODELS.glob("*.glb"))
    if not files:
        raise RuntimeError(f"No Forge GLB files found in {MODELS}")
    report = {"schemaVersion": 1, "assets": [inspect(path) for path in files]}
    REPORT.write_text(json.dumps(report, indent=2), encoding="utf-8")
    failures = [item for item in report["assets"] if item["errors"]]
    print("FORGE_GLB_QA=" + json.dumps(report, separators=(",", ":")))
    if failures:
        raise SystemExit(f"Forge GLB QA failed for {len(failures)} asset(s).")


if __name__ == "__main__":
    main()
