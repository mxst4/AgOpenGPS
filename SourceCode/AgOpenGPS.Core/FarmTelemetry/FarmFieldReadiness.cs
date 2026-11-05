namespace AgOpenGPS.Core.FarmTelemetry
{
    public sealed class FarmFieldReadiness<TField> where TField : class
    {
        private TField _completedField;

        public void Clear() => _completedField = null;

        public void Complete(TField field) => _completedField = field;

        public string GetStatus(TField activeField, bool transitionInProgress, bool easyDrive)
        {
            if (easyDrive) return "CLOSED";
            if (transitionInProgress) return "LOADING";
            if (activeField == null) return "CLOSED";
            return ReferenceEquals(activeField, _completedField) ? "OPEN" : "LOADING";
        }
    }
}
