using System.Globalization;
using System.Text.Json;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Infrastructure.Parsing;

/// <summary>
/// Converts raw CCEE JSON payloads into domain records used by the gateway.
/// </summary>
internal static class PldRecordParser
{
    private static readonly CultureInfo PtBrCulture = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] ValuePropertyNames = ["VALOR", "PLD_HORA"];

    /// <summary>
    /// Extracts and orders PLD records from a raw CCEE payload.
    /// </summary>
    public static IReadOnlyList<PldRecord> ParseRecords(JsonElement root)
    {
        return GetRecordsElement(root)
            .EnumerateArray()
            .Select((record, index) => ParseRecord(record, index))
            .OrderByDescending(record => record.Dia)
            .ThenByDescending(record => record.Hora)
            .ToArray();
    }

    private static JsonElement GetRecordsElement(JsonElement root)
    {
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new CceeGatewayException("A CCEE retornou success=false para a consulta informada.");

        if (!root.TryGetProperty("result", out var result))
            throw new CceeGatewayException("Resposta invalida da CCEE: campo 'result' ausente.");

        if (!result.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            throw new CceeGatewayException("Resposta invalida da CCEE: campo 'records' ausente.");

        return records;
    }

    private static PldRecord ParseRecord(JsonElement record, int index)
    {
        if (!TryGetString(record, "SUBMERCADO", out var submercado) ||
            !TryGetString(record, "DIA", out var diaText) ||
            !TryGetString(record, "HORA", out var horaText) ||
            !TryGetAnyString(record, ValuePropertyNames, out var valorText))
        {
            throw new CceeGatewayException($"Resposta invalida da CCEE: registro {index} sem os campos obrigatorios de PLD.");
        }

        if (!TryParseDia(record, diaText, out var dia))
            throw new CceeGatewayException($"Resposta invalida da CCEE: registro {index} contem um DIA nao reconhecido: '{diaText}'.");

        if (!int.TryParse(horaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hora))
            throw new CceeGatewayException($"Resposta invalida da CCEE: registro {index} contem uma HORA nao reconhecida: '{horaText}'.");

        if (!TryParseDecimal(valorText, out var valor))
            throw new CceeGatewayException($"Resposta invalida da CCEE: registro {index} contem um valor de PLD nao reconhecido: '{valorText}'.");

        return new PldRecord
        {
            Submercado = submercado,
            Dia = dia,
            Hora = hora,
            Valor = valor
        };
    }

    private static bool TryParseDia(JsonElement record, string diaText, out DateOnly value)
    {
        if (DateOnly.TryParse(diaText, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            return true;

        if (DateOnly.TryParse(diaText, PtBrCulture, DateTimeStyles.None, out value))
            return true;

        if (int.TryParse(diaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayNumber) &&
            TryGetString(record, "MES_REFERENCIA", out var mesReferencia) &&
            mesReferencia.Length == 6 &&
            int.TryParse(mesReferencia[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) &&
            int.TryParse(mesReferencia[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month))
        {
            try
            {
                // Some responses send DIA only as the day number and rely on MES_REFERENCIA for year/month.
                value = new DateOnly(year, month, dayNumber);
                return true;
            }
            catch
            {
                // Ignore invalid day/month combinations from upstream data.
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseDecimal(string valueText, out decimal value)
    {
        return decimal.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
               decimal.TryParse(valueText, NumberStyles.Any, PtBrCulture, out value);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (element.TryGetProperty(propertyName, out var property))
        {
            value = GetPropertyValue(property);
            return !string.IsNullOrWhiteSpace(value);
        }

        foreach (var member in element.EnumerateObject())
        {
            if (!string.Equals(member.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = GetPropertyValue(member.Value);
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static bool TryGetAnyString(JsonElement element, IEnumerable<string> propertyNames, out string value)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetString(element, propertyName, out value))
                return true;
        }

        value = string.Empty;
        return false;
    }

    private static string GetPropertyValue(JsonElement property)
    {
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }
}
