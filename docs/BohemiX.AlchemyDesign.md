# BohemiX 炼金系统设计文档 (KCD2 Alchemy Engine)

> 对应代码：`BohemiX.Core.Models.Alchemy` / `BohemiX.Core.Services.Alchemy` /
> `BohemiX.Infrastructure.Services.Alchemy` / `BohemiX.Infrastructure.Alchemy.Seed`
> 状态：引擎层已实现并通过 61 项单元测试（2026-06-14）。

---

## 1. 设计目标与红线

本系统严格复刻《天国：拯救2》(KCD2) 的**写实派中世纪炼金交互**，作为 BohemiX
桌面客户端 "Lab / Alchemy" 模块的逻辑核心。

### 绝对红线（已在代码中落地）
- **不存在任何独立工具实体**。代码中无 `Hands` / `WoodenSpoon` / `FilterCloth` /
  `Knife` 类、字段或枚举。所有操作均由"物理容器 + 动词动作"组合表达。
- **温控是物理位移，不是数值开关**。大锅通过 `CauldronPosition { Raised, Lowered }`
  位移状态接触/离开火源；风箱仅在 `Lowered` 时加速至沸腾。
- **配方 = 有序动作序列**。配方数据只描述"动词 + 目标实体 + 参数"，由状态机顺序消费。

---

## 2. 物理实体映射 (Entities)

对应 spec 第一节的 9 类物理实体，本引擎建模如下（**无伪工具**）：

| 物理实体 | 代码载体 | 说明 |
|---|---|---|
| 基底区 (Water/Wine/Oil/Spirits) | `AlchemyBase` 枚举 | 倾倒入锅的溶剂，每剂唯一。 |
| 大锅 Cauldron | `BenchState`（位置 + 液相 + 内容） | 可位移反应容器，温控核心。 |
| 风箱 Bellows | `PullBellows` 动词 | 火力放大器，仅 `Lowered` 生效。 |
| 研钵与杵 Mortar & Pestle | `Grind` 动词 + `Plate` 暂存 | 研磨产出粉末落入盘子。 |
| 盘子 Plate | `BenchState.Plate` | 粉末/预处理材料的暂存区。 |
| 沙漏 Hourglass | `TurnHourglass` 动词 + `BoilTurnsElapsed` | 推进"煮一轮"计时。 |
| 蒸馏器 Alembic | `DistillationTransferred` 标志 | 蒸馏路径收尾容器。 |
| 药瓶 Phial | `Bottle` 动词 + `Potion` 产出 | 最终装瓶。 |
| 药材架 Herb Shelf | `Herb` + `RecipeIngredient` | 新鲜/干燥状态影响品质。 |

---

## 3. 动作指令集 (Action Verbs)

`AlchemyActionKind` 枚举（9 个动词，**全部是动词，无一工具**）：

| 动词 | 关键参数 | 物理约束（在 `AlchemyBench.Apply` 中校验） |
|---|---|---|
| `LowerCauldron` | — | 锅已 `Lowered` 则拒绝。 |
| `RaiseCauldron` | — | 锅已 `Raised` 则拒绝；沸腾液冷却为 Heated。 |
| `PullBellows` | — | 锅须 `Lowered` 且非空；推动液相至 `Boiling`。 |
| `Grind` | HerbId, HerbState, Count | 自包含：研钵研磨→粉末入盘。 |
| `Crush` | HerbId, HerbState, Count | 手工捏碎多汁草药→入盘。 |
| `Stir` | Direction, StirCount | 锅须非空；记录方向与次数。 |
| `TurnHourglass` | Turns | 液相须为 `Boiling`；累加熬煮轮数。 |
| `Pour` | Base / HerbId / 无 | 多义动词：注基底 / 加草药 / 盘→锅 / 锅→蒸馏器。 |
| `Bottle` | — | 已蒸馏则从蒸馏器装瓶，否则从大锅装瓶。 |

> `Pour` 是唯一的多义动词：依据携带参数与当前状态自动判定流向
> （见 `AlchemyBench.ApplyPour` 的四级分支）。

---

## 4. 核心状态机流程

### 4.1 温控相位转换
```
Empty ──PourBase──► Cold ──LowerCauldron──► Heated ──PullBellows──► Boiling
                       ▲                                          │
                       └───────────RaiseCauldron (冷却)──────────┘
```
- `RaiseCauldron` 将沸腾液冷却回 `Heated`（暂停加热）。
- `TurnHourglass` 仅在 `Boiling` 时推进，模拟"起锅间隙研磨"高级技巧。

### 4.2 完整酿造编排（`AlchemyBenchService.Brew`）
1. 新建 `AlchemyBench`，逐条 `Apply` 玩家动作。
2. 物理非法动作 → 记录 `ActionValidationError`，**跳过不生效**，流程继续（宽容语义）。
3. 每步生成 `BrewLogEntry`（含 `BenchState` 深拷贝快照）。
4. `CanFinalize` 判定（有基底 + 有草药 + 已装瓶）。
5. 通过 `IPotionQualityEvaluator` 三因子评分。
6. 返回 `BrewResult`（Potion / QualityBreakdown / Log / Errors）。

### 4.3 收尾路径二选一
- **常规药剂**：直接 `Bottle` 大锅。
- **蒸馏药剂**：`Pour`（锅→蒸馏器）→ `Bottle`。
  （蒸馏前可选 `PullBellows` 加热，由配方 `RequiresDistillation` 标识。）

---

