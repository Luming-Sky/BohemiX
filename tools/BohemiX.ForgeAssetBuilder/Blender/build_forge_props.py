"""Build the Forge hero props in Blender and export renderer-compatible GLB files.

Usage:
    blender --background --factory-startup --python build_forge_props.py

The BohemiX renderer resolves material slots by name, so these assets deliberately
use the ForgeMaterialId names and stay uncompressed (SharpGLTF does not decode Draco).
"""

from __future__ import annotations

import json
import math
import os
from pathlib import Path

import bpy
from mathutils import Vector


ROOT = Path(r"D:\BohemiX")
OUTPUT_DIR = ROOT / "src" / "BohemiX.Modules.Forge" / "Assets" / "Models"
PREVIEW_PATH = ROOT / ".tmp" / "blender" / "forge_props_preview.png"
SOURCE_BLEND = ROOT / "src" / "BohemiX.Modules.Forge" / "Assets" / "Source" / "forge-props.blend"
TEXTURE_SIZE = 512


def reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.cameras, bpy.data.lights):
        for block in list(datablocks):
            if block.users == 0:
                datablocks.remove(block)


def _noise(seed: int, x: float, y: float) -> float:
    value = math.sin((x + seed * 17.0) * 12.9898 + (y + seed * 31.0) * 78.233) * 43758.5453
    return value - math.floor(value)


def _value_noise(seed: int, x: float, y: float, cell_size: float) -> float:
    gx = x / cell_size
    gy = y / cell_size
    ix = math.floor(gx)
    iy = math.floor(gy)
    tx = gx - ix
    ty = gy - iy
    tx = tx * tx * (3.0 - 2.0 * tx)
    ty = ty * ty * (3.0 - 2.0 * ty)
    a = _noise(seed, ix, iy) * (1.0 - tx) + _noise(seed, ix + 1, iy) * tx
    b = _noise(seed, ix, iy + 1) * (1.0 - tx) + _noise(seed, ix + 1, iy + 1) * tx
    return a * (1.0 - ty) + b * ty


def _stone_field(seed: int, x: float, y: float) -> tuple[float, float, float]:
    coarse = _value_noise(seed, x, y, 92.0)
    warped_x = x + (coarse - .5) * 29.0
    warped_y = y + (_value_noise(seed + 5, x, y, 71.0) - .5) * 21.0
    aggregate = _value_noise(seed + 13, warped_x, warped_y, 23.0)
    sand = _value_noise(seed + 29, warped_x, warped_y, 6.0)
    return coarse, aggregate, sand


