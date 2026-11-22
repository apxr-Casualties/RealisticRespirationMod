# Realistic Respiration & Blood Gas Mod (V1.0.0)

为 *Casualties Unknown* 打造的呼吸系统、动脉血气（$PaO_2$ / $PaCO_2$ / $\text{pH}$ / $[\text{HCO}_3^-]$）与延髓呼吸中枢大修 Mod（硬依赖 **CUCoreLib**，零编译双向软兼容 **RealisticCardiacEcgMod** 与 **NeurologyOverhaulMod**）。

## 核心特性

1. **突破原版 25/m 限制至 80/m**：
   - 将 `Body.respiratoryRate` 保持原版 4× 等价标度扩展至 `[0, 320]`（显示与生理上限 **`0 ~ 80 次/分`**，静息稳态 **`14 ~ 16 次/分`**），修正原版 `MoodleManager` `< 90f` 误报低通气问题，并兼容原版脚本与第三方 `CUCoreLib` `CCLBody.RespiratoryRate`。
2. **完整动脉血气与酸碱平衡**：
   - 动脉血二氧化碳分压 **$PaCO_2$**（静息 `40 mmHg`，动态产气与肺泡有效通气清除；高 $CO_2$ 刺激延髓、重度高 $CO_2$ 诱发二氧化碳麻醉与双相抑制）；
   - 动脉血氧分压 **$PaO_2$**（静息 `97 mmHg`）结合 **Hill 氧离曲线 + Bohr 效应**实时驱动 `body.bloodOxygen`（$SpO_2$）；
   - **Henderson-Hasselbalch 血液 $\text{pH}$**（静息 `7.40`）与代谢性 $[\text{HCO}_3^-]$（休克、缺氧、脓毒症、癫痫产乳酸消耗 + 肾脏缓慢代偿，触发 Kussmaul 深快代偿呼吸）。
3. **延髓呼吸中枢与阿片/药物抑制**：
   - 延髓呼吸中枢依据 $PaCO_2$、$\text{pH}$、$PaO_2$ 与神经/药物输入计算目标呼吸接近值，平滑驱动呼吸频率；
   - 原版阿片药物（`Painkillers.actualOpiateReception`）右移并压平 $CO_2$ 反应曲线，过量导致中枢呼吸骤停；纳洛酮清除阿片后触发蓄积高 $CO_2$ 驱动的代偿性深快呼吸。
4. **急性肺衰竭机制**：
   - 胸廓严重毁损（`limbs[1].muscleHealth < 4%`）、重度血胸压陷（`hemothorax >= 85%`）、持续未解肺栓塞并发重度肺伤或外部 Mod 触发时进入**肺衰竭**——延髓中枢仍可高兴奋发出冲动，但肺部**强制无法呼吸（`0/m`）**，解除病因（如胸腔穿刺引流针 `chestdrain`、修复胸部、溶栓）后恢复通气。
5. **双形态血气分析仪 & 配套药物/物品**：
   - **芯片血气按钮与浮动卡片**：锚定在医疗面板血氧数值右侧，点击展开不中断操作的实时血气卡片，卡片右上角可点 `[展开 ↗]` 进入完整居中 `AbgAnalyzerMinigame`（含 5 项参考区色条、3 分钟趋势图与临床诊断，亦支持热键默认 `K`）；
   - **便携式血气分析仪（`abgmonitor`）**：无生物芯片也可打开完整动脉血气分析仪；
   - **尼可刹米注射液（`nikethamide` / `nikethamide_ampoule`）**：中枢呼吸兴奋剂，抢救阿片/镇静药物所致中枢抑制，对肺衰竭无效，过量致震颤/惊厥；
   - **5% 碳酸氢钠注射液（`sodium_bicarbonate` / `sodium_bicarbonate_bottle`）**：迅速纠正严重代谢性酸中毒，但释放游离 $CO_2$，肺衰竭或通气不足时慎用。
6. **三方零编译双向软兼容**：
   - 与 **心电大修（`RealisticCardiacEcgMod`）**：肺栓塞/肺衰竭激活心电预留的 `RightVentricularIschemia`；酸碱失衡影响心肌缺血/儿茶酚胺敏感性与早搏；室颤/停搏无 `AutoPump` 灌注时肺换气中止及脑干低灌注中枢衰竭。
   - 与 **神经大修（`NeurologyOverhaulMod`）**：自动屏蔽神经大修的 `CCLBody.RespiratoryRate` 杜绝双算；精准拆分咪达唑仑/氯硝西泮/东莨菪碱+阿片协同抑制、普萘洛尔 $\beta_2$ 支气管收缩、高迷走痉挛与阿托品解痉、库欣反射、癫痫强直期呼吸暂停与发作后代偿；反向将高 $CO_2$/酸碱变化反馈给颅内压、癫痫阈值/失神发作与慢波脑电，并在双通道脑电图与紧凑脑电条实时显示 **延髓呼吸中枢兴奋度**。

## 对外呼吸中枢兴奋性接口（`RealisticRespirationMod.API.RespirationApi`）

所有核心静态方法仅接收 `Body` 与基础类型（`string`, `float`, `bool`），支持直接引用或使用 Harmony `AccessTools.Method` 零依赖软调用：

- `RespirationApi.SetMedullaryExcitabilityModifier(Body body, string sourceId, float additiveOffset, float multiplier = 1f, float durationSec = -1f)`
- `RespirationApi.SetChemosensitivityModifier(Body body, string sourceId, float sensitivityMult, float co2ThresholdOffsetMmHg = 0f, float durationSec = -1f)`
- `RespirationApi.RemoveModifier(Body body, string sourceId)` / `ClearAllModifiers(Body body)`
- `RespirationApi.SetPulmonaryFailureOverride(Body body, string sourceId, bool failed, float durationSec = -1f)`
- `RespirationApi.AddPaCO2Delta(Body body, float deltaMmHg)` / `AddBicarbonateDelta(Body body, float deltaMmolL)`
- `RespirationApi.GetPaCO2(Body)` / `GetPaO2(Body)` / `GetBloodPh(Body)` / `GetBicarbonate(Body)` / `GetMedullaryExcitability(Body)` / `IsPulmonaryFailure(Body)`

## 安装与部署

1. 确保已安装 **BepInEx 5** 与硬前置 **CUCoreLib (v1.1+)**。
2. 将构建产物放入 `BepInEx/plugins/`：
   - `RespirationSimCore/bin/Release/netstandard2.1/RespirationSimCore.dll`
   - `RealisticRespirationMod/bin/Release/netstandard2.1/RealisticRespirationMod.dll`
3. 若同时使用 **NeurologyOverhaulMod**，请同步更新重新编译后的 `NeuroSimCore.dll` 与 `NeuroOverhaulMod.dll` 以启用脑电图延髓读数与方案 A 深度桥接。
