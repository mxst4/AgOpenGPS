using System;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.Core.FarmTelemetry
{
    public sealed class FarmPositionTracker
    {
        private FarmPosition _position;
        private bool _simulator;
        private bool _valid;

        public void Observe(Wgs84 position, DateTime observedAtUtc, bool valid, bool simulator)
        {
            _valid = valid && position.IsValid;
            _simulator = simulator;
            _position = _valid ? new FarmPosition(position.Longitude, position.Latitude, observedAtUtc) : null;
        }

        public void Correct(Wgs84 position)
        {
            if (_valid && _position != null && position.IsValid)
                _position = new FarmPosition(position.Longitude, position.Latitude, _position.ObservedAtUtc);
        }

        public FarmPosition Capture(DateTime capturedAtUtc, bool simulator)
        {
            if (!_valid || _position == null || simulator != _simulator) return null;
            double age = (capturedAtUtc - _position.ObservedAtUtc).TotalSeconds;
            return age >= 0 && age <= 10 ? _position : null;
        }
    }
}