def pbr_image(name, kind, color, metallic, roughness, seed, grain=False):
    image = bpy.data.images.get(name) or bpy.data.images.new(name, width=TEXTURE_SIZE, height=TEXTURE_SIZE, alpha=True)
    pixels = [0.0] * (TEXTURE_SIZE * TEXTURE_SIZE * 4)
    for y in range(TEXTURE_SIZE):
        for x in range(TEXTURE_SIZE):
            u = x / TEXTURE_SIZE
            v = y / TEXTURE_SIZE
            broad = _noise(seed, x / 73.0, y / 89.0)
            medium = _noise(seed + 11, x / 23.0, y / 29.0)
            fine = _noise(seed + 37, x / 5.1, y / 6.7)
            is_stone = name.lower().startswith("stone")
            directional = .5 + .5 * math.sin(v * math.tau * 15.0 + math.sin(u * math.tau * 2.0) * 1.8) if grain else .5
            stone_coarse, stone_aggregate, stone_sand = _stone_field(seed + 83, x, y) if is_stone else (.5, .5, .5)
            stone_grit = _noise(seed + 109, x, y) if is_stone else .5
            scratch = max(0.0, 1.0 - abs(math.sin((u * 19.0 + v * 2.3 + seed) * math.pi))) ** 28
            pit = 1.0 if _noise(seed + 73, x / 2.7, y / 2.9) > .987 else 0.0
            contact = .5 + .5 * math.sin(v * math.pi)
            index = (y * TEXTURE_SIZE + x) * 4
            if kind == "base":
                if is_stone:
                    inclusion = .12 if stone_grit > .984 else (-.11 if stone_grit < .014 else 0.0)
                    variation = .66 + stone_coarse * .14 + stone_aggregate * .11 + stone_sand * .055 + inclusion
                elif grain:
                    variation = .86 + directional * .065 + broad * .055 + medium * .025
                elif metallic > .5:
                    variation = .80 + broad * .11 + medium * .045 + scratch * .055 - pit * .12
                else:
                    variation = .75 + broad * .16 + medium * .08 - pit * .08
                pixels[index:index + 4] = [min(1.0, component * variation) for component in color[:3]] + [1.0]
            elif kind == "normal":
                if is_stone:
                    side_aggregate = _value_noise(seed + 96, x + 1.5, y, 9.0) - _value_noise(seed + 96, x - 1.5, y, 9.0)
                    vertical_aggregate = _value_noise(seed + 117, x, y + 1.5, 9.0) - _value_noise(seed + 117, x, y - 1.5, 9.0)
                    nx = side_aggregate * .22 + (stone_grit - .5) * .025
                    ny = vertical_aggregate * .22 + (stone_sand - .5) * .055 - pit * .020
                else:
                    nx = ((directional if grain else medium) - .5) * (.045 if grain else .055) + scratch * .018
                    ny = (fine - .5) * (.030 if grain else .035) - pit * .035
                pixels[index:index + 4] = [.5 + nx, .5 + ny, 1.0, 1.0]
            else:
                ao = max(.58, .94 - pit * .26 - (1.0 - contact) * .035)
                if is_stone:
                    surface_roughness = roughness * (.92 + (1.0 - stone_aggregate) * .10 + pit * .07 - stone_sand * .025)
                    surface_metallic = 0.0
                else:
                    surface_roughness = roughness * (.88 + (1.0 - broad) * .15 + pit * .14 - scratch * .12)
                    surface_metallic = metallic
                pixels[index:index + 4] = [ao, min(1.0, max(.04, surface_roughness)), surface_metallic, 1.0]
    image.pixels = pixels
    image.pack()
    image.colorspace_settings.name = "sRGB" if kind == "base" else "Non-Color"
    return image


def forge_material(name: str, color: tuple[float, float, float, float], metallic: float, roughness: float):
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.use_nodes = True
    material.diffuse_color = color
    node = material.node_tree.nodes.get("Principled BSDF")
    node.inputs["Base Color"].default_value = color
    node.inputs["Metallic"].default_value = metallic
    node.inputs["Roughness"].default_value = roughness
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    seed = sum((index + 1) * ord(char) for index, char in enumerate(name))
    base = nodes.new("ShaderNodeTexImage")
    base.name = "ForgePbr_BaseColor"
    grain = name.lower().startswith("wood")
    base.image = pbr_image(name + "_BaseColor", "base", color, metallic, roughness, seed, grain)
    links.new(base.outputs["Color"], node.inputs["Base Color"])
    normal = nodes.new("ShaderNodeTexImage")
    normal.name = "ForgePbr_Normal"
    normal.image = pbr_image(name + "_Normal", "normal", color, metallic, roughness, seed + 19, grain)
    normal_map = nodes.new("ShaderNodeNormalMap")
    normal_map.name = "ForgePbr_NormalMap"
    normal_map.inputs["Strength"].default_value = .38 if name == "Stone" else (.24 if metallic > .5 else .14)
    links.new(normal.outputs["Color"], normal_map.inputs["Color"])
    links.new(normal_map.outputs["Normal"], node.inputs["Normal"])
    orm = nodes.new("ShaderNodeTexImage")
    orm.name = "ForgePbr_MetallicRoughnessAO"
    orm.image = pbr_image(name + "_ORM", "orm", color, metallic, roughness, seed + 41)
    split = nodes.new("ShaderNodeSeparateColor")
    split.name = "ForgePbr_ORMChannels"
    split.mode = "RGB"
    links.new(orm.outputs["Color"], split.inputs["Color"])
    links.new(split.outputs["Green"], node.inputs["Roughness"])
    links.new(split.outputs["Blue"], node.inputs["Metallic"])
    return material


