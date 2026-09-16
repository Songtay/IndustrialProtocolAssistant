using System.Linq;
using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers;
using Xunit;

namespace IndustrialProtocolAssistant.Tests;

public class DriverFactoryTests
{
    [Fact]
    public void Create_ModbusTcp_ReturnsModbusDriver()
    {
        var cfg = new DeviceConfig("d", "n", "ModbusTcp", 1000, new Dictionary<string, string>{{"Host","127.0.0.1"},{"Port","502"}});
        var driver = DriverFactory.Create(cfg);
        Assert.Equal("ModbusTcp", driver.DriverType);
    }

    [Fact]
    public void Create_OpcUa_ReturnsOpcUaDriver()
    {
        var cfg = new DeviceConfig("d", "n", "OpcUa", 1000, new Dictionary<string, string>{{"EndpointUrl","opc.tcp://127.0.0.1:4840"}});
        var driver = DriverFactory.Create(cfg);
        Assert.Equal("OpcUa", driver.DriverType);
    }

    [Fact]
    public void Create_S7_ReturnsS7Driver()
    {
        var cfg = new DeviceConfig("d", "n", "S7", 1000, new Dictionary<string, string>
        {
            { "Host", "192.168.0.1" },
            { "Port", "102" },
            { "CpuType", "S7300" },
            { "Rack", "0" },
            { "Slot", "1" },
        });
        var driver = DriverFactory.Create(cfg);
        Assert.Equal("S7", driver.DriverType);
    }

    [Fact]
    public void Fields_SerialFreeAndSocket_DeclareFrameChecksumSelect()
    {
        string[] expected = ["无", "CRC16", "XOR", "SUM"];
        foreach (var driver in new[] { "SerialFree", "Socket" })
        {
            var fields = DriverFactory.GetFields(driver);
            Assert.Contains(fields, f => f.Key == "FrameChecksum" && f.Kind == ConnectionFieldKind.Select
                && f.DefaultValue == "无" && f.Options is not null && f.Options.SequenceEqual(expected));
        }
    }

    [Fact]
    public void Create_Unsupported_Throws()
    {
        var cfg = new DeviceConfig("d", "n", "Foo", 1000, new Dictionary<string, string>());
        Assert.Throws<NotSupportedException>(() => DriverFactory.Create(cfg));
    }
}
