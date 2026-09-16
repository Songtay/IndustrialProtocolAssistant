using System.Text.Json;
using IndustrialProtocolAssistant.Core;
using Microsoft.Data.Sqlite;

namespace IndustrialProtocolAssistant.Storage;

/// <summary>
/// 系统配置持久化：设备连接参数 + Tag 配置，存 SQLite（system-config.db）。
/// 与 TimeSeriesStore（时序采样数据）分开存储：本类负责"配置"，时序库负责"采样数据"。
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public ConfigStore(string dbPath = "system-config.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS device_configs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                driver_type TEXT NOT NULL,
                params_json TEXT NOT NULL DEFAULT '{}',
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                slave_id INTEGER NOT NULL,
                poll_interval_ms INTEGER NOT NULL,
                serial_port TEXT NOT NULL DEFAULT '',
                baud_rate INTEGER NOT NULL DEFAULT 9600
            );
            CREATE TABLE IF NOT EXISTS tag_definitions (
                tag_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                data_type TEXT NOT NULL,
                device_id TEXT NOT NULL,
                address TEXT NOT NULL,
                length INTEGER NOT NULL,
                high_alarm REAL,
                low_alarm REAL,
                write_address TEXT,
                driver_type TEXT NOT NULL DEFAULT 'ModbusTcp'
            );
            """;
        cmd.ExecuteNonQuery();
        MigrateDeviceTable();
        MigrateTagTable();
        MigrateTagColumns();
    }

    /// <summary>兼容旧库：老版本的 device_configs 缺少列时逐列补齐。</summary>
    private void MigrateDeviceTable()
    {
        lock (_gate)
        {
            foreach (var (col, ddl) in new[]
                     {
                         ("params_json", "ALTER TABLE device_configs ADD COLUMN params_json TEXT NOT NULL DEFAULT '{}'"),
                         ("serial_port", "ALTER TABLE device_configs ADD COLUMN serial_port TEXT NOT NULL DEFAULT ''"),
                         ("baud_rate", "ALTER TABLE device_configs ADD COLUMN baud_rate INTEGER NOT NULL DEFAULT 9600"),
                     })
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = ddl;
                try { cmd.ExecuteNonQuery(); }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* 列已存在，忽略 */ }
            }
        }
    }

    /// <summary>兼容旧库：tag_definitions.address 旧版为 INTEGER（仅 Modbus 寄存器号），
    /// 新版本为 TEXT（兼容 OPC UA NodeId / MQTT Topic）；新版还增加 driver_type 列按协议分组保存。
    /// 检测到任一缺失时重建表并做转换（旧数据统一归入 ModbusTcp）。</summary>
    private void MigrateTagTable()
    {
        lock (_gate)
        {
            string? addrType = null;
            var hasDriverType = false;
            using (var probe = _conn.CreateCommand())
            {
                probe.CommandText = "PRAGMA table_info(tag_definitions)";
                using var reader = probe.ExecuteReader();
                while (reader.Read())
                {
                    switch (reader.GetString(1))
                    {
                        case "address": addrType = reader.GetString(2); break;
                        case "driver_type": hasDriverType = true; break;
                    }
                }
            }
            if (addrType is "TEXT" && hasDriverType) return;

            using var tx = _conn.BeginTransaction();
            using (var c1 = _conn.CreateCommand())
            {
                c1.Transaction = tx;
                c1.CommandText = """
                    CREATE TABLE tag_definitions_new (
                        tag_id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        data_type TEXT NOT NULL,
                        device_id TEXT NOT NULL,
                        address TEXT NOT NULL,
                        length INTEGER NOT NULL,
                        high_alarm REAL,
                        low_alarm REAL,
                        write_address TEXT,
                        driver_type TEXT NOT NULL DEFAULT 'ModbusTcp'
                    )
                    """;
                c1.ExecuteNonQuery();
            }
            using (var c2 = _conn.CreateCommand())
            {
                c2.Transaction = tx;
                c2.CommandText = """
                    INSERT INTO tag_definitions_new(tag_id,name,data_type,device_id,address,length,high_alarm,low_alarm,driver_type)
                    SELECT tag_id,name,data_type,device_id,CAST(address AS TEXT),length,high_alarm,low_alarm,'ModbusTcp'
                    FROM tag_definitions
                    """;
                c2.ExecuteNonQuery();
            }
            using (var c3 = _conn.CreateCommand())
            {
                c3.Transaction = tx;
                c3.CommandText = "DROP TABLE tag_definitions";
                c3.ExecuteNonQuery();
            }
            using (var c4 = _conn.CreateCommand())
            {
                c4.Transaction = tx;
                c4.CommandText = "ALTER TABLE tag_definitions_new RENAME TO tag_definitions";
                c4.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>兼容旧库：tag_definitions 表缺失可选列时逐列补齐（已重建的新库列已包含，此处幂等跳过）。</summary>
    private void MigrateTagColumns()
    {
        lock (_gate)
        {
            foreach (var (col, ddl) in new[]
                     {
                         ("write_address", "ALTER TABLE tag_definitions ADD COLUMN write_address TEXT"),
                     })
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = ddl;
                try { cmd.ExecuteNonQuery(); }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* 列已存在，忽略 */ }
            }
        }
    }

    /// <summary>
    /// 保存设备连接配置（UPSERT 覆盖）。
    /// 协议参数统一存 params_json；host/port/slave_id 等旧列同步写入，保证旧版本工具仍可读取。
    /// </summary>
    public void SaveDevice(DeviceConfig cfg)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO device_configs(id, name, driver_type, params_json, host, port, slave_id, poll_interval_ms, serial_port, baud_rate)
                VALUES($id, $name, $drv, $params, $host, $port, $slave, $poll, $serial, $baud)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name, driver_type=excluded.driver_type, params_json=excluded.params_json,
                    host=excluded.host, port=excluded.port, slave_id=excluded.slave_id, poll_interval_ms=excluded.poll_interval_ms,
                    serial_port=excluded.serial_port, baud_rate=excluded.baud_rate
                """;
            cmd.Parameters.AddWithValue("$id", cfg.Id);
            cmd.Parameters.AddWithValue("$name", cfg.Name);
            cmd.Parameters.AddWithValue("$drv", cfg.DriverType);
            cmd.Parameters.AddWithValue("$params", SerializeParams(cfg.Params));
            cmd.Parameters.AddWithValue("$host", cfg.Get("Host"));
            cmd.Parameters.AddWithValue("$port", cfg.GetInt("Port", 0));
            cmd.Parameters.AddWithValue("$slave", cfg.GetInt("SlaveId", 1));
            cmd.Parameters.AddWithValue("$poll", cfg.PollIntervalMs);
            cmd.Parameters.AddWithValue("$serial", cfg.Get("SerialPort"));
            cmd.Parameters.AddWithValue("$baud", cfg.GetInt("BaudRate", 9600));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>读取设备配置；无配置返回 null（首次运行）。</summary>
    public DeviceConfig? LoadDevice()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id,name,driver_type,params_json,host,port,slave_id,poll_interval_ms,serial_port,baud_rate
                FROM device_configs LIMIT 1
                """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            // 优先使用 params_json（新版本协议参数），为空则从旧列还原（兼容老库）
            var dict = DeserializeParams(reader.IsDBNull(3) ? "" : reader.GetString(3));
            if (dict.Count == 0)
            {
                dict["Host"] = reader.IsDBNull(4) ? "" : reader.GetString(4);
                dict["Port"] = reader.GetInt32(5).ToString();
                dict["SlaveId"] = reader.GetInt32(6).ToString();
                if (!reader.IsDBNull(8))
                    dict["SerialPort"] = reader.GetString(8);
                dict["BaudRate"] = reader.IsDBNull(9) ? "9600" : reader.GetInt32(9).ToString();
            }

            return new DeviceConfig(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(7), dict);
        }
    }

    private static string SerializeParams(Dictionary<string, string> p) =>
        JsonSerializer.Serialize(p);

    private static Dictionary<string, string> DeserializeParams(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>全量覆盖保存指定协议（driverType）下的 Tag 配置（事务，避免残留已删除项，
    /// 且只影响该协议的 Tag，不影响其他协议已保存的配置）。</summary>
    public void SaveTags(IEnumerable<TagDefinition> tags, string driverType)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM tag_definitions WHERE driver_type=$drv";
                del.Parameters.AddWithValue("$drv", driverType);
                del.ExecuteNonQuery();
            }
            foreach (var t in tags)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO tag_definitions(tag_id, name, data_type, device_id, address, length, high_alarm, low_alarm, write_address, driver_type)
                    VALUES($id, $name, $type, $dev, $addr, $len, $high, $low, $write, $drv)
                    """;
                cmd.Parameters.AddWithValue("$id", t.Id);
                cmd.Parameters.AddWithValue("$name", t.Name);
                cmd.Parameters.AddWithValue("$type", t.DataType.ToString());
                cmd.Parameters.AddWithValue("$dev", t.DeviceId);
                cmd.Parameters.AddWithValue("$addr", t.Address);
                cmd.Parameters.AddWithValue("$len", t.Length);
                cmd.Parameters.AddWithValue("$high", (object?)t.HighAlarm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$low", (object?)t.LowAlarm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$write", (object?)t.WriteAddress ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$drv", driverType);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>读取指定协议（driverType）下的全部 Tag 配置（按地址排序）。</summary>
    public List<TagDefinition> LoadTags(string driverType)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT tag_id,name,data_type,device_id,address,length,high_alarm,low_alarm,write_address
                FROM tag_definitions WHERE driver_type=$drv
                ORDER BY CASE WHEN address GLOB '[0-9]*' THEN CAST(address AS INTEGER) ELSE 2147483647 END, address
                """;
            cmd.Parameters.AddWithValue("$drv", driverType);
            using var reader = cmd.ExecuteReader();
            var list = new List<TagDefinition>();
            while (reader.Read())
            {
                list.Add(new TagDefinition(
                    reader.GetString(0), reader.GetString(1),
                    Enum.Parse<TagDataType>(reader.GetString(2)), reader.GetString(3),
                    reader.GetString(4), (ushort)reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
            return list;
        }
    }

    public void Dispose() => _conn.Dispose();
}
