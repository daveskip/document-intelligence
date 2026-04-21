using DocumentIntelligence.Contracts.Responses;
using DocumentIntelligence.Domain.Enums;
using FluentAssertions;

namespace DocumentIntelligence.Contracts.Tests;

public class PagedResultTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(20, 10, 2)]
    [InlineData(21, 10, 3)]
    [InlineData(100, 25, 4)]
    public void TotalPages_IsCorrectlyCeilingDivided(int totalCount, int pageSize, int expectedPages)
    {
        var result = new PagedResult<DocumentDto>([], totalCount, 1, pageSize);
        result.TotalPages.Should().Be(expectedPages);
    }

    [Theory]
    [InlineData(1, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    public void HasNextPage_ReturnsTrueWhenNotOnLastPage(int page, int totalPages, bool expected)
    {
        // totalPages = ceil(totalCount / pageSize). Use pageSize=1, totalCount=totalPages.
        var result = new PagedResult<DocumentDto>([], totalPages, page, 1);
        result.HasNextPage.Should().Be(expected);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void HasPreviousPage_ReturnsTrueWhenNotOnFirstPage(int page, bool expected)
    {
        var result = new PagedResult<DocumentDto>([], 100, page, 10);
        result.HasPreviousPage.Should().Be(expected);
    }

    [Fact]
    public void PagedResult_ZeroTotalCount_HasZeroPages()
    {
        var result = new PagedResult<DocumentDto>([], 0, 1, 10);
        result.TotalPages.Should().Be(0);
        result.HasNextPage.Should().BeFalse();
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public void PagedResult_ItemsAreStored()
    {
        var items = new List<DocumentDto>
        {
            new(Guid.NewGuid(), "file.pdf", "application/pdf", 1024,
                DocumentStatus.Pending, "Pending", DateTimeOffset.UtcNow, null)
        };
        var result = new PagedResult<DocumentDto>(items, 1, 1, 10);
        result.Items.Should().HaveCount(1);
        result.Items[0].FileName.Should().Be("file.pdf");
    }
}
