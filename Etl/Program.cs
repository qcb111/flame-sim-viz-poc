using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DuckDB.NET.Data;

const string DataDir = @"F:\work\duckdb\flame_sim_results";
const string OutParquet = @"F:\work\duckdb\data\flame.parquet";

string[] paramNames = { "airFlowSLM", "o2FlowSLM", "c2h4FlowSCCM", "outerVelocityCmS", "innerVelocityCmS", "o2VolumeFraction", "o2MassFraction", "n2MassFraction" };

// ---------- 1. 读取工况设定.xlsx ----------
var caseParams = ReadCaseParams(Path.Combine(DataDir, "工况设定.xlsx"));
Console.WriteLine($"xlsx: {caseParams.Count} cases");
foreach (var kv in caseParams.OrderBy(x => x.Key))
    Console.WriteLine($"  case {kv.Key}: {string.Join(", ", kv.Value.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)))}");

// ---------- 2. 读取 case 1 头：变量清单 + 网格 ----------
var (variables, I, J, K) = ReadHeader(Path.Combine(DataDir, "case 1.dat"));
Console.WriteLine($"header: {variables.Count} variables, grid {I} x {J} x {K}");
var fields = variables.Where(v => v != "X" && v != "Y").ToList();
Console.WriteLine($"fields: {fields.Count} (excl X/Y)");

// ---------- 3. 建表 + Appender 追加 ----------
using var conn = new DuckDBConnection("Data Source=:memory:");
conn.Open();

var createSql = new StringBuilder("CREATE TABLE flame (i INTEGER, j INTEGER, x FLOAT, y FLOAT");
foreach (var p in paramNames) createSql.Append($", {QuoteIdent(p)} DOUBLE");
foreach (var f in fields) createSql.Append($", {QuoteIdent(f)} FLOAT");
createSql.Append(')');
using (var cmd = conn.CreateCommand()) { cmd.CommandText = createSql.ToString(); cmd.ExecuteNonQuery(); }
Console.WriteLine("table flame created");

long total = 0;
using (var appender = conn.CreateAppender("flame"))
{
    for (int caseNo = 1; caseNo <= 7; caseNo++)
    {
        var pars = caseParams[caseNo];
        int p = 0;
        foreach (var row in ReadDataRows(Path.Combine(DataDir, $"case {caseNo}.dat")))
        {
            int i = p % I, j = p / I;
            var r = appender.CreateRow();
            r.AppendValue(i).AppendValue(j).AppendValue(row[0]).AppendValue(row[1]);
            for (int k = 0; k < paramNames.Length; k++) r.AppendValue(pars[k]);
            for (int f = 2; f < row.Length; f++) r.AppendValue(row[f]);
            r.EndRow();
            p++;
        }
        total += p;
        Console.WriteLine($"  case {caseNo}: {p} rows");
    }
    appender.Close();
}
Console.WriteLine($"total rows: {total}");

// ---------- 4. COPY 到 Parquet ----------
Directory.CreateDirectory(Path.GetDirectoryName(OutParquet)!);
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = $"COPY flame TO '{OutParquet.Replace('\\', '/')}' (FORMAT PARQUET, COMPRESSION ZSTD)";
    cmd.ExecuteNonQuery();
}
Console.WriteLine($"wrote {OutParquet}");
Console.WriteLine("ETL DONE");

// ================= helpers =================

static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

static (List<string> vars, int I, int J, int K) ReadHeader(string path)
{
    var vars = new List<string>();
    int I = 0, J = 0, K = 0;
    bool inVars = false;
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Trim();
        if (line.StartsWith("VARIABLES", StringComparison.OrdinalIgnoreCase))
        {
            inVars = true;
            var m = Regex.Match(line, "\"([^\"]*)\"");
            if (m.Success) vars.Add(m.Groups[1].Value);
            continue;
        }
        if (inVars && line.StartsWith('"'))
        {
            var m = Regex.Match(line, "\"([^\"]*)\"");
            if (m.Success) vars.Add(m.Groups[1].Value);
            continue;
        }
        if (inVars && line.StartsWith("ZONE", StringComparison.OrdinalIgnoreCase))
            inVars = false;
        var mi = Regex.Match(line, @"\bI\s*=\s*(\d+)");
        var mj = Regex.Match(line, @"\bJ\s*=\s*(\d+)");
        var mk = Regex.Match(line, @"\bK\s*=\s*(\d+)");
        if (mi.Success) I = int.Parse(mi.Groups[1].Value);
        if (mj.Success) J = int.Parse(mj.Groups[1].Value);
        if (mk.Success) K = int.Parse(mk.Groups[1].Value);
        if (line.StartsWith("DT=")) break;
    }
    return (vars, I, J, K);
}

static IEnumerable<float[]> ReadDataRows(string path)
{
    bool dataStart = false;
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Trim();
        if (!dataStart)
        {
            if (line.StartsWith("DT=")) dataStart = true;
            continue;
        }
        if (line.Length == 0) continue;
        var toks = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var vals = new float[toks.Length];
        for (int k = 0; k < toks.Length; k++)
            vals[k] = float.Parse(toks[k], NumberStyles.Float, CultureInfo.InvariantCulture);
        yield return vals;
    }
}

static Dictionary<int, double[]> ReadCaseParams(string path)
{
    var result = new Dictionary<int, double[]>();
    using var zip = ZipFile.OpenRead(path);
    var entry = zip.Entries.First(e =>
        e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml"));
    using var stream = entry.Open();
    var doc = XDocument.Load(stream);
    XNamespace ns = doc.Root!.Name.Namespace;
    foreach (var row in doc.Root.Elements(ns + "sheetData").Elements(ns + "row").Skip(1))
    {
        int caseNo = 0;
        var vals = new List<double>();
        foreach (var c in row.Elements(ns + "c"))
        {
            int col = ColIndex((string)c.Attribute("r")!);
            var v = c.Element(ns + "v")?.Value;
            if (v is null) continue;
            if (col == 1) caseNo = (int)double.Parse(v, CultureInfo.InvariantCulture);
            else vals.Add(double.Parse(v, CultureInfo.InvariantCulture));
        }
        if (caseNo > 0 && vals.Count == 8)
            result[caseNo] = vals.Select(x => Math.Round(x, 3, MidpointRounding.AwayFromZero)).ToArray();
    }
    return result;
}

static int ColIndex(string cellRef)
{
    int idx = 0;
    foreach (var ch in cellRef)
    {
        if (char.IsLetter(ch)) idx = idx * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        else break;
    }
    return idx;
}
