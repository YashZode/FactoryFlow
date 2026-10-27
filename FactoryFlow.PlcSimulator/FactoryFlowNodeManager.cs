using Opc.Ua;
using Opc.Ua.Server;

namespace FactoryFlow.PlcSimulator;

public sealed class FactoryFlowNodeManager : CustomNodeManager2
{
    private readonly SimulatedPlc _plc;
    private readonly Dictionary<string, BaseDataVariableState> _tags = new();

    public FactoryFlowNodeManager(IServerInternal server, ApplicationConfiguration configuration, SimulatedPlc plc)
        : base(server, configuration, PlcTagNames.NamespaceUri) => _plc = plc;

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var folder = new FolderState(null)
            {
                NodeId = new NodeId("Line1", NamespaceIndex),
                BrowseName = new QualifiedName("Line1", NamespaceIndex),
                DisplayName = new LocalizedText("Line1"),
                TypeDefinitionId = ObjectTypeIds.FolderType,
                ReferenceTypeId = ReferenceTypeIds.Organizes
            };
            folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));

            AddTag(folder, PlcTagNames.Status, DataTypeIds.Int32, false, (int)_plc.State);
            AddTag(folder, PlcTagNames.ActualWeight, DataTypeIds.Double, false, _plc.ActualWeight);
            AddTag(folder, PlcTagNames.StartRequest, DataTypeIds.Boolean, true, false);
            AddTag(folder, PlcTagNames.StopRequest, DataTypeIds.Boolean, true, false);
            AddTag(folder, PlcTagNames.EStopActive, DataTypeIds.Boolean, true, _plc.EmergencyStopActive);
            AddTag(folder, PlcTagNames.GuardClosed, DataTypeIds.Boolean, true, _plc.GuardClosed);
            AddTag(folder, PlcTagNames.TargetWeight, DataTypeIds.Double, false, _plc.TargetWeight);
            AddTag(folder, PlcTagNames.ResetRequest, DataTypeIds.Boolean, true, false);
            var start = new MethodState(folder)
            {
                NodeId = new NodeId(PlcTagNames.StartBatchWithTarget, NamespaceIndex),
                BrowseName = new QualifiedName("StartBatchWithTarget", NamespaceIndex),
                DisplayName = new LocalizedText("Start batch with recipe target"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent, Executable = true, UserExecutable = true
            };
            start.InputArguments = new PropertyState<ArrayOf<Argument>>.Implementation<StructureBuilder<Argument>>(start)
            {
                NodeId = new NodeId(PlcTagNames.StartBatchWithTarget + ".InputArguments", NamespaceIndex),
                BrowseName = new QualifiedName(BrowseNames.InputArguments), DisplayName = new LocalizedText("InputArguments"),
                TypeDefinitionId = VariableTypeIds.PropertyType, ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.Argument, ValueRank = ValueRanks.OneDimension,
                Value = [new Argument { Name = "TargetWeightKg", DataType = DataTypeIds.Double,
                    ValueRank = ValueRanks.Scalar, Description = new LocalizedText("Recipe target: 0.1 to 1000000 kg") }]
            };
            start.OutputArguments = new PropertyState<ArrayOf<Argument>>.Implementation<StructureBuilder<Argument>>(start)
            {
                NodeId = new NodeId(PlcTagNames.StartBatchWithTarget + ".OutputArguments", NamespaceIndex),
                BrowseName = new QualifiedName(BrowseNames.OutputArguments), DisplayName = new LocalizedText("OutputArguments"),
                TypeDefinitionId = VariableTypeIds.PropertyType, ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.Argument, ValueRank = ValueRanks.OneDimension,
                Value = [new Argument { Name = "Accepted", DataType = DataTypeIds.Boolean, ValueRank = ValueRanks.Scalar }]
            };
            start.OnCallMethod = (context, method, inputs, outputs) =>
            {
                if (inputs.Count != 1 || !inputs[0].TryGetValue(out double target)) return StatusCodes.BadTypeMismatch;
                if (!double.IsFinite(target) || target < 0.1 || target > 1000000) return StatusCodes.BadOutOfRange;
                // Recipe assignment and start are one server operation, preventing cross-client target races.
                lock (Lock)
                lock (_plc.SyncRoot)
                {
                    bool accepted = _plc.StartBatch(target);
                    outputs.Clear();
                    outputs.Add(accepted);
                    Publish();
                    return ServiceResult.Good;
                }
            };
            folder.AddChild(start);
            AddPredefinedNode(SystemContext, folder);
            foreach (var tag in _tags.Values)
                Console.WriteLine($"  {tag.NodeId} ({tag.DataType}, {tag.AccessLevel})");
        }
    }

    private void AddTag(FolderState folder, string name, NodeId dataType, bool writable, Variant value)
    {
        var node = new BaseDataVariableState(folder)
        {
            NodeId = new NodeId(name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            UserAccessLevel = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };
        if (writable) node.OnSimpleWriteValue = OnWrite;
        folder.AddChild(node);
        _tags.Add(name, node);
    }

    private ServiceResult OnWrite(ISystemContext context, NodeState node, ref Variant value)
    {
        if (!value.TryGetValue(out bool requested)) return StatusCodes.BadTypeMismatch;
        // The manager serializes UA operations; use the same lock for the timer.
        lock (Lock)
        lock (_plc.SyncRoot)
        {
            var name = node.BrowseName.Name;
            switch (name)
            {
                case PlcTagNames.StartRequest:
                    if (requested) _plc.StartBatch();
                    value = false; // Momentary command, acknowledged even if the PLC rejects startup.
                    break;
                case PlcTagNames.StopRequest:
                    if (requested) _plc.StopBatch();
                    value = false;
                    break;
                case PlcTagNames.ResetRequest:
                    if (requested && !_plc.ResetFault()) return StatusCodes.BadInvalidState;
                    value = false;
                    break;
                case PlcTagNames.EStopActive: _plc.EmergencyStopActive = requested; break;
                case PlcTagNames.GuardClosed: _plc.GuardClosed = requested; break;
                default: return StatusCodes.BadNotWritable;
            }
            Publish();
            return ServiceResult.Good;
        }
    }

    public void TickAndPublish()
    {
        lock (Lock)
        lock (_plc.SyncRoot)
        {
            _plc.Tick();
            Publish();
        }
    }

    private void Publish()
    {
        var timestamp = DateTime.UtcNow;
        Set(PlcTagNames.Status, (int)_plc.State, timestamp);
        Set(PlcTagNames.ActualWeight, _plc.ActualWeight, timestamp);
        Set(PlcTagNames.EStopActive, _plc.EmergencyStopActive, timestamp);
        Set(PlcTagNames.GuardClosed, _plc.GuardClosed, timestamp);
        Set(PlcTagNames.StartRequest, false, timestamp);
        Set(PlcTagNames.StopRequest, false, timestamp);
        Set(PlcTagNames.ResetRequest, false, timestamp);
        Set(PlcTagNames.TargetWeight, _plc.TargetWeight, timestamp);
    }

    private void Set(string name, Variant value, DateTime timestamp)
    {
        var node = _tags[name];
        node.Value = value;
        node.Timestamp = timestamp;
        node.StatusCode = StatusCodes.Good;
        node.ClearChangeMasks(SystemContext, false);
    }
}


