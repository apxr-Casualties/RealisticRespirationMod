# Realistic Respiration & Blood Gas Mod (V1.0.2)

为 Unity 游戏 *Casualties Unknown* 打造的**呼吸系统、动脉血气（$PaO_2$ / $PaCO_2$ / 血液 $\text{pH}$ / $[\text{HCO}_3^-]$）与延髓呼吸中枢大修 Mod**（基于 BepInEx 5 + Harmony + **CUCoreLib**）。

支持独立运行，并与 **心电大修（`RealisticCardiacEcgMod`）**、**神经大修（`NeurologyOverhaulMod`）** 实现零编译双向软兼容，构成完整的“心-肺-脑”实时生理闭环。

---

## 核心特性

### 1. 突破原版 25/m 上限至 80/m & 动脉血气酸碱模型
- **真实呼吸频率区间**：在保持原版 4× 等价内部标度的前提下，将 `Body.respiratoryRate` 上限从 `100 (25/m)` 扩展至 `320 (80/m)`，静息稳态约 **`14 ~ 16 次/分`**，同步修正原版 `MoodleManager` `< 90f` 误报通气不足的问题，并兼容第三方 `CUCoreLib` `CCLBody.RespiratoryRate`。
- **动脉血二氧化碳分压（$PaCO_2$）**：正常静息 `40.0 mmHg`（区间 `12 ~ 150 mmHg`），随运动/体力消耗、体温、应激/脓毒症/癫痫产气动态演化，并由有效肺泡通气量清除；重度高碳酸血症（`> 62 ~ 85+ mmHg`）引起二氧化碳麻醉、意识上限压低及双相中枢抑制。
- **动脉血氧分压（$PaO_2$）与 Hill + Bohr 氧离曲线**：结合肺泡气方程、血胸/肺栓塞/低灌注分流及受 $\text{pH}$ 和体温调制的 **Bohr 效应** 实时映射至 `body.bloodOxygen`（$SpO_2$）；完全停止呼吸下 $SpO_2$ 从 100% 降至 50% 约 **`1.5 分钟`**（~`90s`）。
- **完整血液酸碱平衡（$\text{pH}$ & $[\text{HCO}_3^-]$）**：基于 Henderson-Hasselbalch 方程实时计算 $\text{pH}$（正常 `7.35 ~ 7.45`）与碱剩余 $BE$；低灌注休克、严重缺氧、脓毒症、癫痫持续状态与饥饿酮症会消耗 $[\text{HCO}_3^-]$ 引发代谢性酸中毒，并驱动延髓产生 **Kussmaul 深快代偿呼吸**。

### 2. 延髓呼吸中枢、阿片抑制、急性肺衰竭与窒息恐慌
- **延髓呼吸中枢控制器**：综合中枢 $CO_2$ 化学感受器、代谢性酸中毒 $\text{pH}$ 驱动、外周低氧驱动与神经/药物输入计算目标呼吸接近值，按生理惯性平滑驱动呼吸频率。
- **阿片类与镇静药物抑制**：原版阿片药物（`Painkillers.actualOpiateReception`）右移并压平 $CO_2$ 反应曲线，过量可致呼吸骤停；使用纳洛酮（`naloxone`）拮抗清除阿片后，蓄积的高 $PaCO_2$ 会立刻激发代偿性深快呼吸。
- **急性肺衰竭（Pulmonary Failure）**：当胸廓肌肉 `< 4%`、血胸 `>= 85%`、持续未解肺栓塞并发重度肺伤或外部 Mod 触发肺衰竭时，延髓中枢仍可高度兴奋发出呼吸冲动，但肺通气泵崩溃导致**无法自主呼吸（`0/m`）**；通过胸腔穿刺引流针（`chestdrain`）、修复胸部或溶栓解除病因后恢复通气。
- **空气饥渴窒息恐慌与心理创伤**：清醒状态下若延髓呼吸中枢处于兴奋状态且实际呼吸频率远低于延髓目标呼吸频率（如肺衰竭、气道受阻），会触发强烈恐慌（拉升 `horrifiedLevel`、惊恐眼神、肾上腺素与持续 `traumaAmount` 心理创伤）。

