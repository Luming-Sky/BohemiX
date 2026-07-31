from pathlib import Path
import json
import math
import bpy
from mathutils import Vector
from mathutils.geometry import tessellate_polygon
import bmesh


SCRIPT_FILE = Path(globals().get(
    "__file__",
    Path.cwd() / "src" / "BohemiX.Modules.Forge" / "Assets" / "Source" / "Scripts" / "build_weapons.py")).resolve()
ASSETS = SCRIPT_FILE.parents[2]
MODELS = ASSETS / "Models"
THUMBNAILS = ASSETS / "Thumbnails"
SOURCE = ASSETS / "Source"
MODELS.mkdir(parents=True, exist_ok=True)
THUMBNAILS.mkdir(parents=True, exist_ok=True)
QA_PATH = SOURCE / "forge-asset-qa.json"
TEXTURE_SIZE = 1024


def _noise(seed, x, y):
    # Deterministic value noise keeps generated assets reproducible across builds.
    value = math.sin((x + seed * 17.0) * 12.9898 + (y + seed * 31.0) * 78.233) * 43758.5453
    return value - math.floor(value)


def pbr_image(name, kind, color, metallic, roughness, seed):
    image = bpy.data.images.get(name)
    if image is None or image.size[0] != TEXTURE_SIZE:
        image = bpy.data.images.new(name, width=TEXTURE_SIZE, height=TEXTURE_SIZE, alpha=True)
    pixels = [0.0] * (TEXTURE_SIZE * TEXTURE_SIZE * 4)
    for y in range(TEXTURE_SIZE):
        for x in range(TEXTURE_SIZE):
            u = x / TEXTURE_SIZE
            v = y / TEXTURE_SIZE
            broad = _noise(seed, x / 71.0, y / 83.0)
            medium = _noise(seed + 7, x / 23.0, y / 27.0)
            fine = _noise(seed + 13, x / 5.4, y / 6.1)
            is_wood = name.lower().startswith("wood")
            is_leather = name.lower().startswith("leather")
            forged = .5 + .5 * math.sin(v * math.tau * 21.0 + math.sin(u * math.tau * 3.0) * 2.4)
            wood_grain = .5 + .5 * math.sin(v * math.tau * 12.0 + math.sin(u * math.tau * 1.8) * 1.5)
            scratch = max(0.0, 1.0 - abs(math.sin((u * 27.0 + v * 1.7 + seed) * math.pi))) ** 34
            pit = 1.0 if _noise(seed + 37, x / 2.8, y / 3.2) > .988 else 0.0
            i = (y * TEXTURE_SIZE + x) * 4
            if kind == "base":
                if is_wood:
                    variation = .84 + broad * .07 + wood_grain * .08 + medium * .025
                elif is_leather:
                    variation = .82 + broad * .09 + medium * .045 - pit * .035
                else:
                    variation = .82 + broad * .09 + medium * .035 + forged * .035 + scratch * .055 - pit * .12
                pixels[i:i + 4] = [min(1.0, c * variation) for c in color] + [1.0]
            elif kind == "normal":
                if is_wood:
                    pixels[i:i + 4] = [0.5 + (wood_grain - .5) * .035, 0.5 + (fine - .5) * .025, 1.0, 1.0]
                elif is_leather:
                    pixels[i:i + 4] = [0.5 + (medium - .5) * .035, 0.5 + (fine - .5) * .035, 1.0, 1.0]
                else:
                    pixels[i:i + 4] = [0.5 + (forged - 0.5) * .065 + scratch * .025, 0.5 + (fine - 0.5) * .045 - pit * .035, 1.0, 1.0]
            else:
                ao = max(.62, .96 - pit * .28)
                pixels[i:i + 4] = [ao, max(.04, min(1.0, roughness * (.87 + broad * .15 + pit * .12 - scratch * .10))), metallic, 1.0]
    image.pixels = pixels
    image.pack()
    image.colorspace_settings.name = "Non-Color" if kind != "base" else "sRGB"
    return image


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials, bpy.data.cameras, bpy.data.lights):
        for block in list(datablocks):
            if block.users == 0:
                datablocks.remove(block)


def material(name, color, metallic, roughness):
    value = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    value.diffuse_color = (*color, 1.0)
    value.use_nodes = True
    bsdf = value.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    # Use actual embedded images so GLB consumers receive a PBR texture set.
    nodes = value.node_tree.nodes
    links = value.node_tree.links
    for node in list(nodes):
        if node.name.startswith("ForgePbr"):
            nodes.remove(node)
    seed = sum((index + 1) * ord(char) for index, char in enumerate(name))
    base = nodes.new("ShaderNodeTexImage")
    base.name = "ForgePbr_BaseColor"
    base.image = pbr_image(f"{name}_BaseColor", "base", color, metallic, roughness, seed)
    links.new(base.outputs["Color"], bsdf.inputs["Base Color"])
    normal = nodes.new("ShaderNodeTexImage")
    normal.name = "ForgePbr_Normal"
    normal.image = pbr_image(f"{name}_Normal", "normal", color, metallic, roughness, seed + 19)
    normal.image.colorspace_settings.name = "Non-Color"
    normal_map = nodes.new("ShaderNodeNormalMap")
    normal_map.name = "ForgePbr_NormalMap"
    normal_map.inputs["Strength"].default_value = (
        .055 if name == "WorkpieceEdge" else
        .085 if name == "Workpiece" else
        .10 if metallic > .5 else
        .075 if name == "Wood" else
        .09)
    links.new(normal.outputs["Color"], normal_map.inputs["Color"])
    links.new(normal_map.outputs["Normal"], bsdf.inputs["Normal"])
    orm = nodes.new("ShaderNodeTexImage")
    orm.name = "ForgePbr_MetallicRoughnessAO"
    orm.image = pbr_image(f"{name}_ORM", "orm", color, metallic, roughness, seed + 41)
    orm.image.colorspace_settings.name = "Non-Color"
    split = nodes.new("ShaderNodeSeparateColor")
    split.name = "ForgePbr_ORMChannels"
    split.mode = "RGB"
    links.new(orm.outputs["Color"], split.inputs["Color"])
    links.new(split.outputs["Green"], bsdf.inputs["Roughness"])
    links.new(split.outputs["Blue"], bsdf.inputs["Metallic"])
    return value


