using System;

namespace WhisperNote.Services;

public readonly record struct SphereParticle(float X, float Y, float Z, float Longitude, float Latitude, int Band);

public readonly record struct GrainOffset(float DX, float DY, float Phase);

// Pure geometry for the particle-based visualizer styles: placement, rotation and
// grain layout without WPF types, so the math stays unit-testable and the control
// only paints. All of it is static/precomputed: a 30 fps frame must not rebuild
// thousand-point lattices.
public static class ParticleFieldGeometry
{
    // Fibonacci lattice: evenly spread points on the unit sphere with no visible
    // polar clustering, like the shell in reference art of force fields.
    public static SphereParticle[] BuildSphere(int count, int bands)
    {
        var points = new SphereParticle[count];
        var goldenAngle = Math.PI * (3 - Math.Sqrt(5));

        for (int i = 0; i < count; i++)
        {
            var y = 1f - 2f * (i + 0.5f) / count;
            var radiusXZ = (float)Math.Sqrt(Math.Max(0f, 1f - y * y));
            var azimuth = (float)(goldenAngle * i);
            var x = radiusXZ * MathF.Cos(azimuth);
            var z = radiusXZ * MathF.Sin(azimuth);
            var longitude = MathF.Atan2(z, x) + MathF.PI;

            points[i] = new SphereParticle(
                x, y, z,
                longitude,
                MathF.Asin(Math.Clamp(y, -1f, 1f)),
                Math.Min(bands - 1, (int)(longitude / (2f * MathF.PI) * bands)));
        }

        return points;
    }

    // Spin about the vertical axis, then a fixed camera tilt; depth is the
    // camera-axis coordinate the renderer shades with.
    public static void RotateProject(
        SphereParticle p, float cosSpin, float sinSpin, float cosTilt, float sinTilt,
        out float px, out float py, out float depth)
    {
        var spunX = p.X * cosSpin + p.Z * sinSpin;
        var spunZ = -p.X * sinSpin + p.Z * cosSpin;
        px = spunX;
        py = p.Y * cosTilt - spunZ * sinTilt;
        depth = p.Y * sinTilt + spunZ * cosTilt;
    }

    // Jittered placement inside a cell plus a per-grain phase, from a fixed seed
    // so the grain layout is identical across runs and only its twinkle animates.
    public static GrainOffset[] BuildGrainPattern(int cells, int seed)
    {
        var grain = new GrainOffset[cells];
        var rng = new Random(seed);
        for (int i = 0; i < cells; i++)
            grain[i] = new GrainOffset(
                (float)(rng.NextDouble() * 0.64 - 0.32),
                (float)rng.NextDouble(),
                (float)(rng.NextDouble() * 2 * Math.PI));

        return grain;
    }
}