MATERIALS = {
    "Iron": forge_material("Iron", (0.34, 0.36, 0.39, 1.0), 0.94, 0.36),
    "IronDark": forge_material("IronDark", (0.14, 0.15, 0.17, 1.0), 0.88, 0.58),
    "Wood": forge_material("Wood", (0.27, 0.105, 0.035, 1.0), 0.02, 0.61),
    "WoodDark": forge_material("WoodDark", (0.105, 0.038, 0.013, 1.0), 0.01, 0.73),
    "Leather": forge_material("Leather", (0.19, 0.060, 0.022, 1.0), 0.01, 0.69),
    "Stone": forge_material("Stone", (0.22, 0.205, 0.175, 1.0), 0.0, 0.88),
}


def move_to_collection(obj: bpy.types.Object, collection: bpy.types.Collection) -> None:
    for owner in list(obj.users_collection):
        owner.objects.unlink(obj)
    collection.objects.link(obj)


def apply_transform(obj: bpy.types.Object) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def apply_bevel(obj: bpy.types.Object, width: float, segments: int = 3, angle: float = math.radians(32)) -> None:
    apply_transform(obj)
    modifier = obj.modifiers.new(name="ForgeBevel", type="BEVEL")
    modifier.width = width
    modifier.segments = segments
    modifier.limit_method = "ANGLE"
    modifier.angle_limit = angle
    modifier.harden_normals = True
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=modifier.name)


def smooth_mesh(obj: bpy.types.Object, sharp_angle: float = math.radians(38)) -> None:
    obj.data.shade_smooth()
    obj.data.set_sharp_from_angle(angle=sharp_angle)


def add_cube(name: str, location, size, material: str, bevel: float, collection):
    bpy.ops.mesh.primitive_cube_add(location=location)
    obj = bpy.context.object
    obj.name = name
    obj.scale = (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5)
    obj.data.materials.append(MATERIALS[material])
    if bevel > 0:
        apply_bevel(obj, bevel, 4)
    smooth_mesh(obj)
    move_to_collection(obj, collection)
    return obj


def add_cylinder(name: str, location, radius: float, depth: float, material: str, collection, vertices=64, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth, location=location, rotation=rotation)
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(MATERIALS[material])
    apply_bevel(obj, min(radius * 0.08, depth * 0.16), 3)
    smooth_mesh(obj)
    move_to_collection(obj, collection)
    return obj


def add_loft_x(name: str, sections, radial_segments: int, material: str, collection):
    vertices = []
    faces = []
    for x, radius_y, radius_z in sections:
        for index in range(radial_segments):
            angle = index / radial_segments * math.tau
            vertices.append((x, math.cos(angle) * radius_y, math.sin(angle) * radius_z))
    rings = len(sections)
    for ring in range(rings - 1):
        for index in range(radial_segments):
            nxt = (index + 1) % radial_segments
            a = ring * radial_segments + index
            b = ring * radial_segments + nxt
            c = (ring + 1) * radial_segments + nxt
            d = (ring + 1) * radial_segments + index
            faces.append((a, b, c, d))
    faces.append(tuple(reversed(range(radial_segments))))
    start = (rings - 1) * radial_segments
    faces.append(tuple(start + index for index in range(radial_segments)))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    smooth_mesh(obj)
    return obj


def add_ellipse_loft_x(name: str, sections, radial_segments: int, material: str, collection):
    vertices = []
    faces = []
    for x, center_z, radius_y, radius_z in sections:
        for index in range(radial_segments):
            angle = index / radial_segments * math.tau
            vertices.append((x, math.cos(angle) * radius_y, center_z + math.sin(angle) * radius_z))
    rings = len(sections)
    for ring in range(rings - 1):
        for index in range(radial_segments):
            nxt = (index + 1) % radial_segments
            a = ring * radial_segments + index
            b = ring * radial_segments + nxt
            c = (ring + 1) * radial_segments + nxt
            d = (ring + 1) * radial_segments + index
            faces.append((a, b, c, d))
    faces.append(tuple(reversed(range(radial_segments))))
    start = (rings - 1) * radial_segments
    faces.append(tuple(start + index for index in range(radial_segments)))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    smooth_mesh(obj)
    return obj


