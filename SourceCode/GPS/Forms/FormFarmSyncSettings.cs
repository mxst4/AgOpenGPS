using System;
using System.Windows.Forms;
using AgOpenGPS.Controls;
using AgOpenGPS.Core.FarmTelemetry;
using AgOpenGPS.Forms;
using AgOpenGPS.Properties;

namespace AgOpenGPS
{
    public partial class FormFarmSyncSettings : Form
    {
        private readonly FormGPS _gps;

        public FormFarmSyncSettings(FormGPS gps)
        {
            _gps = gps;
            InitializeComponent();
            if (!Guid.TryParse(Settings.Default.FarmTelemetryId, out Guid id) || id == Guid.Empty)
            {
                Settings.Default.FarmTelemetryId = Guid.NewGuid().ToString();
                Settings.Default.Save();
            }
            telemetryId.Text = Settings.Default.FarmTelemetryId;
            enabled.Checked = Settings.Default.FarmSyncEnabled;
            interval.Value = Math.Max(60, Math.Min(3600, Settings.Default.FarmSyncIntervalSeconds));
            interval.Controls[0].Enabled = false;
            statusTimer.Tick += (sender, args) => RefreshStatus();
            statusTimer.Start();
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            status.Text = _gps.FarmSyncStatus + (_gps.FarmLastSuccessUtc.HasValue
                ? "\nLast success: " + _gps.FarmLastSuccessUtc.Value.ToLocalTime().ToString("g") : "\nNo successful upload this session.");
        }

        private void Interval_Click(object sender, EventArgs e) => interval.ShowKeypad(this);

        private void CopyId_Click(object sender, EventArgs e) => Clipboard.SetText(telemetryId.Text);

        private void NewId_Click(object sender, EventArgs e)
        {
            if (FormDialog.ShowQuestion("New tractor identity",
                "Create a different telemetry ID for this tablet? Register the new ID in Farm before enabling sync. Other tractors must keep their own IDs.") != DialogResult.OK) return;
            telemetryId.Text = Guid.NewGuid().ToString();
            enabled.Checked = false;
            SaveSettings();
        }

        private bool SaveSettings()
        {
            if (enabled.Checked && (!FarmTelemetryClient.IsCustomHttpsServer(Settings.Default.AgShareServer) || string.IsNullOrWhiteSpace(Settings.Default.AgShareApiKey)))
            {
                status.Text = "Save a custom Farm HTTPS server and API key in AgShare settings first.";
                return false;
            }
            Settings.Default.FarmTelemetryId = telemetryId.Text;
            Settings.Default.FarmSyncEnabled = enabled.Checked;
            Settings.Default.FarmSyncIntervalSeconds = (int)interval.Value;
            Settings.Default.Save();
            _gps.RefreshFarmSyncSettings();
            return true;
        }

        private void Save_Click(object sender, EventArgs e)
        {
            if (SaveSettings()) Close();
        }

        private async void Sync_Click(object sender, EventArgs e)
        {
            if (!SaveSettings()) return;
            sync.Enabled = false;
            try { await _gps.SyncFarmNowAsync(); }
            finally
            {
                if (!IsDisposed) { sync.Enabled = true; RefreshStatus(); }
            }
        }
    }
}
