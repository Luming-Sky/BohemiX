"""Prepare a non-destructive Blender scene for forge grinding-pose calibration.

Run this from Blender's Text Editor after opening
Assets/Source/forge-weapons.blend. Move only CAL_GrinderWheel and
CAL_BeardedAxe. The contact markers are intentionally separate controls so the
final exporter can validate the exact edge-to-wheel relationship.
"""

from pathlib import Path

import bpy
from mathutils import Vector


CALIBRATION_COLLECTION = "GRINDING_CALIBRATION"
WHEEL_ROOT = "CAL_GrinderWheel"
AXE_ROOT = "CAL_BeardedAxe"
WHEEL_CONTACT = "CAL_WheelContact"
AXE_CONTACT = "CAL_AxeEdgeContact"


def find_models_directory():
    """Find Assets/Models from the opened Forge source blend file."""
    blend_path = Path(bpy.data.filepath).resolve()
    for directory in (blend_path.parent, *blend_path.parents):
        candidate = directory / "Models"
        if candidate.is_dir() and (candidate / "grinder-wheel.glb").is_file():
            return candidate
    raise RuntimeError(
        "Open Assets/Source/forge-weapons.blend before running this script. "
        "Assets/Models could not be located from the current blend file."
    )


def ensure_collection():
    collection = bpy.data.collections.get(CALIBRATION_COLLECTION)
    if collection is None:
        collection = bpy.data.collections.new(CALIBRATION_COLLECTION)
        bpy.context.scene.collection.children.link(collection)
    return collection


def remove_previous_calibration(collection):
    for obj in list(collection.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def create_empty(collection, name, location=(0.0, 0.0, 0.0), display_type="PLAIN_AXES", size=0.18):
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_type = display_type
    obj.empty_display_size = size
    obj.location = location
    collection.objects.link(obj)
    return obj


def import_asset(collection, path, root_name, root_location, root_scale):
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    imported = [obj for obj in bpy.context.scene.objects if obj not in before]
    if not imported:
        raise RuntimeError(f"Blender did not import {path.name}.")

    root = create_empty(collection, root_name, root_location, "ARROWS", .28)
    root.scale = root_scale
    root["forge_calibration_role"] = "wheel" if root_name == WHEEL_ROOT else "weapon"

    for obj in imported:
        for owner in list(obj.users_collection):
            owner.objects.unlink(obj)
        collection.objects.link(obj)
        obj.parent = root
        # Keep each imported mesh rigid while the root remains the user control.
        obj.lock_location = (True, True, True)
        obj.lock_rotation = (True, True, True)
        obj.lock_scale = (True, True, True)
    return root


def add_marker(collection, name, parent, local_location, color):
    marker = create_empty(collection, name, local_location, "SPHERE", .10)
    marker.color = (*color, 1.0)
    marker.parent = parent
    marker.matrix_parent_inverse = parent.matrix_world.inverted()
    marker["forge_calibration_role"] = "contact"
    return marker


def add_axis_marker(collection, name, parent, local_location, direction, color):
    marker = create_empty(collection, name, local_location, "SINGLE_ARROW", .34)
    marker.color = (*color, 1.0)
    marker.parent = parent
    marker.matrix_parent_inverse = parent.matrix_world.inverted()
    marker.rotation_mode = "QUATERNION"
    marker.rotation_quaternion = direction.to_track_quat("Z", "Y")
    marker["forge_calibration_role"] = "direction"
    return marker


def main():
    models = find_models_directory()
    collection = ensure_collection()
    remove_previous_calibration(collection)

    wheel = import_asset(
        collection,
        models / "grinder-wheel.glb",
        WHEEL_ROOT,
        (0.0, 0.0, 0.0),
        (1.0, 1.0, 1.0),
    )
    # The wheel mesh is offset upward in its GLB: its axle origin is (0, 0, .46),
    # Y is the axle, and the dressed crown contact is (0, 0, 1.105).
    wheel_contact = add_marker(collection, WHEEL_CONTACT, wheel, (0.0, 0.0, 1.105), (1.0, .38, .08))
    add_axis_marker(collection, "CAL_WheelAxis", wheel, (0.0, 0.0, .46), Vector((0.0, 1.0, 0.0)), (.15, .55, 1.0))

    axe = import_asset(
        collection,
        models / "weapon-bearded-axe.glb",
        AXE_ROOT,
        (0.0, 1.105, 0.0),
        (.62, .62, .62),
    )
    axe_contact = add_marker(collection, AXE_CONTACT, axe, (-1.275, .08, 0.0), (1.0, .82, .12))
    add_axis_marker(collection, "CAL_AxeEdgeDirection", axe, (-1.275, .08, 0.0), Vector((0.0, 1.0, 0.0)), (.18, 1.0, .42))

    wheel_contact["calibration_note"] = "Place this marker at the wheel crown contact point."
    axe_contact["calibration_note"] = "Place this marker on the middle of the cutting edge."
    axe["calibration_note"] = "Rotate this root until the cutting edge is horizontal across the wheel."

    bpy.context.view_layer.objects.active = axe
    axe.select_set(True)
    print("Grinding calibration ready. Adjust CAL_GrinderWheel, CAL_BeardedAxe, and the two contact markers; then save the blend and tell Codex to export.")


main()