def add_rect_loft_z(name: str, sections, material: str, collection, bevel=0.04):
    vertices = []
    faces = []
    for z, half_x, half_y in sections:
        vertices.extend(((-half_x, -half_y, z), (half_x, -half_y, z), (half_x, half_y, z), (-half_x, half_y, z)))
    for ring in range(len(sections) - 1):
        a = ring * 4
        b = (ring + 1) * 4
        faces.extend(((a, a + 1, b + 1, b), (a + 1, a + 2, b + 2, b + 1), (a + 2, a + 3, b + 3, b + 2), (a + 3, a, b, b + 3)))
    faces.extend(((3, 2, 1, 0), tuple((len(sections) - 1) * 4 + index for index in range(4))))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    if bevel > 0:
        apply_bevel(obj, bevel, 4)
    smooth_mesh(obj)
    return obj


def add_curve_tube(name: str, points, radius: float, material: str, collection):
    curve = bpy.data.curves.new(name + "Curve", type="CURVE")
    curve.dimensions = "3D"
    curve.resolution_u = 3
    curve.bevel_depth = radius
    curve.bevel_resolution = 3
    curve.resolution_u = 8
    spline = curve.splines.new("BEZIER")
    spline.bezier_points.add(len(points) - 1)
    for control, point in zip(spline.bezier_points, points):
        control.co = point
        control.handle_left_type = "AUTO"
        control.handle_right_type = "AUTO"
    obj = bpy.data.objects.new(name, curve)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.convert(target="MESH")
    smooth_mesh(obj)
    return obj


def boolean_difference(target, cutter) -> None:
    bpy.context.view_layer.objects.active = target
    modifier = target.modifiers.new(name="ForgeHole", type="BOOLEAN")
    modifier.operation = "DIFFERENCE"
    modifier.solver = "EXACT"
    modifier.object = cutter
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    bpy.data.objects.remove(cutter, do_unlink=True)


def semantic_uv(obj: bpy.types.Object, world_scale: float = .72) -> None:
    mesh = obj.data
    while mesh.uv_layers:
        mesh.uv_layers.remove(mesh.uv_layers[0])
    uv_layer = mesh.uv_layers.new(name="ForgeUV")
    for polygon in mesh.polygons:
        normal = polygon.normal
        axis = max(range(3), key=lambda index: abs(normal[index]))
        for loop_index in polygon.loop_indices:
            point = mesh.vertices[mesh.loops[loop_index].vertex_index].co
            if axis == 0:
                uv = (point.y * world_scale, point.z * world_scale)
            elif axis == 1:
                uv = (point.x * world_scale, point.z * world_scale)
            else:
                uv = (point.x * world_scale, point.y * world_scale)
            uv_layer.data[loop_index].uv = uv


def join_asset(name: str, objects, collection, simple_subdivision: bool = False):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    result = bpy.context.object
    result.name = name
    apply_transform(result)
    smooth_mesh(result)
    semantic_uv(result)
    if simple_subdivision:
        subdivision = result.modifiers.new(name="ForgeSimpleSubdivision", type="SUBSURF")
        subdivision.subdivision_type = "SIMPLE"
        subdivision.levels = 1
        subdivision.render_levels = 1
        bpy.context.view_layer.objects.active = result
        bpy.ops.object.modifier_apply(modifier=subdivision.name)
    triangulate = result.modifiers.new(name="ForgeTriangulate", type="TRIANGULATE")
    triangulate.keep_custom_normals = True
    bpy.context.view_layer.objects.active = result
    bpy.ops.object.modifier_apply(modifier=triangulate.name)
    move_to_collection(result, collection)
    return result


def build_hammer():
    collection = bpy.data.collections.new("Forge_Hammer")
    bpy.context.scene.collection.children.link(collection)
    pieces = []
    # The handle is an asymmetric oval, thicker at the butt and compressed at
    # the eye. It is intentionally straight enough for the gameplay swing rig.
    pieces.append(add_loft_x("HammerHandle", [
        (0.00, .105, .090), (.08, .119, .102), (.30, .108, .095), (.72, .092, .082),
        (1.10, .098, .086), (1.31, .108, .096), (1.48, .122, .108), (1.60, .106, .094),
    ], 48, "Wood", collection))
    pieces.append(add_cylinder("HammerEyeCollar", (1.43, 0, 0), .145, .27, "IronDark", collection, 48, rotation=(0, math.pi / 2, 0)))

    # One continuous forged head with two flat working faces. The stepped
    # shoulders and unequal faces read as forged mass rather than stacked boxes.
    head = add_rect_loft_z("HammerHead", [
        (-.64, .315, .285), (-.59, .335, .302), (-.48, .300, .282),
        (-.32, .252, .248), (-.19, .310, .285), (.38, .310, .285),
        (.56, .282, .268), (.82, .238, .242), (.96, .285, .270),
        (1.01, .302, .282),
    ], "Iron", collection, 0)
    head.location.x = 1.49
    apply_transform(head)
    pieces.append(head)
    pieces.append(add_cylinder("HammerGripRing", (.20, 0, 0), .118, .060, "Leather", collection, 48, rotation=(0, math.pi / 2, 0)))
    return join_asset("hammer", pieces, collection)


