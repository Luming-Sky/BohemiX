using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct ParticleInstance
{
    public Vector3 Position;
    public Vector4 Color;
    public float Size;
    public float Life;
    public Vector3 Velocity;
    public float Gravity;
    public float Kind;
}

public sealed class ParticlePool : IDisposable
{
    internal const float SettledHammerSparkLifetime = .78f;
    private readonly ParticleInstance[] particles;
    private readonly GL gl;
    private readonly uint vertexArray;
    private readonly uint vertexBuffer;
    private readonly Random random = new(11837);
    private int cursor;
    private bool disposed;

    public ParticlePool(GL gl, int capacity)
    {
        this.gl = gl;
        particles = new ParticleInstance[Math.Max(32, capacity)];
        vertexArray = gl.GenVertexArray();
        vertexBuffer = gl.GenBuffer();
        gl.BindVertexArray(vertexArray);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer);
        unsafe { gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(particles.Length * Marshal.SizeOf<ParticleInstance>()), null, BufferUsageARB.StreamDraw); }
        var stride = (uint)Marshal.SizeOf<ParticleInstance>();
        gl.EnableVertexAttribArray(0);
        unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0); }
        gl.EnableVertexAttribArray(1);
        unsafe { gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float))); }
        gl.EnableVertexAttribArray(2);
        unsafe { gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float))); }
        gl.EnableVertexAttribArray(3);
        unsafe { gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float))); }
        gl.EnableVertexAttribArray(4);
        unsafe { gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, (void*)(13 * sizeof(float))); }
        gl.EnableVertexAttribArray(5);
        unsafe { gl.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float))); }
        gl.VertexAttribDivisor(0, 1);
        gl.VertexAttribDivisor(1, 1);
        gl.VertexAttribDivisor(2, 1);
        gl.VertexAttribDivisor(3, 1);
        gl.VertexAttribDivisor(4, 1);
        gl.VertexAttribDivisor(5, 1);
    }

    public int Capacity => particles.Length;
    public void Update(float seconds)
    {
        for (var i = 0; i < particles.Length; i++)
        {
            AdvanceParticle(ref particles[i], seconds);
        }
    }

    internal static void AdvanceParticle(ref ParticleInstance particle, float seconds)
    {
        if (particle.Life <= 0 || seconds <= 0) return;
        particle.Life -= seconds;
        if (particle.Life <= 0)
        {
            // Every pool slot is drawn each frame. An expired slot must therefore be
            // explicitly made transparent; otherwise its last visible GPU instance
            // remains frozen forever after simulation stops advancing it.
            particle.Life = 0;
            particle.Color.W = 0;
            particle.Size = 0;
            particle.Velocity = Vector3.Zero;
            particle.Gravity = 0;
            return;
        }

        var previousPosition = particle.Position;
        particle.Velocity.Y += particle.Gravity * seconds;
        particle.Position += particle.Velocity * seconds;
        if (particle.Kind < .5f)
        {
            ResolveHammerSparkCollision(ref particle, previousPosition);
        }
        particle.Size *= 1 + seconds * (particle.Kind is >= 1.5f and < 2.5f ? .65f : .16f);
        particle.Color.W = particle.Kind is >= .3f and < .5f
            ? Math.Clamp(particle.Life / SettledHammerSparkLifetime, 0, 1)
            : Math.Clamp(particle.Life * 2.4f, 0, 1);
    }

    internal static float HammerSparkCollisionHeight(Vector3 position) =>
        position.X is >= -1.66f and <= 1.02f && position.Z is >= -.56f and <= .80f
            ? .46f
            : -1.02f;

    private static void ResolveHammerSparkCollision(ref ParticleInstance particle, Vector3 previousPosition)
    {
        if (particle.Velocity.Y >= 0) return;
        var surface = HammerSparkCollisionHeight(particle.Position);
        if (previousPosition.Y < surface || particle.Position.Y > surface) return;

        var firstBounce = particle.Kind < .1f;
        particle.Position.Y = surface + .006f;
        particle.Velocity.Y = -particle.Velocity.Y * (firstBounce ? .34f : .18f);
        particle.Velocity.X *= firstBounce ? .72f : .46f;
        particle.Velocity.Z *= firstBounce ? .72f : .46f;
        particle.Kind = .2f;
        if (particle.Velocity.LengthSquared() < .055f)
        {
            particle.Velocity = Vector3.Zero;
            particle.Gravity = 0;
            particle.Kind = .35f;
            // A landed spark keeps a short cooling afterglow before the ordinary
            // expiry path clears its GPU instance.
            particle.Life = Math.Max(particle.Life, SettledHammerSparkLifetime);
        }
    }

    public void EmitHammer(Vector3 origin, double intensity, ForgeHeatBand heat)
    {
        var heatFactor = HammerHeatFactor(heat);
        EmitHammerSparks(origin, intensity, heatFactor, HammerSparkBurstCount(intensity, heat), primaryBurst: true);
        var debrisCount = Math.Clamp((int)(4 + intensity * 8), 4, 12);
        for (var i = 0; i < debrisCount; i++)
        {
            var direction = Vector3.Normalize(new Vector3((float)(random.NextDouble() - .5), (float)(.2 + random.NextDouble() * .65), (float)(random.NextDouble() - .5)));
            var debris = new Vector4(.18f, .16f, .14f, .92f);
            Add(new ParticleInstance { Position = origin, Color = debris, Size = .022f + (float)random.NextDouble() * .032f, Life = .28f + (float)random.NextDouble() * .34f, Gravity = -3.1f, Kind = 3 }, direction * (float)(.30 + intensity * .55));
        }
    }

    public void EmitHammerAfterglow(Vector3 origin, double intensity, ForgeHeatBand heat, int count) =>
        EmitHammerSparks(origin, intensity * .82, HammerHeatFactor(heat), Math.Clamp(count, 1, 10), primaryBurst: false);

    internal static int HammerSparkBurstCount(double intensity, ForgeHeatBand heat)
    {
        var heatFactor = HammerHeatFactor(heat);
        return Math.Clamp((int)Math.Round(18 + intensity * 34 + heatFactor * 12), 20, 68);
    }

    private void EmitHammerSparks(Vector3 origin, double intensity, float heatFactor, int count, bool primaryBurst)
    {
        var heatColor = MaterialLibrary.HeatColor(Math.Clamp(.28 + heatFactor * .62, 0, 1));
        for (var i = 0; i < count; i++)
        {
            var azimuth = (float)(random.NextDouble() * MathF.Tau);
            var radial = .44f + (float)random.NextDouble() * .56f;
            var direction = Vector3.Normalize(new Vector3(
                MathF.Cos(azimuth) * radial,
                .12f + (float)random.NextDouble() * (primaryBurst ? 1.05f : .68f),
                MathF.Sin(azimuth) * radial));
            var color = new Vector4(
                1f,
                Math.Clamp(.48f + heatColor.Y * .55f + (float)random.NextDouble() * .12f, 0, 1),
                .045f + heatFactor * .095f,
                1f);
            var offset = new Vector3(
                (float)(random.NextDouble() - .5) * .055f,
                (float)random.NextDouble() * .025f,
                (float)(random.NextDouble() - .5) * .055f);
            var speed = (float)(1.05 + intensity * 2.15 + heatFactor * .65) *
                        (.72f + (float)random.NextDouble() * .62f);
            Add(new ParticleInstance
            {
                Position = origin + offset,
                Color = color,
                Size = .026f + (float)random.NextDouble() * .040f,
                Life = (primaryBurst ? .82f : .58f) + (float)random.NextDouble() * (primaryBurst ? .78f : .58f),
                Gravity = -4.15f,
                Kind = 0
            }, direction * speed);
        }
    }

    private static float HammerHeatFactor(ForgeHeatBand heat) => heat switch
    {
        ForgeHeatBand.DarkRed => .52f,
        ForgeHeatBand.CherryRed => .76f,
        ForgeHeatBand.OrangeRed => 1f,
        ForgeHeatBand.Yellow => 1.18f,
        _ => .22f
    };

    public void EmitFire(Vector3 origin, float intensity)
    {
        for (var i = 0; i < 1; i++)
        {
            var color = new Vector4(1, .18f + (float)random.NextDouble() * .40f, .025f, .8f);
            Add(new ParticleInstance { Position = origin + new Vector3((float)(random.NextDouble() - .5) * .45f, 0, (float)(random.NextDouble() - .5) * .30f), Color = color, Size = .035f + (float)random.NextDouble() * .055f, Life = .28f + (float)random.NextDouble() * .36f, Kind = 1 }, new Vector3(0, .20f + intensity * .24f, 0));
        }
    }

    public void EmitOverheatScale(Vector3 origin, float intensity, int count = 1)
    {
        intensity = Math.Clamp(intensity, 0, 1.25f);
        count = Math.Clamp(count, 1, 48);
        for (var i = 0; i < count; i++)
        {
            var outward = new Vector3(
                (float)(random.NextDouble() - .5) * 1.10f,
                .18f + (float)random.NextDouble() * .92f,
                .34f + (float)random.NextDouble() * 1.10f);
            var color = new Vector4(
                1f,
                .54f + (float)random.NextDouble() * .42f,
                .055f + (float)random.NextDouble() * .14f,
                1f);
            var offset = new Vector3(
                (float)(random.NextDouble() - .5) * .24f,
                (float)random.NextDouble() * .055f,
                (float)(random.NextDouble() - .5) * .18f);
            Add(new ParticleInstance
            {
                Position = origin + offset,
                Color = color,
                Size = .016f + (float)random.NextDouble() * .032f,
                Life = .28f + (float)random.NextDouble() * .38f,
                Gravity = -1.65f,
                Kind = 4
            }, Vector3.Normalize(outward) * (.72f + intensity * 1.02f));
        }
    }

    public void EmitSteam(Vector3 origin, float intensity)
    {
        var count = Math.Clamp((int)(8 + intensity * 18), 8, 30);
        for (var i = 0; i < count; i++)
        {
            var color = new Vector4(.74f, .80f, .78f, .58f);
            Add(new ParticleInstance { Position = origin + new Vector3((float)(random.NextDouble() - .5) * .55f, 0, (float)(random.NextDouble() - .5) * .55f), Color = color, Size = .08f + (float)random.NextDouble() * .16f, Life = .55f + (float)random.NextDouble() * .7f, Kind = 2 }, new Vector3((float)(random.NextDouble() - .5) * .14f, .25f + (float)random.NextDouble() * .25f, (float)(random.NextDouble() - .5) * .14f));
        }
    }

    public void EmitGrind(Vector3 origin, float intensity, float quality = .5f, int? requestedCount = null)
    {
        intensity = Math.Clamp(intensity, 0, 1.35f);
        quality = Math.Clamp(quality, 0, 1);
        var count = requestedCount ?? GrindingSparkCount(intensity, quality);
        count = Math.Clamp(count, 1, 18);
        var wheelTravel = -ForgeWorkpieceMotion.GrindingTangent;
        for (var i = 0; i < count; i++)
        {
            var spread = new Vector3(
                (float)(random.NextDouble() - .5) * .28f,
                -.08f + (float)random.NextDouble() * .34f,
                (float)(random.NextDouble() - .5) * .28f);
            var direction = Vector3.Normalize(wheelTravel + spread);
            var color = new Vector4(1, .50f + quality * .24f + (float)random.NextDouble() * .20f, .055f, 1);
            var offset = ForgeWorkpieceMotion.GrindingWheelAxis * (float)(random.NextDouble() - .5) * .055f;
            Add(new ParticleInstance
            {
                Position = origin + offset,
                Color = color,
                Size = .014f + (float)random.NextDouble() * (.018f + quality * .010f),
                Life = .16f + (float)random.NextDouble() * (.24f + intensity * .12f),
                Gravity = -2.35f,
                Kind = 4
            }, direction * (.72f + intensity * 1.28f) * (.78f + (float)random.NextDouble() * .44f));
        }
    }

    internal static int GrindingSparkCount(float intensity, float quality) =>
        Math.Clamp((int)MathF.Round(2 + Math.Clamp(intensity, 0, 1.35f) * (7 + Math.Clamp(quality, 0, 1) * 5)), 2, 18);

    public unsafe void Draw()
    {
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer);
        fixed (ParticleInstance* pointer = particles)
        {
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(particles.Length * Marshal.SizeOf<ParticleInstance>()), pointer);
        }
        gl.BindVertexArray(vertexArray);
        gl.DrawArraysInstanced(PrimitiveType.Points, 0, 1, (uint)particles.Length);
    }

    private void Add(ParticleInstance particle, Vector3 velocity)
    {
        ref var slot = ref particles[cursor++ % particles.Length];
        slot = particle;
        slot.Velocity = velocity;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gl.DeleteBuffer(vertexBuffer);
        gl.DeleteVertexArray(vertexArray);
    }
}