def finish_mesh(obj, bevel=0.018, segments=4):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    if bevel > 0:
        bevel_modifier = obj.modifiers.new("Forged edge bevel", "BEVEL")
        bevel_modifier.width = bevel
        bevel_modifier.segments = segments
        bevel_modifier.limit_method = "ANGLE"
        bpy.ops.object.modifier_apply(modifier=bevel_modifier.name)
    weighted = obj.modifiers.new("Forge weighted normals", "WEIGHTED_NORMAL")
    weighted.keep_sharp = True
    weighted.weight = 50
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=weighted.name)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    obj.data.set_sharp_from_angle(angle=math.radians(48))
    semantic_uv(obj)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.quads_convert_to_tris(quad_method="BEAUTY", ngon_method="BEAUTY")
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.select_set(False)
    return obj


def semantic_uv(obj, world_scale: float = .68):
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


def extruded_runtime_polygon(name, points_xz, depth, mat, bevel=0.018):
    # Blender exports (x, y, z) to glTF (x, z, -y). Input points are runtime X/Z.
    count = len(points_xz)
    vertices = []
    for height in (-depth * 0.5, depth * 0.5):
        vertices.extend((x, -z, height) for x, z in points_xz)
    faces = [tuple(range(count - 1, -1, -1)), tuple(range(count, count * 2))]
    for index in range(count):
        following = (index + 1) % count
        faces.append((index, following, count + following, count + index))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    return finish_mesh(obj, bevel, 5)


def double_edged_blade(name, sections, depth, mat, edge_mat, fuller=0.0):
    """Build a welded blade with a thick medial ridge and two physical cutting bevels."""
    sections = densify_sections(sections, 10)
    lane_fractions = (0.0, 0.10, 0.22, 0.36, 0.5, 0.64, 0.78, 0.90, 1.0)
    vertices = []
    max_width = max(upper - lower for _, lower, upper, _ in sections)
    for x, lower, upper, sharpness in sections:
        width = max(0.002, upper - lower)
        tip_scale = 1.0 if sharpness < 0.05 else 0.28 + 0.72 * math.sqrt(min(1.0, width / max_width))
        ridge_half = depth * 0.5 * tip_scale
        sharpened = (
            max(0.0035, depth * 0.045),
            ridge_half * 0.35,
            ridge_half * 0.66,
            ridge_half * (0.86 - fuller * .22),
            ridge_half,
            ridge_half * (0.86 - fuller * .22),
            ridge_half * 0.66,
            ridge_half * 0.35,
            max(0.0035, depth * 0.045),
        )
        blunt_half = ridge_half * 0.88
        half_depths = tuple(blunt_half + (value - blunt_half) * sharpness for value in sharpened)

        for side in (-1.0, 1.0):
            for fraction, half_depth in zip(lane_fractions, half_depths):
                z = lower + (upper - lower) * fraction
                vertices.append((x, -z, side * half_depth))

    lane_count = len(lane_fractions)
    stride = lane_count * 2
    faces = []
    edge_faces = set()
    for section in range(len(sections) - 1):
        current = section * stride
        following = (section + 1) * stride
        for lane in range(lane_count - 1):
            # Negative-thickness and positive-thickness blade surfaces.
            first_face = len(faces)
            faces.append((current + lane + 1, following + lane + 1, following + lane, current + lane))
            faces.append((current + lane_count + lane, following + lane_count + lane,
                          following + lane_count + lane + 1, current + lane_count + lane + 1))
            if lane in (0, lane_count - 2): edge_faces.update((first_face, first_face + 1))

        # The two cutting edges join both blade faces without a fake perimeter rim.
        faces.append((current, following, following + lane_count, current + lane_count))
        upper = lane_count - 1
        faces.append((current + upper + lane_count, following + upper + lane_count,
                      following + upper, current + upper))
        edge_faces.add(len(faces) - 2)
        edge_faces.add(len(faces) - 1)

    first = 0
    last = (len(sections) - 1) * stride
    for lane in range(lane_count - 1):
        faces.append((first + lane, first + lane + 1,
                      first + lane_count + lane + 1, first + lane_count + lane))
        faces.append((last + lane_count + lane, last + lane_count + lane + 1,
                      last + lane + 1, last + lane))

    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    obj.data.materials.append(edge_mat)
    for index, polygon in enumerate(obj.data.polygons):
        polygon.material_index = 1 if index in edge_faces else 0
    return finish_mesh(obj, 0, 1)


