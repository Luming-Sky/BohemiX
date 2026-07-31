using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Engine;

public interface IShapeSimulation
{
    int Columns { get; }
    int Rows { get; }
    bool IsFlipped { get; }
    long Revision { get; }
    ShapeMetrics Metrics { get; }
    void Reset(ShapeTemplateDefinition template, ForgeBilletDefinition? billet = null);
    void Flip();
    void ApplyHammer(
        double x,
        double y,
        double force,
        HammerFace face,
        double plasticity,
        double damage,
        ForgeFormationGuide? guide = null);
    void AddDamage(double amount);
    IReadOnlyList<ShapeCellSnapshot> SnapshotCells();
}
