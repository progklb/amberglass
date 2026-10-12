using Amberglass.Core.Models;
using Xunit;

namespace Amberglass.Tests;

public class MediaTypeRegistryTests
{
    [Theory]
    [InlineData("a.jpg", MediaType.Image)]
    [InlineData("a.JPEG", MediaType.Image)]
    [InlineData("a.cr3", MediaType.Image)]
    [InlineData("a.NEF", MediaType.Image)]
    [InlineData("a.dng", MediaType.Image)]
    [InlineData("a.mp4", MediaType.Video)]
    [InlineData("a.MOV", MediaType.Video)]
    [InlineData("a.mxf", MediaType.Video)]
    [InlineData("a.txt", MediaType.Unsupported)]
    [InlineData("a", MediaType.Unsupported)]
    public void Classifies_by_extension(string fileName, MediaType expected)
        => Assert.Equal(expected, MediaTypeRegistry.GetMediaType(fileName));
}
