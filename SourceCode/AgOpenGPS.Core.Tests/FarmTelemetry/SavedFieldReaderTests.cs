using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgOpenGPS.Core.FarmTelemetry;
using AgOpenGPS.Core.Models;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests.FarmTelemetry
{
    public class SavedFieldReaderTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        private const string Strip = "5\n27,151,160\n0,0,0\n3,0,0\n0,10,0\n3,10,0\n";
        private static LocalPlane Plane => new LocalPlane(new Wgs84(52, 20), new SharedFieldProperties());

        [Test]
        public void KeepsEverySavedStripInOriginalOrderAndUsesCopiedDrift()
        {
            var plane = new LocalPlane(new Wgs84(52, 20), new SharedFieldProperties { DriftCompensation = new GeoDelta(12, 4) });
            var result = SavedFieldReader.ParseCoverage(Strip + Strip, Now.AddMinutes(-3), Now, plane, CancellationToken.None);
            var expected = plane.ConvertGeoCoordToWgs84(new GeoCoord(10, 3));
            Assert.That(result.Status, Is.EqualTo("READY"));
            Assert.That(result.TriangleStrips, Has.Length.EqualTo(2));
            Assert.That(result.TriangleStrips[0], Has.Length.EqualTo(4));
            Assert.That(result.TriangleStrips[0][3], Is.EqualTo(new[] { expected.Longitude, expected.Latitude }));
            Assert.That(result.TriangleStrips[1], Is.EqualTo(result.TriangleStrips[0]));
            Assert.That(result.FileModifiedAtUtc, Is.EqualTo(Now.AddMinutes(-3)));
        }

        [Test]
        public void EmptySavedFileClearsButIncompleteFirstBlockDoesNot()
        {
            var empty = SavedFieldReader.ParseCoverage("", Now, Now, Plane, CancellationToken.None);
            var partial = SavedFieldReader.ParseCoverage("5\n27,151,160\n0,0,0\n", Now, Now, Plane, CancellationToken.None);
            Assert.That(empty.Status, Is.EqualTo("READY"));
            Assert.That(empty.TriangleStrips, Is.Empty);
            Assert.That(empty.TrailingBlockDeferred, Is.False);
            Assert.That(partial.Status, Is.EqualTo("UNAVAILABLE"));
            Assert.That(partial.Reason, Is.EqualTo("INCOMPLETE_READ"));
            Assert.That(partial.TriangleStrips, Is.Null);
        }

        [Test]
        public void DefersIncompleteTrailingBlockWithoutDroppingCompletedStrips()
        {
            var partial = SavedFieldReader.ParseCoverage(Strip + "5\n27,151,160\n0,0,0\n", Now, Now, Plane, CancellationToken.None);
            Assert.That(partial.Status, Is.EqualTo("READY"));
            Assert.That(partial.TrailingBlockDeferred, Is.True);
            Assert.That(partial.TriangleStrips, Has.Length.EqualTo(1));
        }

        [TestCase("5\n27,151,160\n0,0,0\n3,0,0\n0,10,0\n3,10,0")]
        [TestCase("5\n")]
        public void UnterminatedFirstBlockCannotBeMistakenForACompleteSnapshot(string text)
        {
            var result = SavedFieldReader.ParseCoverage(text, Now, Now, Plane, CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo("UNAVAILABLE"));
            Assert.That(result.Reason, Is.EqualTo("INCOMPLETE_READ"));
        }

        [TestCase("6\n", "INVALID_DATA")]
        [TestCase("200003\n", "LIMIT_EXCEEDED")]
        [TestCase("5\n27,151,160\nNaN,0,0\n3,0,0\n0,10,0\n3,10,0\n", "INVALID_DATA")]
        public void InvalidOrOversizedCoverageIsNeverSilentlyTruncated(string text, string reason)
        {
            var result = SavedFieldReader.ParseCoverage(text, Now, Now, Plane, CancellationToken.None);
            Assert.That(result.Reason, Is.EqualTo(reason));
            Assert.That(result.TriangleStrips, Is.Null);
        }

        [TestCase("boundary-truncated-outer.txt")]
        [TestCase("boundary-truncated-hole.txt")]
        public void IncompleteBoundaryOmitsTheEntirePolygon(string fixture)
        {
            Assert.That(SavedFieldReader.ParseBoundary(Fixture(fixture), Plane, CancellationToken.None), Is.Null);
        }

        [Test]
        public void CompleteBoundaryPreservesAndClosesOuterAndHole()
        {
            var boundary = SavedFieldReader.ParseBoundary(Fixture("boundary-complete.txt"), Plane, CancellationToken.None);
            Assert.That(boundary.Coordinates, Has.Length.EqualTo(2));
            Assert.That(boundary.Coordinates.All(ring => ring.Length == 5 && ring[0].SequenceEqual(ring[4])), Is.True);
        }

        [TestCase("$Boundary\nFalse\n4\n0,0,0\n100,100,0\n100,0,0\n0,100,0\n")]
        [TestCase("$Boundary\nFalse\n4\n0,0,0\n100,0,0\n100,100,0\n0,100,0\nFalse\n4\n200,200,0\n210,200,0\n210,210,0\n200,210,0\n")]
        public void SelfCrossingRingsAndHolesOutsideTheOuterRingAreOmitted(string text)
            => Assert.That(SavedFieldReader.ParseBoundary(text, Plane, CancellationToken.None), Is.Null);

        [Test]
        public void HoleCrossingOuterRingIsOmitted()
        {
            var rings = new[]
            {
                new[] { new[] { 0.0, 0.0 }, new[] { 4.0, 0.0 }, new[] { 4.0, 4.0 }, new[] { 0.0, 4.0 }, new[] { 0.0, 0.0 } },
                new[] { new[] { 2.0, 2.0 }, new[] { 6.0, 2.0 }, new[] { 6.0, 3.0 }, new[] { 2.0, 3.0 }, new[] { 2.0, 2.0 } }
            };
            Assert.That(FarmBoundaryValidator.IsValid(rings, CancellationToken.None), Is.False);
        }

        [Test]
        public void ConcurrentFirstUsesPersistOneIdentityAndExplicitReplacementInvalidatesIt()
        {
            string directory = NewField();
            try
            {
                var ids = new Guid[30];
                Parallel.For(0, ids.Length, i => ids[i] = AgShareFieldIdentity.GetOrCreate(directory));
                Assert.That(ids.Distinct().Count(), Is.EqualTo(1));
                Assert.That(Guid.Parse(File.ReadAllText(Path.Combine(directory, "agshare.txt"))), Is.EqualTo(ids[0]));
                var prepared = SavedFieldReader.Read(new FarmFieldCapture(directory, "Field", new Wgs84(52, 20), new GeoDelta(0, 0)), CancellationToken.None);
                Assert.That(prepared.IsCurrent(), Is.True);
                AgShareFieldIdentity.Replace(directory, Guid.NewGuid());
                Assert.That(prepared.IsCurrent(), Is.False);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void OriginReplacementInvalidatesPreparedCoverageAndMismatchIsUnavailable()
        {
            string directory = NewField();
            try
            {
                var capture = new FarmFieldCapture(directory, "Field", new Wgs84(52, 20), new GeoDelta(0, 0));
                var prepared = SavedFieldReader.Read(capture, CancellationToken.None);
                AgShareFieldIdentity.Write(directory, () => File.WriteAllText(Path.Combine(directory, "Field.txt"), "StartFix\n53,20\n"));
                Assert.That(prepared.IsCurrent(), Is.False);
                var mismatch = SavedFieldReader.Read(capture, CancellationToken.None);
                Assert.That(mismatch.Coverage.Reason, Is.EqualTo("ORIGIN_CHANGED"));
                Assert.That(mismatch.Field.Boundary, Is.Null);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void MissingSavedFileDoesNotBecomeEmptyCoverage()
        {
            string directory = NewField();
            try
            {
                File.Delete(Path.Combine(directory, "Sections.txt"));
                var prepared = SavedFieldReader.Read(new FarmFieldCapture(directory, "Field", new Wgs84(52, 20), new GeoDelta(0, 0)), CancellationToken.None);
                Assert.That(prepared.Coverage.Reason, Is.EqualTo("FILE_MISSING"));
            }
            finally { Directory.Delete(directory, true); }
        }

        private static string NewField()
        {
            string directory = Path.Combine(Path.GetTempPath(), "aog-farm-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Field.txt"), "StartFix\n52,20\n");
            File.WriteAllText(Path.Combine(directory, "Sections.txt"), Strip);
            return directory;
        }

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "FarmTelemetry", "Fixtures", name));
    }
}
