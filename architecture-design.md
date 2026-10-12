# 火焰燃烧模拟结果可视化服务 — 架构设计与可行性分析

> 目标：做一个前后端分离的服务，在前端以 2D 等值线图可视化 `flame_sim_results` 中的模拟结果，
> 验证「Parquet 存储 + 服务端 DuckDB 查询 + .NET Web API 后端 + 前端按需加载单个物理量」这条技术路线的可行性，
> 并验证该架构对**敏感性分析结果可视化**的支持。

---

## 1. 数据调研结论

### 1.1 数据格式（已实际解析确认）

`flame_sim_results` 下共 7 个 `.dat` 文件（`case 1.dat` ~ `case 7.dat`），每个约 **28 MB**，是 **Tecplot ASCII** 格式，结构如下：

| 属性 | 值 |
| --- | --- |
| 格式 | Tecplot ASCII，单 `ZONE`，`ZONETYPE=Ordered`，`DATAPACKING=POINT` |
| 网格 | `I=333`（X 向）× `J=88`（Y 向）× `K=1`（二维）= **29,304 个网格点** |
| 网格类型 | **rectilinear（直角非均匀网格）**：X 只随 i 变化、Y 只随 j 变化（已实测验证） |
| 变量数 | **56 个**（`DT=(SINGLE ×56)`，每行一个点、空格分隔 56 个浮点数） |
| 坐标范围 | X ∈ [0, 15.353]，Y ∈ [0, 6]（单位应为 m） |
| 示例量级 | Temperature ∈ [300, 2035.5] K；OH ∈ [0, 0.003035]（质量分数） |

文件头示例（`case 1.dat`）：

```text
TITLE     = ""
VARIABLES = "X"
"Y"
"U"
...（共 56 个变量名，每个占一行）...
ZONE T="ZONE 001"
 STRANDID=0, SOLUTIONTIME=0
 I=333, J=88, K=1, ZONETYPE=Ordered
 DATAPACKING=POINT
 DT=(SINGLE SINGLE ... ×56)
 0.000000000E+000 0.000000000E+000 7.059999943E+000 ...（每行 56 列）
```

### 1.2 56 个变量清单

| 类别 | 变量 |
| --- | --- |
| 坐标 | `X`, `Y` |
| 速度 | `U`, `V` |
| 主要组分（质量分数） | `C2H4`, `O2`, `CO2`, `H2O`, `CO`, `N2` |
| 关键物理量 | `Temperature`, `Density` |
| 微量组分/自由基（30 个） | `OH`, `O`, `H`, `H2`, `C2H2`, `HO2`, `H2O2`, `C`, `CH`, `CH2`, `CH2(S)`, `CH3`, `CH4`, `HCO`, `CH2O`, `CH2OH`, `CH3O`, `CH3OH`, `C2H`, `C2H3`, `C2H5`, `C2H6`, `HCCO`, `CH2CO`, `HCCOH`, `AR`, `C3H7`, `C3H8`, `CH2CHO`, `CH3CHO` |
| 颗粒/烟灰 | `Soot`, `dp`, `NP` |
| 源项/反应/热 | `QR`, `R1`, `R2`, `R3`, `R_OH`, `R_O`, `HEATCON` |
| 其他 | `N`, `NA`, `Ys`, `YSUM` |

> 注：后两类变量的确切物理含义来自变量名推断，实现前如需精确单位/含义可与仿真数据方确认。

### 1.3 关键结论（对架构的影响）

1. 数据量很小：7 个 case × 29,304 点 × 56 字段 ≈ 20.5 万个点。即便全存 float32，约 **47 MB**，Parquet 压缩后更小。**性能不是瓶颈，按需加载主要是为了演示正确的架构模式，而非被迫优化。**
2. 网格是 **rectilinear** 且各变量共用同一套 X/Y 坐标 → 前端画等值线只需要「X 一维数组 + Y 一维数组 + 字段的 2D 矩阵」，非常适合 Plotly `contour`。
3. 7 个 case 结构完全一致（同网格、同 56 变量），差异只在数值与工况参数 → 便于合并建模。

### 1.4 工况设定（case 参数）

`flame_sim_results\工况设定.xlsx`（单表 `Sheet1`）定义了 7 个 case 的对应工况，`编号` 1~7 与 `case 1.dat`~`case 7.dat` 一一对应：

