using System;
using RespirationSimCore;

namespace RespirationSimVerifier
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static RespirationEnvironmentInput NormalEnv() => new RespirationEnvironmentInput
        {
            Alive = true,
            Conscious = true,
            Standing = true,
            BrainHealth = 100f,
            Consciousness = 100f,
            BloodVolumePercentage = 1.0f,
            BloodPressure = 120f,
            HeartRate = 72f,
            BloodViscosity = 0f,
            TemperatureC = 37f,
            Stamina = 100f,
            Hunger = 90f,
            ThoraxMuscleHealth = 100f,
            MetabolismSettingMult = 1.0f,
            CardiacLvOutputLpm = 5.78f
        };

        private static void StepSeconds(BodyRespirationSimulator sim, in RespirationEnvironmentInput env, in RespirationSimConfig cfg, float seconds, float dt = 0.05f)
        {
            float t = 0f;
            while (t < seconds)
            {
                float step = Math.Min(dt, seconds - t);
                sim.Step(step, in env, in cfg);
                t += step;
            }
        }

        private static void Assert(string name, bool condition, string detail)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine($"[PASS] {name}: {detail}");
            }
            else
            {
                _failed++;
                Console.WriteLine($"[FAIL] {name}: {detail}");
            }
        }

        private static int Main()
        {
            var cfg = RespirationSimConfig.Default();

            // 1. Resting equilibrium
            {
                var sim = new BodyRespirationSimulator(1);
                var env = NormalEnv();
                StepSeconds(sim, in env, in cfg, 120f);
                var s = sim.LastTelemetry;
                Assert("Resting Equilibrium",
                    s.ActualRpm >= 13.5f && s.ActualRpm <= 16.5f &&
                    s.PaCO2MmHg >= 38.5f && s.PaCO2MmHg <= 41.5f &&
                    s.BloodPh >= 7.38f && s.BloodPh <= 7.42f &&
                    s.SpO2Percent >= 97.5f,
                    $"RR={s.ActualRpm:0.0}/m PaCO2={s.PaCO2MmHg:0.0} PaO2={s.PaO2MmHg:0.0} pH={s.BloodPh:0.00} SpO2={s.SpO2Percent:0.0}%");
            }

            // 2. High CO2 hypercapnia drives RR up toward 80/m (breaking vanilla 25/m cap)
            {
                var sim = new BodyRespirationSimulator(2);
                var env = NormalEnv();
                sim.AddPaCO2Delta(32f); // 72 mmHg
                StepSeconds(sim, in env, in cfg, 4f);
                var s = sim.LastTelemetry;
                Assert("Hypercapnia Breaks 25/m Cap (<=80/m)",
                    s.MedullaryTargetRpm >= 55f && s.ActualRpm > 35f && s.ActualRpm <= 80f,
                    $"PaCO2={s.PaCO2MmHg:0.0} TargetRR={s.MedullaryTargetRpm:0.0}/m ActualRR={s.ActualRpm:0.0}/m Excit={s.MedullaryExcitability:0.00}");
            }

            // 3. Opiate respiratory depression & Naloxone rebound hyperventilation
            {
                var sim = new BodyRespirationSimulator(3);
                var env = NormalEnv();
                env.OpiateReception = 82f; // Heavy fentanyl/morphine OD
                StepSeconds(sim, in env, in cfg, 35f);
                var od = sim.LastTelemetry;
                bool suppressed = od.ActualRpm < 3.5f && od.PaCO2MmHg > 60f && od.BloodPh < 7.28f;

                // Naloxone clears opiates
                env.OpiateReception = 0f;
                StepSeconds(sim, in env, in cfg, 8f);
                var rebound = sim.LastTelemetry;
                bool rebounded = rebound.ActualRpm > 30f && rebound.MedullaryExcitability > 2.2f;

                Assert("Opiate Arrest + Naloxone Rebound",
                    suppressed && rebounded,
                    $"OD: RR={od.ActualRpm:0.0}/m PaCO2={od.PaCO2MmHg:0.0} pH={od.BloodPh:0.00} | Post-Naloxone: RR={rebound.ActualRpm:0.0}/m Excit={rebound.MedullaryExcitability:0.00}");
            }

            // 4. Nikethamide rescues moderate-heavy opiate depression via medullary excitation
            {
                var sim = new BodyRespirationSimulator(4);
                var env = NormalEnv();
                env.OpiateReception = 65f;
                StepSeconds(sim, in env, in cfg, 25f);
                float preRr = sim.LastTelemetry.ActualRpm;

                sim.Drugs.Administer(RespirationDrugId.Nikethamide, 5f, RespirationAdminRoute.Injectable);
                StepSeconds(sim, in env, in cfg, 15f);
                var post = sim.LastTelemetry;
                Assert("Nikethamide Rescues Opiate Depression",
                    preRr < 6f && post.ActualRpm > preRr + 12f,
                    $"PreRR={preRr:0.0}/m -> PostNikethamide RR={post.ActualRpm:0.0}/m Excit={post.MedullaryExcitability:0.00}");
            }

            // 5. Pulmonary Failure (Hemothorax >= 85%): medulla fires high, but lungs CANNOT breathe (RR = 0)
            {
                var sim = new BodyRespirationSimulator(5);
                var env = NormalEnv();
                env.Hemothorax = 88f;
                StepSeconds(sim, in env, in cfg, 22f);
                var fail = sim.LastTelemetry;
                bool failedProperly = fail.IsPulmonaryFailure &&
                                      fail.ActualRpm <= 0.01f &&
                                      !fail.Breathing &&
                                      fail.MedullaryExcitability > 1.8f &&
                                      fail.PaCO2MmHg > 58f;

                // Even Nikethamide cannot force failed lungs to breathe!
                sim.Drugs.Administer(RespirationDrugId.Nikethamide, 6f, RespirationAdminRoute.Injectable);
                StepSeconds(sim, in env, in cfg, 5f);
                bool stillBlocked = sim.LastTelemetry.ActualRpm <= 0.01f && !sim.LastTelemetry.Breathing;

                // Chest drain relieves hemothorax below hysteresis threshold (< 80%)
                env.Hemothorax = 50f;
                StepSeconds(sim, in env, in cfg, 8f);
                var recovered = sim.LastTelemetry;
                bool resumed = !recovered.IsPulmonaryFailure && recovered.Breathing && recovered.ActualRpm > 25f;

                Assert("Pulmonary Failure (Hemothorax) + ChestDrain Recovery",
                    failedProperly && stillBlocked && resumed,
                    $"Fail: RR={fail.ActualRpm:0.0} MedExcit={fail.MedullaryExcitability:0.00} PaCO2={fail.PaCO2MmHg:0.0} | PostDrain: RR={recovered.ActualRpm:0.0}/m");
            }

            // 6. Metabolic Acidosis Kussmaul Compensation + Sodium Bicarbonate (good vent vs lung failure trap)
            {
                var sim = new BodyRespirationSimulator(6);
                var env = NormalEnv();
                sim.AddBicarbonateDelta(-12f); // HCO3- = 12 mmol/L -> severe metabolic acidosis
                float peakKussmaulRr = 0f;
                for (int k = 0; k < 30; k++)
                {
                    StepSeconds(sim, in env, in cfg, 0.5f);
                    if (sim.LastTelemetry.ActualRpm > peakKussmaulRr)
                        peakKussmaulRr = sim.LastTelemetry.ActualRpm;
                }
                var acid = sim.LastTelemetry;
                bool kussmaul = peakKussmaulRr > 26f && acid.PaCO2MmHg < 30f;

                // NaHCO3 with intact ventilation raises pH back toward normal without trapping CO2
                sim.Drugs.Administer(RespirationDrugId.SodiumBicarbonate, 20f, RespirationAdminRoute.Injectable);
                StepSeconds(sim, in env, in cfg, 25f);
                var postBicarb = sim.LastTelemetry;
                bool corrected = postBicarb.BloodPh >= 7.35f && postBicarb.BloodPh > acid.BloodPh + 0.05f && postBicarb.BicarbonateMmolL > acid.BicarbonateMmolL + 3f;

                // NaHCO3 during pulmonary failure traps released CO2 -> PaCO2 surges!
                var simFail = new BodyRespirationSimulator(61);
                var envFail = NormalEnv();
                envFail.ThoraxMuscleHealth = 2f; // Thorax destroyed -> pulmonary failure
                StepSeconds(simFail, in envFail, in cfg, 10f);
                float co2Before = simFail.PaCO2MmHg;
                simFail.Drugs.Administer(RespirationDrugId.SodiumBicarbonate, 25f, RespirationAdminRoute.Injectable);
                StepSeconds(simFail, in envFail, in cfg, 15f);
                float co2After = simFail.PaCO2MmHg;

                Assert("Metabolic Acidosis Kussmaul + NaHCO3 Good vs Lung-Failure Trap",
                    kussmaul && corrected && (co2After > co2Before + 25f),
                    $"Kussmaul PeakRR={peakKussmaulRr:0.0}/m PaCO2={acid.PaCO2MmHg:0.0} pH={acid.BloodPh:0.00} -> PostNaHCO3 pH={postBicarb.BloodPh:0.00} | LungFail NaHCO3 PaCO2: {co2Before:0.0}->{co2After:0.0}");
            }

            // 7. External Excitability Modifier API (multi-source stacking + timed expiration)
            {
                var sim = new BodyRespirationSimulator(7);
                var env = NormalEnv();
                sim.Modifiers.SetExcitabilityModifier("mod.stim", additiveExcitability: 1.5f, excitabilityMultiplier: 1.0f, durationSec: 10f);
                sim.Modifiers.SetExcitabilityModifier("mod.boost", additiveExcitability: 0f, excitabilityMultiplier: 1.3f, durationSec: -1f);
                StepSeconds(sim, in env, in cfg, 6f);
                float boostedRr = sim.LastTelemetry.ActualRpm;

                StepSeconds(sim, in env, in cfg, 12f); // mod.stim expired, mod.boost remains
                float afterExpireRr = sim.LastTelemetry.ActualRpm;
                bool stimExpired = sim.Modifiers.ActiveExcitabilityModifierCount == 1;

                sim.Modifiers.RemoveExcitabilityModifier("mod.boost");
                StepSeconds(sim, in env, in cfg, 25f);
                float normalRr = sim.LastTelemetry.ActualRpm;

                Assert("External Medullary Excitability API (Stack + Auto-Expire)",
                    boostedRr > 30f && stimExpired && afterExpireRr < boostedRr && normalRr >= 13.5f && normalRr <= 16.5f,
                    $"Boosted={boostedRr:0.0}/m -> PostExpire={afterExpireRr:0.0}/m -> Normal={normalRr:0.0}/m");
            }

            // 8. Soft-compat Neuro: GTCS Tonic Apnea -> Postictal Hyperventilation + BZD+Opiate Synergy
            {
                var sim = new BodyRespirationSimulator(8);
                var env = NormalEnv();
                env.NeuroModPresent = true;
                env.NeuroSeizurePhase = 3; // Tonic apnea + huge VCO2
                StepSeconds(sim, in env, in cfg, 16f);
                var tonic = sim.LastTelemetry;

                env.NeuroSeizurePhase = 5; // Postictal: muscles relax, high CO2 fires medulla!
                StepSeconds(sim, in env, in cfg, 8f);
                var postictal = sim.LastTelemetry;

                Assert("Neuro Compat: GTCS Tonic Apnea -> Postictal Rebound",
                    tonic.ActualRpm < 1f && tonic.PaCO2MmHg > 62f && postictal.ActualRpm > 32f,
                    $"Tonic: RR={tonic.ActualRpm:0.0} PaCO2={tonic.PaCO2MmHg:0.0} | Postictal: RR={postictal.ActualRpm:0.0}/m");
            }

            // 9. Soft-compat Cardiac: VF Arrest without vs with AutoPump + PE Right Ventricular Ischemia
            {
                var sim = new BodyRespirationSimulator(9);
                var env = NormalEnv();
                env.CardiacModPresent = true;
                env.CardiacIsVfOrAsystole = true;
                env.CardiacLvOutputLpm = 0f;
                env.BloodPressure = 12f;
                env.HasAutoPumpWorking = false;
                StepSeconds(sim, in env, in cfg, 20f);
                var vfNoPump = sim.LastTelemetry;

                env.HasAutoPumpWorking = true;
                env.BloodPressure = 78f;
                env.HasPulmonaryEmbolism = true;
                StepSeconds(sim, in env, in cfg, 10f);
                var vfPumpPe = sim.LastOutput;

                Assert("Cardiac Compat: VF Low-Perfusion Arrest + AutoPump + PE RV Ischemia",
                    vfNoPump.PerfusionEff < 0.05f && vfNoPump.ActualRpm < 3f && sim.LastTelemetry.PerfusionEff > 0.35f && vfPumpPe.CardiacRvIschemiaAdd >= 0.45f,
                    $"VF NoPump: Q={vfNoPump.PerfusionEff:0.00} RR={vfNoPump.ActualRpm:0.0} | AutoPump+PE: Q={sim.LastTelemetry.PerfusionEff:0.00} RVIschemia={vfPumpPe.CardiacRvIschemiaAdd:0.00}");
            }

            // 10. 25x Time-Scale Stability (large dt = 0.65s per frame)
            {
                var sim = new BodyRespirationSimulator(10);
                var env = NormalEnv();
                bool valid = true;
                for (int i = 0; i < 300; i++)
                {
                    var o = sim.Step(0.65f, in env, in cfg);
                    if (float.IsNaN(o.ActualRespiratoryRateBpm) || float.IsNaN(sim.PaCO2MmHg) || float.IsNaN(sim.BloodPh))
                    {
                        valid = false;
                        break;
                    }
                }
                var s = sim.LastTelemetry;
                Assert("25x TimeScale Numerical Stability (dt=0.65s)",
                    valid && s.PaCO2MmHg >= 38f && s.PaCO2MmHg <= 42f && s.ActualRpm >= 13.5f && s.ActualRpm <= 16.5f,
                    $"After 195s @ dt=0.65s: RR={s.ActualRpm:0.0}/m PaCO2={s.PaCO2MmHg:0.0} pH={s.BloodPh:0.00}");
            }

            Console.WriteLine($"\nSummary: {_passed} passed, {_failed} failed.");
            return _failed == 0 ? 0 : 1;
        }
    }
}
