using IndustrialProtocolAssistant.Core;
using Xunit;

namespace IndustrialProtocolAssistant.Tests;

public class TagTests
{
    [Fact]
    public void TagDefinition_Create_GeneratesIdAndDefaults()
    {
        var tag = TagDefinition.Create("温度", TagDataType.Float, "dev1", "0", highAlarm: 80);
        Assert.False(string.IsNullOrEmpty(tag.Id));
        Assert.Equal("温度", tag.Name);
        Assert.Equal(TagDataType.Float, tag.DataType);
        Assert.Equal((ushort)2, tag.Length); // Float 占 2 寄存器
        Assert.Equal(80, tag.HighAlarm);
    }

    [Fact]
    public void TagDefinition_Create_UInt16_HasLengthOne()
    {
        var tag = TagDefinition.Create("计数", TagDataType.UInt16, "dev1", "3");
        Assert.Equal((ushort)1, tag.Length);
    }

    [Fact]
    public void TagValue_Bad_And_Stale_AreCorrect()
    {
        var bad = TagValue.Bad("t1");
        Assert.Equal(DataQuality.Bad, bad.Quality);
        Assert.Null(bad.Value);

        var stale = TagValue.Stale("t1");
        Assert.Equal(DataQuality.Stale, stale.Quality);
    }
}