def densify_sections(sections, subdivisions):
    """Preserve authored shoulders while making the blade taper continuous."""
    dense = []
    for index in range(len(sections) - 1):
        first = sections[index]
        second = sections[index + 1]
        for step in range(subdivisions):
            t = step / subdivisions
            eased = t * t * (3.0 - 2.0 * t)
            dense.append(tuple(first[channel] + (second[channel] - first[channel]) * eased for channel in range(4)))
    dense.append(sections[-1])
    return dense


def boolean_eye(target, x, z_runtime, radius_x, radius_z, depth):
    bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=1.0, depth=depth, location=(x, -z_runtime, 0))
    cutter = bpy.context.object
    cutter.scale = (radius_x, radius_z, 1.0)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bpy.context.view_layer.objects.active = target
    modifier = target.modifiers.new("Forged eye", "BOOLEAN")
    modifier.operation = "DIFFERENCE"
    modifier.solver = "EXACT"
    modifier.object = cutter
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    bpy.data.objects.remove(cutter, do_unlink=True)
    for polygon in target.data.polygons:
        polygon.use_smooth = True
    semantic_uv(target)


def single_edged_axe_head(name, points_xz, shoulder_points, depth, mat, edge_mat):
    """Create one closed axe volume with a continuous lower beard wedge."""
    count = len(points_xz)
    edge_count = len(shoulder_points)
    if edge_count < 2 or edge_count >= count:
        raise ValueError("An axe edge needs at least two points and must leave a body boundary.")

    edge_half = max(0.005, depth * 0.045)
    body_half = depth * 0.5
    vertices = []

    def add_point(point, side, half_depth):
        vertices.append((point[0], -point[1], side * half_depth))
        return len(vertices) - 1

    def cheek_half(point):
        distance = math.sqrt(((point[0] - .56) / 1.15) ** 2 + ((point[1] - .04) / .82) ** 2)
        return body_half * (.68 + .32 * max(0.0, 1.0 - min(1.0, distance)))

    body_points = points_xz[edge_count:]
    shoulder_depths = [cheek_half(point) for point in shoulder_points]
    # Keep each transition cap coplanar with the neighbouring cheek.  A tiny
    # depth mismatch here creates an isolated specular triangle even though
    # the topology is closed.
    shoulder_depths[0] = cheek_half(body_points[-1])
    shoulder_depths[-1] = cheek_half(body_points[0])

    # A real forged bit does not terminate as a thin wedge against a thick
    # cheek.  It progressively gains thickness at the toe and heel.  Blend
    # three edge stations into the cheek depth so reflections and normals
    # remain continuous instead of producing a dark triangular end cap.
    edge_depths = []
    for index in range(edge_count):
        distance_from_end = min(index, edge_count - 1 - index)
        blend = min(1.0, distance_from_end / 3.0)
        blend = blend * blend * (3.0 - 2.0 * blend)
        cheek_depth = shoulder_depths[index]
        edge_depths.append(cheek_depth * (1.0 - blend) + edge_half * blend)

    edge_back = [add_point(point, -1.0, edge_depths[index])
                 for index, point in enumerate(points_xz[:edge_count])]
    edge_front = [add_point(point, 1.0, edge_depths[index])
                  for index, point in enumerate(points_xz[:edge_count])]

    shoulder_back = [add_point(point, -1.0, shoulder_depths[index])
                     for index, point in enumerate(shoulder_points)]
    shoulder_front = [add_point(point, 1.0, shoulder_depths[index])
                      for index, point in enumerate(shoulder_points)]
    body_back = [add_point(point, -1.0, cheek_half(point)) for point in body_points]
    body_front = [add_point(point, 1.0, cheek_half(point)) for point in body_points]

    main_back = tuple([edge_back[0]] + shoulder_back + [edge_back[-1]] + body_back)
    main_front = tuple([edge_front[0]] + shoulder_front + [edge_front[-1]] + body_front)
    # These loops are concave around the beard and poll.  Passing them as
    # n-gons lets Blender choose a diagonal that can cross the concavity,
    # producing the black triangular slivers seen in the rendered axe.  Use
    # Blender's polygon tessellator explicitly and keep every triangle in the
    # original winding so the front/back surfaces remain closed and stable.
    faces = []
    def append_tessellated(loop, reverse=False):
        points = [Vector(vertices[index]) for index in loop]
        for triangle in tessellate_polygon([[point for point in points]]):
            indices = []
            for point in triangle:
                if isinstance(point, int):
                    indices.append(loop[point])
                else:
                    indices.append(loop[points.index(point)])
            if reverse:
                indices.reverse()
            faces.append(tuple(indices))
    append_tessellated(main_back, reverse=True)
    append_tessellated(main_front)
    edge_faces = set()

    for index in range(edge_count - 1):
        face_index = len(faces)
        faces.append((edge_back[index], edge_back[index + 1], shoulder_back[index + 1], shoulder_back[index]))
        faces.append((edge_front[index], shoulder_front[index], shoulder_front[index + 1], edge_front[index + 1]))
        edge_faces.update((face_index, face_index + 1))

    # Close the external perimeter. The first edge path is the sharpened cutting edge;
    # the remaining path wraps the poll and top of the head.
    perimeter_back = edge_back + body_back
    perimeter_front = edge_front + body_front
    for index in range(len(perimeter_back)):
        following = (index + 1) % len(perimeter_back)
        face_index = len(faces)
        faces.append((perimeter_back[index], perimeter_back[following],
                      perimeter_front[following], perimeter_front[index]))
        if index < edge_count - 1:
            edge_faces.add(face_index)

    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    obj.data.materials.append(edge_mat)
    for index, polygon in enumerate(obj.data.polygons):
        polygon.material_index = 1 if index in edge_faces else 0
    # The wedge already supplies the visible cutting-edge radius.  A broad
    # modifier bevel at the tiny toe/heel caps folds over itself and reads as
    # a black notch, so keep only a sub-millimetre manufacturing chamfer.
    return finish_mesh(obj, 0.0008, 2)


