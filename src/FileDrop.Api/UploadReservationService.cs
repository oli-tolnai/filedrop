public sealed class UploadReservationService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, long> _remainingByUpload = [];

    public UploadReservation? TryReserve(long bytes, long currentlyAvailableBytes)
    {
        lock (_gate)
        {
            var alreadyReserved = _remainingByUpload.Values.Sum();
            if (bytes > Math.Max(0, currentlyAvailableBytes - alreadyReserved))
            {
                return null;
            }

            var id = Guid.NewGuid();
            _remainingByUpload.Add(id, bytes);
            return new UploadReservation(this, id);
        }
    }

    private void ReportWritten(Guid id, int bytes)
    {
        lock (_gate)
        {
            if (_remainingByUpload.TryGetValue(id, out var remaining))
            {
                _remainingByUpload[id] = Math.Max(0, remaining - bytes);
            }
        }
    }

    private void Release(Guid id)
    {
        lock (_gate)
        {
            _remainingByUpload.Remove(id);
        }
    }

    public sealed class UploadReservation(UploadReservationService owner, Guid id) : IDisposable
    {
        private bool _disposed;

        public void ReportWritten(int bytes)
        {
            if (!_disposed)
            {
                owner.ReportWritten(id, bytes);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Release(id);
        }
    }
}
