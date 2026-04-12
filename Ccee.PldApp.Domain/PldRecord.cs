namespace Ccee.PldApp.Domain;

/// <summary>
/// Represents a single hourly PLD value returned by the gateway.
/// </summary>
public sealed record PldRecord
{
    /// <summary>
    /// Submarket label returned by CCEE, such as SUDESTE or SUL.
    /// </summary>
    public required string Submercado { get; init; }
    /// <summary>
    /// Calendar date of the PLD value.
    /// </summary>
    public required DateOnly Dia { get; init; }
    /// <summary>
    /// Hour of the day in the source dataset.
    /// </summary>
    public required int Hora { get; init; }
    /// <summary>
    /// PLD amount in R$/MWh.
    /// </summary>
    public required decimal Valor { get; init; }
}
