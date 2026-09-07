private void LoadTestNodes()
{
    var now = DateTimeOffset.UtcNow;
    var testDevices = new[]
    {
            new DeviceStatus { DeviceId = "A1-TEMP-01", ZoneName = "Zone A", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Healthy,      LastValue = 22.4,  ZScore = 0.3,  ThroughputLastMinute = 12, LastSeen = now },
            new DeviceStatus { DeviceId = "A1-TEMP-02", ZoneName = "Zone A", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Healthy,      LastValue = 21.9,  ZScore = 0.1,  ThroughputLastMinute = 11, LastSeen = now },
            new DeviceStatus { DeviceId = "A2-HUM-01",  ZoneName = "Zone A", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Elevated,     LastValue = 68.2,  ZScore = 1.9,  ThroughputLastMinute = 9,  LastSeen = now },
            new DeviceStatus { DeviceId = "B1-PRES-01", ZoneName = "Zone B", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Healthy,      LastValue = 101.3, ZScore = 0.2,  ThroughputLastMinute = 14, LastSeen = now },
            new DeviceStatus { DeviceId = "B1-PRES-02", ZoneName = "Zone B", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Anomalous,    LastValue = 87.1,  ZScore = 4.6,  ThroughputLastMinute = 3,  LastSeen = now },
            new DeviceStatus { DeviceId = "B2-VIB-01",  ZoneName = "Zone B", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Elevated,     LastValue = 4.7,   ZScore = 2.1,  ThroughputLastMinute = 10, LastSeen = now },
            new DeviceStatus { DeviceId = "B2-VIB-02",  ZoneName = "Zone B", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Healthy,      LastValue = 1.2,   ZScore = 0.4,  ThroughputLastMinute = 13, LastSeen = now },
            new DeviceStatus { DeviceId = "C1-FLOW-01", ZoneName = "Zone C", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Disconnected, LastValue = 0.0,   ZScore = 0.0,  ThroughputLastMinute = 0,  LastSeen = now.AddMinutes(-14) },
            new DeviceStatus { DeviceId = "C1-FLOW-02", ZoneName = "Zone C", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Healthy,      LastValue = 5.3,   ZScore = 0.6,  ThroughputLastMinute = 12, LastSeen = now },
            new DeviceStatus { DeviceId = "C2-TEMP-01", ZoneName = "Zone C", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Anomalous,    LastValue = 41.8,  ZScore = 5.2,  ThroughputLastMinute = 2,  LastSeen = now },
            new DeviceStatus { DeviceId = "C2-TEMP-02", ZoneName = "Zone C", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Healthy,      LastValue = 23.1,  ZScore = 0.2,  ThroughputLastMinute = 11, LastSeen = now },
            new DeviceStatus { DeviceId = "C3-HUM-01",  ZoneName = "Zone C", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Disconnected, LastValue = 0.0,   ZScore = 0.0,  ThroughputLastMinute = 0,  LastSeen = now.AddMinutes(-31) },
            new DeviceStatus { DeviceId = "A3-CO2-01",  ZoneName = "Zone A", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Healthy,      LastValue = 612,   ZScore = 0.5,  ThroughputLastMinute = 10, LastSeen = now },
            new DeviceStatus { DeviceId = "A3-CO2-02",  ZoneName = "Zone A", Category = SensorCategory.Environmental, Severity = AnomalySeverity.Elevated,     LastValue = 940,   ZScore = 2.3,  ThroughputLastMinute = 8,  LastSeen = now },
        };

    foreach (var device in testDevices)
    {
        _statusByDevice[device.DeviceId] = device;
    }
    Recompute();
}