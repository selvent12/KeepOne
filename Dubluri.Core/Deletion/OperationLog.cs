using System.Globalization;
using System.Text;
using Dubluri.Core.Scanning;

namespace Dubluri.Core.Deletion;

/// <summary>Jurnal CSV cu fiecare operatie: data, cale, hash, actiune, rezultat.</summary>
public sealed class OperationLog : IDisposable
{
    private readonly StreamWriter _writer;

    public OperationLog(string file)
    {
        FilePath = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        bool isNew = !File.Exists(file);
        _writer = new StreamWriter(file, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        if (isNew) _writer.WriteLine("Data;Cale;Hash;Acțiune;Rezultat");
    }

    public string FilePath { get; }

    public void Write(string path, string hash, string action, string result) =>
        _writer.WriteLine(string.Join(';', Csv.Field(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                                      Csv.Field(path), Csv.Field(hash), Csv.Field(action), Csv.Field(result)));

    public void Dispose() => _writer.Dispose();
}

internal static class Csv
{
    public static string Field(string value) =>
        value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

public static class ReportExporter
{
    /// <summary>Exporta raportul de duplicate in CSV (separator ; si UTF-8 cu BOM, pentru Excel).</summary>
    public static void Write(IReadOnlyList<DuplicateGroup> groups, string file)
    {
        using var writer = new StreamWriter(file, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("Grup;Hash SHA-256;Mărime (octeți);Cale;Data modificării;Locație;Protejat;Selectat");
        for (int i = 0; i < groups.Count; i++)
        {
            foreach (var f in groups[i].Files)
            {
                writer.WriteLine(string.Join(';',
                    (i + 1).ToString(CultureInfo.InvariantCulture), Csv.Field(groups[i].Hash),
                    f.Size.ToString(CultureInfo.InvariantCulture), Csv.Field(f.Path),
                    f.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Csv.Field(f.Location), f.IsProtected ? "da" : "nu", f.IsSelected ? "da" : "nu"));
            }
        }
    }
}
