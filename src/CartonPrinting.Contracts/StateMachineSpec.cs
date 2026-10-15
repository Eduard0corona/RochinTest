using System.Text.Json;

namespace CartonPrinting.Contracts;

/// <summary>Transición de una máquina de <c>spec/contracts/state-machine.json</c>.</summary>
public sealed record StateTransition(string Id, string Machine, IReadOnlyList<string> From, string To, string Mode, string Tx);

/// <summary>Máquina de estados tal como la define la especificación.</summary>
public sealed record StateMachineDefinition(
    string Name,
    string Table,
    string Column,
    string Initial,
    IReadOnlyList<string> States,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Reasons,
    IReadOnlyList<StateTransition> Transitions,
    IReadOnlyList<(string From, string To)> Forbidden,
    IReadOnlyDictionary<string, string?> ResultOutcome);

/// <summary>
/// Lectura de <c>spec/contracts/state-machine.json</c>, incrustado al compilar desde spec/ (no se
/// copia a src/). Es la fuente de verdad de estados, motivos, transiciones, namespace UUIDv5 y
/// semántica de <c>final</c> de cada outcome.
/// </summary>
public sealed class StateMachineSpec
{
    private static readonly Lazy<StateMachineSpec> Embedded = new(() =>
    {
        using var stream = SpecResources.Open(SpecResources.StateMachine);
        using var doc = JsonDocument.Parse(stream);
        return new StateMachineSpec(doc.RootElement);
    });

    private StateMachineSpec(JsonElement root)
    {
        Version = root.GetProperty("version").GetString()!;
        UuidNamespace = Guid.Parse(root.GetProperty("uuidNamespace").GetString()!);
        UuidNames = root.GetProperty("uuidNames").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        var machines = new Dictionary<string, StateMachineDefinition>();
        foreach (var m in root.GetProperty("machines").EnumerateObject())
        {
            var def = m.Value;
            var reasons = new Dictionary<string, IReadOnlyList<string>>();
            if (def.TryGetProperty("reasons", out var r))
            {
                foreach (var p in r.EnumerateObject())
                {
                    reasons[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString()!).ToArray();
                }
            }

            var transitions = def.GetProperty("transitions").EnumerateArray().Select(t => new StateTransition(
                t.GetProperty("id").GetString()!,
                m.Name,
                t.GetProperty("from").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                t.GetProperty("to").GetString()!,
                t.GetProperty("mode").GetString()!,
                t.GetProperty("tx").GetString()!)).ToArray();

            var forbidden = def.GetProperty("forbidden").EnumerateArray()
                .Select(f => (f.GetProperty("from").GetString()!, f.GetProperty("to").GetString()!)).ToArray();

            var outcomes = def.GetProperty("resultOutcome").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString());

            machines[m.Name] = new StateMachineDefinition(
                m.Name,
                def.GetProperty("table").GetString()!,
                def.GetProperty("column").GetString()!,
                def.GetProperty("initial").GetString()!,
                def.GetProperty("states").EnumerateObject().Select(p => p.Name).ToArray(),
                reasons,
                transitions,
                forbidden,
                outcomes);
        }

        Machines = machines;
        ContractOutcomeFinal = root.GetProperty("contractOutcomes").EnumerateObject()
            .Where(p => !p.Name.StartsWith('$'))
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("final").GetBoolean());
    }

    public static StateMachineSpec Instance => Embedded.Value;

    public string Version { get; }

    public Guid UuidNamespace { get; }

    public IReadOnlyDictionary<string, string> UuidNames { get; }

    public IReadOnlyDictionary<string, StateMachineDefinition> Machines { get; }

    /// <summary><c>final</c> que exige cada outcome de <c>PrintJobResult</c>.</summary>
    public IReadOnlyDictionary<string, bool> ContractOutcomeFinal { get; }

    public IEnumerable<StateTransition> AllTransitions => Machines.Values.SelectMany(m => m.Transitions);
}
