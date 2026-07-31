"""Render a temporary Blender review image from the actual C# dynamic billet GLBs."""

from __future__ import annotations

import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector


args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
input_dir = Path(args[0]) if args else Path(r"D:\BohemiX\.tmp\blender\billet-exports")
preview_path = Path(args[1]) if len(args) > 1 else Path(r"D:\BohemiX\.tmp\blender\forge_billets_preview.png")


def look_at(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()


def add_pedestal(name, location):
    bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=.92, depth=.24, location=location)
    obj = bpy.context.object
    obj.name = name
    bevel = obj.modifiers.new("ReviewBevel", "BEVEL")
    bevel.width = .05
    bevel.segments = 3
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    material = bpy.data.materials.get("ReviewIron") or bpy.data.materials.new("ReviewIron")
    material.use_nodes = True
    shader = material.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (.055, .065, .078, 1)
    shader.inputs["Metallic"].default_value = .82
    shader.inputs["Roughness"].default_value = .48
    obj.data.materials.append(material)


bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)

placements = {
    "longsword": (-3.1, 0, .58),
    "shortsword": (0, 0, .58),
    "axe": (3.0, 0, .58),
}
for recipe, position in placements.items():
    bpy.ops.import_scene.gltf(filepath=str(input_dir / f"billet-{recipe}.glb"))
    imported = [obj for obj in bpy.context.selected_objects if obj.type == "MESH"]
    for obj in imported:
        obj.name = f"Billet_{recipe}"
        obj.location = position
        obj.rotation_euler[2] = math.radians(-6 if recipe == "longsword" else 5 if recipe == "axe" else 2)
        for slot in obj.material_slots:
            if slot.material is None or not slot.material.name.startswith("Workpiece"):
                continue
            shader = slot.material.node_tree.nodes.get("Principled BSDF")
            shader.inputs["Base Color"].default_value = (.085, .078, .070, 1)
            shader.inputs["Metallic"].default_value = .68
            shader.inputs["Roughness"].default_value = .76
    add_pedestal(f"Pedestal_{recipe}", (position[0], position[1], .22))

bpy.ops.mesh.primitive_plane_add(size=14, location=(0, 0, .08))
ground = bpy.context.object
ground_material = bpy.data.materials.new("ReviewGround")
ground_material.use_nodes = True
ground_shader = ground_material.node_tree.nodes.get("Principled BSDF")
ground_shader.inputs["Base Color"].default_value = (.018, .012, .009, 1)
ground_shader.inputs["Roughness"].default_value = .82
ground.data.materials.append(ground_material)

bpy.ops.object.camera_add(location=(7.6, -11.8, 7.2))
camera = bpy.context.object
camera.data.lens = 58
look_at(camera, (0, 0, .60))
bpy.context.scene.camera = camera

for light_type, location, energy, color, size in (
    ("AREA", (-4.2, -4.0, 6.5), 1250, (1.0, .34, .10), 4.0),
    ("AREA", (4.8, -1.2, 5.2), 850, (.30, .48, 1.0), 3.2),
    ("AREA", (0, 4.0, 6.8), 1050, (1.0, .72, .38), 3.0),
):
    bpy.ops.object.light_add(type=light_type, location=location)
    light = bpy.context.object
    light.data.energy = energy
    light.data.color = color
    light.data.shape = "DISK"
    light.data.size = size
    look_at(light, (0, 0, .55))

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1400
scene.render.resolution_y = 760
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.filepath = str(preview_path)
scene.world.color = (.006, .004, .003)
scene.view_settings.look = "AgX - Medium High Contrast"
bpy.ops.render.render(write_still=True)
print(f"FORGE_BILLET_PREVIEW={preview_path}")
