# 测试与调试工作流

本文档描述如何排查和修复模拟引擎的准确性问题。

---

## 概览

```
游戏内导出 → 离线回放 → 自动对比 → 定位问题 → 修复 → 重跑验证
```

整条链路无需反复进入游戏。只有在离线结果达标后，才回游戏验证。

---

## 第一步：游戏内导出调试包

1. 进入野怪选择界面（Hour=2 Choice）。
2. 打开调试 UI。
3. 选择目标野怪，点击 **"导出当前选中调试包"**。
4. **先和选中的野怪打一场真实战斗**（这样导出目录里的 `CombatSimEvents.jsonl` 才有对应战斗记录）。

导出目录：

```
<游戏目录>/BepInEx/DebugExports/<时间戳>_<EncounterName>/
```

导出文件：

| 文件 | 用途 |
|------|------|
| `player_snapshot.json` | 玩家快照（手牌、属性） |
| `selected_encounter.json` | 野怪快照（对手阵容、效果） |
| `summary.txt` | 在线模拟摘要 |
| `selected_trace.txt` | 在线模拟详细时间轴 |
| `CombatSimEvents.jsonl` | 最近 5 场真实战斗（结构化 JSONL） |
| `CombatSimEvents.log` | 最近 5 场真实战斗（可读文本） |
| `GameSimEvents.tail.log` | GameSim 日志尾部 |

> **新特性**：`CombatSimEvents.jsonl` 中每场战斗现在包含 `encounter_id` 和 `encounter_name` 字段，标识该场战斗对应的野怪遭遇。

---

## 第二步：运行离线回放

离线 runner 从导出目录加载快照，用当前代码重新归一化效果并运行模拟。

```powershell
dotnet run --project BazaarEventLogger.Tests -- offline "<导出目录>" [runs] [seedBase] [traceSamples]
```

示例：

```powershell
dotnet run --project BazaarEventLogger.Tests -- offline "E:\SteamLibrary\...\20260313_151048_Covetous Thief (Silver)" 10 1337 1
```

| 参数 | 含义 | 默认值 |
|------|------|--------|
| `offline` | 固定子命令 | — |
| `export_dir` | 调试导出目录 | — |
| `runs` | 模拟次数 | `10` |
| `seedBase` | 起始随机种子 | `1337` |
| `traceSamples` | 保留详细时间轴的样本数 | `1` |

输出文件：

| 文件 | 内容 |
|------|------|
| `offline_summary.txt` | 离线模拟摘要 |
| `offline_result.json` | 结构化结果 |
| `offline_trace.txt` | 一份详细时间轴（字符串格式） |
| `offline_trace.jsonl` | 一份详细时间轴（结构化 JSON，供 diff 工具使用） |

---

## 第三步：运行自动对比工具

`combat_diff.py` 自动对比真实战斗和模拟战斗，生成差异报告。

```powershell
python BazaarEventLogger/tools/combat_diff.py "<导出目录>"
```

工具会自动在导出目录中查找 `CombatSimEvents.jsonl`（真实）和 `offline_trace.jsonl`（模拟）。

### 自动匹配机制

当 `CombatSimEvents.jsonl` 包含多场战斗时，工具自动选择正确的一场：

1. **encounter_name 匹配**（优先）：如果真实战斗 JSONL 包含 `encounter_name` 字段，直接与模拟的目标野怪名匹配。
2. **卡牌指纹匹配**（回退）：从 `selected_encounter.json` 读取对手卡牌名称，在真实战斗的 `card_stats` 中查找重叠最大的一场。

也可以手动指定：

```powershell
# 使用第 3 场真实战斗
python BazaarEventLogger/tools/combat_diff.py "<导出目录>" --real-index 2
```

### 报告内容

报告包含以下部分：

- **Winner**：胜负是否一致
- **Damage Summary**：总伤害对比
- **Per-Card Trigger Counts**：每张卡的触发次数对比（找到缺失或多余的卡）
- **HP Divergence**：按触发序列对齐的血量发散（定位首次偏差的时刻）
- **Trigger Sequence Diff**：触发顺序逐项对比
- **Actionable Summary**：可操作建议（哪些卡需要检查 EffectNormalizer，哪些 cooldown/haste 有问题）

---

## 卡牌检查工具（inspect）

`inspect` 子命令用于快速查看卡牌的原始模板数据和归一化结果，无需手动翻阅 `cards.json`。

```powershell
dotnet run --project BazaarEventLogger.Tests -- inspect <cardsJsonDirOrPath> "<卡牌名>" [Tier]
```

示例：

```powershell
# 按名称模糊搜索，默认使用 StartingTier
dotnet run --project BazaarEventLogger.Tests -- inspect `
  "E:\SteamLibrary\steamapps\common\The Bazaar" "Silver Stake"

