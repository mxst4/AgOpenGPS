namespace AgOpenGPS
{
    partial class FormFarmSyncSettings
    {
        private System.ComponentModel.IContainer components;
        private System.Windows.Forms.CheckBox enabled;
        private NudlessNumericUpDown interval;
        private System.Windows.Forms.TextBox telemetryId;
        private System.Windows.Forms.Label status;
        private System.Windows.Forms.Button sync;
        private System.Windows.Forms.Timer statusTimer;

        protected override void Dispose(bool disposing)
        {
            if (disposing) components?.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            statusTimer = new System.Windows.Forms.Timer(components) { Interval = 1000 };
            Text = "Farm sync";
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            ClientSize = new System.Drawing.Size(620, 575);
            Font = new System.Drawing.Font("Tahoma", 13F);
            BackColor = System.Drawing.Color.Gainsboro;
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;

            var info = new System.Windows.Forms.Label
            {
                Text = "Share position, selected implement and complete saved field coverage with Farm. Uses the saved AgShare server/key.",
                Location = new System.Drawing.Point(20, 15), Size = new System.Drawing.Size(580, 65)
            };
            enabled = new System.Windows.Forms.CheckBox
            {
                Text = "Enable Farm sync", Appearance = System.Windows.Forms.Appearance.Button,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Location = new System.Drawing.Point(20, 85), Size = new System.Drawing.Size(300, 75),
                Font = new System.Drawing.Font("Tahoma", 16F, System.Drawing.FontStyle.Bold)
            };
            var intervalLabel = new System.Windows.Forms.Label
            {
                Text = "Interval (seconds)", Location = new System.Drawing.Point(340, 85), Size = new System.Drawing.Size(260, 28)
            };
            interval = new NudlessNumericUpDown
            {
                Minimum = 60, Maximum = 3600, Value = 300, ReadOnly = true, InterceptArrowKeys = false,
                Location = new System.Drawing.Point(340, 115), Size = new System.Drawing.Size(260, 45),
                Font = new System.Drawing.Font("Tahoma", 24F), TextAlign = System.Windows.Forms.HorizontalAlignment.Center
            };
            interval.Click += Interval_Click;
            var idLabel = new System.Windows.Forms.Label
            {
                Text = "Telemetry ID — assign this to a tractor in Farm → AgOpenGPS",
                Location = new System.Drawing.Point(20, 175), Size = new System.Drawing.Size(580, 30)
            };
            telemetryId = new System.Windows.Forms.TextBox
            {
                ReadOnly = true, Location = new System.Drawing.Point(20, 210), Size = new System.Drawing.Size(580, 35),
                Font = new System.Drawing.Font("Tahoma", 16F)
            };
            var copy = CreateButton("Copy ID", 20, 255, 280);
            copy.Click += CopyId_Click;
            var newId = CreateButton("New ID for this tablet", 320, 255, 280);
            newId.Click += NewId_Click;
            var copyHint = new System.Windows.Forms.Label
            {
                Text = "Copied settings? Each tractor needs a different ID. IDs survive restarts and key changes.",
                Location = new System.Drawing.Point(20, 340), Size = new System.Drawing.Size(580, 48)
            };
            status = new System.Windows.Forms.Label
            {
                Location = new System.Drawing.Point(20, 392), Size = new System.Drawing.Size(580, 85)
            };
            sync = CreateButton("Sync now", 20, 485, 180);
            sync.Click += Sync_Click;
            var save = CreateButton("Save", 220, 485, 180);
            save.Click += Save_Click;
            var cancel = CreateButton("Cancel", 420, 485, 180);
            cancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            CancelButton = cancel;
            Controls.AddRange(new System.Windows.Forms.Control[]
            { info, enabled, intervalLabel, interval, idLabel, telemetryId, copy, newId, copyHint, status, sync, save, cancel });
        }

        private static System.Windows.Forms.Button CreateButton(string text, int left, int top, int width)
        {
            var button = new System.Windows.Forms.Button
            {
                Text = text, Location = new System.Drawing.Point(left, top), Size = new System.Drawing.Size(width, 75),
                FlatStyle = System.Windows.Forms.FlatStyle.Flat, Font = new System.Drawing.Font("Tahoma", 14F, System.Drawing.FontStyle.Bold)
            };
            button.FlatAppearance.BorderColor = System.Drawing.Color.SteelBlue;
            button.FlatAppearance.BorderSize = 2;
            return button;
        }
    }
}
