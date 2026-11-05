using System;
using System.IO;
using AgOpenGPS.Core.FarmTelemetry;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests.FarmTelemetry
{
    public class FarmContractTests
    {
        [TestCase("snapshot-request.example.json")]
        [TestCase("snapshot-second-tractor.example.json")]
        [TestCase("snapshot-easy-drive.example.json")]
        [TestCase("snapshot-no-field.example.json")]
        [TestCase("snapshot-unavailable.example.json")]
        [TestCase("snapshot-boundary-unavailable.example.json")]
        public void ProductionSerializerMatchesSharedContractFixtures(string fixture)
        {
            var expected = JObject.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "FarmTelemetry", "Fixtures", fixture)));
            var snapshot = Build(expected);
            string serialized = FarmTelemetryJson.Serialize(snapshot);
            Assert.That(JToken.DeepEquals(JObject.Parse(serialized), expected), Is.True, serialized);
            Export(fixture, serialized);
        }

        [Test]
        public void EmptyAndTooLargeKeepTheirDifferentContractShapes()
        {
            var normal = JObject.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
                "FarmTelemetry", "Fixtures", "snapshot-request.example.json")));
            var snapshot = Build(normal);
            var empty = snapshot.WithField(snapshot.Field, FarmCoverage.Ready(snapshot.Coverage.ReadAtUtc,
                snapshot.Coverage.FileModifiedAtUtc.Value, false, new double[0][][]));
            string emptyJson = FarmTelemetryJson.Serialize(empty);
            string reducedJson = FarmTelemetryJson.Serialize(snapshot, true);
            var emptyObject = JObject.Parse(emptyJson);
            var reducedObject = JObject.Parse(reducedJson);
            Assert.That((string)emptyObject["coverage"]["status"], Is.EqualTo("READY"));
            Assert.That((JArray)emptyObject["coverage"]["triangleStrips"], Is.Empty);
            Assert.That((bool)emptyObject["coverage"]["trailingBlockDeferred"], Is.False);
            Assert.That((string)reducedObject["coverage"]["status"], Is.EqualTo("TOO_LARGE"));
            Assert.That((string)reducedObject["coverage"]["reason"], Is.EqualTo("LIMIT_EXCEEDED"));
            Assert.That(reducedObject["coverage"]["triangleStrips"], Is.Null);
            Export("snapshot-empty.example.json", emptyJson);
            Export("snapshot-too-large.example.json", reducedJson);
        }

        private static FarmSnapshot Build(JObject fixture)
        {
            var p = fixture["position"] as JObject;
            var i = fixture["implement"] as JObject;
            var f = fixture["field"] as JObject;
            var c = fixture["coverage"] as JObject;
            FarmCoverage coverage = c == null ? null : (string)c["status"] == "READY"
                ? FarmCoverage.Ready((DateTime)c["readAtUtc"], (DateTime)c["fileModifiedAtUtc"],
                    (bool)c["trailingBlockDeferred"], c["triangleStrips"].ToObject<double[][][]>())
                : FarmCoverage.Unavailable((string)c["reason"], (DateTime)c["readAtUtc"]);
            return new FarmSnapshot((Guid)fixture["telemetryId"], (Guid)fixture["snapshotId"],
                (DateTime)fixture["capturedAtUtc"], (string)fixture["appVersion"], (int)fixture["syncIntervalSeconds"],
                (bool)fixture["simulator"], p == null ? null : new FarmPosition((double)p["longitude"], (double)p["latitude"], (DateTime)p["observedAtUtc"]),
                i == null ? null : new FarmImplement((string)i["name"], (double?)i["widthMeters"]), (string)fixture["fieldStatus"],
                f == null ? null : new FarmField((Guid)f["agShareId"], (string)f["name"], f["origin"].ToObject<double[]>(),
                    f["boundary"] is JObject boundary ? new FarmBoundary(boundary["coordinates"].ToObject<double[][][]>()) : null), coverage);
        }

        private static void Export(string name, string json)
        {
            string directory = Environment.GetEnvironmentVariable("AOG_TELEMETRY_PAYLOAD_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name), json);
        }
    }
}
