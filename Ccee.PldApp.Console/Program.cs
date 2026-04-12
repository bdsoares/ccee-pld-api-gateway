using Ccee.PldApp.Application;
using System.Globalization;
using System.Text.Json;
using Ccee.PldApp.Domain;
using Ccee.PldApp.Infrastructure.Clients;
using Ccee.PldApp.Infrastructure.Configuration;
using Ccee.PldApp.Infrastructure.Persistence;

// Thin console entry point kept for local usage and troubleshooting.
var allowedSubmercados = new HashSet<string>(StringComparer.Ordinal)
{
    "SUDESTE",
    "SUL",
    "NORDESTE",
    "NORTE"
};

if (args.Any(arg => arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase)))
{
    PrintHelp();
    return;
}

try
{
    var dateArg = GetArgValue(args, "--date");
    var submercadoArg = GetArgValue(args, "--submercado");
    var limitArg = GetArgValue(args, "--limit");

    var query = new PldQuery
    {
        ResourceId = PldQuery.DefaultResourceId,
        Dia = ParseDateOrThrow(dateArg),
        Submercado = ParseSubmercadoOrThrow(submercadoArg, allowedSubmercados),
        Limit = ParseLimitOrThrow(limitArg)
    };

    var options = new PldGatewayOptions();
    var initializer = new SqliteDatabaseInitializer(options);
    await initializer.InitializeAsync();

    using var source = new CceePldSource(options);
    var cacheRepository = new SqlitePldCacheRepository(options);
    var useCase = new GetPldDataUseCase(source, cacheRepository);

    var result = await useCase.ExecuteAsync(query);

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true
    }));
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}

static string? GetArgValue(string[] args, string key)
{
    var prefix = key + "=";
    var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return arg?.Substring(prefix.Length);
}

static DateOnly? ParseDateOrThrow(string? input)
{
    if (string.IsNullOrWhiteSpace(input))
        return null;

    if (DateOnly.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        return parsed;

    throw new ArgumentException("O parametro --date deve estar no formato YYYY-MM-DD.");
}

static int ParseLimitOrThrow(string? input)
{
    if (string.IsNullOrWhiteSpace(input))
        return PldQuery.DefaultLimit;

    if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value > 0
        && value <= PldQuery.MaxLimit)
        return value;

    throw new ArgumentException($"O parametro --limit deve ser um numero inteiro entre 1 e {PldQuery.MaxLimit}.");
}

static string? ParseSubmercadoOrThrow(string? input, ISet<string> allowedSubmercados)
{
    if (string.IsNullOrWhiteSpace(input))
        return null;

    var candidate = input.Trim();
    if (allowedSubmercados.Contains(candidate))
        return candidate;

    throw new ArgumentException("O parametro --submercado deve ser exatamente um dos valores: SUDESTE, SUL, NORDESTE, NORTE.");
}

static void PrintHelp()
{
    Console.WriteLine("Uso: dotnet run -- [--date=YYYY-MM-DD] [--submercado=SUDESTE|SUL|NORDESTE|NORTE] [--limit=1000]");
}



