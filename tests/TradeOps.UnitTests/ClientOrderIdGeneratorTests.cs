using TradeOps.Application.Services;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ClientOrderIdGeneratorTests
{
    [Fact]
    public void Generate_IsDeterministic_AndFitsBybitLimit()
    {
        var generator = new ClientOrderIdGenerator();
        var signalId = Guid.Parse("d0f8625f-2ad4-44eb-a1ec-22acbfbb2e58");

        var first = generator.Generate(signalId);
        var second = generator.Generate(signalId);

        Assert.Equal(first, second);
        Assert.Equal(36, first.Length);
        Assert.Equal("trd-d0f8625f2ad444eba1ec22acbfbb2e58", first);
    }
}
