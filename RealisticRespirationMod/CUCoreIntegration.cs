using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CUCoreLib.Data;
using CUCoreLib.Helpers;
using CUCoreLib.Registries;
using CUCoreLib.Saving;
using Newtonsoft.Json.Linq;
using RealisticRespirationMod.API;
using RealisticRespirationMod.Compat;
using RealisticRespirationMod.UI;
using RespirationSimCore;
using UnityEngine;

namespace RealisticRespirationMod
{
    internal sealed class RespirationBodySaveProvider : IBodySaveProvider
    {
        public int GetVersion() => 1;

        public JToken Capture(Body body)
        {
            var sim = RespirationModManager.GetOrCreate(body).Sim;

            var modsArr = new JArray();
            foreach (var m in sim.Modifiers.ExportExcitabilityModifiers())
            {
                modsArr.Add(new JObject
                {
                    ["id"] = m.SourceId,
                    ["add"] = m.AdditiveExcitability,
                    ["mult"] = m.ExcitabilityMultiplier,
                    ["chemo"] = m.ChemoSensitivityMultiplier,
                    ["thr"] = m.Co2ThresholdOffsetMmHg,
                    ["rem"] = m.RemainingDurationSec
                });
            }

            var failArr = new JArray();
            foreach (var f in sim.Modifiers.ExportFailureOverrides())
            {
                failArr.Add(new JObject
                {
                    ["id"] = f.SourceId,
                    ["act"] = f.Active,
                    ["rem"] = f.RemainingDurationSec
                });
            }

            return new JObject
            {
                ["paco2"] = sim.PaCO2MmHg,
                ["pao2"] = sim.PaO2MmHg,
                ["hco3"] = sim.BicarbonateMmolL,
                ["rpm"] = sim.CurrentRpm,
                ["med"] = sim.MedullaryExcitability,
                ["peSec"] = sim.SustainedPeSeconds,
                ["nikD"] = sim.Drugs.NikethamideDepot,
                ["nikC"] = sim.Drugs.NikethamideConc,
                ["bicD"] = sim.Drugs.BicarbonateDepotMmolL,
                ["mods"] = modsArr,
                ["fails"] = failArr
            };
        }

