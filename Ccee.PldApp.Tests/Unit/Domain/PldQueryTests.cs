using Xunit;
using FluentAssertions;
using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Tests.Unit.Domain;

public class PldQueryTests
{
    [Fact]
    public void Normalize_WithWhitespaceSubmercado_ShouldTrimWithoutAliasConversion()
    {
        var query = new PldQuery
        {
            Dia = null,
            Submercado = "  SUL  ",
            Limit = 100
        };

        var normalized = query.Normalize();

        normalized.Submercado.Should().Be("SUL");
    }

    [Fact]
    public void Normalize_WithLowercaseSubmercado_ShouldKeepOriginalValue()
    {
        var query = new PldQuery
        {
            Dia = null,
            Submercado = "sul",
            Limit = 100
        };

        var normalized = query.Normalize();

        normalized.Submercado.Should().Be("sul");
    }

    [Fact]
    public void Normalize_WithEmptyResourceId_ShouldUseDefault()
    {
        var query = new PldQuery
        {
            ResourceId = "  ",
            Dia = null,
            Submercado = null,
            Limit = 100
        };

        var normalized = query.Normalize();

        normalized.ResourceId.Should().Be(PldQuery.DefaultResourceId);
    }

    [Fact]
    public void ToCacheKey_ShouldGenerateConsistentKey()
    {
        var query = new PldQuery
        {
            ResourceId = "test-id",
            Dia = new DateOnly(2026, 4, 11),
            Submercado = "SUL",
            Limit = 24
        };

        var key1 = query.ToCacheKey();
        var key2 = query.ToCacheKey();

        key1.Should().Be(key2);
        key1.Should().Contain("test-id");
        key1.Should().Contain("2026-04-11");
        key1.Should().Contain("SUL");
        key1.Should().Contain("24");
    }
}