## 5. 三因子品质模型 (Quality Model)

`PotionQualityEvaluator` 是纯函数、确定性，可由动作日志重放复现。

### 5.1 公式
```
总分 = 0.5 × OrderScore + 0.3 × BoilTurnsScore + 0.2 × FreshnessScore   (各 ∈ [0,1])
```

| 因子 | 计算 |
|---|---|
| **OrderScore** | 顺序匹配的步骤数 / 配方总步骤数（`ActionsEquivalent` 逐位比对 Kind + 动词相关参数） |
| **BoilTurnsScore** | `max(0, 1 − |实际轮数 − 期望轮数| × 0.25)` |
| **FreshnessScore** | 新鲜度匹配的药材项数 / 配方药材项数 |

### 5.2 阈值（本轮常量，集中定义于 `PotionQualityEvaluator`）
| 总分 | 品质 |
|---|---|
| ≥ 0.95 | `HenryLevel`（亨利级，最高） |
| ≥ 0.80 | `Strong`（强效） |
| ≥ 0.60 | `Regular`（普通） |
| < 0.60 | `Weak`（弱效） |

> **扩展点**：后续可将阈值与因子权重提升为 `QualityPolicy` 配置对象，
> 支持"简易/硬核"难度切换，无需改动评估器核心。

---

## 6. 配方数据结构 (JSON Schema)

配方存于 `RootDirectory/Recipes/*.json`（单对象），药材存于
`RootDirectory/Herbs/*.json`（对象数组）。采用 camelCase + 字符串枚举。

### 6.1 配方示例（救世药剂 — 蒸馏路径）
```json
{
  "id": "saviour-schnapps",
  "displayName": "Saviour Schnapps",
  "base": "Wine",
  "ingredients": [
    { "herbId": "nettle", "count": 1, "requiredState": "Fresh", "requiredPreparation": "None" },
    { "herbId": "belladonna", "count": 2, "requiredState": "Fresh", "requiredPreparation": "Ground" }
  ],
  "steps": [
    { "kind": "Pour", "base": "Wine" },
    { "kind": "Pour", "herbId": "nettle", "herbState": "Fresh" },
    { "kind": "LowerCauldron" },
    { "kind": "PullBellows" },
    { "kind": "TurnHourglass", "turns": 1 },
    { "kind": "RaiseCauldron" },
    { "kind": "Grind", "herbId": "belladonna", "herbState": "Fresh", "count": 2 },
    { "kind": "Pour" },
    { "kind": "Pour" },
    { "kind": "Bottle" }
  ],
  "expectedBoilTurns": 1,
  "requiresDistillation": true
}
```

### 6.2 引用完整性
`RecipeRepository.LoadAsync` 加载后校验：每个 `ingredient.herbId` 必须能解析到
已加载的 `Herb`。悬空引用以 `CatalogIntegrityIssue(Warning)` 报告，配方仍加载（UI
可显示警告徽标）。无法解析的 JSON 文件以 `Error` 报告并跳过。

---

## 7. 架构与依赖注入

遵循 BohemiX 三层架构：

```
BohemiX.Core.Models.Alchemy        —— record 模型（纯数据，无依赖）
BohemiX.Core.Services.Alchemy      —— 服务契约（IRecipeRepository / IAlchemyBenchService / IPotionQualityEvaluator）
BohemiX.Infrastructure.Services.Alchemy —— 实现（注入 Serilog.ILogger）
```

`DependencyInjection.AddBohemiXInfrastructure` 注册 4 项 Singleton：
`AlchemyDataOptions`、`IRecipeRepository→RecipeRepository`、
`IPotionQualityEvaluator→PotionQualityEvaluator`、
`IAlchemyBenchService→AlchemyBenchService`。

**零新依赖**：`System.Text.Json` 是 net8.0 BCL 内置；Serilog 已在 Infrastructure 引用。

---

## 8. 测试覆盖（61 项，全部通过）

| 测试文件 | 覆盖点 |
|---|---|
| `AlchemyBenchServiceTests.cs` | 风箱仅 `Lowered` 生效；起锅冷却；沙漏仅沸腾推进；蒸馏 vs 装瓶分流；缺瓶→null 药剂；过多熬煮轮数降级；错误新鲜度降级；两份 spec 示例端到端 HenryLevel |
| `PotionQualityEvaluatorTests.cs` | 完美=HenryLevel；熬煮轮数偏差；搅拌方向错误；干燥替代新鲜；灾难性操作=Weak；阈值边界 |
| `RecipeRepositoryTests.cs` | 缺根目录宽容/严格；正常加载；悬空药材引用警告；损坏 JSON 报错跳过 |

---

## 9. 不在本轮范围（后续迭代）

- [ ] **UI 接入**：将引擎接入 `MainWindowViewModel` 的 Lab/Alchemy 占位面板
      （`IsLabAlchemyActive` / `LabAlchemyFeatures` 已就位），搭建最小可酿造交互视图。
- [ ] **种子部署**：启动时将 `Infrastructure/Alchemy/Seed/` 复制到
      `%LocalAppData%/BohemiX/alchemy/`（`AlchemyDataOptions.RootDirectory` 默认值）。
- [ ] **品质策略化**：把阈值/权重提升为 `QualityPolicy`，支持难度切换。
- [ ] **配方编辑器**：UI 内可视化编排动作序列并导出 JSON。
- [ ] **炼金日志持久化**：将 `BrewResult` 存 SQLite，支持历史回放与统计。
