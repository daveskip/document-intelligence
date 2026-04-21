using DocumentIntelligence.Domain.Enums;
using FluentAssertions;

namespace DocumentIntelligence.Domain.Tests.Enums;

public class DocumentStatusTests
{
    [Fact]
    public void DocumentStatus_Values_AreCorrectIntegers()
    {
        ((int)DocumentStatus.Pending).Should().Be(0);
        ((int)DocumentStatus.Processing).Should().Be(1);
        ((int)DocumentStatus.Completed).Should().Be(2);
        ((int)DocumentStatus.Failed).Should().Be(3);
    }

    [Fact]
    public void DocumentStatus_HasExactlyFourValues()
    {
        Enum.GetValues<DocumentStatus>().Should().HaveCount(4);
    }
}