        public void Restore(Body body, JToken payload, int version, SaveRestoreContext context)
        {
            if (body == null || payload is not JObject o) return;
            var ctrl = RespirationModManager.GetOrCreate(body);
            var sim = ctrl.Sim;

            sim.PaCO2MmHg = Mathf.Clamp(o["paco2"]?.Value<float>() ?? BodyRespirationSimulator.RestingPaCO2, 12f, 150f);
            sim.PaO2MmHg = Mathf.Clamp(o["pao2"]?.Value<float>() ?? BodyRespirationSimulator.RestingPaO2, 10f, 125f);
            sim.BicarbonateMmolL = Mathf.Clamp(o["hco3"]?.Value<float>() ?? BodyRespirationSimulator.RestingBicarbonate, 6f, 48f);
            sim.CurrentRpm = Mathf.Clamp(o["rpm"]?.Value<float>() ?? BodyRespirationSimulator.RestingRpm, 0f, RespirationConfig.MaxRespiratoryRateBpm);
            sim.MedullaryExcitability = Mathf.Clamp(o["med"]?.Value<float>() ?? 1.0f, 0f, 6f);
            sim.SustainedPeSeconds = Mathf.Max(0f, o["peSec"]?.Value<float>() ?? 0f);

            sim.Drugs.NikethamideDepot = Mathf.Max(0f, o["nikD"]?.Value<float>() ?? 0f);
            sim.Drugs.NikethamideConc = Mathf.Max(0f, o["nikC"]?.Value<float>() ?? 0f);
            sim.Drugs.BicarbonateDepotMmolL = Mathf.Max(0f, o["bicD"]?.Value<float>() ?? 0f);

            sim.Modifiers.ClearAll();
            if (o["mods"] is JArray modsArr)
            {
                foreach (var tok in modsArr)
                {
                    if (tok is not JObject mo) continue;
                    string? id = mo["id"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    sim.Modifiers.SetFullModifier(new ExcitabilityModifier
                    {
                        SourceId = id!,
                        AdditiveExcitability = mo["add"]?.Value<float>() ?? 0f,
                        ExcitabilityMultiplier = mo["mult"]?.Value<float>() ?? 1f,
                        ChemoSensitivityMultiplier = mo["chemo"]?.Value<float>() ?? 1f,
                        Co2ThresholdOffsetMmHg = mo["thr"]?.Value<float>() ?? 0f,
                        RemainingDurationSec = mo["rem"]?.Value<float>() ?? -1f
                    });
                }
            }
            if (o["fails"] is JArray failArr)
            {
                foreach (var tok in failArr)
                {
                    if (tok is not JObject fo) continue;
                    string? id = fo["id"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    sim.Modifiers.SetPulmonaryFailureOverride(
                        id!,
                        fo["act"]?.Value<bool>() ?? false,
                        fo["rem"]?.Value<float>() ?? -1f);
                }
            }

            body.respiratoryRate = sim.CurrentRpm * BodyRespirationSimulator.VanillaRespScale;
            ctrl.LastWrittenRespRate = body.respiratoryRate;
            ctrl.LastWrittenBloodOxygen = body.bloodOxygen;
            ctrl.HasSteppedOnce = true;
        }
    }

    internal static class CUCoreIntegration
    {
        public static readonly string[] BonusCrateDropPool =
        {
            RespirationModManager.NikethamideAmpouleItemId,
            RespirationModManager.SodiumBicarbonateBottleItemId,
            RespirationModManager.PortableAbgItemId
        };

        public static void RegisterAll(ManualLogSource logger)
        {
            RegisterLocales();
            RegisterModOptions();
            RegisterSaveProviders();
            RegisterLiquidsAndRecipes();
            RegisterItems();
            RegisterPlayerEvents();
            RegisterConsoleCommands();

            logger.LogInfo("CUCoreLib integrations registered (Nikethamide, Sodium Bicarbonate, Portable ABG Monitor, recipes, keybind, per-save persistence, console commands).");
        }

        private static void RegisterLocales()
        {
            LocaleRegistry.Register(LocaleRegistry.LocaleCategory.Other, "abgAnalyzerGuide",
                "动脉血气分析仪 (ABG) — 查看实时 PaO2 / PaCO2 / 血液 pH / HCO3- 与延髓呼吸中枢状态，支持冻结观察");
        }

        private static void RegisterModOptions()
        {
            ModOptionsRegistry.Register(ModOptionDefinition.Keybind(
                id: "realisticresp.abg_hotkey",
                label: "动脉血气分析仪(ABG)热键",
                description: "在持有生物芯片时快速开关居中动脉血气分析仪界面。",
                category: Setting.SettingCategory.Input,
                defaultValue: KeyCode.K,
                apply: key => RespirationConfig.AbgAnalyzerHotkey = key));
        }

        private static void RegisterSaveProviders()
        {
            SaveRegistry.RegisterBodyProvider("realisticresp.respirationState", new RespirationBodySaveProvider());
        }

        private static void RegisterLiquidsAndRecipes()
        {
            // 1. Nikethamide (尼可刹米注射液) — central medullary respiratory stimulant
            LiquidRegistry.Register(RespirationModManager.NikethamideLiquidId, new CustomLiquidInfo
            {
                name = "尼可刹米注射液",
                description = "经典中枢呼吸兴奋剂（推荐静注 3~5 mL；口服效价减半）。直接兴奋延髓呼吸中枢并提高 CO2 敏感度，专治阿片/苯二氮䓬/镇静剂所致中枢呼吸抑制；对肺衰竭无效，过量可致震颤与惊厥。",
                color = new Color(1.0f, 0.86f, 0.48f, 0.94f),
                valuePerLiter = 380f,
                healthUsable = true,
                injectable = true,
                injectionSickness = 0f,
                onHealthUse = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    RespirationModManager.AdministerDrug(limb.body, RespirationDrugId.Nikethamide, ml, RespirationAdminRoute.Injectable, limb);
                },
                onInject = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    RespirationModManager.AdministerDrug(limb.body, RespirationDrugId.Nikethamide, ml, RespirationAdminRoute.Injectable, limb);
                },
                onDrink = (ml, body) =>
                {
                    if (body == null) return;
                    RespirationModManager.AdministerDrug(body, RespirationDrugId.Nikethamide, ml, RespirationAdminRoute.Oral);
                }
            });

            // 2. 5% Sodium Bicarbonate (碳酸氢钠注射液) — corrects metabolic acidosis, releases CO2!
            LiquidRegistry.Register(RespirationModManager.SodiumBicarbonateLiquidId, new CustomLiquidInfo
            {
                name = "碳酸氢钠注射液",
                description = "5% 碱性纠酸注射液（推荐静注 10~20 mL；口服效价减弱）。补充血浆 HCO3- 迅速纠正严重代谢性酸中毒；中和过程会释放 CO2，通气不足或肺衰竭时盲目静注将反致 PaCO2 飙升！",
                color = new Color(0.84f, 0.95f, 1.0f, 0.92f),
                valuePerLiter = 220f,
                healthUsable = true,
                injectable = true,
                injectionSickness = 0f,
                onHealthUse = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    RespirationModManager.AdministerDrug(limb.body, RespirationDrugId.SodiumBicarbonate, ml, RespirationAdminRoute.Injectable, limb);
                },
                onInject = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    RespirationModManager.AdministerDrug(limb.body, RespirationDrugId.SodiumBicarbonate, ml, RespirationAdminRoute.Injectable, limb);
                },
                onDrink = (ml, body) =>
                {
                    if (body == null) return;
                    RespirationModManager.AdministerDrug(body, RespirationDrugId.SodiumBicarbonate, ml, RespirationAdminRoute.Oral);
                }
            });