### 3. 双形态血气分析仪与配套医疗物品/药物
- **芯片医疗面板血气按钮 + 浮动卡片**：在持芯片医疗面板血氧数值右侧新增 `[血气]` 按钮，点击展开**不中断伤口操作**的实时浮动血气卡片，可边打药/引流边观察 `pH`、`PaO2`、`PaCO2`、`HCO3-`、`BE` 与延髓呼吸中枢状态。
- **完整居中动脉血气分析仪（`AbgAnalyzerMinigame`）**：点击浮动卡片右上角 `[展开 ↗]`、按可改建热键（持芯片默认 `K`），或使用 **便携式血气分析仪（`abgmonitor`，无视芯片限制）** 开启；展示 5 项指标参考区色条、近 3 分钟多曲线趋势图、实时呼吸/延髓遥测与临床酸碱诊断。
- **尼可刹米注射液（`nikethamide` / `nikethamide_ampoule`）**：经典中枢呼吸兴奋剂，通过兴奋延髓中枢与提高 $CO_2$ 敏感度抢救阿片/苯二氮䓬/镇静剂所致中枢抑制；**对肺衰竭无效**；过量具有中枢惊厥毒性。配方：`20 mL oxyline + 10 mL biochem -> 20 mL nikethamide`（`INT >= 7`）。
- **5% 碳酸氢钠注射液（`sodium_bicarbonate` / `sodium_bicarbonate_bottle`）**：静脉补充 $[\text{HCO}_3^-]$ 迅速纠正严重代谢性酸中毒；中和过程会释放游离 $CO_2$，通气正常时平稳呼出，**肺衰竭或通气不足时盲目静注将反致 $PaCO_2$ 飙升**。配方：`30 mL saline + 10 mL biochem -> 40 mL sodium_bicarbonate`（`INT >= 5`）。

---

## 三方软兼容联动

| 兼容模组 | 依赖类型 | 联动机制 |
| --- | --- | --- |
| **CUCoreLib** (`net.cucorelib`) | 硬依赖 | 存档持久化（`SaveRegistry`）、自定义物品/液体/配方/Moodle/设置/热键/控制台指令，并兼容第三方 `CCLBody.RespiratoryRate` |
| **RealisticCardiacEcgMod** (`com.casualties.realistic.ecg`) | 软兼容 | 肺栓塞/肺衰竭激活右心室缺血（`RightVentricularIschemia`）；酸碱失衡影响心肌缺血/儿茶酚胺敏感性与早搏；室颤/停搏无 `AutoPump` 灌注时肺换气中止并致脑干低灌注中枢衰竭 |
| **NeurologyOverhaulMod** (`com.casualties.neurology.overhaul`) | 软兼容 | 避免 `CCLBody.RespiratoryRate` 双重扣减；精细拆分咪达唑仑/氯硝西泮/东莨菪碱+阿片协同抑制、普萘洛尔 $\beta_2$ 支气管收缩、高迷走痉挛与阿托品解痉、库欣反射、癫痫强直窒息与发作后代偿；反向将高 $CO_2$/酸碱变化反馈给颅内压、癫痫阈值/失神发作与慢波脑电，并在脑电图实时显示 **延髓呼吸中枢兴奋度** |

---

## 开发者接口：延髓呼吸中枢兴奋性 API（`RespirationApi`）

本 Mod 通过 `RealisticRespirationMod.API.RespirationApi` 提供按 `sourceId` 隔离的多源修正接口，多 Mod 叠加互不覆盖，支持自动定时过期。所有核心方法**仅接受 `Body` 与基础类型参数**，既可直接引用程序集，也可通过 Harmony 反射实现**零编译依赖软调用**。

