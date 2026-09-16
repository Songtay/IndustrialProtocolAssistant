using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers;
using IndustrialProtocolAssistant.Drivers.Driver;
using Xunit;

namespace IndustrialProtocolAssistant.Tests;

public class SerialFreeDriverTests
{
    // ---------- 地址语法 ----------

    [Theory]
    [InlineData("01 03 00 00 00 01 84 0A", true)]                       // 空格分隔
    [InlineData("01,03,00,00,00,01,84,0A", true)]                       // 逗号分隔
    [InlineData("010300000001840A", true)]                              // 连续 hex 自动切分
    [InlineData("01 03 00 00 00 01 84 0A@4", true)]                     // 带回复偏移
    [InlineData("01 03 00 00 00 01 84 0A || 01 06 00 00 {value} {crc16}", true)] // 读写双段
    [InlineData("AA 55 01 {xor} {sum} {crc16}", true)]                  // 校验占位符
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("01 03 00 00 00 01 84 0A@x", false)]                    // 偏移非数字
    [InlineData("01 03 ZZ 84 0A", false)]                               // 非法字节
    [InlineData("01 03 {foo}", false)]                                  // 未知占位符
    [InlineData("01 03 00 00 || 01 06 {value} || 07", false)]           // 多个 ||
    public void TryValidateAddress_ChecksSyntax(string address, bool expected)
        => Assert.Equal(expected, SerialFreeDriver.TryValidateAddress(address, out _));

    // ---------- 设备级帧尾校验（连接参数 FrameChecksum 自动补尾） ----------

    [Fact]
    public void TryParse_WithCrc16Checksum_AppendsCrcTokenWhenAbsent()
    {
        // 地址只写命令，CRC16 由设备级选项在帧尾自动补齐（01 03 00 00 00 01 的 CRC16 = 84 0A）
        Assert.True(SerialFreeProtocol.TryParse("01 03 00 00 00 01@4", "CRC16", out var spec, out var error));
        Assert.Null(error);
        byte[] frame = SerialFreeProtocol.RenderFrame(spec!.ReadTokens, null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A }, frame);
    }

    [Fact]
    public void TryParse_WithXorChecksum_AppendsSingleTailByte()
    {
        Assert.True(SerialFreeProtocol.TryParse("AA 55 01@0", "XOR", out var spec, out _));
        // AA ^ 55 ^ 01 = FE
        byte[] frame = SerialFreeProtocol.RenderFrame(spec!.ReadTokens, null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x01, 0xFE }, frame);
    }

    [Fact]
    public void TryParse_Checksum_DoesNotDuplicateExplicitPlaceholder()
    {
        // 地址已显式写 {crc16}：即使设备级选了 CRC16 也不重复追加
        Assert.True(SerialFreeProtocol.TryParse("01 03 00 00 00 01 {crc16}@4", "CRC16", out var spec, out _));
        byte[] frame = SerialFreeProtocol.RenderFrame(spec!.ReadTokens, null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A }, frame);
    }

    [Fact]
    public void TryParse_NoneChecksum_KeepsAddressAsIs()
    {
        // 连接参数“无”/旧配置缺省 → 与原始解析完全一致，不追加任何 token
        Assert.True(SerialFreeProtocol.TryParse("01 03 00 00 00 01@4", "无", out var spec, out _));
        Assert.Equal(6, spec!.ReadTokens.Count);
        byte[] frame = SerialFreeProtocol.RenderFrame(spec.ReadTokens, null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 }, frame);
    }

    [Fact]
    public void TryParse_Checksum_AppliesToExplicitWriteSection()
    {
        // 读/写双段：两段帧尾都自动补 CRC16
        Assert.True(SerialFreeProtocol.TryParse(
            "01 03 00 00 00 02@3 || 01 06 00 02 {value}", "CRC16", out var spec, out _));
        Assert.Equal("{crc16}", spec!.ReadTokens[^1]);
        Assert.Equal("{crc16}", spec.WriteTokens[^1]);
    }

    [Fact]
    public void TryParse_Checksum_WriteOnlyTag()
    {
        // 纯写 Tag（读段为空）：只在写帧帧尾补校验
        Assert.True(SerialFreeProtocol.TryParse("|| 01 06 00 02 {value}", "SUM", out var spec, out _));
        Assert.True(spec!.IsWriteOnly);
        Assert.Equal("{sum}", spec.WriteTokens[^1]);
    }

    // ---------- 帧渲染 ----------

    [Fact]
    public void RenderFrame_StaticFrame_NoChecksum()
    {
        byte[] frame = SerialFreeProtocol.RenderFrame(
            ["01", "03", "00", "00", "00", "01"], null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 }, frame);
    }

    [Fact]
    public void RenderFrame_Crc16_LsbFirst_MatchesModbusExample()
    {
        // 标准 Modbus RTU 示例：01 03 00 00 00 01 的 CRC16 = 0x0A84（发送顺序 84 0A）
        byte[] frame = SerialFreeProtocol.RenderFrame(
            ["01", "03", "00", "00", "00", "01", "{crc16}"], null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A }, frame);
    }

    [Fact]
    public void RenderFrame_XorAndSum_CalculatedOverRestOfFrame()
    {
        // AA ^ 55 = FF；AA + 55 = 0xFF（低 8 位）
        byte[] frame = SerialFreeProtocol.RenderFrame(
            ["AA", "55", "{xor}", "{sum}"], null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0xAA, 0x55, 0xFF, 0xFF }, frame);

        // 01 + 02 = 03；01 ^ 02 = 03
        frame = SerialFreeProtocol.RenderFrame(["01", "02", "{sum}", "{xor}"], null, TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x03 }, frame);
    }

    [Fact]
    public void RenderFrame_ValuePlaceholder_IsReplacedByEncodedBytes()
    {
        // 写保持寄存器（功能码 06）：帧体 01 06 00 00 12 34 的 CRC16 = 0xBD84（发送顺序 84 BD）
        byte[] frame = SerialFreeProtocol.RenderFrame(
            ["01", "06", "00", "00", "{value}", "{crc16}"],
            [0x12, 0x34], TagDataType.UInt16, 1);
        Assert.Equal(new byte[] { 0x01, 0x06, 0x00, 0x00, 0x12, 0x34, 0x84, 0xBD }, frame);
    }

    // ---------- 写值编码（大端） ----------

    [Theory]
    [InlineData(TagDataType.UInt16, "4660", "12 34")]
    [InlineData(TagDataType.Int16, "-2", "FF FE")]
    [InlineData(TagDataType.UInt32, "16909060", "01 02 03 04")]
    [InlineData(TagDataType.Int32, "-2", "FF FF FF FE")]
    [InlineData(TagDataType.Float, "25.5", "41 CC 00 00")]
    [InlineData(TagDataType.Double, "1.5", "3F F8 00 00 00 00 00 00")]
    [InlineData(TagDataType.Bool, "1", "01")]
    [InlineData(TagDataType.Bool, "true", "01")]
    [InlineData(TagDataType.Bool, "0", "00")]
    public void EncodeValue_EncodesBigEndian(TagDataType type, string text, string expectedHex)
    {
        var tag = Tag(type);
        byte[] bytes = SerialFreeProtocol.EncodeValue(tag, text);
        Assert.Equal(expectedHex, string.Join(' ', bytes.Select(b => b.ToString("X2"))));
    }

    [Fact]
    public void EncodeValue_String_TruncatesToMaxBytes()
    {
        var tag = Tag(TagDataType.String, length: 3);
        byte[] bytes = SerialFreeProtocol.EncodeValue(tag, "ABCDEF");
        Assert.Equal("41 42 43", string.Join(' ', bytes.Select(b => b.ToString("X2"))));
    }

    [Fact]
    public void EncodeValue_String_AsciiBytes()
    {
        var tag = Tag(TagDataType.String, length: 8);
        byte[] bytes = SerialFreeProtocol.EncodeValue(tag, "AB");
        Assert.Equal("41 42", string.Join(' ', bytes.Select(b => b.ToString("X2"))));
    }

    // ---------- 回复解析 ----------

    [Theory]
    [InlineData(TagDataType.UInt16, "12 34", 0, (ushort)4660)]
    [InlineData(TagDataType.Int16, "FF FE", 0, (short)-2)]
    [InlineData(TagDataType.UInt32, "01 02 03 04", 0, (uint)16909060)]
    [InlineData(TagDataType.Int32, "FF FF FF FE", 0, -2)]
    public void ParseReply_Integers_BigEndian(TagDataType type, string hex, int offset, object expected)
    {
        var tv = SerialFreeProtocol.ParseReply(Tag(type), HexToBytes(hex), offset);
        Assert.Equal(DataQuality.Good, tv.Quality);
        Assert.Equal(Convert.ToDouble(expected), Convert.ToDouble(tv.Value));
    }

    [Fact]
    public void ParseReply_Float_BigEndian()
    {
        var tv = SerialFreeProtocol.ParseReply(Tag(TagDataType.Float), [0x41, 0xCC, 0x00, 0x00], 0);
        Assert.Equal(DataQuality.Good, tv.Quality);
        Assert.Equal(25.5f, (float)tv.Value!);
    }

    [Fact]
    public void ParseReply_Double_BigEndian()
    {
        var tv = SerialFreeProtocol.ParseReply(Tag(TagDataType.Double), HexToBytes("3F F8 00 00 00 00 00 00"), 0);
        Assert.Equal(DataQuality.Good, tv.Quality);
        Assert.Equal(1.5, (double)tv.Value!);
    }

    [Fact]
    public void ParseReply_Bool_NonZeroIsTrue()
    {
        Assert.False((bool)SerialFreeProtocol.ParseReply(Tag(TagDataType.Bool), [0x00], 0).Value!);
        Assert.True((bool)SerialFreeProtocol.ParseReply(Tag(TagDataType.Bool), [0x01], 0).Value!);
        Assert.True((bool)SerialFreeProtocol.ParseReply(Tag(TagDataType.Bool), [0x05], 0).Value!);
    }

    [Fact]
    public void ParseReply_RespectsOffset()
    {
        // 帧头 3 字节（AA 55 01），数据从第 3 字节开始
        var tv = SerialFreeProtocol.ParseReply(
            Tag(TagDataType.UInt16), HexToBytes("AA 55 01 12 34"), offset: 3);
        Assert.Equal(DataQuality.Good, tv.Quality);
        Assert.Equal((ushort)0x1234, (ushort)tv.Value!);
    }

    [Fact]
    public void ParseReply_String_TruncatesAtNullAndRespectsLength()
    {
        var tv = SerialFreeProtocol.ParseReply(
            Tag(TagDataType.String, length: 6), HexToBytes("41 42 43 44 00 45 46"), offset: 0);
        Assert.Equal(DataQuality.Good, tv.Quality);
        Assert.Equal("ABCD", tv.Value);
    }

    [Fact]
    public void ParseReply_TooShort_ReturnsBad()
    {
        var tv = SerialFreeProtocol.ParseReply(Tag(TagDataType.UInt32), HexToBytes("01 02"), 0);
        Assert.Equal(DataQuality.Bad, tv.Quality);

        var tv2 = SerialFreeProtocol.ParseReply(Tag(TagDataType.UInt16), HexToBytes("12 34"), offset: 5);
        Assert.Equal(DataQuality.Bad, tv2.Quality);
    }

    // ---------- 驱动工厂集成 ----------

    [Fact]
    public void DriverFactory_SerialFree_RegisteredAndCreatable()
    {
        Assert.Contains("SerialFree", DriverFactory.SupportedTypes);

        var fields = DriverFactory.GetFields("SerialFree");
        Assert.Contains(fields, f => f.Key == "SerialPort" && f.Kind == ConnectionFieldKind.SerialPort);
        Assert.Contains(fields, f => f.Key == "BaudRate");
        Assert.Contains(fields, f => f.Key == "Parity");
        Assert.Contains(fields, f => f.Key == "StopBits");
        Assert.Contains(fields, f => f.Key == "DataBits");
        Assert.Contains(fields, f => f.Key == "ResponseTimeoutMs");
        Assert.Contains(fields, f => f.Key == "FrameGapMs");
        Assert.Contains(fields, f => f.Key == "FrameChecksum" && f.Kind == ConnectionFieldKind.Select
            && f.DefaultValue == "无" && f.Options is ["无", "CRC16", "XOR", "SUM"]);

        var cfg = new DeviceConfig("d", "n", "SerialFree", 1000, new Dictionary<string, string>
        {
            { "SerialPort", "COM3" },
            { "BaudRate", "9600" },
            { "Parity", "无" },
            { "StopBits", "1" },
            { "DataBits", "8" },
            { "ResponseTimeoutMs", "1000" },
            { "FrameGapMs", "20" },
        });
        var driver = DriverFactory.Create(cfg);
        Assert.Equal("SerialFree", driver.DriverType);
        Assert.IsType<SerialFreeDriver>(driver);
    }

    // ---------- helpers ----------

    private static TagDefinition Tag(TagDataType type, ushort length = 1) =>
        new("id-1", "测试Tag", type, "dev-1", "AA 55", length);

    private static byte[] HexToBytes(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();
}