def cylinder(name, location, radius, depth, mat, vertices=40, along_x=True):
    rotation = (0, math.pi * 0.5, 0) if along_x else (0, 0, 0)
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth, location=location, rotation=rotation)
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    return finish_mesh(obj, min(radius * .20, .015), 3)


def lathe_x(name, profile, mat, segments=64, bevel=.004):
    """Create a closed rotational hard-surface part along the weapon X axis."""
    vertices = []
    faces = []
    for x, radius in profile:
        for segment in range(segments):
            angle = segment / segments * math.tau
            runtime_y = math.sin(angle) * radius
            runtime_z = math.cos(angle) * radius
            vertices.append((x, -runtime_z, runtime_y))
    for ring in range(len(profile) - 1):
        for segment in range(segments):
            nxt = (segment + 1) % segments
            a = ring * segments + segment
            b = ring * segments + nxt
            c = (ring + 1) * segments + nxt
            d = (ring + 1) * segments + segment
            faces.append((a, b, c, d))
    faces.append(tuple(reversed(range(segments))))
    last = (len(profile) - 1) * segments
    faces.append(tuple(last + index for index in range(segments)))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    return finish_mesh(obj, bevel, 3)


def ellipsoid_runtime(name, center, scale, mat, subdivisions=3):
    x, y, z = center
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdivisions, radius=1.0, location=(x, -z, y))
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    obj.data.materials.append(mat)
    return finish_mesh(obj, min(scale) * .06, 2)


def oval_grip_x(name, start_x, end_x, radii, mat, segments=32):
    middle = (start_x + end_x) * .5
    profile = [
        (start_x, radii[0] * .82),
        (start_x + (middle - start_x) * .22, radii[0]),
        (middle, (radii[0] + radii[1]) * .5),
        (end_x + (middle - end_x) * .22, radii[1]),
        (end_x, radii[1] * .82),
    ]
    obj = lathe_x(name, profile, mat, segments, .006)
    # Compress one transverse axis to avoid the toy-like round dowel silhouette.
    obj.scale.z = .84
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    semantic_uv(obj)
    return obj


def grip_riser(name, x, radius, mat, minor=.009):
    return lathe_x(name, [
        (x - minor * .72, radius - minor * .20),
        (x - minor * .42, radius + minor * .58),
        (x + minor * .42, radius + minor * .58),
        (x + minor * .72, radius - minor * .20),
    ], mat, 48, 0)


def elliptical_eye_ring(name, center_x, center_z, radius_x, radius_z, tube, mat, major_segments=64, minor_segments=10):
    vertices = []
    faces = []
    for major in range(major_segments):
        angle = major / major_segments * math.tau
        cosine = math.cos(angle)
        sine = math.sin(angle)
        normal = Vector((cosine / radius_x, sine / radius_z)).normalized()
        for minor in range(minor_segments):
            around = minor / minor_segments * math.tau
            radial = math.cos(around) * tube
            depth = math.sin(around) * tube
            vertices.append((
                center_x + cosine * radius_x + normal.x * radial,
                -(center_z + sine * radius_z + normal.y * radial),
                depth,
            ))
    for major in range(major_segments):
        next_major = (major + 1) % major_segments
        for minor in range(minor_segments):
            next_minor = (minor + 1) % minor_segments
            a = major * minor_segments + minor
            b = next_major * minor_segments + minor
            c = next_major * minor_segments + next_minor
            d = major * minor_segments + next_minor
            faces.append((a, b, c, d))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    return finish_mesh(obj, 0, 1)