| 编号 | 空气流量 SLM | 氧气流量 SLM | 乙烯流量 SCCM | 外筒流速 cm/s | 内筒流速 cm/s | O2体积分数 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 284.1 | 0 | 194 | 77.98 | 3.529 | 0.210 |
| 2 | 240 | 0 | 194 | 65.88 | 3.529 | 0.210 |
| 3 | 240 | 0 | 150 | 65.88 | 2.729 | 0.210 |
| 4 | 240 | 0 | 132 | 65.88 | 2.402 | 0.210 |
| 5 | 75.9 | 44.1 | 194 | 32.94 | 3.529 | 0.500 |
| 6 | 91.1 | 28.9 | 194 | 32.94 | 3.529 | 0.400 |
| 7 | 106.3 | 13.7 | 194 | 32.94 | 3.529 | 0.300 |

（另有 `O2质量分数`、`N2质量分数` 两列。）

**关键设计决定**：这些工况参数**不放在独立的 meta 文件里**，而是在 ETL 阶段作为**列**写入 Parquet（§4）。前端通过 DuckDB 的 `SELECT DISTINCT` 直接从 Parquet 数据中取得各参数的可能取值并让用户选择——这是本架构支持敏感性分析可视化的核心机制（§7）。

---

## 2. 技术选型（调研结论）

| 环节 | 选型 | 理由 |
| --- | --- | --- |
| 存储 | **Apache Parquet**（单文件，工况参数作为列） | 列式存储，DuckDB 原生读写，天然支持「只读单个物理量那一列」与「按工况参数列过滤」 |
| 查询 | **DuckDB（嵌入式，跑在 .NET 服务进程内）** | 原生 `read_parquet` + 投影/谓词下推 + `SELECT DISTINCT`，零运维；满足「服务端 DuckDB」要求 |
| 后端 | **ASP.NET Core Web API** | 官方指定；用 `DuckDB.NET.Data.Full` 作为 ADO.NET 客户端 |
| 前端 | **Vite + 原生 JS + Plotly.js** | Plotly `contour` 一行配置出等值线图，含色标/hover/缩放；原生 JS 最省依赖 |

### 2.1 DuckDB 的 .NET 客户端（已验证）

