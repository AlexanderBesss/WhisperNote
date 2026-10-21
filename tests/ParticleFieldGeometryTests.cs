using System;
using System.Linq;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The particle styles run their lattice inside the 30 fps path, so the shell and
// grain math is pinned here without WPF: points must stay on the unit sphere,
// every band must own a shell sector, and the grain layout must be deterministic.
public class ParticleFieldGeometryTests
{
    [Fact]
    public void SpherePointsSitOnTheUnitShellAndSpreadOverEveryBand()
    {
        var points = ParticleFieldGeometry.BuildSphere(400, SpectrumAnalyzer.BandCount);

        Assert.Equal(400, points.Length);
        Assert.All(points, point =>
        {
            var length = MathF.Sqrt(point.X * point.X + point.Y * point.Y + point.Z * point.Z);
            Assert.Equal(1f, length, 3);
            Assert.True(point.Longitude >= 0f && point.Longitude < 2f * MathF.PI);
            Assert.True(point.Band >= 0 && point.Band < SpectrumAnalyzer.BandCount);
        });

        Assert.Equal(SpectrumAnalyzer.BandCount, points.Select(point => point.Band).Distinct().Count());
    }

    [Fact]
    public void IdentityRotationKeepsThePointAndReportsCameraDepth()
    {
        var points = ParticleFieldGeometry.BuildSphere(64, SpectrumAnalyzer.BandCount);
        foreach (var point in points)
        {
            ParticleFieldGeometry.RotateProject(point, 1f, 0f, 1f, 0f, out var px, out var py, out var depth);
            Assert.Equal(point.X, px, 4);
            Assert.Equal(point.Y, py, 4);
            Assert.Equal(point.Z, depth, 4);
        }
    }

    [Fact]
    public void RotationStaysOnTheUnitShell()
    {
        var points = ParticleFieldGeometry.BuildSphere(100, SpectrumAnalyzer.BandCount);
        var angle = 0.7f;
        foreach (var point in points)
        {
            ParticleFieldGeometry.RotateProject(point,
                MathF.Cos(angle), MathF.Sin(angle), MathF.Cos(-0.4f), MathF.Sin(-0.4f),
                out var px, out var py, out var depth);

            var length = MathF.Sqrt(px * px + py * py + depth * depth);
            Assert.Equal(1f, length, 3);
        }
    }

    [Fact]
    public void GrainPatternIsDeterministicAndStaysInsideItsCell()
    {
        var first = ParticleFieldGeometry.BuildGrainPattern(120, 0x50414C);
        var second = ParticleFieldGeometry.BuildGrainPattern(120, 0x50414C);

        Assert.Equal(first.Length, second.Length);
        Assert.True(first.SequenceEqual(second));

        Assert.All(first, grain =>
        {
            Assert.True(grain.DX >= -0.32f && grain.DX <= 0.32f);
            Assert.True(grain.DY >= 0f && grain.DY < 1f);
            Assert.True(grain.Phase >= 0f && grain.Phase < 2f * MathF.PI);
        });
    }
}
