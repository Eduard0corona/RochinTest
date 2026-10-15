using Opc.Ua;
using Opc.Ua.Server;

namespace CartonPrinting.Simulators.Plc;

/// <summary>
/// Espacio de direcciones del PLC simulado (namespace <c>urn:cartonprinting:plc</c>):
/// <list type="bullet">
/// <item><c>{station}.CartonCycleRecord</c>: registro latched del ciclo (String JSON, un valor atómico).</item>
/// <item><c>{station}.PendingCycles</c>: FIFO de registros (arreglo de String) para el perfil extendido.</item>
/// <item><c>{station}.LabelingReady</c>: latido que escribe el servicio.</item>
/// <item><c>{station}.PrintWindowCycleId</c>: ciclo en la zona de impresión (compuerta PLC_WINDOW_TAG).</item>
/// </list>
/// </summary>
public sealed class PlcNodeManager : CustomNodeManager2
{
    public const string NamespaceUri = "urn:cartonprinting:plc";

    private readonly string _stationId;
    private BaseDataVariableState? _record;
    private BaseDataVariableState? _fifo;
    private BaseDataVariableState? _labelingReady;
    private BaseDataVariableState? _printWindow;

    public PlcNodeManager(IServerInternal server, ApplicationConfiguration configuration, string stationId)
        : base(server, configuration, NamespaceUri)
    {
        _stationId = stationId;
    }

    public string RecordNodeId => $"ns={NamespaceIndex};s={_stationId}.CartonCycleRecord";

    public string FifoNodeId => $"ns={NamespaceIndex};s={_stationId}.PendingCycles";

    public string LabelingReadyNodeId => $"ns={NamespaceIndex};s={_stationId}.LabelingReady";

    public string PrintWindowNodeId => $"ns={NamespaceIndex};s={_stationId}.PrintWindowCycleId";

    /// <summary>Valores que el servicio escribió en LabelingReady, en orden.</summary>
    public List<bool> LabelingReadyWrites { get; } = [];

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = [];
            }

            var folder = new FolderState(null)
            {
                SymbolicName = _stationId,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(_stationId, NamespaceIndex),
                BrowseName = new QualifiedName(_stationId, NamespaceIndex),
                DisplayName = _stationId,
                EventNotifier = EventNotifiers.None,
            };
            folder.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, folder.NodeId));

            _record = Variable(folder, "CartonCycleRecord", DataTypeIds.String, ValueRanks.Scalar, AccessLevels.CurrentRead, string.Empty);
            _fifo = Variable(folder, "PendingCycles", DataTypeIds.String, ValueRanks.OneDimension, AccessLevels.CurrentRead, Array.Empty<string>());
            _labelingReady = Variable(folder, "LabelingReady", DataTypeIds.Boolean, ValueRanks.Scalar, AccessLevels.CurrentReadOrWrite, false);
            _labelingReady.OnSimpleWriteValue = (_, _, ref value) =>
            {
                if (value is bool b)
                {
                    lock (LabelingReadyWrites)
                    {
                        LabelingReadyWrites.Add(b);
                    }
                }

                return ServiceResult.Good;
            };
            _printWindow = Variable(folder, "PrintWindowCycleId", DataTypeIds.UInt64, ValueRanks.Scalar, AccessLevels.CurrentRead, 0UL);
            AddPredefinedNode(SystemContext, folder);
        }
    }

    /// <summary>El PLC fija el registro del ciclo; el cambio se notifica a los monitored items.</summary>
    public void Publish(LatchedRecord record, StatusCode? status = null) => SetValue(_record!, record.ToJson(), status ?? StatusCodes.Good);

    /// <summary>Publica un valor arbitrario (registros incoherentes o con calidad no Good).</summary>
    public void PublishRaw(string value, StatusCode status) => SetValue(_record!, value, status);

    public void SetFifo(IEnumerable<LatchedRecord> pending) => SetValue(_fifo!, pending.Select(r => r.ToJson()).ToArray(), StatusCodes.Good);

    public void SetPrintWindow(ulong cycleId) => SetValue(_printWindow!, cycleId, StatusCodes.Good);

    public bool LabelingReady
    {
        get
        {
            lock (Lock)
            {
                return _labelingReady!.Value is true;
            }
        }
    }

    private void SetValue(BaseDataVariableState variable, object value, StatusCode status)
    {
        lock (Lock)
        {
            variable.Value = value;
            variable.StatusCode = status;
            variable.Timestamp = DateTime.UtcNow;
            variable.ClearChangeMasks(SystemContext, false);
        }
    }

    private BaseDataVariableState Variable(NodeState parent, string name, NodeId dataType, int valueRank, byte access, object initial)
    {
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId($"{_stationId}.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            DataType = dataType,
            ValueRank = valueRank,
            AccessLevel = access,
            UserAccessLevel = access,
            Historizing = false,
            Value = initial,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(variable);
        return variable;
    }
}
