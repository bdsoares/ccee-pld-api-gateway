using System.Text.Json;
using Xunit;
using FluentAssertions;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Infrastructure.Parsing;

namespace Ccee.PldApp.Tests.Unit.Infrastructure;

public class PldRecordParserTests
{
    [Fact]
    public void ParseRecords_WithValidCceePayload_ShouldParseSuccessfully()
    {
        // Arrange
        var payload = """
            {
              "success": true,
              "result": {
                "records": [
                  {
                    "DIA": "2026-04-11",
                    "HORA": 1,
                    "SUBMERCADO": "SUL",
                    "VALOR": "123.45"
                  },
                  {
                    "DIA": "2026-04-11",
                    "HORA": 2,
                    "SUBMERCADO": "SUL",
                    "PLD_HORA": "124.50"
                  }
                ]
              }
            }
            """;

        using var doc = JsonDocument.Parse(payload);

        // Act
        var records = PldRecordParser.ParseRecords(doc.RootElement).ToList();

        // Assert
        records.Should().HaveCount(2);
        records[0].Valor.Should().Be(124.50m);
        records[0].Hora.Should().Be(2);
        records[0].Submercado.Should().Be("SUL");
        records[1].Valor.Should().Be(123.45m);
        records[1].Hora.Should().Be(1);
    }

    [Fact]
    public void ParseRecords_WithEmptyRecords_ShouldReturnEmptyList()
    {
        // Arrange
        var payload = """
            {
              "success": true,
              "result": {
                "records": []
              }
            }
            """;

        using var doc = JsonDocument.Parse(payload);

        // Act
        var records = PldRecordParser.ParseRecords(doc.RootElement).ToList();

        // Assert
        records.Should().BeEmpty();
    }

    [Fact]
    public void ParseRecords_WithFailedResponse_ShouldThrowException()
    {
        // Arrange
        var payload = """
            {
              "success": false,
              "error": "Invalid dataset"
            }
            """;

        using var doc = JsonDocument.Parse(payload);

        // Act & Assert
        Assert.Throws<CceeGatewayException>(() => PldRecordParser.ParseRecords(doc.RootElement));
    }
}
