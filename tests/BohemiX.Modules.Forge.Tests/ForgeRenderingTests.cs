using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BohemiX.Modules.Forge.Controls;
using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Rendering;
using SharpGLTF.Schema2;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeRenderingTests
{
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void RenderFrameScheduling_RequiresVisibleNonMinimizedSurface(
        bool attached,
        bool effectivelyVisible,
        bool topLevelVisible,
        bool minimized,
        bool expected)
    {
        Assert.Equal(expected, ForgeOpenGlControl.ShouldRenderFrames(
            attached,
            effectivelyVisible,
            topLevelVisible,
            minimized));
    }

    [Fact]
    public void RenderFrameScheduling_StopsAsSoonAsForgeRemovalBegins()
    {
        Assert.False(ForgeOpenGlControl.ShouldRenderFrames(
            attached: true,
            effectivelyVisible: true,
            topLevelVisible: true,
            minimized: false,
            releaseRequested: true));
    }

    [Fact]
    public void GlResourceRegistry_ReleasesEveryResourceInReverseRegistrationOrder()
    {
        var disposalOrder = new List<int>();
        var registry = new GlResourceRegistry();
        registry.Register(new TrackedResource(1, disposalOrder));
        registry.Register(new TrackedResource(2, disposalOrder));
        registry.Register(new TrackedResource(3, disposalOrder));

        registry.Dispose();
        registry.Dispose();

        Assert.Equal([3, 2, 1], disposalOrder);
        Assert.Equal(0, registry.Count);
    }

    private static readonly string[] StaticModelFiles =
    [
        "anvil.glb",
        "bellows.glb",
        "grinder.glb",
        "grinder-stand.glb",
        "grinder-wheel.glb",
        "hammer.glb",
        "hearth.glb",
        "fittings-basilard.glb",
        "fittings-bearded-axe.glb",
        "fittings-duelling-longsword.glb",
        "quench-oil.glb",
        "quench-water.glb",
        "tongs.glb",
        "weapon-basilard.glb",
        "weapon-bearded-axe.glb",
        "weapon-duelling-longsword.glb",
        "workbench.glb"
    ];

    [Fact]
    public void GpuMaterialsDoNotRetainCpuTexturePayloads()
    {
        var retainedTypes = typeof(GpuPbrMaterial)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.PropertyType)
            .ToArray();

        Assert.DoesNotContain(typeof(PbrMaterialAsset), retainedTypes);
        Assert.DoesNotContain(typeof(PbrTextureAsset), retainedTypes);
        Assert.DoesNotContain(typeof(byte[]), retainedTypes);
    }

    [Fact]
    public void ForgeVertexStoresFourComponentTangentHandedness()
    {
        var vertex = new ForgeVertex(
            Vector3.Zero,
            Vector3.UnitY,
            new Vector4(Vector3.UnitX, -1),
            Vector2.Zero,
            0);

        Assert.Equal(typeof(Vector4), typeof(ForgeVertex).GetProperty(nameof(ForgeVertex.Tangent))!.PropertyType);
        Assert.Equal(52, Marshal.SizeOf<ForgeVertex>());
        Assert.Equal(Vector3.UnitX, new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z));
        Assert.Equal(-1, vertex.Tangent.W);
    }

    [Fact]
    public void GeneratedStaticGlbAssetsExistAndAreReadableBySharpGltf()
    {
        var modelsDirectory = FindModelsDirectory();

        foreach (var fileName in StaticModelFiles)
        {
            var path = Path.Combine(modelsDirectory, fileName);
            Assert.True(File.Exists(path), $"Missing generated Forge asset: {path}");

            var model = ReadGlb(path);
            Assert.NotEmpty(model.LogicalMeshes);
            Assert.Contains(model.LogicalNodes, node => node.Mesh is not null);
        }
    }

    [Fact]
    public void DuellingLongswordGuardUsesTheDynamicWorkpieceAxes()
    {
        var model = ReadGlb(Path.Combine(FindModelsDirectory(), "fittings-duelling-longsword.glb"));
        var node = Assert.Single(model.LogicalNodes.Where(item =>
            item.Name?.Contains("Guard", StringComparison.OrdinalIgnoreCase) == true));
        var points = node.Mesh!.Primitives
            .SelectMany(primitive => primitive.GetVertexAccessor("POSITION")!.AsVector3Array())
            .Select(point => Vector3.Transform(point, node.WorldMatrix))
            .ToArray();
        var min = points.Aggregate(Vector3.Min);
        var max = points.Aggregate(Vector3.Max);
        var extent = max - min;

        Assert.True(extent.Z > extent.Y * 5,
            $"The guard must spread along workpiece width Z, not thickness Y: {extent}.");
    }

    [Fact]
    public void GeneratedAnvilHasAtLeastFifteenThousandTriangles()
    {
        var path = Path.Combine(FindModelsDirectory(), "anvil.glb");
        var model = ReadGlb(path);
        var triangleCount = model.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Sum(primitive =>
            {
                var indices = primitive.GetIndices();
                return indices is not null
                    ? indices.Count / 3
                    : (primitive.GetVertexAccessor("POSITION")?.Count ?? 0) / 3;
            });

        Assert.True(triangleCount >= 15_000, $"Anvil contains only {triangleCount:N0} triangles.");
    }

    [Theory]
    [InlineData("weapon-duelling-longsword.glb")]
    [InlineData("weapon-basilard.glb")]
    [InlineData("weapon-bearded-axe.glb")]
    public void FinishedWeaponsEmbedPbrTextureChannels(string fileName)
    {
        var model = ReadGlb(Path.Combine(FindModelsDirectory(), fileName));
        Assert.Contains(model.LogicalMaterials,
            material => material.FindChannel("BaseColor")?.Texture is not null);
        Assert.Contains(model.LogicalMaterials,
            material => material.FindChannel("Normal")?.Texture is not null);
        Assert.Contains(model.LogicalMaterials,
            material => material.FindChannel("MetallicRoughness")?.Texture is not null);
        Assert.All(model.LogicalMeshes.SelectMany(mesh => mesh.Primitives), primitive =>
            Assert.NotNull(primitive.GetVertexAccessor("TEXCOORD_0")));
    }

    [Fact]
    public void AxeAssetsDecodePixelsForEveryMaterialSlot()
    {
        var loader = new ForgeGlbAssetLoader();
        foreach (var fileName in new[] { "weapon-bearded-axe.glb", "fittings-bearded-axe.glb" })
        {
            using var mesh = loader.LoadMesh(new Uri(Path.Combine(FindModelsDirectory(), fileName)));
            Assert.NotEmpty(mesh.Materials);
            Assert.All(mesh.Materials, material =>
            {
                Assert.NotNull(material.BaseColorTexture);
                Assert.NotNull(material.NormalTexture);
                Assert.NotNull(material.MetallicRoughnessTexture);
                Assert.True(material.BaseColorTexture!.Rgba.Length >= material.BaseColorTexture.Width * material.BaseColorTexture.Height * 4);
                Assert.True(material.NormalTexture!.Rgba.Length >= material.NormalTexture.Width * material.NormalTexture.Height * 4);
                Assert.True(material.MetallicRoughnessTexture!.Rgba.Length >= material.MetallicRoughnessTexture.Width * material.MetallicRoughnessTexture.Height * 4);
            });
        }
    }

    [Fact]
    public void WorkshopAssetsEmbedPbrTextureChannels()
    {
        var modelsDirectory = FindModelsDirectory();
        foreach (var fileName in StaticModelFiles.Where(fileName =>
                     !fileName.StartsWith("weapon-", StringComparison.OrdinalIgnoreCase) &&
                     !fileName.StartsWith("fittings-", StringComparison.OrdinalIgnoreCase)))
        {
            var model = ReadGlb(Path.Combine(modelsDirectory, fileName));
            Assert.Contains(model.LogicalMaterials,
                material => material.FindChannel("BaseColor")?.Texture is not null);
            Assert.Contains(model.LogicalMaterials,
                material => material.FindChannel("Normal")?.Texture is not null);
            Assert.Contains(model.LogicalMaterials,
                material => material.FindChannel("MetallicRoughness")?.Texture is not null);
        }
    }

    [Fact]
    public void WorkshopGlbAssetsKeepDetailedGeometryBudgets()
    {
        var minimumTriangles = new Dictionary<string, int>
        {
            ["workbench.glb"] = 15_000,
            ["hearth.glb"] = 30_000,
            ["bellows.glb"] = 5_000,
            ["quench-water.glb"] = 8_000,
            ["quench-oil.glb"] = 8_000,
            ["grinder-stand.glb"] = 6_000,
            ["grinder-wheel.glb"] = 9_000
        };

        foreach (var (fileName, minimum) in minimumTriangles)
        {
            var model = ReadGlb(Path.Combine(FindModelsDirectory(), fileName));
            var triangles = model.LogicalMeshes
                .SelectMany(mesh => mesh.Primitives)
                .Sum(primitive => primitive.GetIndices()?.Count / 3 ??
                                  (primitive.GetVertexAccessor("POSITION")?.Count ?? 0) / 3);

            Assert.True(triangles >= minimum,
                $"{fileName} regressed to {triangles:N0} triangles; expected at least {minimum:N0}.");
        }
    }

    [Fact]
    public void WorkshopGlbAssetsUseForgeMaterialsAndExportTangentSpace()
    {
        var modelsDirectory = FindModelsDirectory();
        foreach (var fileName in StaticModelFiles)
        {
            var model = ReadGlb(Path.Combine(modelsDirectory, fileName));
            foreach (var primitive in model.LogicalMeshes.SelectMany(mesh => mesh.Primitives))
            {
                if (!fileName.StartsWith("weapon-", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.StartsWith("fittings-", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.NotNull(primitive.GetVertexAccessor("TANGENT"));
                }
                Assert.NotNull(primitive.Material);
                Assert.True(Enum.TryParse<ForgeMaterialId>(primitive.Material!.Name, true, out _),
                    $"{fileName} uses unknown material '{primitive.Material.Name}', which would fall back to Iron.");
            }
        }
    }

    [Fact]
    public void GlbFallbackTangentsAreFiniteUnitLengthAndOrthogonalToNormals()
    {
        var positions = new[]
        {
            new Vector3(-1, 0, -1), new Vector3(1, 0, -1),
            new Vector3(1, 0, 1), new Vector3(-1, 0, 1)
        };
        var normals = Enumerable.Repeat(Vector3.UnitY, 4).ToArray();
        var texCoords = new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY };
        IList<uint> indices = new uint[] { 0, 1, 2, 0, 2, 3 };

        var tangents = ForgeGlbAssetLoader.GenerateTangents(positions, normals, texCoords, indices);

        Assert.Equal(positions.Length, tangents.Length);
        Assert.All(tangents, tangent =>
        {
            var direction = new Vector3(tangent.X, tangent.Y, tangent.Z);
            Assert.True(IsFinite(direction));
            Assert.InRange(direction.Length(), .999f, 1.001f);
            Assert.InRange(Math.Abs(Vector3.Dot(direction, Vector3.UnitY)), 0, .001f);
            Assert.Equal(1, tangent.W);
        });
    }

    [Fact]
    public void StaticGlbAssetsHaveValidTriangleIndicesAndFiniteNormals()
    {
        var modelsDirectory = FindModelsDirectory();

        foreach (var fileName in StaticModelFiles)
        {
            var model = ReadGlb(Path.Combine(modelsDirectory, fileName));
            var primitives = model.LogicalMeshes.SelectMany(mesh => mesh.Primitives).ToArray();
            Assert.NotEmpty(primitives);

            foreach (var primitive in primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
                var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var indices = primitive.GetIndices();

                Assert.NotNull(positions);
                Assert.NotEmpty(positions!);
                Assert.NotNull(normals);
                Assert.Equal(positions.Count, normals!.Count);
                Assert.All(normals, normal => Assert.True(IsFinite(normal), $"{fileName} contains a non-finite normal."));

                Assert.NotNull(indices);
                Assert.NotEmpty(indices!);
                Assert.Equal(0, indices.Count % 3);
                Assert.All(indices, index => Assert.InRange(index, 0u, checked((uint)positions.Count - 1)));
            }
        }
    }

    [Theory]
    [InlineData("weapon-duelling-longsword.glb", "DuellingLongsword_Blade")]
    [InlineData("weapon-basilard.glb", "Basilard_Blade")]
    [InlineData("weapon-bearded-axe.glb", "BeardedAxe_Head")]
    public void FinishedWeaponSteelUsesARealWedgeInsteadOfAFlatExtrusion(string fileName, string meshName)
    {
        var model = ReadGlb(Path.Combine(FindModelsDirectory(), fileName));
        var steelMesh = Assert.Single(model.LogicalMeshes.Where(mesh =>
            mesh.Name?.Contains(meshName, StringComparison.OrdinalIgnoreCase) == true));
        var positions = steelMesh.Primitives
            .SelectMany(primitive => primitive.GetVertexAccessor("POSITION")!.AsVector3Array())
            .ToArray();

        var halfThicknesses = positions
            .Select(position => Math.Abs(position.Y))
            .Where(value => value > .0001f)
            .ToArray();
        Assert.NotEmpty(halfThicknesses);
        Assert.True(halfThicknesses.Min() < halfThicknesses.Max() * .22f,
            $"{fileName} has no physical cutting wedge: minimum half-thickness " +
            $"{halfThicknesses.Min():F4}, body half-thickness {halfThicknesses.Max():F4}.");
    }

    [Fact]
    public void DuellingLongswordShoulderExpandsInFrontOfTheGuardInsteadOfInsideIt()
    {
        var model = ReadGlb(Path.Combine(FindModelsDirectory(), "weapon-duelling-longsword.glb"));
        var bladeNode = Assert.Single(model.LogicalNodes.Where(node =>
            node.Name?.Contains("DuellingLongsword_Blade", StringComparison.OrdinalIgnoreCase) == true));
        var positions = bladeNode.Mesh!.Primitives
            .SelectMany(primitive => primitive.GetVertexAccessor("POSITION")!.AsVector3Array())
            .Select(position => Vector3.Transform(position, bladeNode.WorldMatrix))
            .ToArray();

        var throughGuard = positions.Where(position => position.X is >= -1.03f and <= -.92f).ToArray();
        var inFrontOfGuard = positions.Where(position => position.X is >= -.82f and <= -.76f).ToArray();

        Assert.NotEmpty(throughGuard);
        Assert.NotEmpty(inFrontOfGuard);
        Assert.True(throughGuard.Max(position => Math.Abs(position.Z)) < .075f,
            "Only the narrow ricasso may pass through the guard and ecusson.");
        Assert.True(inFrontOfGuard.Max(position => Math.Abs(position.Z)) > .105f,
            "The full blade shoulder must develop after it clears the guard.");
    }

    [Theory]
    [InlineData("duelling-longsword")]
    [InlineData("basilard")]
    public void FinishedDynamicSwordUsesAThinBladeCrossSection(string recipeId)
    {
        var recipe = new ForgeCatalog().GetRecipe(recipeId);
        var mesh = WorkpieceMeshBuilder.Build(TargetCells(recipe.Shape), false, recipeId, shape: recipe.Shape);
        var width = mesh.Vertices.Max(vertex => vertex.Position.Z) - mesh.Vertices.Min(vertex => vertex.Position.Z);
        var thickness = mesh.Vertices.Max(vertex => vertex.Position.Y) - mesh.Vertices.Min(vertex => vertex.Position.Y);

        Assert.InRange(thickness / width, .12f, .34f);
    }

    [Theory]
    [InlineData("duelling-longsword")]
    [InlineData("basilard")]
    public void DynamicSwordMeshAndPointerUseTheSameSurfaceHeight(string recipeId)
    {
        var recipe = new ForgeCatalog().GetRecipe(recipeId);
        var cells = TargetCells(recipe.Shape);
        var mesh = WorkpieceMeshBuilder.Build(cells, false, recipeId, shape: recipe.Shape);
        var centerVertex = mesh.Vertices
            .Where(vertex => vertex.MaterialId == (int)ForgeMaterialId.Workpiece && vertex.Normal.Y > 0)
            .OrderBy(vertex => Vector2.DistanceSquared(vertex.TexCoord, new Vector2(.5f)))
            .First();
        var hitHeight = WorkpieceMeshBuilder.SurfaceHeightAt(
            cells,
            new Vector2(.5f),
            recipeId,
            recipe.Shape);

        Assert.InRange(Math.Abs(centerVertex.Position.Y - hitHeight), 0, .006f);
    }

    [Fact]
    public void WorkshopMeshHasFiniteTangentSpaceAndIndexedTopology()
    {
        var mesh = ProceduralMeshFactory.CreateWorkshop();

        Assert.True(mesh.Vertices.Length > 300);
        Assert.NotEmpty(mesh.Indices);
        Assert.Equal(0, mesh.Indices.Length % 3);
        Assert.True(mesh.IsFinite);
        Assert.All(mesh.Vertices, vertex => Assert.True(float.IsFinite(vertex.TexCoord.X) && float.IsFinite(vertex.TexCoord.Y)));
    }

    [Fact]
    public void AnvilMeshUsesClosedBeveledComponentsWithoutOpenShellEdges()
    {
        var mesh = ProceduralMeshFactory.CreateAnvil();
        var edges = new Dictionary<(PositionKey A, PositionKey B), int>();
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            AddEdge(mesh.Indices[i], mesh.Indices[i + 1]);
            AddEdge(mesh.Indices[i + 1], mesh.Indices[i + 2]);
            AddEdge(mesh.Indices[i + 2], mesh.Indices[i]);
        }

        Assert.All(edges.Values, count => Assert.Equal(2, count));
        return;

        void AddEdge(uint first, uint second)
        {
            var a = PositionKey.From(mesh.Vertices[first].Position);
            var b = PositionKey.From(mesh.Vertices[second].Position);
            var edge = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            edges[edge] = edges.GetValueOrDefault(edge) + 1;
        }
    }

    [Fact]
    public void WorkpieceMeshIsClosedForAxeEyeAndKeepsContinuousSteelMaterialWhenDamaged()
    {
        var shape = new LatticeShapeSimulation();
        shape.Reset(new ShapeTemplateDefinition("axe", 1, 1, 1, .15, .48, .26, .26));
        var cells = shape.SnapshotCells()
            .Select(cell => cell with { Damage = cell.X is >= 28 and <= 62 ? .95 : cell.Damage })
            .ToArray();
        var mesh = WorkpieceMeshBuilder.Build(cells, flipped: false);

        Assert.NotEmpty(mesh.Vertices);
        Assert.Equal(0, mesh.Indices.Length % 3);
        Assert.True(mesh.IsFinite);
        Assert.All(mesh.Vertices, vertex => Assert.Contains(
            vertex.MaterialId,
            new[] { (float)ForgeMaterialId.Workpiece, (float)ForgeMaterialId.WorkpieceEdge }));
        Assert.Contains(mesh.Vertices, vertex => vertex.MaterialId == (int)ForgeMaterialId.WorkpieceEdge);
        Assert.Contains(mesh.Vertices, vertex => vertex.MaterialId == (int)ForgeMaterialId.Workpiece && vertex.Normal.Y > .5f && Math.Abs(vertex.Normal.X) + Math.Abs(vertex.Normal.Z) > .0001f);
        Assert.DoesNotContain(cells, cell => cell.X is >= 33 and <= 50 && cell.Y is >= 12 and <= 19 && cell.Occupancy < .05);
        AssertClosedByPosition(mesh);
    }

    [Fact]
    public void FormedBeardedAxeExposesAPolishedCuttingBevelOnItsFace()
    {
        var recipe = new ForgeCatalog().GetRecipe("bearded-axe");
        var edge = recipe.Zones.Single(zone => zone.Id == "edge");
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape, recipe.Billet);
        shape.ApplyHammer(
            (edge.Start + edge.End) * .5,
            .5,
            .65,
            HammerFace.Flat,
            .95,
            0,
            new ForgeFormationGuide(edge.Id, edge.Start, edge.End, 1, false));

        var mesh = WorkpieceMeshBuilder.Build(
            shape.SnapshotCells(),
            flipped: false,
            recipe.Id,
            shape: recipe.Shape);
        var minimumX = mesh.Vertices.Min(vertex => vertex.Position.X);

        Assert.Contains(mesh.Vertices, vertex =>
            vertex.MaterialId == (int)ForgeMaterialId.WorkpieceEdge &&
            Math.Abs(vertex.Normal.Y) > .45f &&
            vertex.Position.X < minimumX + .55f);
    }

    [Fact]
    public void RecipeWorkpiecesHaveDistinctBilletProportionsAndContinuousForgedSurfaces()
    {
        var catalog = new ForgeCatalog();
        var dimensions = new Dictionary<string, Vector3>();
        foreach (var recipeId in new[] { "duelling-longsword", "basilard", "bearded-axe" })
        {
            var shape = new LatticeShapeSimulation();
            shape.Reset(catalog.GetRecipe(recipeId).Shape);
            var mesh = WorkpieceMeshBuilder.Build(shape.SnapshotCells(), flipped: false, recipeId);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var vertex in mesh.Vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }

            dimensions[recipeId] = max - min;
            Assert.True(mesh.IsFinite);
            Assert.Contains(mesh.Vertices, vertex =>
                Math.Abs(vertex.Normal.Y) is > .18f and < .94f &&
                Math.Abs(vertex.Normal.X) + Math.Abs(vertex.Normal.Z) > .18f);
            Assert.InRange(WorkpieceMeshBuilder.ProfileFor(recipeId).Bevel, .008f, .016f);
        }

        Assert.True(dimensions["duelling-longsword"].X > dimensions["basilard"].X);
        Assert.True(dimensions["basilard"].X > dimensions["bearded-axe"].X);
        Assert.True(dimensions["bearded-axe"].Z > dimensions["basilard"].Z);
        Assert.True(dimensions["bearded-axe"].Y > dimensions["basilard"].Y);
    }

    [Fact]
    public void ShortSwordRayMappingUsesItsActualVisualLength()
    {
        var profile = WorkpieceMeshBuilder.ProfileFor("basilard");
        var ray = new ForgeRay(new Vector3(profile.Length * .45f, 1, 0), -Vector3.UnitY);

        Assert.True(ForgeRaycaster.TryHitWorkpiece(
            ray,
            Matrix4x4.Identity,
            out var lattice,
            out _,
            "basilard"));
        Assert.InRange(lattice.X, .93f, .97f);
    }

    [Theory]
    [InlineData("duelling-longsword")]
    [InlineData("basilard")]
    [InlineData("bearded-axe")]
    public void InitialBilletMeshDiffersClearlyFromTheFormedWorkpiece(string recipeId)
    {
        var recipe = new ForgeCatalog().GetRecipe(recipeId);
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape, recipe.Billet);
        var initialCells = shape.SnapshotCells();
        var formedCells = TargetCells(recipe.Shape);
        var initial = WorkpieceMeshBuilder.Build(initialCells, false, recipeId);
        var developed = WorkpieceMeshBuilder.Build(formedCells, false, recipeId);

        Assert.NotEqual(
            initialCells.Count(cell => cell.Occupancy > .05),
            formedCells.Count(cell => cell.Occupancy > .05));
        Assert.True(Math.Abs(
            AverageSurfaceHeight(initial, edge: false) -
            AverageSurfaceHeight(developed, edge: false)) > .004f);
        Assert.Equal(initial.Vertices.Length, developed.Vertices.Length);
        Assert.Equal(initial.Indices, developed.Indices);
        Assert.Contains(initial.Vertices.Zip(developed.Vertices), pair =>
            Vector3.Distance(pair.First.Position, pair.Second.Position) > .02f);
        var initialWidth = initial.Vertices.Max(vertex => vertex.Position.Z) - initial.Vertices.Min(vertex => vertex.Position.Z);
        var formedWidth = developed.Vertices.Max(vertex => vertex.Position.Z) - developed.Vertices.Min(vertex => vertex.Position.Z);
        Assert.True(Math.Abs(initialWidth - formedWidth) > .08f);
    }

    [Fact]
    public void ForgeProgressIsRecipeSpecificAndClamped()
    {
        Assert.Equal(0, ForgeSceneRenderer.ForgeProgress("duelling-longsword", 0));
        Assert.InRange(ForgeSceneRenderer.ForgeProgress("basilard", 21), .49f, .51f);
        Assert.Equal(1, ForgeSceneRenderer.ForgeProgress("bearded-axe", 100));
    }

    [Fact]
    public void CameraOrbitAndZoomAreConstrained()
    {
        var camera = new ForgeCameraRig();
        camera.AddOrbit(100, 100);
        camera.AddZoom(100);
        var pose = camera.Update(1, false, 0);
        var forward = Vector3.Normalize(pose.Target - pose.Position);
        Assert.InRange(Vector3.Dot(forward, Vector3.Normalize(new Vector3(0, -0.35f, -1))), -.1f, 1.1f);
        camera.AddOrbit(-100, -100);
        camera.AddZoom(-100);
        pose = camera.Update(1, false, 1);
        Assert.InRange(pose.Position.Length(), 5.5f, 12f);
    }

    [Fact]
    public void HammeringCameraLooksDownOntoTheAnvil()
    {
        var pose = ForgeCameraRig.GetAnchor(ForgeStateId.Hammering);
        var forward = Vector3.Normalize(pose.Target - pose.Position);
        var horizontal = Vector3.Normalize(new Vector3(forward.X, 0, forward.Z));

        Assert.True(-forward.Y > .80f, "Hammering view must read as a steep first-person smithing view.");
        Assert.InRange(pose.FieldOfView, .54f, .60f);
        Assert.True(pose.Target.Y > 0, "Camera target should stay on the workpiece surface, not below the anvil.");
        Assert.InRange(Vector3.Distance(pose.Position, pose.Target), 6.0f, 6.9f);
        Assert.InRange(Math.Abs(horizontal.X), .60f, .75f);
        Assert.InRange(Math.Abs(horizontal.Z), .65f, .80f);
    }

    [Fact]
    public void RaycasterMapsProjectedWorkpieceCenterToLattice()
    {
        var pose = ForgeCameraRig.GetAnchor(ForgeStateId.Hammering);
        var view = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, 16f / 9f, .08f, 40f);
        var world = Matrix4x4.CreateTranslation(-.10f, .49f, .12f);
        var clip = Vector4.Transform(new Vector4(new Vector3(-.10f, .49f, .12f), 1), view * projection);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        var pixel = new Vector2((ndc.X * .5f + .5f) * 1600, (1 - (ndc.Y * .5f + .5f)) * 900);
        var ray = ForgeRaycaster.CreateRay(pixel, new Vector2(1600, 900), view, projection);

        Assert.True(ForgeRaycaster.TryHitWorkpiece(ray, world, out var lattice, out _));
        Assert.InRange(lattice.X, .35f, .65f);
        Assert.InRange(lattice.Y, .25f, .75f);
    }

    [Fact]
    public void ActualLongswordSurfacePixelHitsAnOccupiedHammerCell()
    {
        var engine = new ForgeEngine();
        engine.Execute(new SelectRecipeCommand("duelling-longsword"));
        engine.Execute(new SelectMaterialCommand("soft-steel"));
        engine.Execute(new MoveToAnvilCommand());
        var snapshot = engine.Snapshot;
        var cell = snapshot.ShapeCells
            .Where(candidate => candidate.Occupancy > .05)
            .OrderBy(candidate => Math.Abs(candidate.X - 48) + Math.Abs(candidate.Y - 16))
            .First();
        var world = ForgeWorkpieceMotion.PoseFor(
            ForgeStateId.Hammering,
            flipped: false,
            QuenchMedium.Water).ToMatrix();
        var surfaceHeight = WorkpieceMeshBuilder.SurfaceHeightAt(
            snapshot.ShapeCells,
            new Vector2(
                (cell.X + .5f) / WorkpieceMeshBuilder.Columns,
                (cell.Y + .5f) / WorkpieceMeshBuilder.Rows),
            snapshot.RecipeId,
            snapshot.RecipeShape);
        var local = new Vector3(
            (cell.X + .5f) / WorkpieceMeshBuilder.Columns * 3.35f - 3.35f / 2,
            surfaceHeight,
            (cell.Y + .5f) / WorkpieceMeshBuilder.Rows * 1.12f - 1.12f / 2);
        var worldPoint = Vector3.Transform(local, world);
        var pose = ForgeCameraRig.GetAnchor(ForgeStateId.Hammering);
        var view = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, 1415f / 843f, .08f, 40f);
        var clip = Vector4.Transform(new Vector4(worldPoint, 1), view * projection);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        var pixel = new Vector2((ndc.X * .5f + .5f) * 1415, (1 - (ndc.Y * .5f + .5f)) * 843);
        var ray = ForgeRaycaster.CreateRay(pixel, new Vector2(1415, 843), view, projection);

        Assert.True(ForgeRaycaster.TryHitOccupiedWorkpiece(
            ray,
            world,
            snapshot.ShapeCells,
            flipped: false,
            out var lattice,
            out _));
        Assert.InRange(lattice.X, 0, 1);
        Assert.InRange(lattice.Y, 0, 1);
    }

    [Fact]
    public void WorkbenchExposesAllInteractiveStationsAndNearestRayHit()
    {
        Assert.Equal(7, ForgeWorkbenchLayout.Stations.Select(station => station.Id).Distinct().Count());
        var ray = new ForgeRay(new Vector3(-.2f, 5, .1f), -Vector3.UnitY);
        Assert.True(ForgeWorkbenchLayout.TryHit(ray, out var station));
        Assert.Equal(ForgeStationId.Anvil, station);
    }

    [Fact]
    public void BellowsNozzleFacesTheHearthAirChannel()
    {
        var transform = ForgeWorkbenchLayout.Bellows.Transform;
        var nozzleDirection = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, transform));
        var nozzleBase = new Vector3(transform.M41, transform.M42, transform.M43);
        var hearthAirChannel = new Vector3(-3.05f, -.80f, .10f);
        var towardHearth = Vector3.Normalize(hearthAirChannel - nozzleBase);
        var nozzleTip = Vector3.Transform(new Vector3(1.50f, .055f, 0), transform);

        Assert.True(Vector3.Dot(nozzleDirection, towardHearth) > .98f);
        Assert.InRange(Vector3.Distance(nozzleTip, hearthAirChannel), 0, .24f);
    }

    [Theory]
    [InlineData(ForgeStateId.Heating, false, ForgeStationId.Bellows, true)]
    [InlineData(ForgeStateId.Reheat, false, ForgeStationId.Anvil, false)]
    [InlineData(ForgeStateId.Hammering, false, ForgeStationId.Hearth, true)]
    [InlineData(ForgeStateId.Hammering, false, ForgeStationId.Hammer, false)]
    [InlineData(ForgeStateId.Hammering, false, ForgeStationId.WaterVat, false)]
    [InlineData(ForgeStateId.Hammering, true, ForgeStationId.WaterVat, true)]
    [InlineData(ForgeStateId.RotateWorkpiece, true, ForgeStationId.WaterVat, false)]
    [InlineData(ForgeStateId.Grinding, true, ForgeStationId.Grinder, true)]
    public void WorkbenchActivationPolicyMatchesForgeStateMachine(
        ForgeStateId state,
        bool canProceed,
        ForgeStationId station,
        bool expected)
    {
        Assert.Equal(expected, ForgeWorkbenchLayout.CanActivate(state, canProceed, station));
    }

    [Theory]
    [InlineData(ForgeStateId.Heating, ForgeStationId.Bellows, false, true)]
    [InlineData(ForgeStateId.Heating, ForgeStationId.Hearth, false, false)]
    [InlineData(ForgeStateId.Hammering, ForgeStationId.Anvil, true, true)]
    [InlineData(ForgeStateId.Hammering, ForgeStationId.Anvil, false, false)]
    [InlineData(ForgeStateId.Quenching, ForgeStationId.WaterVat, true, true)]
    [InlineData(ForgeStateId.Quenching, ForgeStationId.WaterVat, false, false)]
    [InlineData(ForgeStateId.Grinding, ForgeStationId.Grinder, false, true)]
    [InlineData(ForgeStateId.Grinding, ForgeStationId.Anvil, true, false)]
    [InlineData(ForgeStateId.RotateWorkpiece, ForgeStationId.Hammer, true, false)]
    public void WorkbenchGesturePolicyRequiresThePhysicalTool(
        ForgeStateId state,
        ForgeStationId station,
        bool hitsWorkpiece,
        bool expected)
    {
        Assert.Equal(expected, ForgeWorkbenchLayout.CanStartGesture(state, station, hitsWorkpiece));
    }

    [Theory]
    [InlineData(ForgeStationId.WaterVat, QuenchMedium.Water, true)]
    [InlineData(ForgeStationId.OilVat, QuenchMedium.Water, false)]
    [InlineData(ForgeStationId.WaterVat, QuenchMedium.Oil, false)]
    [InlineData(ForgeStationId.OilVat, QuenchMedium.Oil, true)]
    public void QuenchGestureCanStartFromOnlyTheSelectedBath(
        ForgeStationId station,
        QuenchMedium medium,
        bool expected)
    {
        Assert.Equal(expected, ForgeWorkbenchLayout.CanStartGesture(
            ForgeStateId.Quenching,
            station,
            hitsWorkpiece: false,
            medium));
    }

    [Theory]
    [InlineData(ForgeStationId.WaterVat)]
    [InlineData(ForgeStationId.OilVat)]
    public void WorkpieceDragResolvesToQuenchBath(ForgeStationId station)
    {
        Assert.Equal(station, ForgeProcessGestures.ResolveQuenchTransfer(
            startedOnWorkpiece: true,
            canProceed: true,
            movement: 40,
            station));
        Assert.Null(ForgeProcessGestures.ResolveQuenchTransfer(true, true, 10, station));
        Assert.Null(ForgeProcessGestures.ResolveQuenchTransfer(false, true, 40, station));
    }

    [Fact]
    public void QuenchCameraMovesCloseToTheSelectedBath()
    {
        var water = ForgeCameraRig.GetQuenchAnchor(QuenchMedium.Water);
        var oil = ForgeCameraRig.GetQuenchAnchor(QuenchMedium.Oil);

        Assert.InRange(Vector3.Distance(water.Position, water.Target), 4.4f, 5.5f);
        Assert.InRange(Vector3.Distance(oil.Position, oil.Target), 4.4f, 5.5f);
        Assert.InRange(water.Target.X, 2.10f, 2.14f);
        Assert.InRange(oil.Target.X, 3.33f, 3.37f);
        Assert.True(oil.Target.X - water.Target.X > 1.1f);
    }

    [Fact]
    public void QuenchCameraRetargetsWhenTheSelectedMediumChanges()
    {
        var camera = new ForgeCameraRig();
        camera.SetQuenchMedium(QuenchMedium.Water, reducedMotion: false);
        camera.SetState(ForgeStateId.Quenching, reducedMotion: false);
        var water = camera.Update(1, false, 0);

        camera.SetQuenchMedium(QuenchMedium.Oil, reducedMotion: false);
        var oil = camera.Update(1, false, 1);

        Assert.InRange(water.Target.X, 2.10f, 2.14f);
        Assert.InRange(oil.Target.X, 3.33f, 3.37f);
    }

    [Fact]
    public void GrindingCameraUsesTheWheelWorkViewBeforeAndDuringContact()
    {
        var display = ForgeCameraRig.GetGrindingAnchor(engaged: false);
        var active = ForgeCameraRig.GetGrindingAnchor(engaged: true);
        var activeForward = Vector3.Normalize(active.Target - active.Position);

        Assert.Equal(active, display);
        Assert.InRange(Vector3.Distance(active.Position, active.Target), 4.55f, 4.75f);
        Assert.InRange(active.FieldOfView, .54f, .58f);
        Assert.InRange(-activeForward.Y, .69f, .73f);
        Assert.True(active.Position.X - active.Target.X > 3.1f,
            "Grinding camera must face the wheel rim rather than look down its axle.");
        Assert.InRange(Math.Abs(active.Position.Z - active.Target.Z), .55f, .65f);
        Assert.InRange(active.Target.X, 2.78f, 2.86f);
        Assert.InRange(active.Target.Z, -1.34f, -1.26f);
    }

    [Theory]
    [InlineData(ForgeStateId.Grinding, false, true)]
    [InlineData(ForgeStateId.Inspection, false, true)]
    [InlineData(ForgeStateId.Result, false, true)]
    [InlineData(ForgeStateId.Grinding, true, true)]
    [InlineData(ForgeStateId.Hammering, false, false)]
    public void FinishedWeaponPresentationIncludesTheCompleteWeaponDuringGrindingAndInspection(
        ForgeStateId state,
        bool grindingEngaged,
        bool expected)
    {
        Assert.Equal(expected, ForgeSceneRenderer.ShouldRenderFinishedWeapon(state, grindingEngaged));
    }

    [Fact]
    public void GrindingCameraProjectsTheWheelAxisAcrossTheScreen()
    {
        var pose = ForgeCameraRig.GetGrindingAnchor(engaged: true);
        var forward = Vector3.Normalize(pose.Target - pose.Position);
        var screenRight = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var screenUp = Vector3.Normalize(Vector3.Cross(screenRight, forward));
        var wheelAxis = Vector3.Normalize(Vector3.TransformNormal(
            Vector3.UnitZ,
            ForgeWorkbenchLayout.Grinder.Transform));

        Assert.True(Math.Abs(Vector3.Dot(wheelAxis, screenRight)) > .97f);
        Assert.True(Math.Abs(Vector3.Dot(wheelAxis, screenUp)) < .10f);
    }

    [Fact]
    public void GrindingWheelOccupiesTheLowerCenterOfTheReferenceComposition()
    {
        var pose = ForgeCameraRig.GetGrindingAnchor(engaged: true);
        var view = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, 16f / 9f, .08f, 40f);
        var wheelCenter = Vector3.Transform(new Vector3(0, .38f, 0), ForgeWorkbenchLayout.Grinder.Transform);
        var clip = Vector4.Transform(new Vector4(wheelCenter, 1), view * projection);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);

        Assert.InRange(ndc.X, -.20f, .20f);
        Assert.InRange(ndc.Y, -.50f, -.02f);
    }

    [Fact]
    public void GrindingUsesTheTopCrownInsteadOfTheWheelSideOrShoulder()
    {
        var centerToContact = ForgeWorkpieceMotion.GrindingContactPoint -
                              ForgeWorkpieceMotion.GrindingWheelCenter;

        Assert.True(Vector3.Dot(ForgeWorkpieceMotion.GrindingContactNormal, Vector3.UnitY) > .999f);
        Assert.True(MathF.Abs(Vector3.Dot(centerToContact, ForgeWorkpieceMotion.GrindingWheelAxis)) < .001f);
        Assert.True(MathF.Abs(centerToContact.X) < .001f);
        Assert.True(MathF.Abs(centerToContact.Z) < .001f);
        Assert.InRange(
            centerToContact.Y,
            ForgeWorkpieceMotion.GrindingWheelRadius - .001f,
            ForgeWorkpieceMotion.GrindingWheelRadius + .001f);
    }

    [Fact]
    public void SwordGrindingUsesTheBlenderAuthoredPoseWithoutMovingTheAxe()
    {
        var axe = ForgeGrindingAlignment.Fallback("bearded-axe");
        var longsword = ForgeGrindingAlignment.Fallback("duelling-longsword");
        var basilard = ForgeGrindingAlignment.Fallback("basilard");

        Assert.False(axe.UsesAuthoredWheelPose);
        Assert.True(longsword.UsesAuthoredWheelPose);
        Assert.True(basilard.UsesAuthoredWheelPose);
        Assert.True(Vector3.Distance(
            ForgeWorkpieceMotion.GrindingContactPoint,
            ForgeWorkpieceMotion.GrindingContactPointFor(axe)) < .00001f);
    }

    [Theory]
    [InlineData("duelling-longsword", "weapon-duelling-longsword.glb")]
    [InlineData("basilard", "weapon-basilard.glb")]
    public void SwordGrindingPoseExactlyMatchesTheBlenderAuthoredRootMatrix(string recipeId, string fileName)
    {
        var loader = new ForgeGlbAssetLoader();
        using var mesh = loader.LoadMesh(new Uri(Path.Combine(FindModelsDirectory(), fileName)));
        var alignment = ForgeGrindingAlignment.FromMesh(recipeId, mesh);
        var expected =
            Matrix4x4.CreateScale(.62f) *
            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(
                .0739127845f,
                -.7032331824f,
                .0739127919f,
                .7032331824f))) *
            Matrix4x4.CreateTranslation(.1215230003f, 1.1150300503f, .1500000060f) *
            ForgeWorkbenchLayout.Grinder.Transform;
        var actual = ForgeWorkpieceMotion.PoseFor(
            ForgeStateId.Grinding,
            false,
            QuenchMedium.Water,
            alignment).ToMatrix();

        AssertMatrixClose(expected, actual, .00001f);
    }

    [Theory]
    [InlineData("duelling-longsword", "weapon-duelling-longsword.glb")]
    [InlineData("basilard", "weapon-basilard.glb")]
    [InlineData("bearded-axe", "weapon-bearded-axe.glb")]
    public void GrindingUsesTheFinishedWeaponsActualEdgeOnTheWheelRim(string recipeId, string fileName)
    {
        var loader = new ForgeGlbAssetLoader();
        using var mesh = loader.LoadMesh(new Uri(Path.Combine(FindModelsDirectory(), fileName)));
        var alignment = ForgeGrindingAlignment.FromMesh(recipeId, mesh);
        var camera = ForgeCameraRig.GetGrindingAnchor(engaged: true);
        var pose = ForgeWorkpieceMotion.PoseFor(ForgeStateId.Grinding, false, QuenchMedium.Water, alignment);
        var forward = Vector3.Normalize(camera.Target - camera.Position);
        var screenRight = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var screenUp = Vector3.Normalize(Vector3.Cross(screenRight, forward));
        var lengthAxis = Vector3.Normalize(Vector3.Transform(alignment.LocalLengthAxis, pose.Rotation));
        var interiorAxis = Vector3.Normalize(Vector3.Transform(alignment.LocalInteriorAxis, pose.Rotation));
        var faceNormal = Vector3.Normalize(Vector3.Transform(alignment.LocalFaceNormal, pose.Rotation));
        var wheelAxis = Vector3.Normalize(Vector3.TransformNormal(
            Vector3.UnitZ,
            ForgeWorkbenchLayout.Grinder.Transform));
        var contact = ForgeWorkpieceMotion.GrindingContactForPose(pose, alignment);
        var expectedLength = ForgeWorkpieceMotion.GrindingLengthAxisFor(alignment);
        var expectedFace = ForgeWorkpieceMotion.GrindingFaceNormalFor(alignment);
        var expectedInterior = ForgeWorkpieceMotion.GrindingInteriorDirectionFor(alignment);
        var expectedContact = ForgeWorkpieceMotion.GrindingContactPointFor(alignment);
        var towardPlayer = Vector3.Normalize(camera.Position - contact);

        Assert.True(Vector3.Dot(lengthAxis, expectedLength) > .999f);
        Assert.True(Vector3.Dot(interiorAxis, expectedInterior) > .999f);
        Assert.True(Vector3.Dot(faceNormal, expectedFace) > .999f);
        Assert.True(Math.Abs(Vector3.Dot(expectedFace, expectedLength)) < .01f);
        Assert.True(Vector3.Distance(contact, expectedContact) < .001f);

        if (recipeId == "bearded-axe")
        {
            Assert.False(alignment.UsesAuthoredWheelPose);
            Assert.True(Vector3.Dot(expectedFace, ForgeWorkpieceMotion.GrindingContactNormal) > .97f);
            var centerToExpectedContact = expectedContact - ForgeWorkpieceMotion.GrindingWheelCenter;
            var expectedAxialDistance = Vector3.Dot(centerToExpectedContact, ForgeWorkpieceMotion.GrindingWheelAxis);
            var expectedRadial = centerToExpectedContact -
                                 ForgeWorkpieceMotion.GrindingWheelAxis * expectedAxialDistance;
            Assert.InRange(
                expectedRadial.Length(),
                ForgeWorkpieceMotion.GrindingWheelRadius - .001f,
                ForgeWorkpieceMotion.GrindingWheelRadius + .001f);
            Assert.True(alignment.LocalContactPoint.X < -1.20f, "The axe must contact the stone with its cutting edge, not its poll or haft.");
            Assert.True(Math.Abs(Vector3.Dot(lengthAxis, screenUp)) < .10f);
            Assert.True(Math.Abs(Vector3.Dot(lengthAxis, screenRight)) > .97f,
                "The axe cutting edge must lie horizontally across the wheel.");
            Assert.True(Vector3.Dot(lengthAxis, wheelAxis) > .999f);
            Assert.True(Vector3.Dot(interiorAxis, towardPlayer) > .55f,
                "The axe head and handle must open back toward the smith.");
        }
        else
        {
            Assert.True(alignment.UsesAuthoredWheelPose);
            Assert.True(alignment.LocalContactPoint.Y < -.09f, "The sword must contact the stone with its lower cutting edge.");
        }
    }

    [Theory]
    [InlineData("duelling-longsword", "weapon-duelling-longsword.glb")]
    [InlineData("basilard", "weapon-basilard.glb")]
    [InlineData("bearded-axe", "weapon-bearded-axe.glb")]
    public void GrindingFlipAndStrokeKeepTheBevelFixedAlongTheWeaponAxis(string recipeId, string fileName)
    {
        var loader = new ForgeGlbAssetLoader();
        using var mesh = loader.LoadMesh(new Uri(Path.Combine(FindModelsDirectory(), fileName)));
        var alignment = ForgeGrindingAlignment.FromMesh(recipeId, mesh);
        var strokeAxis = ForgeWorkpieceMotion.GrindingLengthAxisFor(alignment);

        foreach (var flipped in new[] { false, true })
        {
            var pose = ForgeWorkpieceMotion.PoseFor(ForgeStateId.Grinding, flipped, QuenchMedium.Water, alignment);
            var contact = ForgeWorkpieceMotion.GrindingContactForPose(pose, alignment);
            Assert.True(Vector3.Distance(contact, ForgeWorkpieceMotion.GrindingContactPointFor(alignment)) < .001f);

            foreach (var position in new[] { 0f, .5f, 1f })
            {
                var stroke = ForgeWorkpieceMotion.GrindingStrokeOffset(position, alignment);
                if (stroke.LengthSquared() > .0001f)
                {
                    Assert.True(Math.Abs(Vector3.Dot(Vector3.Normalize(stroke), strokeAxis)) > .999f);
                }
                Assert.True(Math.Abs(Vector3.Dot(stroke, ForgeWorkpieceMotion.GrindingFaceNormalFor(alignment))) < .001f);
            }
        }
    }

    [Fact]
    public void GrindingStrokeNeverChangesTheFixedBevelAngle()
    {
        var motion = new ForgeWorkpieceMotion();
        motion.SetTarget(ForgeStateId.Grinding, false, QuenchMedium.Water, reducedMotion: false);
        motion.Update(.2f, 0);
        var initial = motion.CurrentPose.Rotation;

        foreach (var position in new[] { 0d, 1d, .25d, .75d })
        {
            motion.SetGrindingStroke(position, 1);
            motion.Update(.12f, (float)position);
            Assert.True(Math.Abs(Quaternion.Dot(initial, motion.CurrentPose.Rotation)) > .99999f,
                "Longitudinal grinding strokes must not roll or yaw the weapon.");
        }
    }

    [Fact]
    public void GrindingFeedbackScalesWithContactAndFinishQuality()
    {
        Assert.Equal(0, ForgeSceneRenderer.GrindingContactIntensity(false, 1, 1, 1));
        Assert.Equal(0, ForgeSceneRenderer.GrindingContactIntensity(true, 0, 1, 1));

        var resting = ForgeSceneRenderer.GrindingContactIntensity(true, .8, .08, .4);
        var controlledStroke = ForgeSceneRenderer.GrindingContactIntensity(true, 1, 1, .9);
        Assert.True(controlledStroke > resting * 2);
        var roughBurst = ParticlePool.GrindingSparkCount(controlledStroke, .1f);
        var finishedBurst = ParticlePool.GrindingSparkCount(controlledStroke, .9f);
        Assert.True(finishedBurst > roughBurst);
        Assert.InRange(finishedBurst, 20, 28);

        var restingRate = ForgeSceneRenderer.GrindingSparkRate(false, resting, .4f);
        var controlledRate = ForgeSceneRenderer.GrindingSparkRate(false, controlledStroke, .9f);
        Assert.InRange(restingRate, 35f, 50f);
        Assert.True(controlledRate > restingRate * 2);
        Assert.True(ForgeSceneRenderer.GrindingSparkRate(true, controlledStroke, .9f) < controlledRate);
    }

    [Theory]
    [InlineData(QuenchMedium.Water)]
    [InlineData(QuenchMedium.Oil)]
    public void QuenchingLowersTheBladeTipBeforeTheTongHeldTang(QuenchMedium medium)
    {
        var pose = ForgeWorkpieceMotion.PoseFor(ForgeStateId.Quenching, false, medium);
        var directionFromTangToTip = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, pose.Rotation));

        Assert.True(directionFromTangToTip.Y < -.90f);
    }

    [Theory]
    [InlineData(QuenchMedium.Water)]
    [InlineData(QuenchMedium.Oil)]
    public void QuenchFlipKeepsTheTipDownWhileChangingOnlyTheWorkpieceFace(QuenchMedium medium)
    {
        var front = ForgeWorkpieceMotion.PoseFor(ForgeStateId.Quenching, false, medium);
        var back = ForgeWorkpieceMotion.PoseFor(ForgeStateId.Quenching, true, medium);
        var frontLength = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, front.Rotation));
        var backLength = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, back.Rotation));
        var frontFace = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, front.Rotation));
        var backFace = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, back.Rotation));

        Assert.True(frontLength.Y < -.90f);
        Assert.True(backLength.Y < -.90f);
        Assert.True(Vector3.Dot(frontLength, backLength) > .99f);
        Assert.True(Vector3.Dot(frontFace, backFace) < -.99f);
    }

    [Fact]
    public void GrinderAnimationUsesTheSelectedWheelSpeed()
    {
        var slow = new ForgeToolMotion();
        var fast = new ForgeToolMotion();

        slow.Update(.10f, ForgeStateId.Grinding, HammerFace.Flat, grindingEngaged: true, grinderSpeedScale: .45);
        fast.Update(.10f, ForgeStateId.Grinding, HammerFace.Flat, grindingEngaged: true, grinderSpeedScale: 1.35);

        Assert.True(Math.Abs(fast.GrinderAngle) > Math.Abs(slow.GrinderAngle) * 2);
    }

    [Fact]
    public void GrindingWheelStartsStoppedAndOnlyAcceleratesAboveZeroSpeed()
    {
        var tools = new ForgeToolMotion();

        tools.Update(.25f, ForgeStateId.Grinding, HammerFace.Flat, grindingEngaged: true, grinderSpeedScale: 0);
        Assert.Equal(0, tools.GrinderAngle);
        Assert.Equal(0, tools.GrinderSpeed);

        tools.Update(.25f, ForgeStateId.Grinding, HammerFace.Flat, grindingEngaged: true, grinderSpeedScale: .10);
        Assert.NotEqual(0, tools.GrinderAngle);
        Assert.True(tools.GrinderSpeed > 0);
    }

    [Fact]
    public void HammerSparkBurstRemainsVisibleAndScalesWithHeatAndForce()
    {
        var coldLight = ParticlePool.HammerSparkBurstCount(.25, ForgeHeatBand.Cold);
        var hotHeavy = ParticlePool.HammerSparkBurstCount(1.0, ForgeHeatBand.OrangeRed);

        Assert.InRange(coldLight, 20, 36);
        Assert.InRange(hotHeavy, 58, 68);
        Assert.True(hotHeavy > coldLight * 1.8);
        Assert.InRange(ForgeSceneRenderer.HammerSparkAfterglowDuration(1), .16f, .18f);
    }

    [Fact]
    public void HammerImpactIsDeliveredOnTheFastContactFrame()
    {
        Assert.InRange(ForgeToolMotion.HammerImpactTime, .17f, .19f);
        Assert.True(ForgeToolMotion.HammerImpactTime < .20f);
    }

    [Fact]
    public void HammerSparksBounceOnTheAnvilAndSettleOnTheGroundPlane()
    {
        var anvilSpark = new ParticleInstance
        {
            Position = new Vector3(-.10f, .52f, .18f),
            Velocity = new Vector3(.2f, -1f, .1f),
            Gravity = -4.15f,
            Life = 1.4f,
            Kind = 0,
            Color = Vector4.One,
            Size = .04f
        };
        ParticlePool.AdvanceParticle(ref anvilSpark, .08f);
        Assert.InRange(anvilSpark.Position.Y, .465f, .467f);
        Assert.True(anvilSpark.Velocity.Y > 0);

        var groundSpark = anvilSpark with
        {
            Position = new Vector3(2f, -.96f, 1.4f),
            Velocity = new Vector3(.05f, -1f, .05f),
            Kind = 0
        };
        ParticlePool.AdvanceParticle(ref groundSpark, .10f);
        Assert.InRange(groundSpark.Position.Y, -1.015f, -1.013f);
        Assert.True(groundSpark.Velocity.Y >= 0);
        Assert.Equal(-1.02f, ParticlePool.HammerSparkCollisionHeight(new Vector3(2, 0, 1.4f)));
    }

    [Fact]
    public void ExpiredForgeSparksAreClearedFromTheGpuInstancePool()
    {
        var spark = new ParticleInstance
        {
            Position = new Vector3(0, .5f, 0),
            Velocity = new Vector3(1, 1, 0),
            Gravity = -4.15f,
            Life = .04f,
            Kind = 0,
            Color = Vector4.One,
            Size = .04f
        };

        ParticlePool.AdvanceParticle(ref spark, .05f);

        Assert.Equal(0, spark.Life);
        Assert.Equal(0, spark.Color.W);
        Assert.Equal(0, spark.Size);
        Assert.Equal(Vector3.Zero, spark.Velocity);
    }

    [Fact]
    public void LandedForgeSparksLingerAndFadeBeforeTheyExpire()
    {
        var spark = new ParticleInstance
        {
            Position = new Vector3(2f, -1.015f, 1.4f),
            Velocity = new Vector3(.01f, -.2f, .01f),
            Gravity = -4.15f,
            Life = .2f,
            Kind = 0,
            Color = Vector4.One,
            Size = .04f
        };

        ParticlePool.AdvanceParticle(ref spark, .05f);
        Assert.Equal(.35f, spark.Kind);
        Assert.Equal(Vector3.Zero, spark.Velocity);
        Assert.Equal(ParticlePool.SettledHammerSparkLifetime, spark.Life, 3);

        ParticlePool.AdvanceParticle(ref spark, .35f);
        Assert.True(spark.Life > 0);
        Assert.InRange(spark.Color.W, .50f, .60f);

        ParticlePool.AdvanceParticle(ref spark, .50f);
        Assert.Equal(0, spark.Life);
        Assert.Equal(0, spark.Color.W);
    }

    [Fact]
    public void GrindingAxeFollowsTheHorizontalPointerDirection()
    {
        var alignment = ForgeGrindingAlignment.Fallback("bearded-axe");
        var camera = ForgeCameraRig.GetGrindingAnchor(engaged: true);
        var forward = Vector3.Normalize(camera.Target - camera.Position);
        var screenRight = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var leftDragOffset = ForgeWorkpieceMotion.GrindingStrokeOffset(0, alignment);
        var rightDragOffset = ForgeWorkpieceMotion.GrindingStrokeOffset(1, alignment);

        Assert.True(Vector3.Dot(leftDragOffset, screenRight) < -.30f);
        Assert.True(Vector3.Dot(rightDragOffset, screenRight) > .30f);
    }

    [Theory]
    [InlineData(ForgeStationId.WaterVat)]
    [InlineData(ForgeStationId.OilVat)]
    public void QuenchBathsAreVisibleFromTheTransferCamera(ForgeStationId station)
    {
        var placement = ForgeWorkbenchLayout.Stations.Single(item => item.Id == station);
        var center = (placement.BoundsMin + placement.BoundsMax) * .5f;
        var pose = ForgeCameraRig.GetTransferAnchor();
        var view = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, 16f / 9f, .08f, 40f);
        var clip = Vector4.Transform(new Vector4(center, 1), view * projection);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);

        Assert.InRange(ndc.X, -1, 1);
        Assert.InRange(ndc.Y, -1, 1);
    }

    [Theory]
    [InlineData(ForgeStateId.Heating, ForgeStationId.Bellows)]
    [InlineData(ForgeStateId.Heating, ForgeStationId.Anvil)]
    [InlineData(ForgeStateId.Grinding, ForgeStationId.Grinder)]
    public void ActionableStationCentersRemainRaycastableFromStageCamera(ForgeStateId state, ForgeStationId expected)
    {
        var placement = ForgeWorkbenchLayout.Stations.Single(item => item.Id == expected);
        var center = (placement.BoundsMin + placement.BoundsMax) * .5f;
        var pose = ForgeCameraRig.GetAnchor(state);
        var view = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, 16f / 9f, .08f, 40f);
        var clip = Vector4.Transform(new Vector4(center, 1), view * projection);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        Assert.InRange(ndc.X, -1, 1);
        Assert.InRange(ndc.Y, -1, 1);
        var pixel = new Vector2((ndc.X * .5f + .5f) * 1600, (1 - (ndc.Y * .5f + .5f)) * 900);
        var ray = ForgeRaycaster.CreateRay(pixel, new Vector2(1600, 900), view, projection);

        Assert.True(ForgeWorkbenchLayout.TryHitInteractive(ray, state, canProceed: true, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShapeRevisionChangesOnlyWhenGeometryChanges()
    {
        var shape = new LatticeShapeSimulation();
        shape.Reset(new ShapeTemplateDefinition("sword", 1, 1, 1, .15, .48));
        var initial = shape.Revision;
        shape.AddDamage(.1);
        Assert.True(shape.Revision > initial);
        var afterDamage = shape.Revision;
        shape.Flip();
        Assert.True(shape.Revision > afterDamage);
    }

    [Fact]
    public void ShapeTransitionCompressesThenSettlesOnThePermanentRevision()
    {
        var transition = new ForgeShapeTransition();
        var before = new[] { new ShapeCellSnapshot(48, 16, 1, .9, 0, 0, 0) };
        var after = new[] { new ShapeCellSnapshot(48, 16, 1, .55, 0, 0, .65) };
        transition.SetImmediate(before);
        transition.Queue(after, .5, .5, 1);

        Assert.True(transition.Commit(reducedMotion: false));
        Assert.True(transition.Update(ForgeShapeTransition.Duration * .5f));
        var middle = transition.Cells[48 * WorkpieceMeshBuilder.Rows + 16];
        Assert.InRange(middle.Formation, .30, .64);
        Assert.True(middle.Thickness < before[0].Thickness);

        transition.Update(ForgeShapeTransition.Duration);
        var settled = transition.Cells[48 * WorkpieceMeshBuilder.Rows + 16];
        Assert.False(transition.IsAnimating);
        Assert.Equal(after[0].Thickness, settled.Thickness, 6);
        Assert.Equal(after[0].Formation, settled.Formation, 6);
    }

    [Fact]
    public void MaterialLibraryUsesPbrRangesAndHeatIsContinuous()
    {
        var library = new MaterialLibrary();
        Assert.All(library.All, material => Assert.True(material.IsValid));
        var stone = library[ForgeMaterialId.Stone];
        Assert.Equal(0, stone.Metallic);
        Assert.InRange(stone.Roughness, .82f, .95f);
        Assert.True(stone.AmbientOcclusion >= .8f);
        var dark = MaterialLibrary.HeatColor(.25);
        var orange = MaterialLibrary.HeatColor(.76);
        var yellow = MaterialLibrary.HeatColor(.96);
        Assert.True(dark.X < orange.X && orange.Y < yellow.Y);
        Assert.True(yellow.Z >= orange.Z);
    }

    [Fact]
    public void GlesShadersExplicitlyConvertIntegerMathToFloat()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scene = (string)typeof(ForgeSceneRenderer).GetField("SceneFragmentShader", flags)!.GetRawConstantValue()!;
        var postVertex = (string)typeof(ForgeSceneRenderer).GetField("PostVertexShader", flags)!.GetRawConstantValue()!;

        Assert.Contains("float(tile%2)", scene);
        Assert.Contains("for(int i=0;i<12;i++)", scene);
        Assert.Contains("taps[i]*texel", scene);
        Assert.DoesNotContain("fract(sin(dot(gl_FragCoord", scene);
        Assert.DoesNotContain("(tile%2)*.5", scene);
        Assert.Contains("float((gl_VertexID<<1)&2)", postVertex);
    }

    [Fact]
    public void ProgramPointSizeIsOnlyEnabledForDesktopOpenGl()
    {
        Assert.True(ForgeSceneRenderer.ShouldEnableProgramPointSize(glesContext: false));
        Assert.False(ForgeSceneRenderer.ShouldEnableProgramPointSize(glesContext: true));
    }

    [Fact]
    public void PostCompositeUsesExactCoverageWithoutNeighborDilationAndOutputsOpaqueFrames()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var post = (string)typeof(ForgeSceneRenderer).GetField("PostFragmentShader", flags)!.GetRawConstantValue()!;

        Assert.Contains("coverage=clamp(source.a,0.,1.)", post);
        Assert.DoesNotContain("coveredWeight", post);
        Assert.DoesNotContain("coverage=max(coverage,neighbor.a)", post);
        Assert.Contains("FragColor=vec4(color*vignette,1.)", post);
    }

    [Fact]
    public void SceneShaderUsesStablePbrNormalMappingAndThermalEmission()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scene = (string)typeof(ForgeSceneRenderer).GetField("SceneFragmentShader", flags)!.GetRawConstantValue()!;

        Assert.Contains("flat in float vMaterial", scene);
        Assert.Contains("uNormalAtlas", scene);
        Assert.Contains("distributionGGX", scene);
        Assert.Contains("uForgeLightIntensity", scene);
        Assert.Contains("uForgeProgress", scene);
        Assert.Contains("scaleCover", scene);
        Assert.Contains("uMaterialEmissive", scene);
        Assert.Contains("hotSurface", scene);
        Assert.Contains("EmissionColor", scene);
        Assert.Contains("uEnvironmentMap", scene);
        Assert.Contains("uBrdfLut", scene);
        Assert.Contains("textureLod", scene);
        Assert.Contains("fillDirection", scene);
        Assert.Contains("ambientSpecular", scene);
        Assert.Contains("uAmbientStrength", scene);
        Assert.Contains("uTaskLightDirection", scene);
        Assert.Contains("taskCone", scene);
        Assert.Contains("uRimLightDirection", scene);
        Assert.Contains("specularOcclusion", scene);
        Assert.Contains("vec2 taps[12]", scene);
        Assert.Contains("uWorkpieceLightStart", scene);
        Assert.Contains("uWorkpieceLightEnd", scene);
        Assert.Contains("glowAttenuation", scene);
        Assert.Contains("uIncandescenceColor", scene);
        Assert.Contains("scaleTransmission", scene);
        Assert.Contains("localThermal", scene);
        Assert.Contains("bool isWorkpiece=(m==10||m==13)", scene);
        Assert.Contains("float overheat=smoothstep(.80,1.06,thermal)", scene);
        Assert.Contains("float thermalPulse", scene);
    }

    [Fact]
    public void OverheatFeedbackAddsIronScaleAndStrongerThermalBloom()
    {
        Assert.Equal(0, ForgeSceneRenderer.OverheatVisualIntensity(
            ForgeStateId.Hammering, ForgeHeatBand.Burnt, 1.08));
        Assert.Equal(0, ForgeSceneRenderer.OverheatVisualIntensity(
            ForgeStateId.Heating, ForgeHeatBand.DarkRed, .37));

        var cherry = ForgeSceneRenderer.OverheatVisualIntensity(
            ForgeStateId.Heating, ForgeHeatBand.CherryRed, .57);
        var orange = ForgeSceneRenderer.OverheatVisualIntensity(
            ForgeStateId.Heating, ForgeHeatBand.OrangeRed, .76);
        var yellow = ForgeSceneRenderer.OverheatVisualIntensity(
            ForgeStateId.Heating, ForgeHeatBand.Yellow, .94);
        var burnt = ForgeSceneRenderer.OverheatVisualIntensity(
            ForgeStateId.Reheat, ForgeHeatBand.Burnt, 1.08);
        Assert.True(cherry > 0);
        Assert.True(cherry < orange && orange < yellow && yellow < burnt);
        Assert.InRange(burnt, 1.10f, 1.25f);

        var cherryRate = ForgeSceneRenderer.ThermalSparkRate(
            ForgeStateId.Heating, ForgeHeatBand.CherryRed, .57);
        var orangeRate = ForgeSceneRenderer.ThermalSparkRate(
            ForgeStateId.Heating, ForgeHeatBand.OrangeRed, .76);
        var yellowRate = ForgeSceneRenderer.ThermalSparkRate(
            ForgeStateId.Heating, ForgeHeatBand.Yellow, .94);
        var burntRate = ForgeSceneRenderer.ThermalSparkRate(
            ForgeStateId.Heating, ForgeHeatBand.Burnt, 1.08);
        Assert.True(cherryRate < orangeRate && orangeRate < yellowRate && yellowRate < burntRate);
        Assert.InRange(cherryRate, 3f, 6f);
        Assert.InRange(burntRate, 215f, 225f);

        var coldBloom = ForgeSceneRenderer.BloomStrengthFor(enabled: true, degraded: false, .20);
        var hotBloom = ForgeSceneRenderer.BloomStrengthFor(enabled: true, degraded: false, 1.08);
        Assert.InRange(coldBloom, .02f, .04f);
        Assert.InRange(hotBloom, .22f, .24f);
        Assert.True(hotBloom > coldBloom * 7);
        Assert.True(ForgeSceneRenderer.BloomStrengthFor(true, true, 1.08) < hotBloom);
        Assert.Equal(0, ForgeSceneRenderer.BloomStrengthFor(false, false, 1.08));

        Assert.Equal(0, ForgeSceneRenderer.WorkpieceLightIntensityFor(.52));
        var orangeGlow = ForgeSceneRenderer.WorkpieceLightIntensityFor(.76);
        var yellowGlow = ForgeSceneRenderer.WorkpieceLightIntensityFor(.94);
        var burntGlow = ForgeSceneRenderer.WorkpieceLightIntensityFor(1.08);
        Assert.True(orangeGlow > 0);
        Assert.True(orangeGlow < yellowGlow && yellowGlow < burntGlow);
        Assert.InRange(burntGlow, 7.19f, 7.21f);
        Assert.True(ForgeSceneRenderer.WorkpieceLightColorFor(.94).Y >
                    ForgeSceneRenderer.WorkpieceLightColorFor(.65).Y);
        var orangeIncandescence = ForgeSceneRenderer.IncandescenceColorFor(.76);
        var overheatedIncandescence = ForgeSceneRenderer.IncandescenceColorFor(1.06);
        Assert.True(orangeIncandescence.X > orangeIncandescence.Y * 3);
        Assert.True(overheatedIncandescence.Y > orangeIncandescence.Y);
        Assert.True(overheatedIncandescence.X > overheatedIncandescence.Y);
    }

    [Fact]
    public void WorkpieceRadianceFollowsTheMovingBilletAsALineLight()
    {
        var world = Matrix4x4.CreateRotationY(.42f) * Matrix4x4.CreateTranslation(1.2f, .4f, -.8f);
        var segment = ForgeSceneRenderer.WorkpieceGlowSegment(world, "duelling-longsword", null);

        Assert.True(Vector3.Distance(segment.Start, segment.End) > 1.5f);
        Assert.True(float.IsFinite(segment.Start.X) && float.IsFinite(segment.End.Z));
        Assert.NotEqual(segment.Start, segment.End);
    }

    [Fact]
    public void LightingCalibrationKeepsWorkStagesReadableWithoutOverexposingTheHearth()
    {
        var heatingExposure = ForgeSceneRenderer.ExposureFor(ForgeStateId.Heating);
        var hammeringExposure = ForgeSceneRenderer.ExposureFor(ForgeStateId.Hammering);

        Assert.InRange(heatingExposure, 1.02f, 1.07f);
        Assert.InRange(hammeringExposure, 1.10f, 1.15f);
        Assert.True(hammeringExposure > heatingExposure);
        Assert.InRange(ForgeSceneRenderer.AmbientStrengthFor(ForgeStateId.Hammering), 1.10f, 1.17f);
        Assert.InRange(ForgeSceneRenderer.AmbientStrengthFor(ForgeStateId.Grinding), 1.10f, 1.16f);
        Assert.True(ForgeSceneRenderer.TaskLightIntensityFor(ForgeStateId.Hammering) > 5f);
        Assert.True(ForgeSceneRenderer.EnvironmentStrengthFor(ForgeStateId.Inspection) > 1.2f);
        Assert.NotEqual(
            ForgeSceneRenderer.TaskLightPositionFor(ForgeStateId.Hammering),
            ForgeSceneRenderer.TaskLightPositionFor(ForgeStateId.Grinding));
        Assert.InRange(ForgeSceneRenderer.TaskLightDirectionFor(ForgeStateId.Hammering).Length(), .999f, 1.001f);
        Assert.True(ForgeSceneRenderer.RimLightIntensityFor(ForgeStateId.Inspection) >
                    ForgeSceneRenderer.RimLightIntensityFor(ForgeStateId.Heating));
        Assert.NotEqual(
            ForgeSceneRenderer.ShadowViewProjectionFor(ForgeStateId.Hammering),
            ForgeSceneRenderer.ShadowViewProjectionFor(ForgeStateId.Grinding));
        Assert.True(ForgeSceneRenderer.ContactShadow(ForgeStateId.Hammering).Z > 0);
        Assert.Equal(1f, ForgeSceneRenderer.RenderScaleFor(ForgeRenderQuality.High, degraded: false));
        Assert.Equal(.75f, ForgeSceneRenderer.RenderScaleFor(ForgeRenderQuality.High, degraded: true));
        Assert.Equal(.85f, ForgeSceneRenderer.RenderScaleFor(ForgeRenderQuality.Medium, degraded: false));
        Assert.Equal(.75f, ForgeSceneRenderer.RenderScaleFor(ForgeRenderQuality.Low, degraded: false));
    }

    [Fact]
    public void PostProcessContainsRestrainedBloomHeatShimmerAndContactShadow()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var post = (string)typeof(ForgeSceneRenderer).GetField("PostFragmentShader", flags)!.GetRawConstantValue()!;

        Assert.Contains("uBloomStrength", post);
        Assert.Contains("uDofStrength", post);
        Assert.Contains("uHeatCenter", post);
        Assert.Contains("uContactShadow", post);
        Assert.Contains("uDepth", post);
        Assert.Contains("depthOcclusion", post);
        Assert.Contains("smoothstep(.045,.28", post);
        Assert.Contains("wide/8.*.28", post);
        Assert.Contains("uExposure", post);
        Assert.Contains("ssao*.23", post);
    }

    [Fact]
    public void ParticleInstancesCarryVelocityGravityAndEffectKindWithoutAllocations()
    {
        Assert.Equal(56, Marshal.SizeOf<ParticleInstance>());
        var particle = new ParticleInstance { Velocity = Vector3.UnitY, Gravity = -2.4f, Kind = 3 };
        Assert.Equal(Vector3.UnitY, particle.Velocity);
        Assert.Equal(-2.4f, particle.Gravity);
        Assert.Equal(3, particle.Kind);
    }

    [Fact]
    public void NumericsCameraConventionPlacesWorkshopOriginInsideClipSpace()
    {
        var pose = ForgeCameraRig.GetAnchor(ForgeStateId.RecipeSelect);
        var view = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, 16f / 9f, .08f, 40f);
        var clip = Vector4.Transform(new Vector4(Vector3.Zero, 1), view * projection);

        Assert.True(clip.W > 0);
        Assert.InRange(clip.X / clip.W, -1, 1);
        Assert.InRange(clip.Y / clip.W, -1, 1);
        Assert.InRange(clip.Z / clip.W, 0, 1);
    }

    [Fact]
    public void InspectionCameraPresentsTheWeaponAcrossMostOfTheViewport()
    {
        var camera = ForgeCameraRig.GetAnchor(ForgeStateId.Inspection);
        var workpiece = ForgeWorkpieceMotion.PoseFor(
            ForgeStateId.Inspection,
            flipped: false,
            QuenchMedium.Water).ToMatrix();
        var profile = WorkpieceMeshBuilder.ProfileFor("duelling-longsword");
        var view = Matrix4x4.CreateLookAt(camera.Position, camera.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(camera.FieldOfView, 16f / 9f, .08f, 40f);
        var first = Project(new Vector3(-profile.Length * .5f, 0, 0));
        var second = Project(new Vector3(profile.Length * .5f, 0, 0));
        var center = Project(Vector3.Zero);
        var widthAxis = Vector2.Distance(new Vector2(center.X, center.Y), new Vector2(Project(Vector3.UnitZ).X, Project(Vector3.UnitZ).Y));
        var thicknessAxis = Vector2.Distance(new Vector2(center.X, center.Y), new Vector2(Project(Vector3.UnitY).X, Project(Vector3.UnitY).Y));

        Assert.InRange(Math.Abs(second.X - first.X), 1.15f, 1.90f);
        Assert.InRange(first.Y, -.70f, .70f);
        Assert.InRange(second.Y, -.70f, .70f);
        Assert.True(widthAxis > thicknessAxis * 1.5f,
            $"Inspection must show the blade face, not the edge: width={widthAxis}, thickness={thicknessAxis}.");
        return;

        Vector3 Project(Vector3 local)
        {
            var world = Vector3.Transform(local, workpiece);
            var clip = Vector4.Transform(new Vector4(world, 1), view * projection);
            return new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        }
    }

    [Fact]
    public void HeatingCameraFacesTheHearthHeadOn()
    {
        var pose = ForgeCameraRig.GetAnchor(ForgeStateId.Heating);

        Assert.InRange(Math.Abs(pose.Position.X - pose.Target.X), 0, .01f);
        Assert.InRange(Math.Abs(pose.Target.X + 3.05f), 0, .01f);
        Assert.True(pose.Position.Z > pose.Target.Z + 5f);
        Assert.True(pose.Position.Z < pose.Target.Z + 5.8f);
        Assert.InRange(pose.FieldOfView, .55f, .57f);
    }

    [Theory]
    [InlineData(ForgeStateId.Heating)]
    [InlineData(ForgeStateId.Reheat)]
    public void HeatingWorkpieceEntersTheHearthPerpendicularToTheMouth(ForgeStateId state)
    {
        var pose = ForgeWorkpieceMotion.PoseFor(state, false, QuenchMedium.Water);
        var lengthAxis = Vector3.Transform(Vector3.UnitX, pose.Rotation);
        var thicknessAxis = Vector3.Transform(Vector3.UnitY, pose.Rotation);

        Assert.InRange(lengthAxis.X, -.02f, .02f);
        Assert.InRange(lengthAxis.Y, -.02f, .02f);
        Assert.InRange(lengthAxis.Z, -1.001f, -.98f);
        Assert.InRange(thicknessAxis.X, -.02f, .02f);
        Assert.InRange(thicknessAxis.Y, .98f, 1.001f);
        Assert.InRange(thicknessAxis.Z, -.02f, .02f);
        Assert.InRange(pose.Position.X, -3.06f, -3.04f);
        Assert.InRange(pose.Position.Y, -.54f, -.50f);
    }

    [Fact]
    public void WorkpieceTransferUsesALiftArcAndSettlesOnTheDestinationTool()
    {
        var motion = new ForgeWorkpieceMotion();
        motion.SetTarget(ForgeStateId.Heating, false, QuenchMedium.Water, reducedMotion: false);
        motion.Update(1, 0);
        motion.SetTarget(ForgeStateId.Hammering, false, QuenchMedium.Water, reducedMotion: false);

        motion.Update(.31f, .31f);
        Assert.True(motion.IsTransferring);
        Assert.True(motion.CurrentPose.Position.Y > .53f);

        motion.Update(1, 1.31f);
        Assert.False(motion.IsTransferring);
        Assert.Equal(new Vector3(-.10f, .53f, .18f), motion.CurrentPose.Position, new Vector3Tolerance(.001f));
    }

    [Fact]
    public void ReheatingAlignsOutsideTheHearthThenInsertsThroughTheMouth()
    {
        var motion = new ForgeWorkpieceMotion();
        motion.SetTarget(ForgeStateId.Hammering, false, QuenchMedium.Water, reducedMotion: false);
        motion.Update(1, 0);
        motion.SetTarget(ForgeStateId.Reheat, false, QuenchMedium.Water, reducedMotion: false);
        var finalPose = ForgeWorkpieceMotion.PoseFor(ForgeStateId.Reheat, false, QuenchMedium.Water);

        motion.Update(.50f, .50f);
        var outsideMouth = motion.CurrentPose;
        Assert.InRange(Math.Abs(outsideMouth.Position.X - finalPose.Position.X), 0, .025f);
        Assert.True(outsideMouth.Position.Z > finalPose.Position.Z + 1.35f);

        motion.Update(.18f, .68f);
        var inserting = motion.CurrentPose;
        Assert.InRange(Math.Abs(inserting.Position.X - finalPose.Position.X), 0, .005f);
        Assert.True(inserting.Position.Z < outsideMouth.Position.Z - .35f);
        Assert.True(inserting.Position.Z > finalPose.Position.Z + .45f);

        motion.Update(.30f, .98f);
        Assert.False(motion.IsTransferring);
        Assert.Equal(finalPose.Position, motion.CurrentPose.Position, new Vector3Tolerance(.001f));
    }

    [Fact]
    public void InspectionMotionSupportsDragInertiaAndWeaponZoom()
    {
        var motion = new ForgeWorkpieceMotion();
        motion.SetTarget(ForgeStateId.Inspection, false, QuenchMedium.Water, reducedMotion: false);
        motion.Update(1, 0);
        var resting = motion.CurrentPose;

        motion.BeginInspectionDrag();
        motion.RotateInspection(.34f, -.16f, .016f);
        motion.Update(.016f, .016f);
        var dragged = motion.CurrentPose;
        Assert.True(Math.Abs(Quaternion.Dot(resting.Rotation, dragged.Rotation)) < .995f);

        motion.AddInspectionZoom(.18f);
        motion.Update(.016f, .032f);
        Assert.True(motion.CurrentPose.Scale.X > dragged.Scale.X + .10f);

        motion.EndInspectionDrag();
        var released = motion.CurrentPose.Rotation;
        motion.Update(.08f, .112f);
        Assert.True(Math.Abs(Quaternion.Dot(released, motion.CurrentPose.Rotation)) < .9999f);
    }

    [Fact]
    public void QuenchMotionTracksImmersionDepthWithoutChangingForgeState()
    {
        var motion = new ForgeWorkpieceMotion();
        motion.SetTarget(ForgeStateId.Quenching, false, QuenchMedium.Oil, reducedMotion: false);
        motion.Update(1, 0);
        var aboveBath = motion.CurrentPose.Position.Y;

        motion.SetQuench(.82, .55);
        motion.Update(.6f, .6f);

        Assert.Equal(ForgeWorkpieceStation.OilVat, ForgeWorkpieceMotion.StationFor(ForgeStateId.Quenching, QuenchMedium.Oil));
        Assert.True(motion.CurrentPose.Position.Y < aboveBath - .65f);
    }

    [Fact]
    public void WorkpiecePointerRejectsEmptyCellsOutsideTheForgedOutline()
    {
        var cells = new[]
        {
            new ShapeCellSnapshot(10, 10, 1, .2, 0, 0),
            new ShapeCellSnapshot(48, 16, 0, .2, 0, 0)
        };

        Assert.True(WorkpieceMeshBuilder.IsOccupiedAt(
            cells,
            new Vector2(10.5f / WorkpieceMeshBuilder.Columns, 10.5f / WorkpieceMeshBuilder.Rows)));
        Assert.False(WorkpieceMeshBuilder.IsOccupiedAt(
            cells,
            new Vector2(48.5f / WorkpieceMeshBuilder.Columns, 16.5f / WorkpieceMeshBuilder.Rows)));
        Assert.False(WorkpieceMeshBuilder.IsOccupiedAt(cells, new Vector2(.9f, .9f)));
    }

    [Fact]
    public void ToolMotionAnimatesBellowsHammerFaceAndGrindingWheel()
    {
        var tools = new ForgeToolMotion();
        var restBellows = tools.BellowsTransform;
        var restHammer = tools.HammerTransform(ForgeStateId.Hammering);

        tools.PumpBellows(1);
        tools.Strike(new Vector3(.2f, .5f, .1f), 1);
        tools.Update(.2f, ForgeStateId.Grinding, HammerFace.Flat, grinderSpeedScale: 1);

        Assert.NotEqual(restBellows, tools.BellowsTransform);
        Assert.NotEqual(restHammer, tools.HammerTransform(ForgeStateId.Hammering));
        Assert.NotEqual(0, tools.GrinderAngle);
        Assert.True(IsFinite(new Vector3(
            tools.GrinderWheelTransform.M41,
            tools.GrinderWheelTransform.M42,
            tools.GrinderWheelTransform.M43)));
    }

    [Fact]
    public void GrindingWheelRotatesInPlaceWithoutVerticalRolling()
    {
        var tools = new ForgeToolMotion();
        var fixedCenter = Vector3.Transform(
            ForgeWorkbenchLayout.GrinderWheelPivotLocal,
            ForgeWorkbenchLayout.Grinder.Transform);

        tools.Update(.37f, ForgeStateId.Grinding, HammerFace.Flat, grinderSpeedScale: 1);
        var animatedCenter = Vector3.Transform(
            ForgeWorkbenchLayout.GrinderWheelPivotLocal,
            tools.GrinderWheelTransform);

        Assert.True(Vector3.Distance(fixedCenter, animatedCenter) < .0001f,
            "The dressed stone must spin around its axle without orbiting up and down.");
    }

    [Fact]
    public void HammerMeshUsesHandleButtPivotAndCrosswiseHead()
    {
        var mesh = ProceduralMeshFactory.CreateHammer();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in mesh.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        Assert.InRange(min.X, -.20f, 0);
        Assert.True(max.X > 1.70f);
        Assert.True(max.Z - min.Z > 1.45f, "Hammer head must span across the handle.");
        Assert.True(mesh.IsFinite);
    }

    [Fact]
    public void RuntimeHammerGlbUsesYForTheHeadAndAnchorsTheFlatFaceAtItsMinimum()
    {
        var model = ReadGlb(Path.Combine(FindModelsDirectory(), "hammer.glb"));
        var headPositions = model.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Where(primitive => primitive.Material?.Name == "Iron")
            .SelectMany(primitive => primitive.GetVertexAccessor("POSITION")!.AsVector3Array())
            .ToArray();
        var min = headPositions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = headPositions.Aggregate(new Vector3(float.MinValue), Vector3.Max);

        Assert.True(max.Y - min.Y > 1.6f);
        Assert.True(max.Z - min.Z < .8f);
        Assert.Equal(min.Y, ForgeToolMotion.HammerFlatFaceCenterLocal.Y, 2);
    }

    [Fact]
    public void TongsJawOverlapsWorkpieceInsteadOfFloatingBehindIt()
    {
        var workpiece = ForgeWorkpieceMotion.PoseFor(
            ForgeStateId.Hammering,
            flipped: false,
            QuenchMedium.Water).ToMatrix();
        var attachment = ForgeSceneRenderer.TongsAttachmentTransform(workpiece);
        var jawCenter = Vector3.Transform(new Vector3(1.22f, 0, 0), attachment);
        Assert.True(Matrix4x4.Invert(workpiece, out var inverse));
        var localJawCenter = Vector3.Transform(jawCenter, inverse);

        Assert.InRange(localJawCenter.X, -1.36f, -1.20f);
        Assert.InRange(localJawCenter.Y, -.02f, .09f);
        Assert.InRange(localJawCenter.Z, -.03f, .05f);
    }

    [Fact]
    public void HammerFollowsPullGestureAndLightStrikeStillUsesFullSwingAnimation()
    {
        var tools = new ForgeToolMotion();
        tools.Update(.1f, ForgeStateId.Hammering, HammerFace.Flat);
        var ready = tools.HammerTransform(ForgeStateId.Hammering);

        tools.BeginHammerGesture(new Vector3(.15f, .55f, .18f));
        tools.UpdateHammerGesture(.75f);
        tools.Update(.1f, ForgeStateId.Hammering, HammerFace.Flat);
        var raised = tools.HammerTransform(ForgeStateId.Hammering);
        Assert.NotEqual(ready, raised);

        tools.EndHammerGesture();
        tools.Strike(new Vector3(.15f, .55f, .18f), ForgeHammerGesture.LightStrikeForce);
        tools.Update(ForgeToolMotion.HammerImpactTime - ForgeToolMotion.HammerRaiseDuration, ForgeStateId.Hammering, HammerFace.Flat);
        var contact = tools.HammerTransform(ForgeStateId.Hammering);
        Assert.NotEqual(ready, contact);
        Assert.True(contact.M42 < raised.M42);
    }

    [Fact]
    public void SuspendedHammerFollowsHoverTargetsWithoutMousePress()
    {
        var tools = new ForgeToolMotion();
        tools.Update(.1f, ForgeStateId.Hammering, HammerFace.Flat);
        var rest = tools.HammerTransform(ForgeStateId.Hammering);

        tools.SetHammerTarget(new Vector3(-.45f, .58f, .08f), true);
        tools.Update(.25f, ForgeStateId.Hammering, HammerFace.Flat);
        var firstTarget = tools.HammerTransform(ForgeStateId.Hammering);

        tools.SetHammerTarget(new Vector3(.42f, .58f, -.12f), true);
        tools.Update(.25f, ForgeStateId.Hammering, HammerFace.Flat);
        var secondTarget = tools.HammerTransform(ForgeStateId.Hammering);

        Assert.NotEqual(rest, firstTarget);
        Assert.NotEqual(firstTarget, secondTarget);
        Assert.True(secondTarget.M41 > firstTarget.M41 + .5f);
    }

    [Fact]
    public void FastClickPlaysRaiseImpactReboundAndEmitsImpactOnce()
    {
        var tools = new ForgeToolMotion();
        var impactPoint = new Vector3(-.12f, .56f, .14f);
        var rest = tools.HammerTransform(ForgeStateId.Hammering);
        var restHead = Vector3.Transform(ForgeToolMotion.HammerFlatFaceCenterLocal, rest);

        tools.Strike(impactPoint, ForgeHammerGesture.LightStrikeForce);
        Assert.True(tools.IsHammerStrikeActive);

        tools.Update(.08f, ForgeStateId.Hammering, HammerFace.Flat);
        var raised = tools.HammerTransform(ForgeStateId.Hammering);
        var raisedHead = Vector3.Transform(ForgeToolMotion.HammerFlatFaceCenterLocal, raised);
        Assert.True(raisedHead.Y > restHead.Y + .3f);
        Assert.False(tools.TryConsumeHammerImpact(out _, out _));

        tools.Update(ForgeToolMotion.HammerImpactTime - .08f, ForgeStateId.Hammering, HammerFace.Flat);
        var contact = tools.HammerTransform(ForgeStateId.Hammering);
        var contactHead = Vector3.Transform(ForgeToolMotion.HammerFlatFaceCenterLocal, contact);
        Assert.True(contactHead.Y < raisedHead.Y - .3f);
        Assert.True(tools.TryConsumeHammerImpact(out var actualPoint, out var intensity));
        Assert.Equal(impactPoint, actualPoint);
        Assert.Equal(ForgeHammerGesture.LightStrikeForce, intensity, 3);
        Assert.False(tools.TryConsumeHammerImpact(out _, out _));

        tools.Update(ForgeToolMotion.HammerStrikeDuration - ForgeToolMotion.HammerImpactTime, ForgeStateId.Hammering, HammerFace.Flat);
        Assert.False(tools.IsHammerStrikeActive);
        Assert.Equal(rest, tools.HammerTransform(ForgeStateId.Hammering));
    }

    [Fact]
    public void HammerHeadMeetsTheSelectedImpactPointWithoutSlidingPastIt()
    {
        var tools = new ForgeToolMotion();
        var impactPoint = new Vector3(.24f, .57f, -.08f);

        tools.Strike(impactPoint, .8);
        tools.Update(ForgeToolMotion.HammerImpactTime, ForgeStateId.Hammering, HammerFace.Flat);
        var transform = tools.HammerTransform(ForgeStateId.Hammering);
        var actualHead = Vector3.Transform(ForgeToolMotion.HammerFlatFaceCenterLocal, transform);
        var expectedHead = impactPoint + ForgeToolMotion.HammerContactFaceOffset;

        Assert.True(Vector3.Distance(actualHead, expectedHead) < .002f,
            $"Hammer head {actualHead} should contact {expectedHead}.");
    }

    [Fact]
    public void FlatHammerFacePointsDownAndGripExtendsTowardTheSmith()
    {
        var tools = new ForgeToolMotion();
        var target = new Vector3(-.10f, .56f, .11f);
        tools.Strike(target, .7);
        tools.Update(ForgeToolMotion.HammerImpactTime, ForgeStateId.Hammering, HammerFace.Flat);
        var transform = tools.HammerTransform(ForgeStateId.Hammering);

        var localOrigin = Vector3.Transform(Vector3.Zero, transform);
        var handleAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, transform) - localOrigin);
        var headAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, transform) - localOrigin);

        Assert.True(headAxis.Y > .98f, $"Flat face (-Y) should point down, actual +Y {headAxis}.");
        Assert.True(handleAxis.Z < -.98f, $"Grip-to-head axis should point toward -Z, actual {handleAxis}.");
    }

    [Fact]
    public void HeldHammerGripProjectsBelowTheHeadInTheForgeCamera()
    {
        var tools = new ForgeToolMotion();
        var target = new Vector3(-.10f, .56f, .11f);
        tools.SetHammerTarget(target, true);
        tools.Update(.6f, ForgeStateId.Hammering, HammerFace.Flat);
        var transform = tools.HammerTransform(ForgeStateId.Hammering);
        var grip = Vector3.Transform(Vector3.Zero, transform);
        var head = Vector3.Transform(ForgeToolMotion.HammerHeadCenterLocal, transform);
        var pose = ForgeCameraRig.GetAnchor(ForgeStateId.Hammering);
        var view = Matrix4x4.CreateLookAt(pose.Position, pose.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(pose.FieldOfView, 16f / 9f, .08f, 40f);
        var gripClip = Vector4.Transform(new Vector4(grip, 1), view * projection);
        var headClip = Vector4.Transform(new Vector4(head, 1), view * projection);
        var gripScreenY = 1 - (gripClip.Y / gripClip.W * .5f + .5f);
        var headScreenY = 1 - (headClip.Y / headClip.W * .5f + .5f);

        Assert.True(gripScreenY > headScreenY + .03f,
            $"Grip should render below the head: gripY={gripScreenY}, headY={headScreenY}.");
    }

    [Fact]
    public void AutomaticReadyPoseDoesNotSkipRaiseOrKeepThePreviousCharge()
    {
        var tools = new ForgeToolMotion();
        var target = new Vector3(-.10f, .56f, .11f);
        tools.SetHammerTarget(target, true);
        tools.Update(.6f, ForgeStateId.Hammering, HammerFace.Flat);

        var hoverHead = Vector3.Transform(
            ForgeToolMotion.HammerFlatFaceCenterLocal,
            tools.HammerTransform(ForgeStateId.Hammering));
        tools.BeginHammerGesture(target);
        tools.EndHammerGesture();
        tools.Strike(target, ForgeHammerGesture.LightStrikeForce);
        var strikeStartHead = Vector3.Transform(
            ForgeToolMotion.HammerFlatFaceCenterLocal,
            tools.HammerTransform(ForgeStateId.Hammering));

        Assert.True(Vector3.Distance(hoverHead, strikeStartHead) < .01f);

        tools.Update(ForgeToolMotion.HammerStrikeDuration, ForgeStateId.Hammering, HammerFace.Flat);
        var returnedHead = Vector3.Transform(
            ForgeToolMotion.HammerFlatFaceCenterLocal,
            tools.HammerTransform(ForgeStateId.Hammering));
        var expectedHoverHead = target + new Vector3(0, .50f, -.02f);
        Assert.True(Vector3.Distance(returnedHead, expectedHoverHead) < .02f);
        Assert.Equal(0, tools.HammerGesturePull);
    }

    [Fact]
    public void ChargingPullsTheHammerBackAwayFromTheCameraBeforeImpact()
    {
        var tools = new ForgeToolMotion();
        var target = new Vector3(-.10f, .56f, .11f);
        tools.SetHammerTarget(target, true);
        tools.Update(.6f, ForgeStateId.Hammering, HammerFace.Flat);
        var hoverHead = Vector3.Transform(
            ForgeToolMotion.HammerFlatFaceCenterLocal,
            tools.HammerTransform(ForgeStateId.Hammering));

        tools.BeginHammerGesture(target);
        tools.UpdateHammerGesture(1);
        tools.Update(.1f, ForgeStateId.Hammering, HammerFace.Flat);
        var raisedHead = Vector3.Transform(
            ForgeToolMotion.HammerFlatFaceCenterLocal,
            tools.HammerTransform(ForgeStateId.Hammering));

        Assert.True(raisedHead.Z < hoverHead.Z - .15f,
            $"Raised hammer should move away from the +Z camera: hover {hoverHead}, raised {raisedHead}.");
        Assert.True(raisedHead.Y > hoverHead.Y + .35f);
    }

    [Fact]
    public void HammerStrikeRemainsVisibleWhenBlowTransitionsToReheat()
    {
        var tools = new ForgeToolMotion();
        var rack = tools.HammerTransform(ForgeStateId.Reheat);

        tools.Strike(new Vector3(-.12f, .56f, .14f), ForgeHammerGesture.LightStrikeForce);
        tools.Update(.08f, ForgeStateId.Reheat, HammerFace.Flat);

        Assert.True(tools.IsHammerStrikeActive);
        Assert.NotEqual(rack, tools.HammerTransform(ForgeStateId.Reheat));

        tools.Update(ForgeToolMotion.HammerStrikeDuration, ForgeStateId.Reheat, HammerFace.Flat);
        Assert.False(tools.IsHammerStrikeActive);
        Assert.Equal(rack, tools.HammerTransform(ForgeStateId.Reheat));
    }

    private static string FindModelsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "BohemiX.Modules.Forge", "Assets", "Models");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate src/BohemiX.Modules.Forge/Assets/Models from the test output directory.");
    }

    private static ModelRoot ReadGlb(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ModelRoot.ReadGLB(stream);
        }
        catch (Exception error)
        {
            throw new InvalidDataException($"Unable to validate GLB '{path}'.", error);
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void AssertClosedByPosition(MeshData mesh)
    {
        var edges = new Dictionary<(PositionKey A, PositionKey B), int>();
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            AddEdge(mesh.Indices[i], mesh.Indices[i + 1]);
            AddEdge(mesh.Indices[i + 1], mesh.Indices[i + 2]);
            AddEdge(mesh.Indices[i + 2], mesh.Indices[i]);
        }

        Assert.All(edges.Values, count => Assert.Equal(2, count));
        return;

        void AddEdge(uint first, uint second)
        {
            var a = PositionKey.From(mesh.Vertices[first].Position);
            var b = PositionKey.From(mesh.Vertices[second].Position);
            var edge = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            edges[edge] = edges.GetValueOrDefault(edge) + 1;
        }
    }

    private static float AverageSurfaceHeight(MeshData mesh, bool edge)
    {
        var top = mesh.Vertices.Where(vertex => vertex.Normal.Y > .35f && vertex.Position.Y > 0).ToArray();
        var extent = top.Max(vertex => Math.Abs(vertex.Position.Z));
        var selected = top.Where(vertex => edge
            ? Math.Abs(vertex.Position.Z) > extent * .70f
            : Math.Abs(vertex.Position.Z) < extent * .22f).ToArray();
        Assert.NotEmpty(selected);
        return selected.Average(vertex => vertex.Position.Y);
    }

    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        var expectedElements = new[]
        {
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44
        };
        var actualElements = new[]
        {
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44
        };

        for (var index = 0; index < expectedElements.Length; index++)
        {
            Assert.InRange(actualElements[index] - expectedElements[index], -tolerance, tolerance);
        }
    }

    private static ShapeCellSnapshot[] TargetCells(ShapeTemplateDefinition definition)
    {
        var result = new List<ShapeCellSnapshot>();
        for (var x = 0; x < WorkpieceMeshBuilder.Columns; x++)
        for (var y = 0; y < WorkpieceMeshBuilder.Rows; y++)
        {
            var target = ForgeShapeProfileSampler.TargetAt(
                definition,
                x / (double)(WorkpieceMeshBuilder.Columns - 1),
                y / (double)(WorkpieceMeshBuilder.Rows - 1));
            if (target.Occupancy > .05)
            {
                result.Add(new ShapeCellSnapshot(x, y, target.Occupancy, target.Thickness, 0, 0, 1));
            }
        }
        return result.ToArray();
    }

    private sealed class TrackedResource(int id, ICollection<int> disposalOrder) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            disposalOrder.Add(id);
        }
    }

    private sealed class Vector3Tolerance(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 left, Vector3 right) => Vector3.Distance(left, right) <= tolerance;
        public int GetHashCode(Vector3 value) => value.GetHashCode();
    }

    private readonly record struct PositionKey(int X, int Y, int Z) : IComparable<PositionKey>
    {
        public static PositionKey From(Vector3 value) => new(
            (int)MathF.Round(value.X * 100_000),
            (int)MathF.Round(value.Y * 100_000),
            (int)MathF.Round(value.Z * 100_000));

        public int CompareTo(PositionKey other)
        {
            var x = X.CompareTo(other.X);
            if (x != 0) return x;
            var y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }
    }
}