### 方式一：直接引用调用
```csharp
using RealisticRespirationMod.API;

// 1. 注册或更新延髓呼吸中枢兴奋性修正（sourceId 隔离，durationSec < 0 为持续生效）
RespirationApi.SetMedullaryExcitabilityModifier(
    body,
    sourceId: "com.yourmod.respiratory_stimulant",
    additiveOffset: 1.2f,      // +1.0 约增加 +15/m 呼吸驱动；负值为抑制
    multiplier: 1.0f,          // 乘算缩放（0 = 完全中枢麻痹）
    durationSec: 60f);         // 60 秒后自动过期移除

// 2. 调节 CO2 化学感受器敏感度与阈值（例如模拟麻醉药钝化 CO2 反应曲线）
RespirationApi.SetChemosensitivityModifier(
    body,
    sourceId: "com.yourmod.anesthetic",
    sensitivityMult: 0.45f,
    co2ThresholdOffsetMmHg: 8f,
    durationSec: 45f);

// 3. 显式触发/解除外源性肺衰竭，或直接调节血气
RespirationApi.SetPulmonaryFailureOverride(body, "com.yourmod.toxin", failed: true, durationSec: 30f);
RespirationApi.AddPaCO2Delta(body, +10f);
RespirationApi.AddBicarbonateDelta(body, -4f);

// 4. 查询当前生理遥测
float paco2 = RespirationApi.GetPaCO2(body);
float pao2  = RespirationApi.GetPaO2(body);
float ph    = RespirationApi.GetBloodPh(body);
float med   = RespirationApi.GetMedullaryExcitability(body); // 1.0 = 静息基线
bool failed = RespirationApi.IsPulmonaryFailure(body);
```

### 方式二：零编译依赖软调用（Harmony `AccessTools`）
```csharp
var apiType = HarmonyLib.AccessTools.TypeByName("RealisticRespirationMod.API.RespirationApi");
var setMod = apiType != null
    ? HarmonyLib.AccessTools.Method(apiType, "SetMedullaryExcitabilityModifier",
        new[] { typeof(Body), typeof(string), typeof(float), typeof(float), typeof(float) })
    : null;

setMod?.Invoke(null, new object[] { body, "com.yourmod.drug", -0.6f, 0.7f, 30f });
```

---

## 控制台调试命令

| 命令 | 说明 |
| --- | --- |
| `resp status` | 打印当前呼吸频率、延髓兴奋度、$PaO_2$、$PaCO_2$、$\text{pH}$、$[\text{HCO}_3^-]$、肺衰竭与联动状态 |
| `resp abg` | 打印状态并打开居中动脉血气分析仪界面 |
| `resp paco2 <mmHg>` | 直接设置当前 $PaCO_2$（如 `resp paco2 70`） |
| `resp hco3 <mmolL>` | 直接设置当前 $[\text{HCO}_3^-]$（如 `resp hco3 12` 模拟代谢性酸中毒） |
| `resp mod <add> [mult] [sec]` | 注入临时延髓中枢兴奋性修正器（如 `resp mod 1.5 1 30`） |
| `resp lungfail <on\|off>` | 手动开关外源性肺衰竭 |
| `resp give <itemId>` | 生成本 Mod 物品（`nikethamide_ampoule` / `sodium_bicarbonate_bottle` / `abgmonitor`） |
| `resp reset` | 重置血气、延髓中枢、药物与所有修正器至正常静息态 |

---

## 目录结构

- `RespirationSimCore/`：零 Unity 依赖的呼吸生理、动脉血气、酸碱平衡与药代动力学核心库（`netstandard2.1`）。
- `RealisticRespirationMod/`：BepInEx 5 插件本体、`RespirationApi`、Harmony 补丁、`CUCoreLib` 集成、心电/神经软兼容桥接与双形态血气分析仪 UI。
- `RespirationSimVerifier/`：离线控制台生理验证程序（覆盖 12 组生理/病理/三方耦合及 25× 倍速稳定性测试）。

---

## 玩家安装方法

1. 安装 [BepInEx 5](https://github.com/BepInEx/BepInEx)（Mono 64 位版本）。
2. 安装硬前置模组 **CUCoreLib**（v1.1+）。
3. 从 Releases 下载（或从源码编译）并将以下两个文件放入 `BepInEx/plugins/` 目录：
   - `RespirationSimCore.dll`
   - `RealisticRespirationMod.dll`

---

## 开发者从源码编译

1. 复制根目录下的 `Directory.Build.props.example` 为 `Directory.Build.props`，填写本地 *Casualties Unknown* 游戏目录的 `BepInEx/core`、`Managed` 与 `CUCoreLib.dll` 路径。
2. 构建完整解决方案：
   ```bash
   dotnet build RealisticRespirationMod.sln -c Release
   ```
3. 运行 12 组离线生理验证（无需游戏本体 DLL 即可独立运行）：
   ```bash
   dotnet run --project RespirationSimVerifier/RespirationSimVerifier.csproj -c Release
   ```

---

## 更新日志与开源协议

- 完整版本变更见 [CHANGELOG.md](CHANGELOG.md)。
- 本项目基于 [MIT License](LICENSE) 开源。
