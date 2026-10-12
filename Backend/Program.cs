using System.Globalization;
using DuckDB.NET.Data;

const string Parquet = @"F:\work\duckdb\data\flame.parquet";

string[] paramNames = { "airFlowSLM", "o2FlowSLM", "c2h4FlowSCCM", "outerVelocityCmS", "innerVelocityCmS", "o2VolumeFraction", "o2MassFraction", "n2MassFraction" };

var paramUnits = new Dictionary<string, string>
{
    ["airFlowSLM"] = "SLM", ["o2FlowSLM"] = "SLM", ["c2h4FlowSCCM"] = "SCCM",
    ["outerVelocityCmS"] = "cm/s", ["innerVelocityCmS"] = "cm/s",
    ["o2VolumeFraction"] = "", ["o2MassFraction"] = "", ["n2MassFraction"] = ""
};

var varUnits = new Dictionary<string, string>
{
    ["Temperature"] = "K", ["Density"] = "kg/m\u00B3", ["U"] = "m/s", ["V"] = "m/s"
};

// ---- 启动时从 Parquet schema 读取变量清单与网格尺寸（缓存） ----
var fieldNames = new List<string>();
int NX = 0, NY = 0;
{
    using var conn = Open();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{Parquet}')";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(0);
            if (name is "i" or "j" or "x" or "y") continue;
            if (paramNames.Contains(name)) continue;
            fieldNames.Add(name);
        }
    }
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT max(i) + 1, max(j) + 1 FROM read_parquet('{Parquet}')";
        using var r = cmd.ExecuteReader();
        if (r.Read()) { NX = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture); NY = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture); }
    }
}
Console.WriteLine($"Backend ready: {fieldNames.Count} fields, grid {NX}x{NY}");

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();
var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// ---- GET /api/schema ----
app.MapGet("/api/schema", () => Results.Json(new
{
    nx = NX, ny = NY,
    parameters = paramNames.Select(p => new { name = p, unit = paramUnits.GetValueOrDefault(p, "") }).ToArray(),
    variables = fieldNames.Select(v => new { name = v, unit = varUnits.GetValueOrDefault(v, "") }).ToArray()
}));

// ---- GET /api/params/values ----
app.MapGet("/api/params/values", () =>
{
    var combos = new List<Dictionary<string, double>>();
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT DISTINCT {string.Join(",", paramNames.Select(Quote))} FROM read_parquet('{Parquet}') ORDER BY 1";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var d = new Dictionary<string, double>();
        for (int k = 0; k < paramNames.Length; k++)
            d[paramNames[k]] = Convert.ToDouble(r.GetValue(k), CultureInfo.InvariantCulture);
        combos.Add(d);
    }
    return Results.Json(new { parameters = paramNames, combinations = combos });
});

// ---- GET /api/field?variable=...&airFlowSLM=...&...&n2MassFraction=... ----
app.MapGet("/api/field", (HttpRequest req) =>
{
    var variable = req.Query["variable"].ToString();
    if (string.IsNullOrEmpty(variable)) return Results.BadRequest(new { error = "variable is required" });
    if (!fieldNames.Contains(variable)) return Results.BadRequest(new { error = $"unknown variable '{variable}'" });

    var where = new List<string>();
    var paramVals = new Dictionary<string, double>();
    foreach (var p in paramNames)
    {
        var qv = req.Query[p].ToString();
        if (string.IsNullOrEmpty(qv)) return Results.BadRequest(new { error = $"missing parameter '{p}'" });
        if (!double.TryParse(qv, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return Results.BadRequest(new { error = $"invalid parameter '{p}'" });
        paramVals[p] = d;
        where.Add($"{Quote(p)} = {d.ToString("R", CultureInfo.InvariantCulture)}");
    }
    var whereSql = string.Join(" AND ", where);

    using var conn = Open();

    var x = new List<double>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT DISTINCT i, x FROM read_parquet('{Parquet}') ORDER BY i";
        using var r = cmd.ExecuteReader();
        while (r.Read()) x.Add(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
    }

    var y = new List<double>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT DISTINCT j, y FROM read_parquet('{Parquet}') ORDER BY j";
        using var r = cmd.ExecuteReader();
        while (r.Read()) y.Add(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
    }

    var z = new List<double>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT {Quote(variable)} FROM read_parquet('{Parquet}') WHERE {whereSql} ORDER BY j, i";
        using var r = cmd.ExecuteReader();
        while (r.Read()) z.Add(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
    }

    double mn = 0, mx = 0;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT min({Quote(variable)}), max({Quote(variable)}) FROM read_parquet('{Parquet}') WHERE {whereSql}";
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            mn = Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture);
            mx = Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture);
        }
    }

    return Results.Json(new
    {
        variable, unit = varUnits.GetValueOrDefault(variable, ""),
        nx = NX, ny = NY,
        x, y,
        parameters = paramVals,
        z, min = mn, max = mx
    });
});

app.Run();

static DuckDBConnection Open()
{
    var c = new DuckDBConnection("Data Source=:memory:");
    c.Open();
    return c;
}

static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
