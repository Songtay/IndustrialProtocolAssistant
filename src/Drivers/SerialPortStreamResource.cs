using System.IO.Ports;
using NModbus.IO;

namespace IndustrialProtocolAssistant.Drivers;

/// <summary>
/// 把 System.IO.Ports.SerialPort 适配为 NModbus 的 IStreamResource。
/// NModbus 3.0.81 未内置串口适配器（仅有 TcpClientAdapter/SocketAdapter），RTU 传输需要此适配层。
/// </summary>
public sealed class SerialPortStreamResource : IStreamResource
{
    private readonly SerialPort _port;

    public SerialPortStreamResource(SerialPort port) => _port = port;

    public int InfiniteTimeout => Timeout.Infinite;

    public int ReadTimeout
    {
        get => _port.ReadTimeout;
        set => _port.ReadTimeout = value;
    }

    public int WriteTimeout
    {
        get => _port.WriteTimeout;
        set => _port.WriteTimeout = value;
    }

    public void DiscardInBuffer() => _port.DiscardInBuffer();

    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

    public void Dispose()
    {
        try { _port.Dispose(); } catch { /* 忽略关闭异常 */ }
    }
}