# 指定 Tier
dotnet run --project BazaarEventLogger.Tests -- inspect `
  "E:\SteamLibrary\steamapps\common\The Bazaar" "Silver Stake" Bronze
```

| 参数 | 含义 | 默认值 |
|------|------|--------|
| `inspect` | 固定子命令 | — |
| `cardsJsonDirOrPath` | 游戏根目录或 cards.json 路径 | — |
| `CardName` | 卡牌名称（模糊匹配，大小写不敏感） | — |
| `Tier` | 要查看的等级 | 卡牌的 `StartingTier` |

### 输出内容

| 区块 | 说明 |
|------|------|
| **Tier Attributes** | 指定等级下合并后的全部属性值（CooldownMax, DamageAmount, Custom_0 等） |
| **Raw Abilities** | 原始模板中的 Abilities，标注 `[*]` 表示当前 Tier 激活。显示 Action 类型、Operation、Value 来源、Target |
| **Raw Auras** | 原始模板中的 Auras，同上格式 |
| **Normalized Effects** | 归一化后的效果列表。带 `WARNING:` 前缀的表示使用了不支持的操作（如 Multiply 被当作 Add 处理） |
| **Unsupported/Warnings** | 汇总所有未支持的效果和操作警告 |
| **Coverage Notes** | 覆盖率分析器给出的注意事项 |

### 典型用途

- **排查模拟偏差**：当 diff 报告显示某张卡伤害/效果异常时，用 inspect 对比原始模板和归一化结果，快速定位是归一化丢失了什么（如 Multiply 被当 Add）。
- **检查新卡支持度**：查看新卡的 Abilities/Auras 是否被完整归一化，Coverage Notes 中是否有未支持的 trigger/effect。
- **理解卡牌机制**：直接看 Raw Abilities/Auras 中的 Operation、Value 引用关系，比翻 JSON 方便。

### 操作警告（OperationWarning）

归一化器在处理卡牌/玩家属性修改时，如果遇到非 Add/Subtract 的 Operation（如 `Multiply`），会在对应效果上标记警告：

```
WARNING:op:Multiply(DamageAmount)_treated_as_Add
```

这意味着该效果的值被错误地当作加法处理。这些警告会同时出现在：
- inspect 输出的 Normalized Effects 和 Unsupported/Warnings 区块
- 离线回放的覆盖率报告中

---

## 第四步：定位和修复问题

根据 diff 报告中的提示，常见问题和对应修复位置：

| 报告提示 | 可能原因 | 修复位置 |
|----------|----------|----------|
| 卡牌在真实中触发但模拟中缺失 | 效果未被归一化 | `EffectNormalizer.cs` |
| 卡牌在模拟中触发但真实中未出现 | 效果归一化错误 / CD 计算错误 | `EffectNormalizer.cs` / `SimulationEngine.cs` |
| 触发次数不同 | cooldown/haste/slow/freeze 逻辑 | `SimulationEngine.cs` |
| HP 大幅发散 | 效果值错误 / 目标选择错误 | `SimulationEngine.cs` → `ResolveCardTargets` / `AdjustEffectValue` |
| 胜负不一致 | 以上任意组合 | — |

---

## 第五步：重跑验证

修改代码后，重复第二、三步：

```powershell
# 重新编译并跑离线回放
dotnet run --project BazaarEventLogger.Tests -- offline "<导出目录>" 10 1337 1

# 重新对比
python BazaarEventLogger/tools/combat_diff.py "<导出目录>"
```

对比前后两次报告，确认问题是否解决、是否引入新问题。

---

## 完整示例

```powershell
# 1. 离线回放（导出目录已有）
dotnet run --project BazaarEventLogger.Tests -- offline `
  "E:\SteamLibrary\steamapps\common\The Bazaar\BepInEx\DebugExports\20260313_151048_Covetous Thief (Silver)" `
  10 1337 1

# 2. 自动对比
python BazaarEventLogger/tools/combat_diff.py `
  "E:\SteamLibrary\steamapps\common\The Bazaar\BepInEx\DebugExports\20260313_151048_Covetous Thief (Silver)"

# 3. 看到报告后修代码，然后回到步骤 1 重跑
```

---

## 回归测试

保留所有"已知应为某结果"的导出目录作为回归样本。例如：

- 某场你确定应为 100% 胜率 → 保留导出目录
- 修改 Rage/Enrage/Crit/Flying 规则后 → 对所有回归样本重跑，确认没有退步

可以用脚本批量跑：

```powershell
$dirs = Get-ChildItem "E:\...\DebugExports" -Directory
foreach ($d in $dirs) {
    dotnet run --project BazaarEventLogger.Tests -- offline $d.FullName 10 1337 1
    python BazaarEventLogger/tools/combat_diff.py $d.FullName
}
```