def join_mesh_objects(name, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    result = bpy.context.object
    result.name = name
    semantic_uv(result)
    return result


def boolean_union(target, operand, name):
    """Merge overlapping closed hard-surface parts into one watertight visible shell."""
    bpy.ops.object.select_all(action="DESELECT")
    target.select_set(True)
    bpy.context.view_layer.objects.active = target
    modifier = target.modifiers.new("Forge solid union", "BOOLEAN")
    modifier.operation = "UNION"
    modifier.solver = "EXACT"
    modifier.object = operand
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    bpy.data.objects.remove(operand, do_unlink=True)
    target.name = name
    return finish_mesh(target, 0, 1)


def torus_x(name, x, major_radius, minor_radius, mat, z_runtime=0, bevel=None):
    bpy.ops.mesh.primitive_torus_add(
        major_radius=major_radius,
        minor_radius=minor_radius,
        major_segments=40,
        minor_segments=10,
        location=(x, -z_runtime, 0),
        rotation=(0, math.pi * .5, 0))
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    if bevel is None:
        bevel = min(.0025, minor_radius * .10)
    return finish_mesh(obj, bevel, 2)


def torus_runtime_y(name, x, z_runtime, major_radius, minor_radius, mat):
    bpy.ops.mesh.primitive_torus_add(
        major_radius=major_radius,
        minor_radius=minor_radius,
        major_segments=40,
        minor_segments=10,
        location=(x, -z_runtime, 0))
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    return finish_mesh(obj, .004, 2)


def beveled_runtime_curve(name, runtime_points, radius, mat, resolution=5):
    curve = bpy.data.curves.new(name + "Curve", "CURVE")
    curve.dimensions = "3D"
    curve.resolution_u = 3
    curve.bevel_depth = radius
    curve.bevel_resolution = resolution
    spline = curve.splines.new("BEZIER")
    spline.bezier_points.add(len(runtime_points) - 1)
    for point, (x, y, z) in zip(spline.bezier_points, runtime_points):
        point.co = (x, -z, y)
        point.handle_left_type = "AUTO"
        point.handle_right_type = "AUTO"
    obj = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.convert(target="MESH")
    return finish_mesh(bpy.context.object, min(radius * .12, .010), 2)


def forged_guard(name, runtime_points, widths, depth, mat):
    """A forged rectangular guard with tapered quillons, not a round tube."""
    if len(runtime_points) != len(widths):
        raise ValueError("Guard point and width counts must match")
    vertices = []
    for index, (point, width) in enumerate(zip(runtime_points, widths)):
        x, y, z = point
        previous = Vector((runtime_points[max(0, index - 1)][0], 0, runtime_points[max(0, index - 1)][2]))
        following = Vector((runtime_points[min(len(runtime_points) - 1, index + 1)][0], 0, runtime_points[min(len(runtime_points) - 1, index + 1)][2]))
        tangent = (following - previous).normalized()
        normal = Vector((-tangent.z, 0, tangent.x))
        half_depth = depth * (.58 if index in (0, len(runtime_points) - 1) else .5)
        center = Vector((x, y, z))
        ring = [
            center + normal * width + Vector((0, half_depth, 0)),
            center + normal * width + Vector((0, -half_depth, 0)),
            center - normal * width + Vector((0, -half_depth, 0)),
            center - normal * width + Vector((0, half_depth, 0)),
        ]
        vertices.extend((point.x, -point.z, point.y) for point in ring)
    faces = []
    for index in range(len(runtime_points) - 1):
        a = index * 4
        b = (index + 1) * 4
        for lane in range(4):
            nxt = (lane + 1) % 4
            faces.append((a + lane, b + lane, b + nxt, a + nxt))
    faces.append((3, 2, 1, 0))
    last = (len(runtime_points) - 1) * 4
    faces.append((last, last + 1, last + 2, last + 3))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    return finish_mesh(obj, min(depth * .18, .018), 3)


def helical_wrap_x(name, start_x, end_x, radius, pitch, mat, tube=.0075):
    points = []
    length = abs(end_x - start_x)
    turns = max(1.0, length / max(.01, pitch))
    samples = max(24, int(turns * 8))
    for index in range(samples + 1):
        t = index / samples
        angle = t * turns * math.tau
        x = start_x + (end_x - start_x) * t
        points.append((x, math.sin(angle) * radius, math.cos(angle) * radius))
    return beveled_runtime_curve(name, points, tube, mat, 2)


def shaped_handle(name, runtime_points, radii, mat, segments=16):
    """Continuous oval handle loft with a stable frame along a curved path."""
    if len(runtime_points) != len(radii):
        raise ValueError("Handle point and radius counts must match")
    vertices = []
    for index, (point, radius) in enumerate(zip(runtime_points, radii)):
        center = Vector(point)
        previous = Vector(runtime_points[max(0, index - 1)])
        following = Vector(runtime_points[min(len(runtime_points) - 1, index + 1)])
        tangent = (following - previous).normalized()
        binormal = Vector((0, 1, 0))
        if abs(tangent.dot(binormal)) > .92:
            binormal = Vector((0, 0, 1))
        side = tangent.cross(binormal).normalized()
        binormal = side.cross(tangent).normalized()
        for segment in range(segments):
            angle = segment / segments * math.tau
            p = center + side * (math.cos(angle) * radius * .78) + binormal * (math.sin(angle) * radius)
            vertices.append((p.x, -p.z, p.y))
    faces = []
    for ring in range(len(runtime_points) - 1):
        for segment in range(segments):
            nxt = (segment + 1) % segments
            a = ring * segments + segment
            b = ring * segments + nxt
            c = (ring + 1) * segments + nxt
            d = (ring + 1) * segments + segment
            faces.append((a, b, c, d))
    faces.append(tuple(reversed(range(segments))))
    last = (len(runtime_points) - 1) * segments
    faces.append(tuple(last + i for i in range(segments)))
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    return finish_mesh(obj, min(max(radii) * .10, .012), 3)


def collection(name):
    value = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(value)
    return value


def move_to_collection(obj, target):
    for owner in list(obj.users_collection):
        owner.objects.unlink(obj)
    target.objects.link(obj)


def add_duelling_longsword(materials):
    group = collection("DuellingLongsword")
    blade_sections = [
        (-1.725, -.034, .034, 0.0),
        (-1.06, -.037, .037, 0.0),
        # Keep a narrow, blunt ricasso through the guard. The old profile
        # expanded to full blade width inside the ecusson, so the shoulder and
        # guard occupied the same volume and appeared to pass through each other.
        (-1.01, -.040, .040, 0.05),
        (-.94, -.052, .052, .22),
        (-.86, -.082, .082, .68),
        (-.80, -.119, .119, 1.0),
        (-.62, -.119, .119, 1.0),
        (.72, -.106, .106, 1.0),
        (1.20, -.078, .078, 1.0),
        (1.50, -.038, .038, 1.0),
        (1.725, -.004, .004, 1.0),
    ]
    blade = double_edged_blade("DuellingLongsword_Blade", blade_sections, .052, materials["Workpiece"], materials["WorkpieceEdge"], fuller=.32)
    move_to_collection(blade, group)
    fittings = []
    guard_points = [
        (-.90, 0, -.48), (-.94, 0, -.43), (-.985, 0, -.33), (-1.005, 0, -.18),
        (-.965, 0, 0), (-1.005, 0, .18), (-.985, 0, .33), (-.94, 0, .43), (-.90, 0, .48),
    ]
    guard = forged_guard("DuellingLongsword_Guard", guard_points, [.030, .038, .046, .058, .092, .058, .046, .038, .030], .098, materials["IronDark"])
    ecusson = extruded_runtime_polygon("DuellingLongsword_Ecusson", [(-1.075, -.128), (-.895, -.128), (-.845, 0), (-.895, .128), (-1.075, .128)], .162, materials["IronDark"], .010)
    guard = boolean_union(guard, ecusson, "DuellingLongsword_GuardAndShoulder")
    fittings.append(guard)
    fittings.append(ellipsoid_runtime("DuellingLongsword_QuillonL", guard_points[0], (.045, .048, .060), materials["Brass"], 2))
    fittings.append(ellipsoid_runtime("DuellingLongsword_QuillonR", guard_points[-1], (.045, .048, .060), materials["Brass"], 2))
    fittings.append(oval_grip_x("DuellingLongsword_GripCore", -1.68, -1.045, (.094, .102), materials["Leather"], 48))
    fittings.append(lathe_x("DuellingLongsword_Collar", [(-1.060, .088), (-1.04, .108), (-1.006, .112), (-.980, .084)], materials["Brass"], 64, .004))
    fittings.append(helical_wrap_x("DuellingLongsword_Wrap", -1.070, -1.66, .106, .082, materials["Leather"], .0065))
    for index, x in enumerate((-1.10, -1.36, -1.63)):
        fittings.append(grip_riser(f"DuellingLongsword_Riser{index}", x, .108, materials["Leather"], .0065))
    fittings.append(lathe_x("DuellingLongsword_Pommel", [
        (-1.88, .035), (-1.85, .075), (-1.80, .125), (-1.73, .145),
        (-1.68, .115), (-1.655, .073),
    ], materials["IronDark"], 72, .006))
    fittings.append(lathe_x("DuellingLongsword_PommelCap", [(-1.90, .018), (-1.88, .048), (-1.855, .018)], materials["Brass"], 48, .003))
    for obj in fittings:
        move_to_collection(obj, group)
    return group, blade, fittings


def add_basilard(materials):
    group = collection("Basilard")
    blade_sections = [
        (-1.28, -.045, .045, 0.0),
        (-.67, -.050, .050, 0.0),
        (-.61, -.088, .088, .45),
        (-.575, -.154, .154, 1.0),
        (.58, -.160, .160, 1.0),
        (.88, -.140, .140, 1.0),
        (1.08, -.092, .092, 1.0),
        (1.28, -.005, .005, 1.0),
    ]
    blade = double_edged_blade("Basilard_Blade", blade_sections, .060, materials["Workpiece"], materials["WorkpieceEdge"], fuller=.08)
    move_to_collection(blade, group)
    fittings = []
    guard_points = [(-.54, 0, -.32), (-.59, 0, -.27), (-.625, 0, -.15), (-.585, 0, 0), (-.625, 0, .15), (-.59, 0, .27), (-.54, 0, .32)]
    guard = forged_guard("Basilard_Guard", guard_points, [.034, .044, .056, .082, .056, .044, .034], .082, materials["IronDark"])
    fittings.append(guard)
    fittings.append(extruded_runtime_polygon("Basilard_Ecusson", [(-.67, -.115), (-.55, -.115), (-.515, 0), (-.55, .115), (-.67, .115)], .126, materials["IronDark"], .010))
    fittings.append(ellipsoid_runtime("Basilard_QuillonL", guard_points[0], (.046, .050, .055), materials["Brass"], 2))
    fittings.append(ellipsoid_runtime("Basilard_QuillonR", guard_points[-1], (.046, .050, .055), materials["Brass"], 2))
    fittings.append(oval_grip_x("Basilard_GripCore", -1.17, -.67, (.080, .086), materials["Wood"], 40))
    fittings.append(lathe_x("Basilard_Collar", [(-.68, .075), (-.66, .102), (-.625, .106), (-.605, .078)], materials["Brass"], 56, .004))
    fittings.append(helical_wrap_x("Basilard_Wrap", -.70, -1.15, .088, .068, materials["Leather"], .0085))
    for index, x in enumerate((-.74, -.94, -1.14)):
        fittings.append(grip_riser(f"Basilard_Riser{index}", x, .089, materials["Leather"], .008))
    fittings.append(lathe_x("Basilard_PommelNeck", [(-1.235, .048), (-1.205, .085), (-1.165, .088)], materials["IronDark"], 48, .004))
    fittings.append(torus_x("Basilard_RingPommel", -1.265, .130, .028, materials["IronDark"], bevel=.0018))
    fittings.append(torus_x("Basilard_RingInlay", -1.265, .098, .010, materials["Brass"], bevel=.0005))
    for obj in fittings:
        move_to_collection(obj, group)
    return group, blade, fittings


def add_bearded_axe(materials):
    group = collection("BeardedAxe")
    # The cutting edge follows the broad Scandinavian axe construction: a
    # long convex bit from toe to heel, a light lower beard, then a narrow neck
    # that gains mass again at the haft seat. The inset path is the start of
    # the bevel, so the bit is actual wedge geometry rather than a bright rim.
    head_points = [
        (-1.02, .64), (-1.08, .62), (-1.145, .56), (-1.20, .47), (-1.235, .36),
        (-1.26, .23), (-1.275, .09), (-1.275, -.06), (-1.26, -.21),
        (-1.225, -.35), (-1.17, -.49), (-1.09, -.61), (-.99, -.70),
        (-.87, -.77), (-.74, -.80), (-.67, -.79),
        (-.61, -.75), (-.48, -.66), (-.36, -.54), (-.24, -.41),
        (-.10, -.29), (.08, -.20), (.29, -.14), (.52, -.12),
        (.74, -.10), (.88, -.05), (.93, .07), (.93, .20),
        (.86, .29), (.69, .33), (.48, .32), (.26, .34),
        (.03, .40), (-.20, .49), (-.43, .57), (-.66, .63),
        (-.87, .66), (-1.00, .66)
    ]
    beard_shoulder = [
        (-1.014, .636), (-.995, .585), (-1.015, .51), (-1.045, .43), (-1.065, .33),
        (-1.08, .21), (-1.09, .08), (-1.09, -.05), (-1.08, -.18),
        (-1.055, -.30), (-1.015, -.41), (-.96, -.51), (-.89, -.59),
        (-.81, -.65), (-.705, -.72), (-.666, -.784)
    ]
    head = single_edged_axe_head("BeardedAxe_HeadCore", head_points, beard_shoulder, .240, materials["Workpiece"], materials["WorkpieceEdge"])
    head.name = "BeardedAxe_Head"
    head.data.name = "BeardedAxe_HeadMesh"
    move_to_collection(head, group)
    fittings = []
    fittings.append(extruded_runtime_polygon(
        "BeardedAxe_HaftSeatReinforcement",
        [(.34, -.17), (.61, -.18), (.82, -.11), (.91, .02), (.91, .20),
         (.81, .30), (.58, .35), (.36, .29)],
        .252, materials["IronDark"], .010))
    fittings.append(extruded_runtime_polygon(
        "BeardedAxe_PollCap",
        [(.78, -.10), (.94, -.06), (.97, .05), (.97, .20), (.87, .29), (.79, .25)],
        .266, materials["IronDark"], .009))
    handle = shaped_handle(
        "BeardedAxe_Handle",
        [(.57, 0, -.25), (.57, 0, .05), (.53, 0, .42), (.47, 0, .82),
         (.39, 0, 1.25), (.31, 0, 1.66), (.25, 0, 2.02), (.28, 0, 2.28)],
        [.108, .116, .110, .101, .093, .085, .078, .084],
        materials["Wood"], 36)
    fittings.append(handle)
    fittings.append(extruded_runtime_polygon(
        "BeardedAxe_Wedge",
        [(.46, .255), (.65, .265), (.65, .350), (.47, .345)],
        .268, materials["Wood"], .005))
    for side, suffix in ((-.137, "Back"), (.137, "Front")):
        fittings.append(ellipsoid_runtime(
            f"BeardedAxe_HaftPeen{suffix}", (.73, side, .16),
            (.026, .010, .026), materials["IronDark"], 2))
    grip = shaped_handle(
        "BeardedAxe_Grip",
        [(.34, 0, 1.50), (.28, 0, 1.83), (.25, 0, 2.05), (.28, 0, 2.24)],
        [.096, .087, .081, .084], materials["Leather"], 32)
    fittings.append(grip)
    fittings.append(ellipsoid_runtime("BeardedAxe_ButtCap", (.28, 0, 2.30), (.090, .072, .100), materials["IronDark"], 2))
    fittings.append(ellipsoid_runtime("BeardedAxe_ButtPin", (.28, 0, 2.35), (.028, .034, .038), materials["Brass"], 2))
    for obj in fittings:
        move_to_collection(obj, group)
    return group, head, fittings


def select_objects(objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.hide_render = False
        obj.hide_set(False)
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def export_glb(path, objects):
    select_objects(objects)
    bpy.ops.export_scene.gltf(
        filepath=str(path),
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        # Blender 4.5 emits invalid tangent vectors for very small torus wraps.
        # The runtime loader generates finite orthogonal tangents when absent.
        export_tangents=False,
        export_materials="EXPORT",
        export_texcoords=True,
        export_normals=True,
        export_yup=True,
        export_cameras=False,
        export_lights=False)


def validate_object(obj):
    if obj.type != "MESH":
        return {"name": obj.name, "type": obj.type, "triangles": 0, "manifold": True}
    mesh = obj.data
    mesh.validate(verbose=False, clean_customdata=False)
    mesh.calc_loop_triangles()
    topology = bmesh.new()
    topology.from_mesh(mesh)
    non_manifold = sum(1 for edge in topology.edges if not edge.is_manifold)
    topology.free()
    zero_area = sum(1 for tri in mesh.loop_triangles if tri.area <= 1e-8)
    bounds = [obj.matrix_world @ mathutils.Vector(corner) for corner in obj.bound_box]
    return {
        "name": obj.name,
        "type": obj.type,
        "vertices": len(mesh.vertices),
        "triangles": len(mesh.loop_triangles),
        "nonManifoldEdges": non_manifold,
        "zeroAreaTriangles": zero_area,
        "materials": [slot.material.name for slot in obj.material_slots if slot.material],
        "bounds": {
            "min": [min(point[index] for point in bounds) for index in range(3)],
            "max": [max(point[index] for point in bounds) for index in range(3)]
        }
    }


def look_at(camera, target=(0, 0, 0)):
    direction = mathutils.Vector(target) - camera.location
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()


def render_thumbnail(path, objects, camera_location, target, lens=58):
    for obj in bpy.context.scene.objects:
        if obj.type in {"MESH", "CURVE"}:
            obj.hide_render = obj not in objects
    camera = bpy.data.objects.get("WeaponPreviewCamera")
    camera.location = camera_location
    camera.data.lens = lens
    look_at(camera, target)
    bpy.context.scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)


def setup_preview():
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = 640
    scene.render.resolution_y = 360
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    scene.view_settings.look = "AgX - Medium High Contrast"
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (.032, .038, .052, 1.0)
    background.inputs["Strength"].default_value = .34
    bpy.ops.object.light_add(type="AREA", location=(0, -4.4, 2.6))
    fill = bpy.context.object
    fill.name = "ReadableMaterialFill"
    fill.data.energy = 1250
    fill.data.size = 3.8
    fill.data.color = (.72, .82, 1.0)
    look_at(fill, (0, 0, 0))
    bpy.ops.object.camera_add(location=(0, -5.8, 3.8))
    camera = bpy.context.object
    camera.name = "WeaponPreviewCamera"
    camera.data.lens = 58
    scene.camera = camera
    bpy.ops.object.light_add(type="AREA", location=(-2.5, -3.8, 4.5))
    key = bpy.context.object
    key.name = "WarmKey"
    key.data.energy = 950
    key.data.color = (1.0, .58, .30)
    key.data.shape = "DISK"
    key.data.size = 4.0
    look_at(key)
    bpy.ops.object.light_add(type="AREA", location=(3.5, 1.8, 2.6))
    rim = bpy.context.object
    rim.name = "CoolRim"
    rim.data.energy = 720
    rim.data.color = (.42, .58, 1.0)
    rim.data.size = 3.0
    look_at(rim)
    bpy.ops.object.light_add(type="AREA", location=(-2.8, -2.0, 6.4))
    top = bpy.context.object
    top.name = "WeaponFaceLight"
    top.data.energy = 720
    top.data.color = (.92, .96, 1.0)
    top.data.size = 6.0
    look_at(top, (0, 0, 0))


def build():
    clear_scene()
    materials = {
        "Workpiece": material("Workpiece", (.32, .36, .42), 1.0, .24),
        "WorkpieceEdge": material("WorkpieceEdge", (.54, .59, .67), .94, .27),
        "IronDark": material("IronDark", (.15, .17, .21), .92, .32),
        "Wood": material("Wood", (.34, .13, .045), .0, .46),
        "Leather": material("Leather", (.26, .075, .030), .0, .54),
        "Brass": material("Brass", (.50, .29, .075), .78, .28),
    }
    built = {
        "duelling-longsword": add_duelling_longsword(materials),
        "basilard": add_basilard(materials),
        "bearded-axe": add_bearded_axe(materials),
    }
    for weapon_id, (_, forged_part, fittings) in built.items():
        export_glb(MODELS / f"weapon-{weapon_id}.glb", [forged_part, *fittings])
        export_glb(MODELS / f"fittings-{weapon_id}.glb", fittings)
    qa = {
        "schemaVersion": 1,
        "generator": "Blender 4.5 hard-surface forge pipeline",
        "assets": {
            weapon_id: [validate_object(forged_part), *[validate_object(item) for item in fittings]]
            for weapon_id, (_, forged_part, fittings) in built.items()
        }
    }
    QA_PATH.write_text(json.dumps(qa, indent=2), encoding="utf-8")
    setup_preview()
    render_thumbnail(THUMBNAILS / "recipe-duelling-longsword.png", [built["duelling-longsword"][1], *built["duelling-longsword"][2]], (0, -5.6, 3.7), (0, 0, 0))
    render_thumbnail(THUMBNAILS / "recipe-basilard.png", [built["basilard"][1], *built["basilard"][2]], (0, -4.6, 3.0), (0, 0, 0))
    render_thumbnail(THUMBNAILS / "recipe-bearded-axe.png", [built["bearded-axe"][1], *built["bearded-axe"][2]], (0, -1.4, 7.5), (0, -.65, 0), 56)
    bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE / "forge-weapons.blend"))
    bpy.ops.object.select_all(action="DESELECT")
    return {
        "blend": str(SOURCE / "forge-weapons.blend"),
        "models": [str(path) for path in sorted(MODELS.glob("*duelling-long*.glb"))] +
                  [str(path) for path in sorted(MODELS.glob("*basilard*.glb"))] +
                  [str(path) for path in sorted(MODELS.glob("*bearded-axe*.glb"))],
        "thumbnails": [str(path) for path in sorted(THUMBNAILS.glob("recipe-*.png"))],
        "qa": str(QA_PATH),
    }


import mathutils
_result = build()
