using System.Net;
using System.Net.Sockets;
using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers;
using IndustrialProtocolAssistant.Drivers.Driver;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using Xunit;

namespace IndustrialProtocolAssistant.Tests;

/// <summary>
/// MQTT 驱动的端到端集成测试：在本地起一个内置 broker，
/// 验证 连接订阅 → 收到报文缓存 → 读取返回 与 写值发布 的完整链路。
/// </summary>
public class MqttDriverIntegrationTests
{
    [Fact]
    public async Task Connect_Subscribe_Publish_Read_ReturnsLatestValue()
    {
        var port = GetFreePort();
        var broker = await StartBrokerAsync(port);
        try
        {
            var tempTag = TagDefinition.Create("温度", TagDataType.Float, "mqtt-dev", "sensor/temp");
            var statusTag = TagDefinition.Create("状态", TagDataType.Bool, "mqtt-dev", "sensor/status");

            var driver = new MqttDriver(new DeviceConfig("mqtt-dev", "MQTT 设备", "Mqtt", 1000,
                new Dictionary<string, string> { { "BrokerUrl", $"mqtt://127.0.0.1:{port}" } }));
            driver.SetTags([tempTag, statusTag]);

            await driver.ConnectAsync();
            Assert.True(driver.IsConnected);

            // 模拟设备上报：裸值 + JSON 各一条
            await PublishAsync(broker, port, "sensor/temp", "36.5");
            await PublishAsync(broker, port, "sensor/status", "{\"value\": true}");

            await WaitUntilAsync(async () => (await driver.ReadTagAsync(tempTag)).Value is double);
            var temp = await driver.ReadTagAsync(tempTag);
            Assert.Equal(36.5, temp.Value);

            var status = await driver.ReadTagAsync(statusTag);
            Assert.Equal(true, status.Value);

            await driver.DisconnectAsync();
            Assert.False(driver.IsConnected);
        }
        finally
        {
            await broker.StopAsync();
        }
    }

    [Fact]
    public async Task Write_PublishesToSetTopic_WithRawPayload()
    {
        var port = GetFreePort();
        var broker = await StartBrokerAsync(port);
        string? publishedTopic = null;
        string? publishedPayload = null;
        broker.InterceptingPublishAsync += e =>
        {
            publishedTopic = e.ApplicationMessage.Topic;
            publishedPayload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.Array!,
                e.ApplicationMessage.PayloadSegment.Offset, e.ApplicationMessage.PayloadSegment.Count);
            return Task.CompletedTask;
        };
        try
        {
            var setpointTag = TagDefinition.Create("设定温度", TagDataType.Float, "mqtt-dev", "control/setpoint");
            var driver = new MqttDriver(new DeviceConfig("mqtt-dev", "MQTT 设备", "Mqtt", 1000,
                new Dictionary<string, string>
                {
                    { "BrokerUrl", $"mqtt://127.0.0.1:{port}" },
                    { "QoS", "1" },
                }));
            driver.SetTags([setpointTag]);
            await driver.ConnectAsync();

            var ok = await driver.WriteTagAsync(setpointTag, 88.5f);
            Assert.True(ok);

            await WaitUntilAsync(() => Task.FromResult(publishedTopic is not null));
            Assert.Equal("control/setpoint/set", publishedTopic);
            Assert.Equal("88.5", publishedPayload);
        }
        finally
        {
            await broker.StopAsync();
        }
    }

    [Fact]
    public async Task Connect_ToUnreachableBroker_ThrowsAndMarksDisconnected()
    {
        var port = GetFreePort(); // 没有 broker 监听的端口
        var driver = new MqttDriver(new DeviceConfig("mqtt-dev", "MQTT 设备", "Mqtt", 1000,
            new Dictionary<string, string> { { "BrokerUrl", $"mqtt://127.0.0.1:{port}" } }));

        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync());
        Assert.False(driver.IsConnected);
    }

    // ---------- 辅助 ----------

    private static async Task PublishAsync(MqttServer broker, int port, string topic, string payload)
    {
        // 用客户端发布，走真实 MQTT 链路，而非注入
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build());
        await client.DisconnectAsync();
    }

    private static async Task<MqttServer> StartBrokerAsync(int port)
    {
        var options = new MqttServerOptions
        {
            DefaultEndpointOptions = { IsEnabled = true, Port = port },
        };
        var server = new MqttFactory().CreateMqttServer(options);
        await server.StartAsync();
        if (!server.IsStarted)
            throw new Exception($"MQTT 内置 broker 启动失败（端口 {port}）");
        return server;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("等待条件超时");
    }
}
