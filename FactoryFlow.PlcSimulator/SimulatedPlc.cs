
namespace FactoryFlow.PlcSimulator
{
    public enum PlcState
    {
        Idle,
        Running,
        Completed,
        Fault
    }

    public class SimulatedPlc
    {
        internal object SyncRoot { get; } = new();
        private PlcState _state = PlcState.Idle;
        private bool _emergencyStopActive;
        private bool _guardClosed = true;
        private double _actualWeight;
        private double _targetWeight = 500;

        public PlcState State { get { lock (SyncRoot) return _state; } private set { lock (SyncRoot) _state = value; } }

        public bool EmergencyStopActive { get { lock (SyncRoot) return _emergencyStopActive; } set { lock (SyncRoot) _emergencyStopActive = value; } }

        public bool GuardClosed { get { lock (SyncRoot) return _guardClosed; } set { lock (SyncRoot) _guardClosed = value; } }

        public double ActualWeight { get { lock (SyncRoot) return _actualWeight; } private set { lock (SyncRoot) _actualWeight = value; } }
        public double TargetWeight { get { lock (SyncRoot) return _targetWeight; } }

        public bool StartBatch() => StartBatch(TargetWeight);

        public bool StartBatch(double targetWeight)
        {
            lock (SyncRoot)
            {
                if (!double.IsFinite(targetWeight) || targetWeight < 0.1 || targetWeight > 1000000)
                    return false;
                // Keep the completed result visible until the operator starts the next batch.
                if (State != PlcState.Idle && State != PlcState.Completed)
                {
                    Console.WriteLine("Start rejected: Machine must be idle or completed.");
                    return false;
                }
    
                if (EmergencyStopActive)
                {
                    Console.WriteLine("Start rejected: Emergency stop is active.");
                    return false;
                }
    
                if (!GuardClosed)
                {
                    Console.WriteLine("Start rejected: Safety guard is open.");
                    return false;
                }
    
                _targetWeight = targetWeight;
                ActualWeight = 0;
                State = PlcState.Running;
    
                Console.WriteLine("Batch started successfully.");
                return true;
            }
        }

        public void Tick()
        {
            lock (SyncRoot)
            {
                if (State != PlcState.Running)
                    return;
    
                if (EmergencyStopActive || !GuardClosed)
                {
                    State = PlcState.Fault;
                    Console.WriteLine("FAULT: Safety interlock triggered.");
                    return;
                }
    
                ActualWeight += 5;
    
                if (ActualWeight >= _targetWeight)
                {
                    ActualWeight = _targetWeight;
                    State = PlcState.Completed;
                    Console.WriteLine("Batch completed successfully.");
                }
            }
        }

        public void StopBatch()
        {
            lock (SyncRoot)
            {
                if (State == PlcState.Running)
                {
                    State = PlcState.Idle;
                    Console.WriteLine("Batch stopped.");
                }
            }
        }

        public bool ResetFault()
        {
            lock (SyncRoot)
            {
                if (State != PlcState.Fault || EmergencyStopActive || !GuardClosed) return false;
                State = PlcState.Idle;
                Console.WriteLine("Fault reset accepted; machine is Idle.");
                return true;
            }
        }
    }
}


