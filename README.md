# 火焰燃烧模拟结果可视化 PoC

前后端分离的 2D 等值线可视化服务，用于验证技术路线可行性：

> **Tecplot `.dat` →（C# ETL）→ Parquet →（服务端 DuckDB 查询）→ .NET Web API → Vite + Plotly 前端**

- 前端只加载用户当前选中的**一个物理量**（如 Temperature / OH）的数据，提供两个视图：**2D 等值线** 与 **1D 剖面**（field vs. x @ 指定 y）。
- 7 个 case 在 ETL 中**合并为一个 `flame.parquet`**，8 个工况参数作为**列**；前端通过 `SELECT DISTINCT` 从 Parquet 现查参数取值，**级联选择定位唯一 case**（支持敏感性分析切片）。

完整架构设计见 [architecture-design.md](architecture-design.md)。

---

## 目录结构

```
Etl/            C# 控制台：解析 7 个 .dat + 工况设定.xlsx → 写 data/flame.parquet
Backend/        ASP.NET Core Web API（.NET 11，DuckDB.NET.Data.Full）
frontend/       Vite + 原生 JS + Plotly.js（2D 等值线 + 1D 剖面 + 级联选择）
data/           flame.parquet（ETL 产物，约 33 MB）
screenshots/    端到端渲染截图
flame_sim_results/   原始 Tecplot .dat + 工况设定.xlsx
```

## 数据概况

- 网格：333 × 88 = 29,304 点（2D rectilinear 非均匀网格），7 个 case。
- 每点 56 个变量（X/Y 坐标 + 54 个物理量），Parquet 中另加 8 个工况参数列。
- 总行数 205,128，列式存储，查询单字段时 DuckDB 只读对应列。

---

## 运行步骤

### 0. 前置

- .NET 11 SDK（本机已装 11.0-preview）
- Node.js + pnpm（前端）

### 1. ETL（生成 Parquet，一次性）

```powershell
dotnet run --project Etl -c Release
```

产出 `data/flame.parquet`。

### 2. 后端

```powershell
dotnet run --project Backend -c Release --urls http://localhost:5080
```

### 3. 前端

```powershell
cd frontend
pnpm install   # 首次
pnpm dev       # 启动 Vite，监听 http://localhost:5173，并将 /api 代理到 5080
```

浏览器打开 <http://localhost:5173/> 即可看到等值线图。

---

## API 端点

| 端点 | 说明 |
| --- | --- |
| `GET /api/schema` | 返回网格尺寸、`x[333]`/`y[88]` 坐标、8 个工况参数、54 个物理量（来自 `DESCRIBE`） |
| `GET /api/params/values` | 返回 7 个工况参数组合（`SELECT DISTINCT`，前端级联选择用） |
| `GET /api/field?variable=Temperature&airFlowSLM=284.1&o2FlowSLM=0&...&n2MassFraction=0.767` | 返回唯一 case 的 `x[333]`、`y[88]`、`z[29304]`、`min/max`（2D 等值线） |
| `GET /api/field/profile?variable=Temperature&...&y=1.5` | 返回唯一 case 在指定 y 的 1D 切片 `x[333]`、`v[333]`、`y`/`j`、`min/max`（就近取网格行） |

示例（case 1 温度场）：

```
GET http://localhost:5080/api/field?variable=Temperature&airFlowSLM=284.1&o2FlowSLM=0&c2h4FlowSCCM=194&outerVelocityCmS=77.98&innerVelocityCmS=3.529&o2VolumeFraction=0.21&o2MassFraction=0.233&n2MassFraction=0.767
```

---

## 截图

见 [screenshots/](screenshots/)：

- `contour_Temperature_case1.png` — case 1 温度场
- `contour_Temperature_case7.png` — case 7 温度场（不同 O₂ 工况）
- `contour_OH_case5.png` — case 5 OH 浓度场

---

## 注意事项

- **.NET 11 preview**：`DuckDB.NET.Data.Full` 目标 net8/net10，可被 net11 引用（TFM 向后兼容）。若遇兼容问题，可回退装 .NET 10 LTS SDK。
- **Plotly 走 CDN**（`cdn.plot.ly`），运行时需联网；如需离线可改为本地打包 `plotly.js-dist-min`。
- **前端 pnpm**：`pnpm install` 时 esbuild 的构建脚本会被 pnpm 拦截（已在 `frontend/pnpm-workspace.yaml` 放行）；该拦截不影响 Vite 运行。
- **参数精度**：ETL 中工况参数已四舍五入到小数点后 3 位。
- **1D 切片**：`/api/field/profile` 单次 payload 约 11 KB，对比 2D 全字段约 434 KB（减少约 97%）。
