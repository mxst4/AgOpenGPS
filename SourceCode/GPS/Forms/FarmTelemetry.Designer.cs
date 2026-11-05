using System;
using System.Threading;
using System.Threading.Tasks;
using AgLibrary.Settings;
using AgOpenGPS.Core.FarmTelemetry;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Properties;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private readonly FarmPositionTracker _farmPosition = new FarmPositionTracker();
        private readonly FarmFieldReadiness<Field> _farmFieldReadiness = new FarmFieldReadiness<Field>();
        private System.Windows.Forms.Timer _farmTimer;
        private FarmTelemetryClient _farmClient;
        private CancellationTokenSource _farmCancellation;
        private string _farmServer;
        private string _farmKey;
        private bool _farmBusy;
        private bool _farmStopped;
        private bool _farmReduceNextSnapshot;
        private int _farmSelectionGeneration;
        private int _farmFieldLoading;
        private DateTime _farmNextCaptureUtc;

        public string FarmSyncStatus { get; private set; } = "Farm sync is disabled.";
        public DateTime? FarmLastSuccessUtc { get; private set; }

        private void StartFarmSync()
        {
            _farmTimer = new System.Windows.Forms.Timer(components) { Interval = 1000 };
            _farmTimer.Tick += (sender, args) =>
            {
                if (!_farmBusy && DateTime.UtcNow >= _farmNextCaptureUtc) _ = SyncFarmNowAsync();
            };
            _farmTimer.Start();
        }

        public void InvalidateFarmFieldCapture() => Interlocked.Increment(ref _farmSelectionGeneration);

        public void BeginFarmFieldTransition()
        {
            Interlocked.Increment(ref _farmFieldLoading);
            _farmFieldReadiness.Clear();
            InvalidateFarmFieldCapture();
        }

        public void EndFarmFieldTransition()
        {
            Interlocked.Decrement(ref _farmFieldLoading);
            InvalidateFarmFieldCapture();
        }

        public void CompleteFarmFieldOpen()
        {
            _farmFieldReadiness.Complete(AppModel.Fields.ActiveField);
            InvalidateFarmFieldCapture();
        }

        public void RefreshFarmSyncSettings()
        {
            _farmCancellation?.Cancel();
            _farmClient?.Dispose();
            _farmClient = null;
            _farmServer = null;
            _farmKey = null;
            _farmNextCaptureUtc = DateTime.UtcNow;
            if (!Settings.Default.FarmSyncEnabled) FarmSyncStatus = "Farm sync is disabled.";
        }

        public async Task SyncFarmNowAsync()
        {
            if (_farmBusy || _farmStopped) return;
            int interval = Math.Max(60, Math.Min(3600, Settings.Default.FarmSyncIntervalSeconds));
            _farmNextCaptureUtc = DateTime.UtcNow.AddSeconds(interval);
            if (!Settings.Default.FarmSyncEnabled)
            {
                FarmSyncStatus = "Farm sync is disabled.";
                return;
            }
            if (!Guid.TryParse(Settings.Default.FarmTelemetryId, out Guid telemetryId) || telemetryId == Guid.Empty)
            {
                FarmSyncStatus = "Set up a telemetry ID in Farm sync settings.";
                return;
            }
            if (!FarmTelemetryClient.IsCustomHttpsServer(Settings.Default.AgShareServer) || string.IsNullOrWhiteSpace(Settings.Default.AgShareApiKey))
            {
                FarmSyncStatus = "Configure a custom Farm HTTPS server and shared AgShare key.";
                return;
            }
            _farmBusy = true;
            try
            {
                if (_farmClient == null || _farmServer != Settings.Default.AgShareServer || _farmKey != Settings.Default.AgShareApiKey)
                {
                    _farmCancellation?.Cancel();
                    _farmCancellation?.Dispose();
                    _farmClient?.Dispose();
                    _farmCancellation = new CancellationTokenSource();
                    _farmServer = Settings.Default.AgShareServer;
                    _farmKey = Settings.Default.AgShareApiKey;
                    _farmClient = new FarmTelemetryClient(_farmServer, _farmKey);
                }
                var client = _farmClient;
                var cancellation = _farmCancellation.Token;
                int generation = Volatile.Read(ref _farmSelectionGeneration);
                DateTime capturedAtUtc = DateTime.UtcNow;
                bool simulator = timerSim.Enabled;
                FarmImplement implement = null;
                string toolName = RegistrySettings.toolProfileName?.Trim();
                if (!string.IsNullOrWhiteSpace(toolName) && toolName.Length <= 120 && RegistrySettings.toolProfileLoadResult == LoadResult.Ok)
                {
                    double? width = !isEasyDriveMode && tool != null && tool.width > 0 && tool.width <= 1000 ? (double?)tool.width : null;
                    implement = new FarmImplement(toolName, width);
                }
                var activeField = AppModel.Fields.ActiveField;
                FarmFieldCapture fieldCapture = null;
                string fieldStatus = _farmFieldReadiness.GetStatus(activeField,
                    Volatile.Read(ref _farmFieldLoading) > 0, isEasyDriveMode);
                if (fieldStatus == "OPEN")
                {
                    if (activeField.FieldDirectory != null && !string.IsNullOrWhiteSpace(activeField.Name) && activeField.Name.Length <= 200 && AppModel.LocalPlane.Origin.IsValid)
                    {
                        fieldCapture = new FarmFieldCapture(activeField.FieldDirectory.FullName, activeField.Name,
                            AppModel.LocalPlane.Origin, AppModel.SharedFieldProperties.DriftCompensation);
                    }
                    else fieldStatus = "LOADING";
                }
                var snapshot = new FarmSnapshot(telemetryId, Guid.NewGuid(), capturedAtUtc,
                    System.Windows.Forms.Application.ProductVersion, interval, simulator,
                    _farmPosition.Capture(capturedAtUtc, simulator), implement, fieldStatus);
                FarmSyncStatus = "Preparing saved field snapshot…";
                PreparedFarmField prepared = null;
                if (fieldCapture != null)
                {
                    try
                    {
                        prepared = await Task.Run(() => SavedFieldReader.Read(fieldCapture, cancellation), cancellation);
                        snapshot = snapshot.WithField(prepared.Field, prepared.Coverage);
                    }
                    catch (System.IO.IOException)
                    {
                        snapshot = new FarmSnapshot(telemetryId, snapshot.SnapshotId, capturedAtUtc,
                            snapshot.AppVersion, interval, simulator, snapshot.Position, implement, "LOADING");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        snapshot = new FarmSnapshot(telemetryId, snapshot.SnapshotId, capturedAtUtc,
                            snapshot.AppVersion, interval, simulator, snapshot.Position, implement, "LOADING");
                    }
                }
                if (generation != Volatile.Read(ref _farmSelectionGeneration) || cancellation.IsCancellationRequested) return;
                if (fieldCapture != null && (!ReferenceEquals(activeField, AppModel.Fields.ActiveField) ||
                    !SameFarmFieldContext(fieldCapture))) return;
                bool reduce = _farmReduceNextSnapshot;
                FarmSyncStatus = "Sending snapshot to Farm…";
                var result = await Task.Run(async () =>
                {
                    string body = FarmTelemetryJson.Serialize(snapshot, reduce);
                    return await client.SendAsync(body, telemetryId, snapshot.SnapshotId,
                        () => generation == Volatile.Read(ref _farmSelectionGeneration) && (prepared == null || prepared.IsCurrent()), cancellation).ConfigureAwait(false);
                }, cancellation);
                _farmReduceNextSnapshot = result.TooLarge;
                FarmSyncStatus = result.Message;
                if (result.Successful) FarmLastSuccessUtc = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                if (!_farmStopped) FarmSyncStatus = "Sync cancelled; the next snapshot will use current settings and field.";
            }
            catch (ObjectDisposedException)
            {
                if (!_farmStopped) FarmSyncStatus = "Farm settings changed; waiting for the next snapshot.";
            }
            catch (Exception)
            {
                FarmSyncStatus = "Unable to prepare Farm snapshot. Check settings and saved field files.";
            }
            finally { _farmBusy = false; }
        }

        private bool SameFarmFieldContext(FarmFieldCapture capture)
        {
            var origin = AppModel.LocalPlane.Origin;
            var drift = AppModel.SharedFieldProperties.DriftCompensation;
            return capture.Origin.Latitude == origin.Latitude && capture.Origin.Longitude == origin.Longitude &&
                capture.Drift.NorthingDelta == drift.NorthingDelta && capture.Drift.EastingDelta == drift.EastingDelta;
        }

        private void StopFarmSync()
        {
            _farmStopped = true;
            _farmTimer?.Stop();
            _farmTimer?.Dispose();
            _farmCancellation?.Cancel();
            _farmClient?.Dispose();
        }
    }
}