def build_anvil():
    collection = bpy.data.collections.new("Forge_Anvil")
    bpy.context.scene.collection.children.link(collection)
    pieces = []
    pieces.append(add_cylinder("AnvilStump", (0, 0, -1.18), .66, .72, "WoodDark", collection, 64))
    pieces.append(add_cylinder("AnvilBandTop", (0, 0, -.88), .675, .07, "IronDark", collection, 64))
    pieces.append(add_cylinder("AnvilBandBottom", (0, 0, -1.47), .655, .065, "IronDark", collection, 64))
    pieces.append(add_cube("AnvilFoot", (-.02, 0, -.67), (1.72, 1.12, .25), "IronDark", .075, collection))
    pieces.append(add_rect_loft_z("AnvilWaist", [(-.57, .76, .49), (-.34, .52, .37), (.00, .45, .34), (.26, .70, .46)], "IronDark", collection, .06))
    face = add_cube("AnvilFace", (-.34, 0, .46), (2.22, 1.08, .30), "Iron", .055, collection)

    bpy.ops.mesh.primitive_cube_add(location=(-.73, .23, .55), scale=(.105, .105, .24))
    hardie = bpy.context.object
    apply_transform(hardie)
    boolean_difference(face, hardie)
    bpy.ops.mesh.primitive_cylinder_add(vertices=48, radius=.075, depth=.42, location=(-.38, -.25, .55))
    pritchel = bpy.context.object
    boolean_difference(face, pritchel)
    pieces.append(face)
    pieces.append(add_cube("AnvilHeel", (-1.48, 0, .42), (.48, 1.00, .38), "Iron", .055, collection))
    pieces.append(add_ellipse_loft_x("AnvilHorn", [
        (.58, .44, .50, .24), (.83, .43, .45, .22), (1.10, .42, .34, .18),
        (1.38, .41, .22, .13), (1.62, .40, .105, .075), (1.76, .40, .025, .025),
    ], 56, "Iron", collection))
    pieces.append(add_cube("AnvilPolishedFace", (-.34, 0, .625), (2.16, 1.02, .025), "Iron", .010, collection))
    return join_asset("anvil", pieces, collection, simple_subdivision=True)


def build_tongs():
    collection = bpy.data.collections.new("Forge_Tongs")
    bpy.context.scene.collection.children.link(collection)
    pieces = []
    pieces.append(add_curve_tube("TongArmA", [(-1.24, -.13, 0), (-.58, -.12, .01), (.10, -.08, .025), (.72, -.035, .015), (1.18, -.015, 0)], .048, "IronDark", collection))
    pieces.append(add_curve_tube("TongArmB", [(-1.24, .13, 0), (-.58, .12, -.01), (.10, .08, -.025), (.72, .035, -.015), (1.18, .015, 0)], .048, "IronDark", collection))
    pieces.append(add_cylinder("TongPivot", (.08, 0, 0), .13, .20, "Iron", collection, 48))
    jaw_a = add_cube("TongJawA", (1.24, -.055, .015), (.48, .13, .16), "Iron", .035, collection)
    jaw_a.rotation_euler[2] = math.radians(4)
    apply_transform(jaw_a)
    pieces.append(jaw_a)
    jaw_b = add_cube("TongJawB", (1.24, .055, -.015), (.48, .13, .16), "Iron", .035, collection)
    jaw_b.rotation_euler[2] = math.radians(-4)
    apply_transform(jaw_b)
    pieces.append(jaw_b)
    pieces.append(add_cylinder("TongGripA", (-1.02, -.13, 0), .072, .42, "Leather", collection, 40, rotation=(0, math.pi / 2, 0)))
    pieces.append(add_cylinder("TongGripB", (-1.02, .13, 0), .072, .42, "Leather", collection, 40, rotation=(0, math.pi / 2, 0)))
    return join_asset("tongs", pieces, collection)


