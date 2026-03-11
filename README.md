# The Bazaar - Event Logger & Battle Simulator

为 Steam 游戏 [The Bazaar](https://store.steampowered.com/app/2232680/The_Bazaar/) 开发的 BepInEx Mod，提供游戏事件日志记录和战斗模拟预测功能。

## 功能

### 1. 游戏事件日志 (GameSimEvents.log)
实时记录游戏流程事件：卡牌购买/出售/升级、状态转换、玩家属性变化等。所有卡牌 ID 自动翻译为可读名称。

### 2. 战斗事件日志 (CombatSimEvents.log)
逐帧记录战斗过程：每张卡的冷却、触发、伤害/治疗/灼烧等效果执行，玩家和对手的血量变化。

### 3. 战斗模拟器 (BattleSimulator.log)
在选择野怪战斗界面 (Hour=2 Choice) 自动运行：
- 读取当前玩家手牌和属性
- 查找野怪阵容（75个预置模板）
- 运行简化的 tick 模拟（50ms/tick，含冷却/伤害/治疗/灼烧/护盾/加速/减速/冰冻/沙暴）
- 输出胜负预测和剩余血量

## 项目结构

```
bazhanixiang/
├── BazaarEventLogger/              # BepInEx 插件源码
│   ├── BazaarEventLogger.csproj    # 项目文件 (.NET Standard 2.1)
│   ├── Plugin.cs                   # 插件入口 (BepInEx BaseUnityPlugin)
│   ├── GameSimPatch.cs             # Harmony 补丁 (hook GameSimHandler/CombatSimHandler)
│   ├── EventLogger.cs              # 游戏事件日志格式化
│   ├── CombatLogger.cs             # 战斗事件日志格式化
│   ├── CardDatabase.cs             # 卡牌数据库 (从 cards.json 加载 2596 张卡)
│   ├── BattleSimulator.cs          # 战斗模拟引擎
│   └── tools/
│       └── export_monsters.py      # 野怪数据导出脚本 (生成 monster_data.json)
├── BazaarGameShared/               # 游戏 DLL 反编译代码 (仅供参考)
│   └── ...
└── README.md
```

## 安装与使用

### 前置要求
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- [BepInEx 5.x (Mono x64)](https://github.com/BepInEx/BepInEx/releases) 已安装到游戏目录
- Python 3.x (仅导出野怪数据时需要)

### 编译
```bash
cd BazaarEventLogger
dotnet build -c Release
```

### 部署
1. 生成野怪数据（首次或游戏更新后）：
   ```bash
   python tools/export_monsters.py
   ```
   生成 `monster_data.json` 到游戏 `StreamingAssets/` 目录。

2. 复制编译产物到 BepInEx 插件目录：
   ```bash
   cp bin/Release/netstandard2.1/BazaarEventLogger.dll "<游戏目录>/BepInEx/plugins/"
   ```

3. 启动游戏，日志文件生成在 `<游戏目录>/BepInEx/` 下。

### 日志文件
| 文件 | 内容 |
|------|------|
| `BepInEx/LogOutput.log` | BepInEx 加载日志（确认插件是否正常加载） |
| `BepInEx/GameSimEvents.log` | 游戏流程事件 |
| `BepInEx/CombatSimEvents.log` | 战斗帧数据 |
| `BepInEx/BattleSimulator.log` | 战斗预测结果 |

## 技术细节

### Hook 机制
通过 Harmony 反射补丁挂钩 `TheBazaarRuntime.dll` 中的：
- `TheBazaar.GameSimHandler.HandleMessage` — 游戏状态更新
- `TheBazaar.CombatSimHandler.HandleMessage` — 战斗模拟数据

### 卡牌效果识别
- 从 `cards.json` 解析 Ability Action 类型：`TActionPlayerDamage`, `TActionPlayerHeal`, `TActionPlayerShield`, `TActionCardBurn`, `TActionCardHaste`, `TActionCardSlow`, `TActionCardFreeze` 等
- 运行时从卡牌属性推断：`DamageAmount`, `BurnApplyAmount`, `HealAmount`, `ShieldApplyAmount` 等
- 卡牌属性按层级继承：Bronze → Silver → Gold → Diamond

### 已知限制
- 约 55% 的遭遇卡无法匹配到野怪模板（模板数据不完整）
- 复杂卡牌效果（条件触发、变形、召唤等）未完全模拟
- 模拟精度依赖于效果推断的准确性
