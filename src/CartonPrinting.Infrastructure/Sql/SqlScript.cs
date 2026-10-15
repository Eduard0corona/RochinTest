using System.Text;
using System.Text.RegularExpressions;

namespace CartonPrinting.Infrastructure.Sql;

/// <summary>
/// Script T-SQL dividido en lotes. SqlClient no entiende el separador <c>GO</c> de sqlcmd: el
/// lote termina en cada línea que solo contiene <c>GO</c> (sin distinguir mayúsculas, con
/// espacios alrededor). Las variables <c>$(Nombre)</c> se sustituyen como en sqlcmd.
/// </summary>
public sealed partial class SqlScript
{
    private SqlScript(string name, string text, IReadOnlyList<string> batches)
    {
        Name = name;
        Text = text;
        Batches = batches;
        Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public string Name { get; }

    public string Text { get; }

    /// <summary>SHA-256 del texto original (antes de sustituir variables).</summary>
    public string Sha256 { get; }

    public IReadOnlyList<string> Batches { get; }

    public static SqlScript Load(string path, IReadOnlyDictionary<string, string>? variables = null) =>
        Parse(Path.GetFileName(path), File.ReadAllText(path, Encoding.UTF8), variables);

    public static SqlScript Parse(string name, string text, IReadOnlyDictionary<string, string>? variables = null)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (GoLine().IsMatch(line))
            {
                Flush();
                continue;
            }

            current.Append(variables is null ? line : Substitute(line, variables)).Append('\n');
        }

        Flush();
        return new SqlScript(name, text, batches);

        void Flush()
        {
            var batch = current.ToString();
            current.Clear();
            if (!string.IsNullOrWhiteSpace(StripComments(batch)))
            {
                batches.Add(batch);
            }
        }
    }

    private static string Substitute(string line, IReadOnlyDictionary<string, string> variables) =>
        Variable().Replace(line, m => variables.TryGetValue(m.Groups[1].Value, out var v)
            ? v
            : throw new InvalidOperationException($"Variable sqlcmd sin valor: {m.Groups[1].Value}."));

    private static string StripComments(string batch) =>
        string.Join('\n', batch.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}
