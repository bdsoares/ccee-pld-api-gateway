namespace Ccee.PldApp.Domain;

/// <summary>
/// Indicates whether the response came from the local SQLite cache or directly from CCEE.
/// </summary>
public enum PldQuerySource
{
    Cache = 1,
    Ccee = 2
}
