# Simulation TODO

这份文件记录当前野怪战斗模拟器仍未完整实现、或虽已实现但仍需实战校准的能力与机制。

规则基线见 [SIMULATION_RULES.md](C:\Users\mzs\Documents\python\BazaarOracle\SIMULATION_RULES.md)。

## Highest Priority

- 完整覆盖 `TActionCardModifyAttribute`
  当前已支持更多属性：
  `DamageAmount`、`BurnApplyAmount`、`PoisonApplyAmount`、`HealAmount`、`ShieldApplyAmount`、`HasteAmount`、`SlowAmount`、`FreezeAmount`、`Cooldown`、`CooldownMax`、`ChargeAmount`、`Multicast`。
  未识别的属性也会先保守落成 `buff_*`，避免直接丢失。
  仍需补：
  - 其他实际出现在日志中的属性种类的真实语义
  - 更准确的目标选择逻辑

- 完整覆盖 `TAuraActionCardModifyAttribute`
  当前已能识别并在开战前应用更多被动 buff，并支持单卡 / 全局 / 相邻范围的近似作用。
  需要补：
  - aura 生效/失效条件
  - aura 对单卡/全局/相邻卡的精确作用范围
  - aura 叠加规则

- 校准 `TActionCardCharge` / `TActionCardReload`
  当前近似成直接减少剩余冷却。
  需要确认它们在真实游戏里是：
  - 直接推进剩余冷却
  - 增加冷却流速
  - 还是只影响特定目标/次数

## Action Coverage Gaps

- `TActionComposite` / `TActionConditional`
  当前已支持多动作串联和嵌套展开，不再只取第一层可识别动作。
  仍需补：
  - 条件分支真假判定
  - 依赖运行时状态的精确求值

- `TActionPlayerModifyAttribute`
  当前已补到更多常见玩家属性，并支持部分正负值方向及 self/opponent 目标差异。
  需要补齐：
  - 更多罕见属性类型
  - `Operation` 不同模式的精确处理
  - 依赖运行时玩家属性引用时的真实取值

- `TAuraActionPlayerModifyAttribute`
  已有基础支持，并会在开战前落地到玩家状态；仍需校准：
  - 是否常驻
  - 是否可叠加
  - 进场时机
  - 退出时机

- 物品 buff 目标选择
  当前 `buff_*` 已支持单卡 / 全局 / 相邻的近似选卡。
  需要确认真实规则是否为：
  - 固定目标
  - 相邻目标
  - 最近触发目标
  - 随机目标
  - 某类标签目标

## Timing / Engine Accuracy

- 同一 `50ms` 时间片内的事件顺序
  仍需用实战日志确认：
  - DOT
  - 治疗
  - 冷却归零触发
  - 沙暴
  - 死亡判定

- 灼烧结算顺序
  当前按 `500ms` 结算并衰减 `3%`，最少 `1`。
  仍需确认：
  - 伤害与衰减先后
  - 多次同 tick 叠火时的取值时机

- 治疗清火/清毒的取整方式
  当前按 `floor(heal * 0.05)`。
  需要确认是否为：
  - 向下取整
  - 四舍五入
  - 最少清 1

- 冻结与加速/减速的交互
  当前实现为：
  冻结阻止冷却推进，但 haste/slow 持续时间继续流逝。
  仍需用实战验证边界：
  - 冻结结束的那个 tick 是否立即推进冷却
  - 同 tick 获得冻结和加速时谁先结算

## Encounter Data / Mapping

- 清理剩余 `unknown` 遭遇
  优先用：
  - `BazaarDb/cards_full.json`
  - 运行时学习到的 `BattleSimulator.learned_mappings.json`
  - 实战日志反查

- 校验 BazaarDb 的 `MonsterMetadata`
  需要确认：
  - `board` 是否完整
  - `skills` 是否完整
  - `health` 是否与游戏内一致
  - tier / enchant 覆盖是否可靠

- 怪物 `skills` 触发建模
  当前模拟已把 `skills` 合并进敌方快照。
  仍需确认：
  - 是否所有 `skills` 都应按普通手牌同样节奏触发
  - 被动 / 主动 skill 是否需要进一步区分

- 遇到遭遇和怪物名不一致时
  优先信任 `MonsterMetadata` 和运行时学习结果，不再信任模糊名称匹配。

## Validation

- 为常见野怪建立“实战对照样本”
  至少覆盖：
  - burn 流
  - poison 流
  - 护盾回复流
  - haste/slow/freeze 节奏流
  - 带 aura 的构筑流

- 给每个对照样本保存：
  - 玩家阵容快照
  - 敌方阵容快照
  - 实战胜负
  - 实战耗时量级
  - 关键伤害来源

- 当 `unsupportedEffects` 非空时
  继续降低 `Confidence` 文案权重，避免把“100% WinRate”误读成真实必胜。

## Code / Docs Maintenance

- 更新 [SIMULATION_RULES.md](C:\Users\mzs\Documents\python\BazaarOracle\SIMULATION_RULES.md) 的 `Implementation Notes`
  其中有些条目已被实现，需要改成最新状态，尤其是：
  - composite / conditional 已支持嵌套展开
  - `buff_*` 已支持更多属性与范围
  - monster `skills` 已纳入快照

- 给 `unsupportedEffects` 做聚类统计
  输出最常见未覆盖动作类型，作为后续开发优先级依据。