- 包名：**`DuckDB.NET.Data.Full`**，当前稳定版 **1.5.6**（[NuGet](https://www.nuget.org/packages/DuckDB.NET.Data.Full)）。
- 目标框架：**net8.0** / net10.0，兼容 net9（依赖 Apache.Arrow ≥ 23.0）。
- 项目：<https://github.com/Giorgi/DuckDB.NET>，总下载量 440 万+，社区活跃，属事实标准。
- 用法为标准 ADO.NET：`DuckDBConnection` → `CreateCommand` → `ExecuteReader`，读 Parquet 只需 `SELECT ... FROM read_parquet('...')`。

### 2.2 前端等值线库

- **Plotly.js `contour`**：直接传入 `{ x: number[333], y: number[88], z: number[88][333] }` 即出图，内置色标、hover、缩放，代码量最小，适合「不要太复杂」。
- 轻量替代：**d3-contour + canvas**（约 10 KB 级），但需自己实现色标/hover/坐标轴，代码更多。

---

## 3. 总体架构

```
┌────────────────┐        ETL（一次性离线步骤：合并 7 个 case）
│  Tecplot .dat   │ ──────────────────────────────┐
│  ×7 case (~28MB)│                               ▼
├────────────────┤                        ┌─────────────────────┐
│ 工况设定.xlsx   │ ──(参数作为列)────────▶ │  Parquet 文件（单文件） │
└────────────────┘                        │  flame.parquet       │
                                          │  坐标 + 8 工况参数列   │
                                          │  + 56 物理量列        │
                                          └─────────┬───────────┘
                                                    │ read_parquet（列裁剪 / 谓词下推）
┌───────────────────────────────────────────────────▼──────────────────────┐
│  .NET Web API（ASP.NET Core）—— 服务端                                      │
│   ┌────────────────┐        DuckDB.NET (ADO.NET)                          │
│   │ REST Controllers │ ◀────▶ DuckDB 嵌入式引擎（进程内）                   │
│   │ · schema 描述    │         SELECT DISTINCT 取工况参数取值               │
│   │ · 按参数过滤查询  │         SELECT x,y,field WHERE 参数=值               │
│   └───────┬────────┘                                                      │
└───────────┼──────────────────────────────────────────────────────────────┘
            │ HTTP / JSON
┌───────────▼─────────────────────────────┐
│  前端（浏览器）Vite + Plotly.js          │
│  · 物理量下拉框（56 选 1）               │
│  · 工况参数选择（取值来自 Parquet）       │
│  · 只请求当前选中的物理量 + 工况切片      │
│  · contour 等值线图渲染（可多 case 对比） │
└──────────────────────────────────────────┘
```

数据流：**Tecplot .dat + 工况设定.xlsx →（ETL 一次性合并）→ 单一 Parquet（工况参数为列）→ DuckDB（列裁剪 + 谓词过滤）→ .NET API → JSON → Plotly contour**。

---

## 4. 数据层设计（ETL + Parquet Schema）

### 4.1 Parquet Schema（单文件，工况参数作为列）

**7 个 case 合并为一个 `flame.parquet`**，每行一个网格点，工况参数与物理量同为列：

```
i, j                 INT32     // X/Y 向网格序号 0..332 / 0..87
x, y                 FLOAT32   // 坐标（各 case 相同，冗余存储，RLE/字典压缩后极小）
airFlowSLM           DOUBLE    // 空气流量 SLM
o2FlowSLM            DOUBLE    // 氧气流量 SLM
c2h4FlowSCCM         DOUBLE    // 乙烯流量 SCCM
outerVelocityCmS     DOUBLE    // 外筒流速 cm/s
innerVelocityCmS     DOUBLE    // 内筒流速 cm/s
o2VolumeFraction     DOUBLE    // O2 体积分数
o2MassFraction       DOUBLE    // O2 质量分数
n2MassFraction       DOUBLE    // N2 质量分数
C2H4 ... NP          FLOAT32   // 其余 54 个物理量各一列（§1.2，不含 X/Y）
```

- 总行数 = 7 × 29,304 = **205,128 行**，列数 ≈ 66。每个 case 的 29,304 行携带该 case 的 8 个参数值（反规范化冗余，Parquet 字典/RLE 压缩后代价极低）。
- **没有 `case_id`**：case 之间的区别由「8 个工况参数列的取值组合」唯一确定。
- 选择宽表 + 参数列的理由：列式 Parquet 下——
  - 查 `Temperature` 只读 X/Y/Temperature 三列（**只加载一个物理量**）；
  - `SELECT DISTINCT innerVelocityCmS` 只扫该参数列，得到**工况参数的可能取值**（**敏感性分析的「因子/水平」**）；
  - `WHERE innerVelocityCmS = 3.529` 走谓词下推，只读匹配行（**按工况切片**）。
- **列名约定**：坐标（`i/j/x/y`）、参数（8 个）、物理量（其余）三类的边界由 ETL 维护的一份**列分类白名单**决定（schema 本身无法自动区分「参数」和「物理量」）。这是仅有的、必要的“配置”，不含任何 per-case 数据。

### 4.2 ETL 流程（.dat + xlsx → 单一 Parquet）

已确认实现语言：**C# 控制台**（与后端同栈，用 `DuckDB.NET` 或 Arrow 写 Parquet）。

1. 解析 Tecplot 头：读取 `VARIABLES` 清单、`I/J/K` 网格尺寸。
2. 逐行解析 29,304 个数据点（空格分隔 56 个 float）。
3. 读取 `工况设定.xlsx`，得到 `编号 → {8 个工况参数}` 映射（用 `DocumentFormat.OpenXml`/`ClosedXML` 读取）。
4. 对每个 case：把该 case 的 8 个参数值拼到该 case 的 29,304 行上，**追加到同一个 Parquet**。
5. **参数精度规整**：xlsx 中存在浮点误差（如 `284.09999999999997`、`0.500325`），入库前**四舍五入到小数点后 3 位**，保证 `SELECT DISTINCT` 与等值过滤得到干净、可比较的取值。

> 敏感性分析扩展：新增 case 只需跑一次 ETL 追加数据；前端无需改代码即可自动发现新的参数取值。

---

## 5. 后端 API 设计（.NET Web API）

统一返回 JSON。三个端点，均**直接从 Parquet 查询**，无独立 meta 存储。

### 5.1 `GET /api/schema`（结构描述，来自 Parquet schema）

由 DuckDB 的 `DESCRIBE SELECT * FROM read_parquet('flame.parquet')`（或 `parquet_schema()`）+ 列分类白名单得到，返回三类列的清单，**不含工况取值、不含字段数据**：

```jsonc
{
  "nx": 333, "ny": 88,
  "parameters": [
    { "name": "airFlowSLM", "unit": "SLM" },
    { "name": "innerVelocityCmS", "unit": "cm/s" },
    /* …共 8 个工况参数 */
  ],
  "variables": [
    { "name": "Temperature", "unit": "K", "group": "field" },
    { "name": "OH", "unit": "mass fraction", "group": "species" },
    /* …共 56 个物理量 */
  ]
}
```

### 5.2 `GET /api/params/values`（工况参数的可能取值，来自 Parquet 数据）

用一条 `SELECT DISTINCT` 直接取得**所有不同的参数组合**（即 7 个 case 的「工况表」，从数据中现查）：

```sql
SELECT DISTINCT airFlowSLM, o2FlowSLM, c2h4FlowSCCM,
                outerVelocityCmS, innerVelocityCmS,
                o2VolumeFraction, o2MassFraction, n2MassFraction
FROM read_parquet('flame.parquet')
ORDER BY airFlowSLM;
```

```jsonc
{
  "parameters": [ { "name": "airFlowSLM", "unit": "SLM" }, /* …8 个 */ ],
  "combinations": [
    { "airFlowSLM": 75.9, "o2FlowSLM": 44.1, "c2h4FlowSCCM": 194,
      "outerVelocityCmS": 32.94, "innerVelocityCmS": 3.529,
      "o2VolumeFraction": 0.5, "o2MassFraction": 0.534, "n2MassFraction": 0.466 }
    /* …共 7 个组合，每个 = 一个 case */
  ]
}
```

- 前端据此**在本地级联**：每个参数选择器的可选值 = 按已选参数过滤后，`combinations` 中该参数的剩余不同取值；逐级收窄，直到只剩 1 个组合 = 唯一 case。
- 这正是「前端从 Parquet 结果（而非 meta）得到工况参数取值」的落地点：取值是**数据**，随数据增删而自动变化。

### 5.3 `GET /api/field?variable=Temperature&airFlowSLM=284.1&o2FlowSLM=0&c2h4FlowSCCM=194&outerVelocityCmS=77.98&innerVelocityCmS=3.529&o2VolumeFraction=0.21&o2MassFraction=0.233&n2MassFraction=0.767`（核心，按需加载单个物理量 + 唯一 case）

- `variable`：必填，物理量列名（白名单校验防注入）。
- 其余 8 个查询参数：**完整的工况参数组合**（8 个全给），由前端级联选择确定，唯一标识一个 case。

后端逻辑（DuckDB 只扫对应列、只读匹配行）：

```sql
SELECT x, y, {variable} AS v
FROM read_parquet('flame.parquet')
WHERE airFlowSLM = ? AND o2FlowSLM = ? AND c2h4FlowSCCM = ?
  AND outerVelocityCmS = ? AND innerVelocityCmS = ?
  AND o2VolumeFraction = ? AND o2MassFraction = ? AND n2MassFraction = ?
ORDER BY j, i;

SELECT min({variable}), max({variable})
FROM read_parquet('flame.parquet')
WHERE <同上 8 个等值条件>;
```

- `x`/`y` 与 case 无关（各 case 相同），可用 `SELECT DISTINCT i,x ORDER BY i` / `SELECT DISTINCT j,y ORDER BY j` 取一次，或从结果里 `slice` 抽出。

响应（**单一 case、单一物理量**）：

```jsonc
{
  "variable": "Temperature",
  "unit": "K",
  "nx": 333, "ny": 88,
  "x": [0, 0.0125, /* …333 个 */],
  "y": [0, 0.00687, /* …88 个 */],
  "params": { "airFlowSLM": 284.1, "innerVelocityCmS": 3.529, /* …全 8 个 */ },
  "z": [ /* 29304 个值，行主序（j 外层，i 内层），前端 reshape 为 88×333 */ ],
  "min": 300.0, "max": 2035.5
}
```

- 变量名与参数名均走**白名单校验**（对照 `DESCRIBE` 列名），防 SQL 注入。

### 5.4 返回格式（JSON vs 二进制）

- **JSON（推荐 PoC）**：29304 个 float 的 JSON 约 0.3~0.5 MB/字段，本地/局域网无压力，实现最简。
- 二进制（Apache Arrow / MessagePack）：更省流量、解析更快，但前端要多一套解码。数据量小，MVP 不必上。

---

## 6. 前端设计

### 6.1 技术栈

- **Vite**（开发服务器 + 打包）+ 原生 JS（或 TS）+ **Plotly.js**。
- 交互元素：物理量下拉框 + 8 个工况参数级联选择器（取值来自 `/api/params/values`）+ 等值线图（单图）。

### 6.2 交互流程

```
页面加载
  → GET /api/schema          → 填充「物理量」下拉框（56 选 1，默认 Temperature）
  → GET /api/params/values   → 得到 7 个参数组合，填充 8 个「工况参数」级联选择器
   ↓
用户选择物理量；工况参数级联选择：每次选择后按已选值过滤组合，
  各参数的可选值随之收窄，直到只剩 1 个组合 = 唯一 case
   ↓
GET /api/field?variable=X&<8 个参数全给> → 得到 x/y/z
   ↓
z 一维 reshape 成 88×333 → Plotly.react(contour) 更新（单图）
```

### 6.3 等值线配置要点

- `Plotly.newPlot(el, [{ type:'contour', x, y, z, colorscale:'Turbo', zmin, zmax }])`。
- 色标：`Temperature` 用线性 `Turbo`/`Jet`；质量分数类（如 `OH`、`C2H4`）跨数量级，建议提供「对数色标」开关（前端对 z 取 log 或 Plotly `zticks`）。
- 网格非均匀：Plotly `contour` 直接吃非均匀的 `x[]`/`y[]`，无需重采样到均匀网格。
- 切换 case 对比同一物理量时，保持**统一的 zmin/zmax 色标**，便于敏感性比较（多图并排对比可作为后续扩展）。

---

## 7. 可行性结论与关键设计点

1. **技术路线可行，且各环节均为成熟组件**：Tecplot ASCII → Parquet 是简单文本解析；DuckDB 读 Parquet 原生支持；`DuckDB.NET.Data.Full` 成熟稳定；Plotly 等值线开箱即用。
2. **「只加载一个物理量」的实现机制**：Parquet 列式存储 + DuckDB 投影下推，后端只 `SELECT x, y, {variable}`，前端也只发起该字段的请求；结构描述走一个轻量 `/api/schema`。
3. **敏感性分析可视化支持**（本架构的关键验证点）：
   - **因子 = 工况参数列**，**水平 = `SELECT DISTINCT` 得到的参数组合/取值**（从数据中现查，非硬编码）；
   - **切片 = 级联选择定位唯一 case + `WHERE 参数=值`**（谓词下推，只读匹配行）；
   - **对比 = 切换唯一 case 查看同一物理量**（统一色标；多图并排可作为后续扩展）。
   - 新增 case/参数/取值只需重跑 ETL 追加数据，前端级联选择器**自动发现**新取值，后端查询逻辑不变。
4. **数据量级评估**：单字段载荷 ~0.3–0.5 MB（JSON），网格 333×88，实时交互无压力；未来数据量增大可平滑切换 Arrow 二进制 + 更细粒度切片。
5. **扩展性**：当前 K=1（纯 2D）；若后续有 3D（K>1）或多时刻，Schema 加 `k`/`t` 列 + API 加切片参数即可，架构不变。

---

## 8. 已确认决策与剩余待定项

### 8.1 已确认决策

| 议题 | 决策 |
| --- | --- |
| 交付范围 | 本次仅交付**架构设计文档**，评审后再决定是否实施 PoC |
| 数据建模 | 7 个**独立工况**在 ETL 中**合并为一个 Parquet**，工况参数作为**列**（不设 `case_id`、无独立 meta 文件） |
| 参数取值来源 | 前端通过 `SELECT DISTINCT` **从 Parquet 结果中**取得工况参数可能取值并让用户选择 |
| 参数精度 | 入库前**四舍五入到小数点后 3 位** |
| 工况选择交互 | **级联选择到唯一 case**（逐级收窄参数组合，直到唯一） |
| ETL 实现 | **C# 控制台**（与后端同栈，DuckDB.NET/Arrow 写 Parquet，`ClosedXML`/`OpenXml` 读 xlsx） |
| 前端栈 | **原生 JS + Vite + Plotly.js** |
| 后端框架 | 用已装的 **.NET 11 preview** 跑本地 PoC |

### 8.2 环境注意事项（实施前须知）

- 当前机器 `.NET` 仅装 **5.0** 与 **11.0-preview**，无 net8/net10 稳定 SDK。`DuckDB.NET.Data.Full`（目标 net8.0/net10.0）可被 net11.0 工程引用（TFM 向后兼容），但 preview SDK 存在小概率兼容问题；若遇阻，回退方案是装 **.NET 10 LTS SDK**。
- ETL 主路径为 C#，不依赖 Python。若因故必须用 Python，工作区已装 **uv**，用 `uv` 管理依赖（`uv add duckdb pyarrow`、`uv run ...`），不直接 `pip install`。

### 8.3 剩余待定项（不阻塞设计，实施时再定）

1. **返回格式**：默认 JSON（单字段 ~0.3–0.5 MB）；数据量小，MVP 不上二进制（Arrow）。
2. **色标**：`Temperature` 线性 `Turbo`；质量分数类提供「对数色标」开关。
3. **量纲/单位**：`QR/R1/R2/R3/R_OH/R_O/HEATCON/NP/dp/Ys/NA/N` 等变量的确切物理含义与单位，建议实施前与数据方确认一次，用于 `variables[].unit` 与前端图例。
