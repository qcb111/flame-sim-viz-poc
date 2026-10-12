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

// ---- 启动缓存：变量清单 + 网格坐标向量 x/y（几何恒定，各 case 共用） ----
var fieldNames = new List<string>();
var xs = new List<double>();
var ys = new List<double>();
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
        cmd.CommandText = $"SELECT DISTINCT i, x FROM read_parquet('{Parquet}') ORDER BY i";
        using var r = cmd.ExecuteReader();
        while (r.Read()) xs.Add(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
    }
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT DISTINCT j, y FROM read_parquet('{Parquet}') ORDER BY j";
        using var r = cmd.ExecuteReader();
        while (r.Read()) ys.Add(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
    }
    NX = xs.Count;
    NY = ys.Count;
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
    x = xs, y = ys,
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

// ---- GET /api/field (2D contour) ----
app.MapGet("/api/field", (HttpRequest req) =>
{
    var variable = req.Query["variable"].ToString();
    if (string.IsNullOrEmpty(variable)) return Results.BadRequest(new { error = "variable is required" });
    if (!fieldNames.Contains(variable)) return Results.BadRequest(new { error = $"unknown variable '{variable}'" });

    var (whereSql, paramVals, err) = BuildWhere(req);
    if (err is not null) return Results.BadRequest(new { error = err });

    using var conn = Open();

    var z = new List<double>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT {Quote(variable)} FROM read_parquet('{Parquet}') WHERE {whereSql} ORDER BY j, i";
        using var r = cmd.ExecuteReader();
        while (r.Read()) z.Add(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
    }

    var (mn, mx) = MinMax(conn, variable, whereSql);

    return Results.Json(new
    {
        variable, unit = varUnits.GetValueOrDefault(variable, ""),
        nx = NX, ny = NY,
        x = xs, y = ys,
        parameters = paramVals,
        z, min = mn, max = mx
    });
});

// ---- GET /api/field/profile (1D slice along x at a given y) ----
app.MapGet("/api/field/profile", (HttpRequest req) =>
{
    var variable = req.Query["variable"].ToString();
    if (string.IsNullOrEmpty(variable)) return Results.BadRequest(new { error = "variable is required" });
    if (!fieldNames.Contains(variable)) return Results.BadRequest(new { error = $"unknown variable '{variable}'" });

    var (whereSql, _, err) = BuildWhere(req);
    if (err is not null) return Results.BadRequest(new { error = err });

    var yStr = req.Query["y"].ToString();
    if (!double.TryParse(yStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var y0))
        return Results.BadRequest(new { error = "invalid y" });

    // nearest grid row (rectilinear grid: y depends only on j)
    int jIdx = 0;
    double best = double.MaxValue;
    for (int k = 0; k < ys.Count; k++)
    {
        var d = Math.Abs(ys[k] - y0);
        if (d < best) { best = d; jIdx = k; }
    }

    using var conn = Open();

    var v = new List<double>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"SELECT {Quote(variable)} FROM read_parquet('{Parquet}') WHERE {whereSql} AND j = {jIdx} ORDER BY i";
        using var r = cmd.ExecuteReader();
        while (r.Read()) v.Add(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
    }

    var (mn, mx) = MinMax(conn, variable, whereSql); // full-field range for a fixed profile y-axis

    return Results.Json(new
    {
        variable, unit = varUnits.GetValueOrDefault(variable, ""),
        y = ys[jIdx], j = jIdx,
        x = xs, v,
        min = mn, max = mx
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

(string whereSql, Dictionary<string, double> vals, string? error) BuildWhere(HttpRequest req)
{
    var where = new List<string>();
    var vals = new Dictionary<string, double>();
    foreach (var p in paramNames)
    {
        var qv = req.Query[p].ToString();
        if (string.IsNullOrEmpty(qv)) return ("", vals, $"missing parameter '{p}'");
        if (!double.TryParse(qv, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return ("", vals, $"invalid parameter '{p}'");
        vals[p] = d;
        where.Add($"{Quote(p)} = {d.ToString("R", CultureInfo.InvariantCulture)}");
    }
    return (string.Join(" AND ", where), vals, null);
}

(double min, double max) MinMax(DuckDBConnection conn, string variable, string whereSql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT min({Quote(variable)}), max({Quote(variable)}) FROM read_parquet('{Parquet}') WHERE {whereSql}";
    using var r = cmd.ExecuteReader();
    if (r.Read())
        return (Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
    return (0, 0);
}