def export_asset(obj, filename: str) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(
        filepath=str(OUTPUT_DIR / filename),
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_texcoords=True,
        export_normals=True,
        export_tangents=True,
        export_materials="EXPORT",
        export_cameras=False,
        export_lights=False,
        export_animations=False,
        export_yup=True,
    )


def look_at(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()


def add_preview(assets) -> None:
    preview = bpy.data.collections.new("PreviewOnly")
    bpy.context.scene.collection.children.link(preview)
    positions = {
        "anvil": ((0, .35, 0), (0, 0, 0)),
        "hammer": ((-1.35, -1.00, .28), (0, math.radians(-13), math.radians(18))),
        "tongs": ((.45, -1.20, -.12), (0, math.radians(5), math.radians(-10))),
    }
    for name, source in assets.items():
        for source_collection in source.users_collection:
            source_collection.hide_render = True
        copy = source.copy()
        copy.data = source.data.copy()
        preview.objects.link(copy)
        copy.location, copy.rotation_euler = positions[name]
    add_cube("PreviewGround", (0, 0, -1.58), (6.6, 5.2, .18), "Stone", .05, preview)

    bpy.ops.object.camera_add(location=(5.8, -7.2, 4.4))
    camera = bpy.context.object
    camera.data.lens = 56
    look_at(camera, (0, 0, -.15))
    bpy.context.scene.camera = camera

    bpy.ops.object.light_add(type="AREA", location=(-2.8, -3.2, 5.2))
    key = bpy.context.object
    key.data.energy = 1050
    key.data.shape = "DISK"
    key.data.size = 4.0
    key.data.color = (1.0, .43, .18)
    look_at(key, (0, 0, 0))
    bpy.ops.object.light_add(type="AREA", location=(3.8, -1.0, 3.2))
    fill = bpy.context.object
    fill.data.energy = 680
    fill.data.size = 3.2
    fill.data.color = (.32, .48, 1.0)
    look_at(fill, (.2, 0, 0))
    bpy.ops.object.light_add(type="AREA", location=(0, 3.6, 4.8))
    rim = bpy.context.object
    rim.data.energy = 900
    rim.data.size = 2.5
    rim.data.color = (1.0, .72, .43)
    look_at(rim, (0, .2, .2))

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = 1280
    scene.render.resolution_y = 720
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = str(PREVIEW_PATH)
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (.030, .036, .050, 1.0)
    background.inputs["Strength"].default_value = .32
    bpy.ops.object.light_add(type="AREA", location=(0, -4.6, 2.8))
    fill = bpy.context.object
    fill.name = "ReadableMaterialFill"
    fill.data.energy = 1050
    fill.data.size = 3.6
    fill.data.color = (.70, .80, 1.0)
    look_at(fill, (0, 0, 0))
    scene.view_settings.look = "AgX - Medium High Contrast"
    bpy.ops.render.render(write_still=True)


def mesh_report(obj):
    obj.data.calc_loop_triangles()
    bounds = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
    return {
        "vertices": len(obj.data.vertices),
        "triangles": len(obj.data.loop_triangles),
        "materials": [slot.material.name for slot in obj.material_slots if slot.material],
        "bounds": {
            "min": [min(point[index] for point in bounds) for index in range(3)],
            "max": [max(point[index] for point in bounds) for index in range(3)],
        },
    }


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    PREVIEW_PATH.parent.mkdir(parents=True, exist_ok=True)
    reset_scene()
    assets = {
        "hammer": build_hammer(),
        "anvil": build_anvil(),
        "tongs": build_tongs(),
    }
    for name, obj in assets.items():
        export_asset(obj, name + ".glb")
    bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE_BLEND))
    report = {name: mesh_report(obj) for name, obj in assets.items()}
    add_preview(assets)
    print("FORGE_BLENDER_BUILD=" + json.dumps(report, separators=(",", ":")))


if __name__ == "__main__":
    main()
