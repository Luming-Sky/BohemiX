"""Build the Forge workshop furniture and stations as renderer-compatible GLBs.

The scene is authored in Blender Z-up coordinates and exported as glTF Y-up.
Every material name deliberately matches ForgeMaterialId. Geometry is original,
uncompressed, UV unwrapped, beveled, and intended for the custom PBR renderer.
"""

from __future__ import annotations

import importlib.util
import json
import math
from pathlib import Path

import bpy
from mathutils import Vector


ROOT = Path(r"D:\BohemiX")
SCRIPT_DIR = ROOT / "tools" / "BohemiX.ForgeAssetBuilder" / "Blender"
OUTPUT_DIR = ROOT / "src" / "BohemiX.Modules.Forge" / "Assets" / "Models"
PREVIEW_PATH = ROOT / ".tmp" / "blender" / "forge_workshop_preview.png"
SOURCE_BLEND = ROOT / "src" / "BohemiX.Modules.Forge" / "Assets" / "Source" / "forge-workshop.blend"


def load_prop_helpers():
    spec = importlib.util.spec_from_file_location("forge_prop_helpers", SCRIPT_DIR / "build_forge_props.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


H = load_prop_helpers()
MATERIALS = H.MATERIALS
MATERIALS.update({
    "Brass": H.forge_material("Brass", (.52, .285, .082, 1), .90, .32),
    "Brick": H.forge_material("Brick", (.205, .067, .030, 1), .02, .88),
    "Coal": H.forge_material("Coal", (.018, .014, .012, 1), .08, .93),
    "Water": H.forge_material("Water", (.045, .10, .115, 1), .04, .12),
    "Oil": H.forge_material("Oil", (.105, .055, .018, 1), .02, .20),
})


def new_collection(name: str):
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


def add_extruded_outline(name, outline, z_bottom, z_top, material, collection, bevel=.025):
    count = len(outline)
    vertices = [(x, y, z_bottom) for x, y in outline] + [(x, y, z_top) for x, y in outline]
    faces = [tuple(reversed(range(count))), tuple(count + i for i in range(count))]
    for i in range(count):
        nxt = (i + 1) % count
        faces.append((i, nxt, count + nxt, count + i))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    H.apply_bevel(obj, bevel, 3)
    H.smooth_mesh(obj)
    return obj


def add_outline_loft(name, outline, levels, material, collection):
    count = len(outline)
    vertices = []
    for z, sx, sy in levels:
        vertices.extend((x * sx, y * sy, z) for x, y in outline)
    faces = []
    for ring in range(len(levels) - 1):
        base = ring * count
        nxt = (ring + 1) * count
        for i in range(count):
            j = (i + 1) % count
            faces.append((base + i, base + j, nxt + j, nxt + i))
    faces.append(tuple(reversed(range(count))))
    top = (len(levels) - 1) * count
    faces.append(tuple(top + i for i in range(count)))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    H.smooth_mesh(obj)
    return obj


def add_lathe_z(name, profile, material, collection, segments=64, cap=True):
    vertices = []
    faces = []
    for radius, z in profile:
        for i in range(segments):
            angle = i / segments * math.tau
            vertices.append((math.cos(angle) * radius, math.sin(angle) * radius, z))
    for ring in range(len(profile) - 1):
        for i in range(segments):
            nxt = (i + 1) % segments
            a = ring * segments + i
            b = ring * segments + nxt
            c = (ring + 1) * segments + nxt
            d = (ring + 1) * segments + i
            faces.append((a, b, c, d))
    if cap:
        faces.append(tuple(reversed(range(segments))))
        start = (len(profile) - 1) * segments
        faces.append(tuple(start + i for i in range(segments)))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    H.smooth_mesh(obj)
    return obj


def add_torus(name, location, major_radius, minor_radius, material, collection, major_segments=64, minor_segments=12, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_torus_add(
        major_radius=major_radius,
        minor_radius=minor_radius,
        major_segments=major_segments,
        minor_segments=minor_segments,
        location=location,
        rotation=rotation,
    )
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(MATERIALS[material])
    H.smooth_mesh(obj)
    H.move_to_collection(obj, collection)
    return obj


def add_arch_wedge(name, angle0, angle1, inner_radius, outer_radius, center_z, y_center, depth, material, collection):
    section = []
    for angle, radius in ((angle0, inner_radius), (angle1, inner_radius), (angle1, outer_radius), (angle0, outer_radius)):
        section.append((math.cos(angle) * radius, center_z + math.sin(angle) * radius))
    vertices = []
    for y in (y_center - depth * .5, y_center + depth * .5):
        vertices.extend((x, y, z) for x, z in section)
    faces = [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.data.materials.append(MATERIALS[material])
    H.apply_bevel(obj, .016, 3)
    H.smooth_mesh(obj)
    return obj


def add_sphere(name, location, scale, material, collection, segments=24, rings=12):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=rings, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    obj.data.materials.append(MATERIALS[material])
    H.apply_transform(obj)
    H.smooth_mesh(obj)
    H.move_to_collection(obj, collection)
    return obj


def rotated_cube(name, location, size, material, bevel, collection, rotation=(0, 0, 0)):
    obj = H.add_cube(name, location, size, material, bevel, collection)
    obj.rotation_euler = rotation
    H.apply_transform(obj)
    return obj


def build_workbench():
    collection = new_collection("Forge_Workbench")
    pieces = []

    # Individual seasoned planks, visible seams and under-top cleats make the
    # workbench read as joinery instead of one rounded CAD slab.
    plank_width = .445
    for index in range(10):
        y = -2.00 + index * plank_width
        z = .015 + (.012 if index % 3 == 0 else -.006 if index % 3 == 1 else 0)
        pieces.append(rotated_cube(
            f"BenchTopPlank{index:02d}",
            (0, y, z),
            (8.72, plank_width - .032, .25),
            "Wood" if index % 4 else "WoodDark",
            .035,
            collection,
            rotation=(0, 0, math.radians((index % 3 - 1) * .08)),
        ))
    for y in (-1.42, 1.42):
        pieces.append(H.add_cube(f"BenchTopCleat{y:+.0f}", (0, y, -.18), (8.15, .26, .22), "WoodDark", .035, collection))
        for x in (-3.45, -1.15, 1.15, 3.45):
            pieces.append(add_sphere(f"BenchPeg{y:+.0f}{x:+.1f}", (x, y, -.31), (.038, .038, .025), "Brass", collection, 14, 7))
    pieces.append(H.add_cube("BenchFrontApron", (0, -2.12, -.42), (8.36, .24, .82), "WoodDark", .055, collection))
    pieces.append(H.add_cube("BenchRearApron", (0, 2.12, -.42), (8.36, .24, .82), "WoodDark", .055, collection))
    pieces.append(H.add_cube("BenchLeftApron", (-4.15, 0, -.42), (.24, 4.02, .82), "WoodDark", .055, collection))
    pieces.append(H.add_cube("BenchRightApron", (4.15, 0, -.42), (.24, 4.02, .82), "WoodDark", .055, collection))

    for x in (-3.98, 3.98):
        for y in (-1.86, 1.86):
            x_splay = math.radians(-5 if x < 0 else 5)
            y_splay = math.radians(5 if y < 0 else -5)
            pieces.append(rotated_cube(f"BenchLeg{x:+.0f}{y:+.0f}", (x, y, -1.72), (.50, .50, 2.82), "WoodDark", .070, collection, rotation=(y_splay, x_splay, 0)))
            pieces.append(H.add_cube(f"BenchFoot{x:+.0f}{y:+.0f}", (x, y, -3.16), (.59, .59, .17), "WoodDark", .026, collection))
            pieces.append(add_torus(f"LegStrap{x:+.0f}{y:+.0f}", (x, y, -.58), .273, .018, "IronDark", collection, 32, 8))

    pieces.append(H.add_cube("BenchLowerFrontRail", (0, -1.86, -2.34), (7.60, .30, .34), "WoodDark", .055, collection))
    pieces.append(H.add_cube("BenchLowerRearRail", (0, 1.86, -2.34), (7.60, .30, .34), "WoodDark", .055, collection))
    pieces.append(H.add_cube("BenchLowerLeftRail", (-3.98, 0, -2.34), (.30, 3.48, .34), "WoodDark", .055, collection))
    pieces.append(H.add_cube("BenchLowerRightRail", (3.98, 0, -2.34), (.30, 3.48, .34), "WoodDark", .055, collection))
    pieces.append(H.add_curve_tube("BenchFrontBraceL", [(-3.70, -1.78, -2.28), (-1.90, -1.83, -1.36), (-.65, -1.86, -2.28)], .075, "Wood", collection))
    pieces.append(H.add_curve_tube("BenchFrontBraceR", [(3.70, -1.78, -2.28), (1.90, -1.83, -1.36), (.65, -1.86, -2.28)], .075, "Wood", collection))

    # Rear tool rack and brass rail are visible in the wide workshop camera.
    for x in (-4.02, 4.02):
        pieces.append(H.add_cube(f"RackPost{x:+.0f}", (x, 2.04, .70), (.28, .28, 1.52), "WoodDark", .045, collection))
    pieces.append(H.add_cube("RackBack", (0, 2.10, 1.24), (8.26, .20, .70), "WoodDark", .050, collection))
    pieces.append(H.add_cylinder("RackRail", (0, 1.94, .75), .045, 7.56, "IronDark", collection, 48, rotation=(0, math.pi / 2, 0)))
    for x in (-3.0, -1.5, 0, 1.5, 3.0):
        pieces.append(H.add_cylinder(f"RackHook{x:+.1f}", (x, 1.86, .59), .030, .34, "IronDark", collection, 24, rotation=(math.pi / 2, 0, 0)))

    # A compact forged vise is attached to the front-left corner and gives the
    # station a believable working function without affecting game hitboxes.
    pieces.append(H.add_cube("BenchViseBody", (-3.22, -2.40, .15), (.92, .48, .46), "IronDark", .075, collection))
    pieces.append(H.add_cube("BenchViseJawFixed", (-3.56, -2.48, .43), (.20, .42, .18), "Iron", .028, collection))
    pieces.append(H.add_cube("BenchViseJawSliding", (-2.92, -2.48, .43), (.20, .42, .18), "Iron", .028, collection))
    pieces.append(H.add_cylinder("BenchViseScrew", (-3.22, -2.76, .25), .055, .72, "IronDark", collection, 32, rotation=(math.pi / 2, 0, 0)))
    pieces.append(H.add_cylinder("BenchViseHandle", (-3.22, -3.10, .25), .040, .52, "Wood", collection, 24, rotation=(0, math.pi / 2, 0)))

    # Forged corner plates and rivets make the joinery read at gameplay distance.
    for x in (-4.32, 4.32):
        for y in (-2.08, 2.08):
            pieces.append(H.add_cube(f"CornerPlate{x:+.0f}{y:+.0f}", (x, y, -.03), (.24, .24, .30), "IronDark", .025, collection))
            pieces.append(add_sphere(f"CornerRivet{x:+.0f}{y:+.0f}", (x, y - math.copysign(.126, y), .03), (.045, .025, .045), "IronDark", collection, 16, 8))
    return H.join_asset("workbench", pieces, collection)


def build_hearth():
    collection = new_collection("Forge_Hearth")
    pieces = []
    # New construction: a heavy stone plinth carries a recessed octagonal iron
    # firepot instead of the previous flat fire-pan box.
    pieces.append(H.add_cube("HearthPlinthLower", (0, .04, -.14), (2.86, 1.96, .24), "Stone", .050, collection))
    pieces.append(H.add_cube("HearthPlinthUpper", (0, .02, .04), (2.58, 1.72, .20), "Stone", .038, collection))
    firepot_outline = [(-.86, -.56), (-.58, -.78), (.58, -.78), (.86, -.56), (.86, .46), (.58, .68), (-.58, .68), (-.86, .46)]
    pieces.append(add_outline_loft("HearthFirepot", firepot_outline, [(.12, 1.0, 1.0), (.25, .96, .96), (.45, .78, .76)], "IronDark", collection))
    pieces.append(add_outline_loft("HearthLining", [(.72*x, .72*y) for x, y in firepot_outline], [(.23, 1.0, 1.0), (.43, .82, .80)], "Stone", collection))

    # A real grate crosses the firepot and leaves visible gaps into the ash box.
    for index in range(-4, 5):
        pieces.append(H.add_cylinder(f"HearthGrate{index:+d}", (index * .13, -.01, .42), .026, 1.18, "Iron", collection, 24, rotation=(math.pi / 2, 0, 0)))
    pieces.append(add_lathe_z("CoalBed", [(0, .37), (.58, .39), (.70, .45), (.64, .53), (.42, .60)], "Coal", collection, 72))
    for index, (x, y, scale) in enumerate(((-.42, -.12, .12), (-.20, .20, .15), (.06, -.18, .14), (.31, .13, .13), (.46, -.21, .10), (-.08, .03, .18))):
        pieces.append(add_sphere(f"CoalChunk{index:02d}", (x, y, .57), (scale, scale * .72, scale * .60), "Coal", collection, 18, 9))

    # Alternating-bond piers and back wall form a deep chamber around the fire.
    brick = (.40, .29, .19)
    for course in range(7):
        z = .28 + course * .185
        stagger = .15 if course % 2 else 0
        for side in (-1, 1):
            for depth in range(5):
                y = -.58 + depth * .30
                pieces.append(H.add_cube(f"HearthPier{course}{side:+d}{depth}", (side * 1.08, y + stagger * (depth % 2 - .5), z), (brick[0], brick[1], brick[2]), "Brick", .016, collection))
        for column in range(6):
            x = -.98 + column * .39 + (course % 2) * .195
            if x < 1.12:
                pieces.append(H.add_cube(f"HearthBack{course}{column}", (x, .70, z), (.37, .23, .18), "Brick", .016, collection))

    # Tapered voussoirs make a proper front arch with a blackened inner ring.
    arch_count = 15
    for index in range(arch_count):
        angle0 = index / arch_count * math.pi + math.radians(.55)
        angle1 = (index + 1) / arch_count * math.pi - math.radians(.55)
        pieces.append(add_arch_wedge(f"HearthVoussoir{index:02d}", angle0, angle1, .63, .91, .96, -.72, .34, "Brick", collection))
    pieces.append(add_arch_wedge("HearthInnerSoot", .04, math.pi - .04, .58, .64, .96, -.735, .355, "Coal", collection))
    pieces.append(H.add_cube("HearthCapstone", (0, .02, 1.72), (2.72, 1.88, .28), "Stone", .055, collection))

    # Bellows air enters through a flared side tuyere; ash falls into a drawer.
    pieces.append(H.add_cylinder("HearthTuyerePipe", (-1.34, .18, .38), .105, .58, "IronDark", collection, 56, rotation=(0, math.pi / 2, 0)))
    pieces.append(add_torus("HearthTuyereFlange", (-1.07, .18, .38), .16, .035, "Brass", collection, 48, 12, rotation=(0, math.pi / 2, 0)))
    pieces.append(H.add_cube("HearthAshDrawer", (0, -.89, .22), (.84, .10, .42), "IronDark", .035, collection))
    pieces.append(H.add_curve_tube("HearthAshHandle", [(-.18, -.96, .23), (0, -1.04, .15), (.18, -.96, .23)], .030, "Brass", collection))
    for x in (-.34, .34):
        pieces.append(add_sphere(f"HearthAshRivet{x:+.0f}", (x, -.955, .30), (.032, .020, .032), "Brass", collection, 16, 8))
    return H.join_asset("hearth", pieces, collection)


def build_bellows():
    collection = new_collection("Forge_Bellows")
    pieces = []
    outline = [(-.84, -.28), (-.64, -.39), (.25, -.38), (.72, -.18), (.81, 0), (.72, .18), (.25, .38), (-.64, .39), (-.84, .28)]
    pieces.append(add_extruded_outline("BellowsLowerBoard", outline, -.14, -.045, "Wood", collection, .035))
    pieces.append(add_extruded_outline("BellowsUpperBoard", outline, .31, .42, "Wood", collection, .035))
    pieces.append(add_outline_loft("BellowsLeather", outline, [(-.055, .92, .92), (.02, 1, 1), (.075, .90, .92), (.14, 1, 1), (.205, .89, .91), (.27, 1, 1), (.32, .92, .92)], "Leather", collection))

    for z in (.015, .135, .255):
        pieces.append(add_outline_loft(f"BellowsPleat{z:.2f}", outline, [(z - .018, .92, .92), (z, 1, 1), (z + .018, .92, .92)], "Leather", collection))
    for x in (-.58, .47):
        pieces.append(H.add_cylinder(f"BellowsHinge{x:+.0f}", (x, 0, .37), .055, .55, "IronDark", collection, 36, rotation=(math.pi / 2, 0, 0)))
        for y in (-.29, .29):
            pieces.append(add_sphere(f"BellowsPin{x:+.0f}{y:+.0f}", (x, y, .37), (.070, .035, .070), "Brass", collection, 18, 8))

    pieces.append(H.add_cube("BellowsHandle", (-.10, 0, .49), (1.18, .15, .14), "Wood", .032, collection))
    pieces.append(H.add_cylinder("BellowsNozzle", (1.12, 0, .12), .11, .76, "Brass", collection, 48, rotation=(0, math.pi / 2, 0)))
    pieces.append(add_torus("BellowsNozzleCollar", (.76, 0, .12), .145, .035, "IronDark", collection, 40, 10, rotation=(0, math.pi / 2, 0)))
    pieces.append(H.add_cylinder("BellowsNozzleTip", (1.48, 0, .12), .07, .18, "IronDark", collection, 40, rotation=(0, math.pi / 2, 0)))
    return H.join_asset("bellows", pieces, collection)


def build_quench_vat(name: str, oil: bool):
    collection = new_collection("Forge_" + name.replace("-", "_").title())
    pieces = []
    vessel = "Brass" if oil else "IronDark"
    liquid = "Oil" if oil else "Water"
    # Closed wall section: outer wall rises, rim rolls inward, inner wall descends.
    profile = [
        (.38, -.46), (.45, -.44), (.49, -.36), (.51, .28), (.54, .40),
        (.50, .46), (.445, .40), (.43, .29), (.42, -.31), (.37, -.39),
    ]
    pieces.append(add_lathe_z("VatBody", profile, vessel, collection, 72))
    for z, radius, thickness in ((-.29, .49, .025), (.08, .515, .024), (.39, .525, .032)):
        pieces.append(add_torus(f"VatBand{z:+.0f}", (0, 0, z), radius, thickness, "IronDark", collection, 64, 10))
    pieces.append(add_lathe_z("VatLiquid", [(0, .34), (.425, .34), (.425, .355), (0, .355)], liquid, collection, 72))

    # Forged side handles and attachment plates.
    for side in (-1, 1):
        x = side * .53
        pieces.append(H.add_cube(f"VatHandlePlate{side:+d}", (side * .50, 0, .12), (.08, .30, .28), vessel, .025, collection))
        pieces.append(H.add_curve_tube(f"VatHandle{side:+d}", [(side * .50, -.11, .22), (side * .59, -.18, .22), (side * .63, 0, .15), (side * .59, .18, .22), (side * .50, .11, .22)], .030, "IronDark", collection))
        for y in (-.10, .10):
            pieces.append(add_sphere(f"VatRivet{side:+d}{y:+.0f}", (side * .54, y, .13), (.025, .025, .025), "Brass", collection, 14, 7))
    return H.join_asset(name, pieces, collection)


def grinder_parts(collection):
    stand = []
    wheel = []
    # Two timber skids, splayed trestles and pegged cross rails replace the old
    # compact box stand. The silhouette remains readable beside the workbench.
    for y in (-.46, .46):
        stand.append(H.add_cube(f"GrinderSkid{y:+.0f}", (0, y, -.68), (1.72, .24, .20), "WoodDark", .040, collection))
        for x in (-.54, .54):
            stand.append(rotated_cube(f"GrinderTrestle{x:+.0f}{y:+.0f}", (x, y, -.05), (.22, .22, 1.34), "Wood", .040, collection, rotation=(0, math.radians(-10 if x < 0 else 10), 0)))
        stand.append(H.add_cube(f"GrinderTopRail{y:+.0f}", (0, y, .38), (1.22, .28, .22), "WoodDark", .038, collection))
        for x in (-.48, .48):
            stand.append(add_sphere(f"GrinderPeg{x:+.0f}{y:+.0f}", (x, y - math.copysign(.15, y), .39), (.035, .022, .035), "Brass", collection, 14, 7))
    stand.append(H.add_cube("GrinderCrossStretcher", (0, 0, -.38), (.26, 1.14, .22), "WoodDark", .035, collection))

    # Bronze pillow blocks support a continuous forged axle.
    stand.append(H.add_cylinder("GrinderAxle", (0, 0, .46), .070, 1.52, "Iron", collection, 56, rotation=(math.pi / 2, 0, 0)))
    for y in (-.55, .55):
        stand.append(H.add_cube(f"GrinderBearingPlinth{y:+.0f}", (0, y, .46), (.42, .26, .25), "IronDark", .048, collection))
        stand.append(add_torus(f"GrinderBearing{y:+.0f}", (0, y - math.copysign(.14, y), .46), .115, .030, "Brass", collection, 48, 12, rotation=(math.pi / 2, 0, 0)))

    # Foot pedal, crank and connecting rod form one visible mechanism.
    stand.append(rotated_cube("GrinderPedal", (-.18, -.02, -.78), (.92, .34, .10), "Wood", .025, collection, rotation=(0, math.radians(-6), 0)))
    stand.append(H.add_cylinder("GrinderPedalPivot", (-.48, 0, -.72), .045, .74, "IronDark", collection, 32, rotation=(math.pi / 2, 0, 0)))
    stand.append(H.add_curve_tube("GrinderLinkage", [(.18, .02, -.72), (.43, .22, -.28), (.28, .66, .26)], .038, "IronDark", collection))
    stand.append(rotated_cube("GrinderCrank", (.19, .67, .31), (.075, .075, .42), "Iron", .018, collection, rotation=(0, math.radians(-54), 0)))
    stand.append(H.add_cylinder("GrinderCrankGrip", (.38, .73, .16), .052, .28, "Wood", collection, 36, rotation=(math.pi / 2, 0, 0)))

    # Tool rest and a shallow water trough sit immediately below the stone.
    stand.append(H.add_cube("GrinderToolRestPost", (.58, -.43, .18), (.12, .18, .50), "IronDark", .020, collection))
    stand.append(H.add_cube("GrinderToolRest", (.56, -.44, .50), (.76, .24, .10), "Iron", .018, collection))
    trough_profile = [(.28, -.20), (.37, -.15), (.40, .05), (.36, .18), (.28, .21), (.25, .08)]
    stand.append(add_lathe_z("GrinderWaterTrough", trough_profile, "IronDark", collection, 64))
    trough = stand[-1]
    trough.scale = (1.0, .72, .42)
    trough.location = (0, 0, -.02)
    H.apply_transform(trough)

    # The animated node is a dressed sandstone wheel with recessed iron hubs,
    # rounded rim and shallow wear channels on both faces.
    stone = add_lathe_z("GrinderStoneCore", [
        (.54, -.18), (.585, -.16), (.625, -.105), (.645, -.045),
        (.645, .045), (.625, .105), (.585, .16), (.54, .18),
    ], "Stone", collection, 256)
    stone.rotation_euler[0] = math.pi / 2
    stone.location.z = .46
    H.apply_transform(stone)
    wheel.append(stone)
    for y in (-.178, .178):
        wheel.append(H.add_cylinder(f"GrinderHub{y:+.0f}", (0, y, .46), .18, .060, "IronDark", collection, 56, rotation=(math.pi / 2, 0, 0)))
        wheel.append(add_torus(f"GrinderHubRing{y:+.0f}", (0, y, .46), .19, .026, "Brass", collection, 72, 12, rotation=(math.pi / 2, 0, 0)))
    return stand, wheel


def build_grinder_assets():
    source_collection = new_collection("Forge_Grinder_Source")
    stand_parts, wheel_parts = grinder_parts(source_collection)
    stand = H.join_asset("grinder-stand", stand_parts, source_collection)
    wheel = H.join_asset("grinder-wheel", wheel_parts, source_collection)

    combined_collection = new_collection("Forge_Grinder_Combined")
    combined_parts = []
    for source in (stand, wheel):
        copy = source.copy()
        copy.data = source.data.copy()
        combined_collection.objects.link(copy)
        combined_parts.append(copy)
    combined = H.join_asset("grinder", combined_parts, combined_collection)
    return stand, wheel, combined


def export_asset(obj, filename: str):
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


def mesh_report(obj):
    obj.data.calc_loop_triangles()
    points = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
    return {
        "vertices": len(obj.data.vertices),
        "triangles": len(obj.data.loop_triangles),
        "materials": [slot.material.name for slot in obj.material_slots if slot.material],
        "bounds": {
            "min": [min(point[i] for point in points) for i in range(3)],
            "max": [max(point[i] for point in points) for i in range(3)],
        },
    }


def add_preview(assets):
    preview = new_collection("PreviewOnly")
    placements = {
        "workbench": ((0, 0, 0), (0, 0, 0)),
        "hearth": ((-3.0, -.25, .20), (0, 0, 0)),
        "bellows": ((-3.25, -1.42, .18), (0, 0, math.radians(18))),
        "quench-water": ((2.05, .25, .28), (0, 0, 0)),
        "quench-oil": ((3.24, .28, .22), (0, 0, 0)),
        "grinder": ((2.70, -1.30, .33), (0, 0, math.radians(-10))),
    }
    for source in assets.values():
        for owner in source.users_collection:
            owner.hide_render = True
    for name, source in assets.items():
        if name not in placements:
            continue
        copy = source.copy()
        copy.data = source.data.copy()
        preview.objects.link(copy)
        copy.location, copy.rotation_euler = placements[name]

    bpy.ops.object.camera_add(location=(8.4, -12.8, 8.0))
    camera = bpy.context.object
    camera.data.lens = 54
    H.look_at(camera, (0, 0, -.55))
    bpy.context.scene.camera = camera

    bpy.ops.object.light_add(type="AREA", location=(-4.5, -4.5, 6.8))
    key = bpy.context.object
    key.data.energy = 1350
    key.data.shape = "DISK"
    key.data.size = 4.5
    key.data.color = (1.0, .34, .10)
    H.look_at(key, (-1.2, 0, 0))
    bpy.ops.object.light_add(type="AREA", location=(4.8, -2.0, 5.4))
    fill = bpy.context.object
    fill.data.energy = 900
    fill.data.size = 4.0
    fill.data.color = (.34, .50, 1.0)
    H.look_at(fill, (1.4, 0, -.2))
    bpy.ops.object.light_add(type="AREA", location=(0, 5.2, 7.5))
    rim = bpy.context.object
    rim.data.energy = 1100
    rim.data.size = 3.0
    rim.data.color = (1.0, .72, .38)
    H.look_at(rim, (0, 0, .2))

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = 1440
    scene.render.resolution_y = 900
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = str(PREVIEW_PATH)
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (.020, .026, .038, 1.0)
    background.inputs["Strength"].default_value = .28
    bpy.ops.object.light_add(type="AREA", location=(0, -6.2, 4.5))
    fill = bpy.context.object
    fill.name = "ReadableMaterialFill"
    fill.data.energy = 1550
    fill.data.size = 5.2
    fill.data.color = (.68, .78, 1.0)
    H.look_at(fill, (0, 0, -.4))
    scene.view_settings.look = "AgX - Medium High Contrast"
    bpy.ops.render.render(write_still=True)


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    PREVIEW_PATH.parent.mkdir(parents=True, exist_ok=True)
    H.reset_scene()
    assets = {
        "workbench": build_workbench(),
        "hearth": build_hearth(),
        "bellows": build_bellows(),
        "quench-water": build_quench_vat("quench-water", False),
        "quench-oil": build_quench_vat("quench-oil", True),
    }
    stand, wheel, combined = build_grinder_assets()
    assets.update({"grinder-stand": stand, "grinder-wheel": wheel, "grinder": combined})
    for name, obj in assets.items():
        export_asset(obj, name + ".glb")
    bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE_BLEND))
    report = {name: mesh_report(obj) for name, obj in assets.items()}
    add_preview(assets)
    print("FORGE_WORKSHOP_BUILD=" + json.dumps(report, separators=(",", ":")))


if __name__ == "__main__":
    main()