            // Crafting recipes
            RecipeRegistry.Register(new Recipe
            {
                INT = 7,
                category = Recipes.RecipeCategory.Medicine,
                result = new RecipeResult
                {
                    id = RespirationModManager.NikethamideLiquidId,
                    isLiquid = true,
                    resultCondition = 20f
                },
                items = new List<RecipeItem>
                {
                    new RecipeItem(20f) { specificId = "oxyline", specific = true, isLiquid = true },
                    new RecipeItem(10f) { specificId = "biochem", specific = true, isLiquid = true }
                }
            });

            RecipeRegistry.Register(new Recipe
            {
                INT = 5,
                category = Recipes.RecipeCategory.Medicine,
                result = new RecipeResult
                {
                    id = RespirationModManager.SodiumBicarbonateLiquidId,
                    isLiquid = true,
                    resultCondition = 40f
                },
                items = new List<RecipeItem>
                {
                    new RecipeItem(30f) { specificId = "saline", specific = true, isLiquid = true },
                    new RecipeItem(10f) { specificId = "biochem", specific = true, isLiquid = true }
                }
            });
        }

        private static void RegisterItems()
        {
            // 1. Nikethamide Ampoule (20 mL)
            ItemRegistry.Register(RespirationModManager.NikethamideAmpouleItemId, new CustomItemInfo
            {
                fullName = "尼可刹米安瓿",
                description = "装有 20 mL 尼可刹米中枢呼吸兴奋剂（推荐抽入注射器静注 3~5 mL）。抢救阿片/苯二氮䓬所致呼吸抑制，对肺衰竭无效；过量致震颤惊厥。",
                category = "drug",
                tags = "medicine",
                weight = 0.08f,
                value = 42,
                slotRotation = -45f,
                usable = true,
                usableWithLMB = true,
                usableOnLimb = false,
                destroyAtZeroCondition = false,
                capacity = 20f,
                autoFill = false,
                defaultContents = new List<LiquidStack> { new LiquidStack(RespirationModManager.NikethamideLiquidId, 20f) },
                DropPool = DropPool.None,
                Icon = RespirationSprites.GetNikethamideAmpouleSprite(),
                LiquidMask = RespirationSprites.GetAmpouleLiquidMask(),
                useAction = (body, item) =>
                {
                    if (body != null && item != null && item.TryGetComponent<WaterContainerItem>(out var wat))
                        wat.Drink(body, 4f, "drink");
                }
            });

            // 2. Sodium Bicarbonate Infusion Bottle (50 mL)
            ItemRegistry.Register(RespirationModManager.SodiumBicarbonateBottleItemId, new CustomItemInfo
            {
                fullName = "碳酸氢钠注射液瓶",
                description = "装有 50 mL 5% 碳酸氢钠注射液（推荐静注 10~20 mL）。用于抢救休克/缺氧/脓毒症/癫痫持续状态所致的严重代谢性酸中毒；肺衰竭或通气不足时慎用！",
                category = "drug",
                tags = "medicine",
                weight = 0.14f,
                value = 34,
                slotRotation = -45f,
                usable = true,
                usableWithLMB = true,
                usableOnLimb = false,
                destroyAtZeroCondition = false,
                capacity = 50f,
                autoFill = false,
                defaultContents = new List<LiquidStack> { new LiquidStack(RespirationModManager.SodiumBicarbonateLiquidId, 50f) },
                DropPool = DropPool.None,
                Icon = RespirationSprites.GetSodiumBicarbonateBottleSprite(),
                LiquidMask = RespirationSprites.GetBottleLiquidMask(),
                useAction = (body, item) =>
                {
                    if (body != null && item != null && item.TryGetComponent<WaterContainerItem>(out var wat))
                        wat.Drink(body, 15f, "drink");
                }
            });

            // 3. Portable Arterial Blood Gas Analyzer (usable even without biochip!)
            ItemRegistry.Register(RespirationModManager.PortableAbgItemId, new CustomItemInfo
            {
                fullName = "便携式血气分析仪",
                description = "便携式动脉血气分析仪（ABG），可在无生物芯片的情况下查看实时 PaO2、PaCO2、血液 pH、HCO3-、延髓呼吸中枢兴奋度与 3 分钟变化趋势。",
                category = "medical",
                tags = "medicine",
                weight = 0.15f,
                value = 50,
                usable = true,
                usableWithLMB = true,
                usableOnLimb = true,
                destroyAtZeroCondition = false,
                onlyHoldInHands = false,
                DropPool = DropPool.None,
                Icon = RespirationSprites.GetPortableAbgSprite(),
                useAction = (body, item) =>
                {
                    if (body != null) RespirationModManager.OpenOrToggleAbgMinigame(body, item);
                },
                useLimbAction = (limb, item) =>
                {
                    if (limb?.body != null) RespirationModManager.OpenOrToggleAbgMinigame(limb.body, item);
                }
            });
        }

        private static void RegisterPlayerEvents()
        {
            CUCoreUtils.OnHealPlayer += body => RespirationModManager.ResetBody(body, clearDrugs: true, clearModifiers: true);
            CUCoreUtils.OnLastStand += () =>
            {
                Body? b = CUCoreUtils.EventPlayer ?? (PlayerCamera.main != null ? PlayerCamera.main.body : null);
                if (b != null) RespirationModManager.ResetBody(b, clearDrugs: false, clearModifiers: false);
            };
        }

        public static void QueueMoodlesForBody(Body body)
        {
            if (body == null || !body.alive) return;
            var ctrl = RespirationModManager.TryGet(body);
            if (ctrl == null || !ctrl.HasSteppedOnce) return;
            ref readonly var s = ref ctrl.Sim.LastTelemetry;

            // 1. Pulmonary Failure (critical)
            if (s.IsPulmonaryFailure)
            {
                MoodleRegistry.AddMoodle(3,
                    RespirationSprites.GetMoodleIcon("lungfail", new Color(0.85f, 0.30f, 0.35f), new Color(1f, 0.15f, 0.15f), 0),
                    "急性肺衰竭",
                    $"肺通气泵崩溃（{BodyRespirationSimulator.GetFailureReasonDisplayName(s.FailureReason)}），无法自主呼吸！需立即解除胸腔压迫/修复胸部损伤。",
                    critical: true, chippedOnly: false, important: true, key: "resp.lungfail", holdSeconds: 0.7f);
            }

            // 2. Hypercapnia / CO2 Narcosis vs Hypocapnia
            if (s.PaCO2MmHg > 50f)
            {
                int tier = s.PaCO2MmHg > 78f ? 3 : (s.PaCO2MmHg > 62f ? 2 : 1);
                string title = s.PaCO2MmHg > 70f ? "二氧化碳麻醉 (重度高碳酸血症)" : "高碳酸血症";
                string desc = s.PaCO2MmHg > 70f
                    ? $"动脉 PaCO2 飙升至 {s.PaCO2MmHg:0.0} mmHg！严重二氧化碳潴留正在引起意识抑制、颅压升高并威胁延髓呼吸中枢。"
                    : $"动脉 PaCO2 升至 {s.PaCO2MmHg:0.0} mmHg，正在强烈激活延髓呼吸中枢加快通气。";
                MoodleRegistry.AddMoodle(tier,
                    RespirationSprites.GetMoodleIcon("hypercapnia", new Color(1f, 0.58f, 0.24f), new Color(1f, 0.88f, 0.35f), 1),
                    title, desc, critical: tier >= 3, chippedOnly: true, important: true, key: "resp.co2", holdSeconds: 0.7f);
            }
            else if (s.PaCO2MmHg < 29f)
            {
                int tier = s.PaCO2MmHg < 22f ? 2 : 0;
                MoodleRegistry.AddMoodle(tier,
                    RespirationSprites.GetMoodleIcon("hypocapnia", new Color(0.48f, 0.86f, 1f), Color.white, 2),
                    "低碳酸血症 (过度通气)",
                    $"动脉 PaCO2 降至 {s.PaCO2MmHg:0.0} mmHg，可能导致脑血管收缩眩晕、手足震颤并降低癫痫阈值。",
                    critical: false, chippedOnly: true, important: false, key: "resp.co2", holdSeconds: 0.7f);
            }

            // 3. Blood pH Imbalance (Acidosis / Alkalosis)
            if (s.BloodPh < 7.32f)
            {
                int tier = s.BloodPh < 7.12f ? 3 : (s.BloodPh < 7.24f ? 2 : 1);
                MoodleRegistry.AddMoodle(tier,
                    RespirationSprites.GetMoodleIcon("acidosis", new Color(1f, 0.32f, 0.38f), new Color(1f, 0.86f, 0.42f), 3),
                    BodyRespirationSimulator.GetAcidBaseDisplayName(s.AcidBase),
                    $"动脉血 pH 降至 {s.BloodPh:0.00} (HCO3- {s.BicarbonateMmolL:0.1} mmol/L)。严重酸中毒抑制心肌与中枢并诱发恶性心律失常。",
                    critical: tier >= 3, chippedOnly: true, important: true, key: "resp.ph", holdSeconds: 0.7f);
            }
            else if (s.BloodPh > 7.48f)
            {
                int tier = s.BloodPh > 7.58f ? 2 : 0;
                MoodleRegistry.AddMoodle(tier,
                    RespirationSprites.GetMoodleIcon("alkalosis", new Color(0.55f, 0.75f, 1f), Color.white, 3),
                    BodyRespirationSimulator.GetAcidBaseDisplayName(s.AcidBase),
                    $"动脉血 pH 升至 {s.BloodPh:0.00}，游离钙倾向下降，增加震颤、早搏及失神发作风险。",
                    critical: false, chippedOnly: true, important: false, key: "resp.ph", holdSeconds: 0.7f);
            }

            // 4. Tachypnea (when RR > 28/m)
            if (s.Breathing && s.ActualRpm > 28f)
            {
                int tier = s.ActualRpm >= 55f ? 2 : (s.ActualRpm >= 40f ? 1 : 0);
                MoodleRegistry.AddMoodle(tier,
                    RespirationSprites.GetMoodleIcon("tachypnea", new Color(0.95f, 0.78f, 0.30f), Color.white, 2),
                    s.ActualRpm >= 45f ? "极度呼吸急促" : "呼吸急促",
                    $"延髓呼吸中枢高度兴奋，当前呼吸频率达 {s.ActualRpm:0.0}/m。",
                    critical: false, chippedOnly: false, important: false, key: "resp.tachypnea", holdSeconds: 0.7f);
            }

            // 5. Opiate / BZD Central Respiratory Depression
            if (s.OpiateSuppressionFactor > 0.38f || s.BzdSuppressionFactor > 0.35f)
            {
                int tier = (s.OpiateSuppressionFactor > 0.72f || s.ActualRpm < 6f) ? 3 : 1;
                MoodleRegistry.AddMoodle(tier,
                    RespirationSprites.GetMoodleIcon("central_dep", new Color(0.75f, 0.42f, 0.96f), new Color(1f, 0.35f, 0.35f), 4),
                    "药物性延髓呼吸抑制",
                    "阿片/镇静药物正在压制延髓呼吸中枢对 CO2 的敏感性！可使用纳洛酮拮抗阿片或静注尼可刹米兴奋呼吸中枢。",
                    critical: tier >= 3, chippedOnly: false, important: true, key: "resp.centraldep", holdSeconds: 0.7f);
            }
        }

        private static void RegisterConsoleCommands()
        {
            ConsoleCommandRegistry.Register(
                "resp",
                "呼吸与血气大修调试命令: resp status | abg | paco2 <mmHg> | hco3 <mmolL> | mod <add> [mult] [sec] | lungfail <on|off> | give <itemId> | reset",
                args =>
                {
                    Body? body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                    if (body == null) return;

                    var ctrl = RespirationModManager.GetOrCreate(body);
                    string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

                    switch (sub)
                    {
                        case "status":
                        case "abg":
                        {
                            var s = ctrl.Sim.LastTelemetry;
                            string msg =
                                $"[Resp/ABG] RR={s.ActualRpm:0.0}/m (Target={s.MedullaryTargetRpm:0.0}/m) Medulla={Mathf.RoundToInt(s.MedullaryExcitability * 100f)}% | " +
                                $"pH={s.BloodPh:0.00} PaO2={s.PaO2MmHg:0.1} PaCO2={s.PaCO2MmHg:0.1} HCO3-={s.BicarbonateMmolL:0.1} BE={s.BaseExcessMmolL:+0.0;-0.0;0.0} SpO2={s.SpO2Percent:0.0}% | " +
                                $"VentEff={Mathf.RoundToInt(s.AlveolarVentilationEff * 100f)}% Q={Mathf.RoundToInt(s.PerfusionEff * 100f)}% LungFail={s.FailureReason} | " +
                                $"ECG={CardiacCompatBridge.IsLoaded} Neuro={NeuroCompatBridge.IsLoaded}";
                            CUCoreUtils.ConsoleLog(ConsoleScript.instance, msg);
                            if (sub == "abg")
                            {
                                RespirationModManager.OpenOrToggleAbgMinigame(body, item: null);
                            }
                            break;
                        }
                        case "paco2":
                        {
                            if (args.Length > 1 && float.TryParse(args[1], out float val))
                            {
                                ctrl.Sim.PaCO2MmHg = Mathf.Clamp(val, 12f, 150f);
                                CUCoreUtils.ConsoleLog(ConsoleScript.instance, $"[Resp] PaCO2 set to {ctrl.Sim.PaCO2MmHg:0.1} mmHg.");
                            }
                            break;
                        }
                        case "hco3":
                        {
                            if (args.Length > 1 && float.TryParse(args[1], out float val))
                            {
                                ctrl.Sim.BicarbonateMmolL = Mathf.Clamp(val, 6f, 48f);
                                CUCoreUtils.ConsoleLog(ConsoleScript.instance, $"[Resp] HCO3- set to {ctrl.Sim.BicarbonateMmolL:0.1} mmol/L.");
                            }
                            break;
                        }
                        case "mod":
                        {
                            float add = args.Length > 1 && float.TryParse(args[1], out float a) ? a : 1.0f;
                            float mult = args.Length > 2 && float.TryParse(args[2], out float m) ? m : 1.0f;
                            float sec = args.Length > 3 && float.TryParse(args[3], out float d) ? d : 30f;
                            RespirationApi.SetMedullaryExcitabilityModifier(body, "console.debug", add, mult, sec);
                            CUCoreUtils.ConsoleLog(ConsoleScript.instance, $"[Resp] Applied console medullary modifier add={add:0.00}, mult={mult:0.00}, dur={sec:0}s.");
                            break;
                        }
                        case "lungfail":
                        {
                            bool on = args.Length <= 1 || args[1].ToLowerInvariant() == "on" || args[1] == "1";
                            RespirationApi.SetPulmonaryFailureOverride(body, "console.debug", on, -1f);
                            CUCoreUtils.ConsoleLog(ConsoleScript.instance, $"[Resp] External pulmonary failure set to {on}.");
                            break;
                        }
                        case "give":
                        {
                            string itemId = args.Length > 1 ? args[1] : RespirationModManager.PortableAbgItemId;
                            var go = Utils.Create(itemId, body.transform.position + Vector3.up * 0.5f, 0f);
                            CUCoreUtils.ConsoleLog(ConsoleScript.instance, go != null ? $"[Resp] Spawned '{itemId}'." : $"[Resp] Failed to spawn '{itemId}'.");
                            break;
                        }
                        case "reset":
                        {
                            RespirationModManager.ResetBody(body, clearDrugs: true, clearModifiers: true);
                            CUCoreUtils.ConsoleLog(ConsoleScript.instance, "[Resp] Reset blood gases, medullary center, drugs, and modifiers.");
                            break;
                        }
                    }
                });
        }
    }
}
