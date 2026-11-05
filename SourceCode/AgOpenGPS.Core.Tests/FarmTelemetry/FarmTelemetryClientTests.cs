using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgOpenGPS.Core.FarmTelemetry;
using AgOpenGPS.Core.Models;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests.FarmTelemetry
{
    public class FarmTelemetryClientTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public async Task RetriesExactlyOneIdenticalBodyAndIdentityWithApiKeyOnly()
        {
            var snapshot = Snapshot();
            var handler = new StubHandler((request, attempt, token) => Task.FromResult(attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Accepted(snapshot)));
            using (var client = Client(handler))
            {
                var result = await client.SendAsync(FarmTelemetryJson.Serialize(snapshot), snapshot.TelemetryId, snapshot.SnapshotId, () => true, CancellationToken.None);
                Assert.That(result.Successful, Is.True);
                Assert.That(handler.Bodies, Has.Count.EqualTo(2));
                Assert.That(handler.Bodies[0], Is.EqualTo(handler.Bodies[1]));
                Assert.That(handler.Paths, Is.All.EqualTo("/api/telemetry/v1/snapshots"));
                Assert.That(handler.Authorization, Is.All.EqualTo("ApiKey shared-test-key"));
                Assert.That(handler.HadIdempotencyKey, Is.False);
            }
        }

        [TestCase(400)]
        [TestCase(401)]
        [TestCase(403)]
        [TestCase(409)]
        [TestCase(413)]
        [TestCase(422)]
        public async Task ConfigurationAndValidationErrorsAreNotRetried(int status)
        {
            var snapshot = Snapshot();
            var handler = new StubHandler((request, attempt, token) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("not JSON") }));
            using (var client = Client(handler))
            {
                var result = await client.SendAsync(FarmTelemetryJson.Serialize(snapshot), snapshot.TelemetryId, snapshot.SnapshotId, () => true, CancellationToken.None);
                Assert.That(result.Successful, Is.False);
                Assert.That(result.TooLarge, Is.EqualTo(status == 413));
                Assert.That(handler.Bodies, Has.Count.EqualTo(1));
                if (status == 409) Assert.That(result.Message, Does.Contain("tractor assignment"));
            }
        }

        [Test]
        public async Task ChangedFieldDiscardsPreparedBodyBeforeRetry()
        {
            var snapshot = Snapshot();
            bool current = true;
            var handler = new StubHandler((request, attempt, token) =>
            {
                current = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            });
            using (var client = Client(handler))
            {
                var result = await client.SendAsync(FarmTelemetryJson.Serialize(snapshot), snapshot.TelemetryId, snapshot.SnapshotId, () => current, CancellationToken.None);
                Assert.That(result.Successful, Is.False);
                Assert.That(handler.Bodies, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public async Task CancellationStopsDisconnectedRequestWithoutRetry()
        {
            var snapshot = Snapshot();
            var entered = new TaskCompletionSource<bool>();
            var handler = new StubHandler(async (request, attempt, token) =>
            {
                entered.SetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Accepted(snapshot);
            });
            using (var client = Client(handler))
            using (var cancellation = new CancellationTokenSource())
            {
                var sending = client.SendAsync(FarmTelemetryJson.Serialize(snapshot), snapshot.TelemetryId, snapshot.SnapshotId, () => true, cancellation.Token);
                await entered.Task;
                Assert.That(sending.IsCompleted, Is.False);
                cancellation.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await sending);
                Assert.That(handler.Bodies, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public async Task SettingsReplacementCancelsOldRetryThenUsesFreshHostKeyAndIdentity()
        {
            var oldSnapshot = Snapshot();
            var firstResponse = new TaskCompletionSource<bool>();
            var oldHandler = new StubHandler((request, attempt, token) =>
            {
                firstResponse.TrySetResult(true);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            });
            using (var oldClient = new FarmTelemetryClient("https://old-farm.example", "old-key", oldHandler, TimeSpan.FromMinutes(1)))
            using (var cancellation = new CancellationTokenSource())
            {
                var oldSending = oldClient.SendAsync(FarmTelemetryJson.Serialize(oldSnapshot), oldSnapshot.TelemetryId,
                    oldSnapshot.SnapshotId, () => true, cancellation.Token);
                await firstResponse.Task;
                cancellation.Cancel();
                oldClient.Dispose();
                Assert.CatchAsync<OperationCanceledException>(async () => await oldSending);
                Assert.That(oldHandler.Bodies, Has.Count.EqualTo(1));

                var fresh = Snapshot();
                var freshHandler = new StubHandler((request, attempt, token) => Task.FromResult(Accepted(fresh)));
                using (var freshClient = new FarmTelemetryClient("https://new-farm.example", "new-key", freshHandler, TimeSpan.Zero))
                {
                    var result = await freshClient.SendAsync(FarmTelemetryJson.Serialize(fresh), fresh.TelemetryId,
                        fresh.SnapshotId, () => true, CancellationToken.None);
                    Assert.That(result.Successful, Is.True);
                    Assert.That(freshHandler.Hosts, Is.EqualTo(new[] { "new-farm.example" }));
                    Assert.That(freshHandler.Authorization, Is.EqualTo(new[] { "ApiKey new-key" }));
                    Assert.That((Guid)JObject.Parse(freshHandler.Bodies[0])["telemetryId"], Is.EqualTo(fresh.TelemetryId));
                    Assert.That(fresh.TelemetryId, Is.Not.EqualTo(oldSnapshot.TelemetryId));
                }
            }
        }

        [TestCase("https://agshare.agopengps.com", false)]
        [TestCase("https://AGSHARE.AGOPENGPS.COM./", false)]
        [TestCase("http://farm.example", false)]
        [TestCase("https://user:key@farm.example", false)]
        [TestCase("https://farm.example", true)]
        public void RequiresExplicitCustomHttpsHost(string host, bool allowed)
            => Assert.That(FarmTelemetryClient.IsCustomHttpsServer(host), Is.EqualTo(allowed));

        [Test]
        public void NullFixNeverGetsANewTimestampAndSimulatorProvenanceIsPreserved()
        {
            var positions = new FarmPositionTracker();
            positions.Observe(new Wgs84(52, 20), Now, true, true);
            positions.Correct(new Wgs84(52.1, 20.1));
            Assert.That(positions.Capture(Now.AddSeconds(5), true).ObservedAtUtc, Is.EqualTo(Now));
            Assert.That(positions.Capture(Now.AddSeconds(11), true), Is.Null);
            Assert.That(positions.Capture(Now.AddSeconds(5), false), Is.Null);
            positions.Observe(new Wgs84(52, 20), Now.AddSeconds(5), false, false);
            Assert.That(positions.Capture(Now.AddSeconds(6), false), Is.Null);
        }

        [Test]
        public void EasyDriveShapeHasNameButNoWidthOrFieldGeometry()
        {
            var json = JObject.Parse(FarmTelemetryJson.Serialize(Snapshot()));
            Assert.That((string)json["fieldStatus"], Is.EqualTo("CLOSED"));
            Assert.That(json["field"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["coverage"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That((string)json["implement"]["name"], Is.EqualTo("Talerzowka"));
            Assert.That(json["implement"]["widthMeters"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(FarmTelemetryJson.Serialize(Snapshot()), Does.Contain("2026-11-05T12:00:00Z"));
        }

        [Test]
        public void ReducedSnapshotKeepsMetadataAndReportsTooLargeWithoutGeometry()
        {
            var original = Snapshot();
            var snapshot = new FarmSnapshot(original.TelemetryId, original.SnapshotId, Now, "test", 300, true, null,
                original.Implement, "OPEN", new FarmField(Guid.NewGuid(), "Field", new[] { 20.0, 52.0 },
                    new FarmBoundary(new[] { new[] { new[] { 20.0, 52.0 } } })),
                FarmCoverage.Ready(Now, Now, false, new double[0][][]));
            var json = JObject.Parse(FarmTelemetryJson.Serialize(snapshot, true));
            Assert.That((string)json["field"]["name"], Is.EqualTo("Field"));
            Assert.That(json["field"]["boundary"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That((string)json["coverage"]["status"], Is.EqualTo("TOO_LARGE"));
            Assert.That(json["coverage"]["triangleStrips"], Is.Null);
            Assert.That(json["coverage"]["trailingBlockDeferred"], Is.Null);
        }

        [Test]
        public void ByteLimitDropsCoverageAndAnOversizedBoundaryWithoutTruncatingEither()
        {
            var coordinates = new double[600000][];
            for (int i = 0; i < coordinates.Length; i++) coordinates[i] = new[] { 20.1234567890123, 52.1234567890123 };
            var snapshot = Snapshot();
            var open = new FarmSnapshot(snapshot.TelemetryId, snapshot.SnapshotId, Now, "test", 300, false, null,
                snapshot.Implement, "OPEN", new FarmField(Guid.NewGuid(), "Field", new[] { 20.0, 52.0 }, new FarmBoundary(new[] { coordinates })),
                FarmCoverage.Ready(Now, Now, false, new[] { coordinates }));
            string serialized = FarmTelemetryJson.Serialize(open);
            var json = JObject.Parse(serialized);
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(serialized), Is.LessThan(FarmTelemetryJson.MaxRequestBytes));
            Assert.That((string)json["coverage"]["status"], Is.EqualTo("TOO_LARGE"));
            Assert.That(json["field"]["boundary"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["coverage"]["triangleStrips"], Is.Null);
        }

        private static FarmSnapshot Snapshot() => new FarmSnapshot(Guid.NewGuid(), Guid.NewGuid(), Now, "test-build", 300, false,
            null, new FarmImplement("Talerzowka", null), "CLOSED");

        private static FarmTelemetryClient Client(HttpMessageHandler handler) => new FarmTelemetryClient("https://farm.example", "shared-test-key", handler, TimeSpan.Zero);

        private static HttpResponseMessage Accepted(FarmSnapshot snapshot) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JObject
            {
                ["schemaVersion"] = 1,
                ["telemetryId"] = snapshot.TelemetryId,
                ["snapshotId"] = snapshot.SnapshotId,
                ["status"] = "ACCEPTED"
            }.ToString())
        };

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _reply;
            public readonly List<string> Bodies = new List<string>();
            public readonly List<string> Paths = new List<string>();
            public readonly List<string> Hosts = new List<string>();
            public readonly List<string> Authorization = new List<string>();
            public bool HadIdempotencyKey;
            public StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> reply) { _reply = reply; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync());
                Paths.Add(request.RequestUri.AbsolutePath);
                Hosts.Add(request.RequestUri.Host);
                Authorization.Add(request.Headers.Authorization.ToString());
                HadIdempotencyKey |= request.Headers.Contains("Idempotency-Key");
                return await _reply(request, Bodies.Count, cancellationToken);
            }
        }
    }
}
