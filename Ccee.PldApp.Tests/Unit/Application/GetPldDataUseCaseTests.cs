using Xunit;
using Moq;
using FluentAssertions;
using Ccee.PldApp.Application;
using Ccee.PldApp.Application.Abstractions;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Tests.Unit.Application;

public class GetPldDataUseCaseTests
{
    private readonly Mock<IPldCacheRepository> _cacheMock;
    private readonly Mock<ICceePldSource> _sourceMock;
    private readonly GetPldDataUseCase _useCase;

    public GetPldDataUseCaseTests()
    {
        _cacheMock = new Mock<IPldCacheRepository>();
        _sourceMock = new Mock<ICceePldSource>();
        _useCase = new GetPldDataUseCase(_sourceMock.Object, _cacheMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidCacheHit_ShouldReturnCachedData()
    {
        // Arrange
        var query = new PldQuery
        {
            ResourceId = "test-id",
            Dia = new DateOnly(2026, 4, 11),
            Submercado = "SUL",
            Limit = 24
        };

        var cachedResult = new PldQueryResult
        {
            Query = query,
            Records = new[] { new PldRecord { Dia = new DateOnly(2026, 4, 11), Hora = 1, Submercado = "SUL", Valor = 100m } },
            Source = PldQuerySource.Cache,
            RetrievedAtUtc = DateTimeOffset.UtcNow,
            UsedMonthReferenceFallback = false
        };

        _cacheMock.Setup(c => c.GetAsync(It.IsAny<PldQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedResult);

        // Act
        var result = await _useCase.ExecuteAsync(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Source.Should().Be(PldQuerySource.Cache);
        result.TotalRecords.Should().Be(1);
        _sourceMock.Verify(s => s.GetAsync(It.IsAny<PldQuery>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithCacheMiss_ShouldFetchFromSource()
    {
        // Arrange
        var query = new PldQuery { Dia = new DateOnly(2026, 4, 11), Submercado = "SUL", Limit = 24 };
        var record = new PldRecord { Dia = new DateOnly(2026, 4, 11), Hora = 1, Submercado = "SUL", Valor = 100m };

        _cacheMock.Setup(c => c.GetAsync(It.IsAny<PldQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PldQueryResult)null!);

        _sourceMock.Setup(s => s.GetAsync(It.IsAny<PldQuery>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { record });

        _cacheMock.Setup(c => c.SaveAsync(It.IsAny<PldQueryResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _useCase.ExecuteAsync(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Source.Should().Be(PldQuerySource.Ccee);
        result.TotalRecords.Should().Be(1);
        _sourceMock.Verify(s => s.GetAsync(It.IsAny<PldQuery>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoResults_ShouldThrowNotFoundException()
    {
        // Arrange
        var query = new PldQuery { Dia = new DateOnly(2026, 4, 11), Submercado = "INVALID", Limit = 24 };

        _cacheMock.Setup(c => c.GetAsync(It.IsAny<PldQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PldQueryResult)null!);

        _sourceMock.Setup(s => s.GetAsync(It.IsAny<PldQuery>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PldRecord>());

        // Act & Assert
        await Assert.ThrowsAsync<PldQueryNotFoundException>(() =>
            _useCase.ExecuteAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WithLimitAboveMaximum_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        var query = new PldQuery { Dia = new DateOnly(2026, 4, 11), Submercado = "SUL", Limit = PldQuery.MaxLimit + 1 };

        // Act
        var act = () => _useCase.ExecuteAsync(query, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
