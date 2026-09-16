using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;

Console.OutputEncoding = Encoding.UTF8;

// ---------- 参数解析：--port=1883 ----------
int port = 1883;
foreach (var arg in args)
{
    if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(arg["--port=".Length..], out var p) && p > 0 && p < 65536)
        port = p;
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------- 内置 Broker ----------
var broker = await StartBrokerAsync(port);

// ---------- 模拟设备（发布端） ----------
var rnd = new Random();
var devices = new List<SimDevice>
{
    new("温度传感器", "sensor/temp",      () => $"{{\"value\": {20 + rnd.NextDouble() * 10:F1}}}", TimeSpan.FromSeconds(2)),
    new("湿度传感器", "sensor/humidity",  () => $"{{\"value\": {40 + rnd.Next(41)}}}",             TimeSpan.FromSeconds(3)),
    new("开关状态",   "sensor/status",    () => $"{{\"value\": {rnd.Next(2) == 1}}}",              TimeSpan.FromSeconds(4)),
    new("运行状态",   "machine/status",   () => $"{{\"value\": \"{(rnd.Next(5) == 0 ? "stopped" : "running")}\"}}", TimeSpan.FromSeconds(5)),
};

var factory = new MqttFactory();
using var client = factory.CreateMqttClient();
await client.ConnectAsync(new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", port)
    .WithClientId("mqtt-simulator")
    .Build());

var publishTasks = devices.Select(d => RunDeviceAsync(client, d, cts.Token)).ToArray();

PrintBanner(port);
PrintHelp();
await Task.Delay(200); // 等待 broker 日志先输出

// ---------- 交互命令 ----------
while (!cts.IsCancellationRequested)
{
    var line = Console.ReadLine();
    if (line is null) break; // Ctrl+C / 输入流关闭
    var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;

    var cmd = parts[0].Trim().Trim('\uFEFF').ToLowerInvariant();
    switch (cmd)
    {
        case "help":
        case "?":
            PrintHelp();
            break;

        case "list":
            Console.WriteLine("  当前模拟设备：");
            foreach (var d in devices)
                Console.WriteLine($"    {d.Name,-6}  每 {d.Interval.TotalSeconds,2:0}s  → {d.Topic,-24}  如 {d.NextValue()}");
            break;

        case "publish":
            if (parts.Length < 3)
            {
                Log("用法: publish <topic> <payload>，例如 publish sensor/temp 25.5", ConsoleColor.DarkGray);
                break;
            }
            try
            {
                await client.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(parts[1])
                    .WithPayload(parts[2])
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build());
                Log($"已发布  {parts[1]} = {parts[2]}", ConsoleColor.Magenta);
            }
            catch (Exception ex)
            {
                Log($"发布失败: {ex.Message}", ConsoleColor.Red);
            }
            break;

        case "quit":
        case "exit":
            cts.Cancel();
            break;

        default:
            Log($"未知命令: {parts[0]}（输入 help 查看帮助）", ConsoleColor.DarkGray);
            break;
    }
}

// ---------- 优雅退出 ----------
cts.Cancel();
try { await Task.WhenAll(publishTasks); } catch { }
try { await client.DisconnectAsync(new MqttClientDisconnectOptions()); } catch { }
try { await broker.StopAsync(); } catch { }
Log("模拟器已退出。", ConsoleColor.Green);

// ==================== 实现 ====================

static async Task<MqttServer> StartBrokerAsync(int port)
{
    var options = new MqttServerOptions
    {
        DefaultEndpointOptions = { IsEnabled = true, Port = port },
    };
    var server = new MqttFactory().CreateMqttServer(options);

    server.ClientConnectedAsync += e =>
    {
        Log($"[客户端] {e.ClientId,-20} 已连接", ConsoleColor.Green);
        return Task.CompletedTask;
    };
    server.ClientDisconnectedAsync += e =>
    {
        Log($"[客户端] {e.ClientId,-20} 已断开", ConsoleColor.Green);
        return Task.CompletedTask;
    };
    // 拦截发布：把驱动写入（xxx/set）的消息高亮显示
    server.InterceptingPublishAsync += e =>
    {
        if (e.ApplicationMessage.Topic.EndsWith("/set", StringComparison.Ordinal))
            Log($"[写  入] {e.ClientId,-20} → {e.ApplicationMessage.Topic} = {GetPayload(e.ApplicationMessage)}", ConsoleColor.Yellow);
        return Task.CompletedTask;
    };

    await server.StartAsync();
    if (!server.IsStarted)
        throw new InvalidOperationException("Broker 启动失败，端口可能被占用: " + port);
    Log($"内置 Broker 已启动: mqtt://127.0.0.1:{port}", ConsoleColor.Green);
    return server;
}

static async Task RunDeviceAsync(IMqttClient client, SimDevice device, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var payload = device.NextValue();
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(device.Topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());
            Log($"[模拟设备] {device.Name,-6} → {device.Topic,-24} {payload}", ConsoleColor.Cyan);
        }
        catch (Exception ex)
        {
            Log($"[错误] {device.Name} 发布失败: {ex.Message}", ConsoleColor.Red);
        }
        try { await Task.Delay(device.Interval, ct); } catch (OperationCanceledException) { break; }
    }
}

static string GetPayload(MqttApplicationMessage msg)
{
    var seg = msg.PayloadSegment;
    return seg.Count == 0 ? string.Empty : Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count);
}

static void Log(string message, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    Console.ResetColor();
}

static void PrintBanner(int port)
{
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine("┌─────────────────────────────────────────────────────┐");
    Console.WriteLine("│  MQTT 模拟器：内置 Broker + 模拟设备自动上报        │");
    Console.WriteLine("└─────────────────────────────────────────────────────┘");
    Console.ResetColor();
    Console.WriteLine($"  内置 Broker:  mqtt://127.0.0.1:{port}");
    Console.WriteLine($"  在主程序添加 MQTT 设备时，BrokerUrl 填上面地址；");
    Console.WriteLine($"  Tag 地址填下面任意主题；写入自动发到 {{Topic}}/set。");
    Console.WriteLine();
}

static void PrintHelp()
{
    Console.WriteLine("  可用命令：");
    Console.WriteLine("    list                查看当前模拟设备");
    Console.WriteLine("    publish <主题> <值>  手动发布一条消息");
    Console.WriteLine("    quit / exit         退出模拟器");
    Console.WriteLine();
}

internal record SimDevice(string Name, string Topic, Func<string> NextValue, TimeSpan Interval);
