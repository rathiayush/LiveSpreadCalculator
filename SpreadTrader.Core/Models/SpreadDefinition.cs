namespace SpreadTrader.Core.Models;

/// <summary>
/// A calendar spread between two legs of the same underlying (e.g. July fut vs August fut).
/// Spread value is always computed as FarLeg.LastPrice - NearLeg.LastPrice.
/// </summary>
public sealed class SpreadDefinition
{
    public required string Id { get; set; } // stable id, e.g. "RELIANCE-JUL-AUG"
    public required string Underlying { get; set; } // e.g. "RELIANCE"
    public required InstrumentConfig NearLeg { get; set; } // e.g. July future
    public required InstrumentConfig FarLeg { get; set; } // e.g. August future
    public bool Enabled { get; set; } = true;
}
