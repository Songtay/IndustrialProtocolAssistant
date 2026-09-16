using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers.Driver;
using Xunit;

namespace IndustrialProtocolAssistant.Tests;

public class MqttDriverTests
{
    // ---------- Topic 通配符匹配 ----------

    [Theory]
    [InlineData("sensor/temp", "sensor/temp", true)]           // 精确匹配
    [InlineData("sensor/temp", "sensor/humidity", false)]      // 不同层级
    [InlineData("sensor/+", "sensor/temp", true)]              // + 匹配单级
    [InlineData("sensor/+", "sensor/a/b", false)]              // + 不跨级
    [InlineData("sensor/#", "sensor/temp", true)]              // # 匹配多级
    [InlineData("sensor/#", "sensor/a/b/c", true)]
    [InlineData("sensor/+/temp", "sensor/dev1/temp", true)]
    [InlineData("sensor/+/temp", "sensor/dev1/humidity", false)]
    [InlineData("#", "any/topic/here", true)]                  // 顶级 # 匹配一切
    public void TopicMatches_HandlesWildcards(string pattern, string topic, bool expected)
        => Assert.Equal(expected, MqttDriver.TopicMatches(pattern, topic));

    // ---------- 裸值 payload 解析（类型自动推断：bool 文本→bool；数字→long/double；其余→字符串） ----------

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("ON", true)]
    [InlineData("off", false)]
    public void TryParsePayload_Bool(string payload, bool expected)
    {
        Assert.True(MqttDriver.TryParsePayload(payload, TagDataType.Bool, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("-5", -5L)]
    public void TryParsePayload_Number(string payload, long expected)
    {
        Assert.True(MqttDriver.TryParsePayload(payload, TagDataType.Int16, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("65535", 65535L)]
    public void TryParsePayload_UInt16(string payload, long expected)
    {
        Assert.True(MqttDriver.TryParsePayload(payload, TagDataType.UInt16, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    public void TryParsePayload_Float(string payload, double expected)
    {
        Assert.True(MqttDriver.TryParsePayload(payload, TagDataType.Float, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryParsePayload_String_ReturnsRaw()
    {
        Assert.True(MqttDriver.TryParsePayload("hello 世界", TagDataType.String, out var value));
        Assert.Equal("hello 世界", value);
    }

    [Fact]
    public void TryParsePayload_NonNumeric_FallsBackToString()
    {
        // 无法识别为 bool/数字的裸值一律按字符串返回（类型自动推断，不再失败）
        Assert.True(MqttDriver.TryParsePayload("abc", TagDataType.Int32, out var value));
        Assert.Equal("abc", value);
    }

    [Fact]
    public void TryParsePayload_DigitNotBoolText_ReturnsNumber()
    {
        // "2" 不是布尔文本，自动推断为数值（不再要求 Tag 类型为 Bool）
        Assert.True(MqttDriver.TryParsePayload("2", TagDataType.Bool, out var value));
        Assert.Equal(2L, value);
    }

    // ---------- JSON payload 解析（兼容 {"value": ...}） ----------

    [Fact]
    public void TryParsePayload_JsonValue_Double()
    {
        Assert.True(MqttDriver.TryParsePayload("{\"value\": 36.6}", TagDataType.Double, out var value));
        Assert.Equal(36.6, value);
    }

    [Fact]
    public void TryParsePayload_JsonValue_Int()
    {
        Assert.True(MqttDriver.TryParsePayload("{\"value\": 42}", TagDataType.Int32, out var value));
        Assert.Equal(42L, value);
    }

    [Fact]
    public void TryParsePayload_JsonValue_Bool()
    {
        Assert.True(MqttDriver.TryParsePayload("{\"value\": true}", TagDataType.Bool, out var value));
        Assert.Equal(true, value);
    }

    [Fact]
    public void TryParsePayload_JsonValue_String()
    {
        Assert.True(MqttDriver.TryParsePayload("{\"value\": \"running\"}", TagDataType.String, out var value));
        Assert.Equal("running", value);
    }

    [Fact]
    public void TryParsePayload_JsonStringNumber_Converts()
    {
        // JSON 里 value 是字符串形式的数值，按数值自动推断
        Assert.True(MqttDriver.TryParsePayload("{\"value\": \"12.5\"}", TagDataType.Float, out var value));
        Assert.Equal(12.5, value);
    }

    [Fact]
    public void TryParsePayload_JsonWithoutValue_FallsBackToRawString()
    {
        // 形如 {"temp": 36.6} 的报文不是约定格式（无 value 字段），整个报文按字符串兜底返回
        Assert.True(MqttDriver.TryParsePayload("{\"temp\": 36.6}", TagDataType.Float, out var value));
        Assert.Equal("{\"temp\": 36.6}", value);
    }
}
