using System;
using System.IO;
using System.Threading;
using AgOpenGPS.Core.FarmTelemetry;
using AgOpenGPS.Core.Models;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests.FarmTelemetry
{
    public class FarmFieldReadinessTests
    {
        [Test]
        public void FailedCreationCannotReadExistingNamedFieldsSavedFilesWhenSyncIsDue()
        {
            string directory = Path.Combine(Path.GetTempPath(), "aog-failed-create-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Guid existingId = Guid.NewGuid();
                File.WriteAllText(Path.Combine(directory, "agshare.txt"), existingId.ToString());
                File.WriteAllText(Path.Combine(directory, "Field.txt"), "StartFix\n52,20\n");
                File.WriteAllText(Path.Combine(directory, "Sections.txt"), "5\n27,151,160\n0,0,0\n3,0,0\n0,10,0\n3,10,0\n");
                var readiness = new FarmFieldReadiness<object>();
                var previouslyLoaded = new object();
                readiness.Complete(previouslyLoaded);

                readiness.Clear();
                var failedCreation = new object();
                Assert.That(readiness.GetStatus(failedCreation, true, false), Is.EqualTo("LOADING"));
                string status = readiness.GetStatus(failedCreation, false, false);
                PreparedFarmField selected = null;
                if (status == "OPEN")
                    selected = SavedFieldReader.Read(new FarmFieldCapture(directory, "Existing field", new Wgs84(52, 20), new GeoDelta(0, 0)), CancellationToken.None);

                Assert.That(status, Is.EqualTo("LOADING"));
                Assert.That(selected, Is.Null);
                Assert.That(File.ReadAllText(Path.Combine(directory, "agshare.txt")), Is.EqualTo(existingId.ToString()));
                Assert.That(readiness.GetStatus(previouslyLoaded, false, false), Is.EqualTo("LOADING"));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void OnlySuccessfulCompletionMakesTheExactFieldReady()
        {
            var readiness = new FarmFieldReadiness<object>();
            var field = new object();
            Assert.That(readiness.GetStatus(field, true, false), Is.EqualTo("LOADING"));
            readiness.Complete(field);
            Assert.That(readiness.GetStatus(field, true, false), Is.EqualTo("LOADING"));
            Assert.That(readiness.GetStatus(field, false, false), Is.EqualTo("OPEN"));
            Assert.That(readiness.GetStatus(new object(), false, false), Is.EqualTo("LOADING"));
            readiness.Clear();
            Assert.That(readiness.GetStatus(field, false, false), Is.EqualTo("LOADING"));
            Assert.That(readiness.GetStatus(null, false, false), Is.EqualTo("CLOSED"));
        }

        [Test]
        public void EasyDriveNeverExportsItsTemporaryFieldEvenDuringATransition()
        {
            var readiness = new FarmFieldReadiness<object>();
            var temporaryField = new object();
            readiness.Complete(temporaryField);
            Assert.That(readiness.GetStatus(temporaryField, false, true), Is.EqualTo("CLOSED"));
            Assert.That(readiness.GetStatus(temporaryField, true, true), Is.EqualTo("CLOSED"));
        }
    }
}
