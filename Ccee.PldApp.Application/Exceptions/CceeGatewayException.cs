namespace Ccee.PldApp.Application.Exceptions;

/// <summary>
/// Represents failures while communicating with or parsing data from CCEE.
/// </summary>
public sealed class CceeGatewayException : Exception
{
    public CceeGatewayException(string message)
        : base(message)
    {
    }

    public CceeGatewayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
