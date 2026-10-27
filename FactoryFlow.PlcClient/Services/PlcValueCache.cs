using Opc.Ua;
using System;
using System.Collections.Generic;

namespace FactoryFlow.PlcClient.Services
{
    // Owned by PlcCommunicationService's state lock. Keep quality rules separate from networking.
    internal sealed class PlcValueCache
    {
        internal static readonly string[] Tags =
        {
            PlcNodeIds.Status, PlcNodeIds.ActualWeight, PlcNodeIds.EStopActive, PlcNodeIds.GuardClosed
        };
        private readonly Dictionary<string, DateTimeUtc> _sourceTimes = new();
        private readonly Dictionary<string, DateTime> _arrivals = new();
        internal int? Status { get; private set; }
        internal double? Weight { get; private set; }
        internal bool? EmergencyStop { get; private set; }
        internal bool? GuardClosed { get; private set; }
        internal bool Complete => Status.HasValue && Weight.HasValue && EmergencyStop.HasValue && GuardClosed.HasValue;
        internal bool StateAndWeightCoherent => Complete &&
            _sourceTimes.TryGetValue(PlcNodeIds.Status, out var statusTime) &&
            _sourceTimes.TryGetValue(PlcNodeIds.ActualWeight, out var weightTime) && statusTime == weightTime;

        internal void Clear()
        {
            Status = null; Weight = null; EmergencyStop = null; GuardClosed = null;
            _sourceTimes.Clear(); _arrivals.Clear();
        }

        internal bool Apply(string tag, DataValue value, DateTime now)
        {
            if (!StatusCode.IsGood(value.StatusCode) || value.SourceTimestamp.IsNull ||
                value.SourceTimestamp.ToDateTime() < now.AddSeconds(-5) ||
                value.SourceTimestamp.ToDateTime() > now.AddSeconds(5))
            {
                Invalidate(tag);
                return true;
            }
            // An initial Read can finish after a newer subscription notification.
            if (_sourceTimes.TryGetValue(tag, out var previous) && value.SourceTimestamp < previous)
                return false;
            _sourceTimes[tag] = value.SourceTimestamp;
            _arrivals[tag] = now;
            switch (tag)
            {
                case PlcNodeIds.Status:
                    Status = value.WrappedValue.TryGetValue(out int status) && status >= 0 && status <= 3 ? status : (int?)null;
                    break;
                case PlcNodeIds.ActualWeight:
                    Weight = value.WrappedValue.TryGetValue(out double weight) && !double.IsNaN(weight) &&
                        !double.IsInfinity(weight) && weight >= 0 ? weight : (double?)null;
                    break;
                case PlcNodeIds.EStopActive:
                    EmergencyStop = value.WrappedValue.TryGetValue(out bool stop) ? stop : (bool?)null;
                    break;
                case PlcNodeIds.GuardClosed:
                    GuardClosed = value.WrappedValue.TryGetValue(out bool closed) ? closed : (bool?)null;
                    break;
                default: return false;
            }
            return true;
        }

        internal bool HasExpired(DateTime now) =>
            Array.Exists(Tags, tag => !_arrivals.TryGetValue(tag, out var time) || now - time > TimeSpan.FromSeconds(5));

        private void Invalidate(string tag)
        {
            switch (tag)
            {
                case PlcNodeIds.Status: Status = null; break;
                case PlcNodeIds.ActualWeight: Weight = null; break;
                case PlcNodeIds.EStopActive: EmergencyStop = null; break;
                case PlcNodeIds.GuardClosed: GuardClosed = null; break;
            }
        }
    }
}
