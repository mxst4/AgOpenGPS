using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AgOpenGPS.Core.FarmTelemetry
{
    public sealed class FarmSnapshot
    {
        public int SchemaVersion => 1;
        public Guid TelemetryId { get; }
        public Guid SnapshotId { get; }
        public DateTime CapturedAtUtc { get; }
        public string AppVersion { get; }
        public int SyncIntervalSeconds { get; }
        public bool Simulator { get; }
        public FarmPosition Position { get; }
        public FarmImplement Implement { get; }
        public string FieldStatus { get; }
        public FarmField Field { get; }
        public FarmCoverage Coverage { get; }

        public FarmSnapshot(Guid telemetryId, Guid snapshotId, DateTime capturedAtUtc, string appVersion,
            int syncIntervalSeconds, bool simulator, FarmPosition position, FarmImplement implement,
            string fieldStatus, FarmField field = null, FarmCoverage coverage = null)
        {
            TelemetryId = telemetryId;
            SnapshotId = snapshotId;
            CapturedAtUtc = capturedAtUtc;
            AppVersion = appVersion;
            SyncIntervalSeconds = syncIntervalSeconds;
            Simulator = simulator;
            Position = position;
            Implement = implement;
            FieldStatus = fieldStatus;
            Field = field;
            Coverage = coverage;
        }

        public FarmSnapshot WithField(FarmField field, FarmCoverage coverage)
        {
            return new FarmSnapshot(TelemetryId, SnapshotId, CapturedAtUtc, AppVersion,
                SyncIntervalSeconds, Simulator, Position, Implement, FieldStatus, field, coverage);
        }
    }

    public sealed class FarmPosition
    {
        public double Longitude { get; }
        public double Latitude { get; }
        public DateTime ObservedAtUtc { get; }

        public FarmPosition(double longitude, double latitude, DateTime observedAtUtc)
        {
            Longitude = longitude;
            Latitude = latitude;
            ObservedAtUtc = observedAtUtc;
        }
    }

    public sealed class FarmImplement
    {
        public string Name { get; }
        public double? WidthMeters { get; }

        public FarmImplement(string name, double? widthMeters)
        {
            Name = name;
            WidthMeters = widthMeters;
        }
    }

    public sealed class FarmField
    {
        public Guid AgShareId { get; }
        public string Name { get; }
        public double[] Origin { get; }
        public FarmBoundary Boundary { get; }

        public FarmField(Guid agShareId, string name, double[] origin, FarmBoundary boundary)
        {
            AgShareId = agShareId;
            Name = name;
            Origin = origin;
            Boundary = boundary;
        }

        public FarmField WithoutBoundary() => new FarmField(AgShareId, Name, Origin, null);
    }

    public sealed class FarmBoundary
    {
        public string Type => "Polygon";
        public double[][][] Coordinates { get; }
        public FarmBoundary(double[][][] coordinates) { Coordinates = coordinates; }
    }

    public sealed class FarmCoverage
    {
        public string Status { get; }
        public DateTime ReadAtUtc { get; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? FileModifiedAtUtc { get; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? TrailingBlockDeferred { get; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double[][][] TriangleStrips { get; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Reason { get; }

        private FarmCoverage(string status, DateTime readAtUtc, DateTime? fileModifiedAtUtc,
            bool? trailingBlockDeferred, double[][][] triangleStrips, string reason)
        {
            Status = status;
            ReadAtUtc = readAtUtc;
            FileModifiedAtUtc = fileModifiedAtUtc;
            TrailingBlockDeferred = trailingBlockDeferred;
            TriangleStrips = triangleStrips;
            Reason = reason;
        }

        public static FarmCoverage Ready(DateTime readAtUtc, DateTime modifiedAtUtc, bool deferred, double[][][] strips)
            => new FarmCoverage("READY", readAtUtc, modifiedAtUtc, deferred, strips, null);

        public static FarmCoverage Unavailable(string reason, DateTime readAtUtc)
            => new FarmCoverage(reason == "LIMIT_EXCEEDED" ? "TOO_LARGE" : "UNAVAILABLE", readAtUtc, null, null, null, reason);
    }

    public static class FarmTelemetryJson
    {
        public const int MaxRequestBytes = 10 * 1024 * 1024;
        public const int MaxCoverageVertices = 200000;
        public const int MaxStrips = 50000;
        public const int MaxBoundaryVertices = 20000;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        public static string Serialize(FarmSnapshot snapshot, bool omitGeometry = false)
        {
            if (omitGeometry && snapshot.Field != null)
                snapshot = snapshot.WithField(snapshot.Field.WithoutBoundary(),
                    FarmCoverage.Unavailable("LIMIT_EXCEEDED", snapshot.Coverage.ReadAtUtc));
            string json = JsonConvert.SerializeObject(snapshot, Settings);
            if (System.Text.Encoding.UTF8.GetByteCount(json) <= MaxRequestBytes) return json;
            if (snapshot.Field == null) throw new InvalidOperationException("Farm snapshot metadata exceeds the request limit.");
            snapshot = snapshot.WithField(snapshot.Field, FarmCoverage.Unavailable("LIMIT_EXCEEDED", snapshot.Coverage.ReadAtUtc));
            json = JsonConvert.SerializeObject(snapshot, Settings);
            if (System.Text.Encoding.UTF8.GetByteCount(json) <= MaxRequestBytes) return json;
            json = JsonConvert.SerializeObject(snapshot.WithField(snapshot.Field.WithoutBoundary(), snapshot.Coverage), Settings);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxRequestBytes)
                throw new InvalidOperationException("Farm snapshot metadata exceeds the request limit.");
            return json;
        }
    }
}
