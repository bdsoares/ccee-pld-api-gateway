using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Application.Exceptions;

/// <summary>
/// Raised when neither the cache nor CCEE can provide records for a valid query.
/// </summary>
public sealed class PldQueryNotFoundException : Exception
{
    public PldQueryNotFoundException(PldQuery query)
        : base(BuildMessage(query))
    {
    }

    private static string BuildMessage(PldQuery query)
    {
        var normalized = query.Normalize();
        var dia = normalized.Dia?.ToString("yyyy-MM-dd") ?? "mais recente";
        var submercado = normalized.Submercado ?? "todos";
        return $"Nenhum registro encontrado para a consulta. Dia: {dia}. Submercado: {submercado}.";
    }
}
