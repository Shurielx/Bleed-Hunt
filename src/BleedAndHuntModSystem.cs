using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace BleedAndHunt
{
    public class BleedAndHuntModSystem : ModSystem
    {
        public static ModConfig Config { get; private set; } = new ModConfig();
        public static BleedAndHuntModSystem? Instance { get; private set; }
        private BleedHighlightRenderer? highlightRenderer;
        private WeaponSweepSystem? weaponSweep;
        private Harmony? harmony;
        private ICoreAPI? coreApi;

        public override double ExecuteOrder() => 0.015;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            Instance = this;
            coreApi = api;

            // Load or create configuration
            try
            {
                Config = api.LoadModConfig<ModConfig>("BleedAndHuntConfig.json");
                if (Config == null)
                {
                    Config = new ModConfig();
                    api.StoreModConfig(Config, "BleedAndHuntConfig.json");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[Bleed & Hunt] Failed to load BleedAndHuntConfig.json, restored defaults. Error: " + ex.Message);
                Config = new ModConfig();
            }

            // Register entity behavior
            api.RegisterEntityBehaviorClass("bleedandlimp", typeof(EntityBehaviorBleedAndLimp));

            // Register /bnh chat command
            RegisterCommands(api);
        }

        private void RegisterCommands(ICoreAPI api)
        {
            api.ChatCommands.Create("bnh")
                .WithDescription("Bleed & Hunt settings and configuration")
                .WithAlias("bleedandhunt")
                .HandleWith(OnBnhMainCommand)
                .BeginSubCommand("help")
                    .WithDescription("Show available Bleed & Hunt commands")
                    .HandleWith(OnBnhHelp)
                .EndSubCommand()
                .BeginSubCommand("xray")
                    .WithDescription("Configure hunter's scent tracking: /bnh xray [animals|monsters|all] [on|off]")
                    .WithAlias("scent")
                    .WithAlias("track")
                    .HandleWith(OnBnhXRay)
                .EndSubCommand()
                .BeginSubCommand("betterrange")
                    .WithDescription("Toggle melee lateral sweep assist: /bnh betterrange [on|off]")
                    .WithAlias("sweep")
                    .HandleWith(OnBnhBetterRange)
                .EndSubCommand()
                .BeginSubCommand("bleeding")
                    .WithDescription("Configure bleeding profile: /bnh bleeding [default|subtle|bloodlust|on|off]")
                    .HandleWith(OnBnhBleeding)
                .EndSubCommand()
                .BeginSubCommand("opacity")
                    .WithDescription("Set scent outline opacity: /bnh opacity [5-100]")
                    .HandleWith(OnBnhOpacity)
                .EndSubCommand()
                .BeginSubCommand("falx")
                    .WithDescription("Configure falx bonuses: /bnh falx [bleed|range|both] [on|off]")
                    .HandleWith(OnBnhFalx)
                .EndSubCommand()
                .BeginSubCommand("stealth")
                    .WithDescription("Configure realistic sneaking & animal FOV: /bnh stealth [on|off]")
                    .WithAlias("sneak")
                    .HandleWith(OnBnhStealth)
                .EndSubCommand();
        }

        private TextCommandResult OnBnhMainCommand(TextCommandCallingArgs args)
        {
            return OnBnhHelp(args);
        }

        private TextCommandResult OnBnhHelp(TextCommandCallingArgs args)
        {
            string xrayState = Config.EnableXRay ? "ON" : "OFF";
            string rangeState = Config.EnableBetterRange ? "ON" : "OFF";
            string bleedState = Config.EnableBleeding ? Config.BleedingPreset.ToUpperInvariant() : "OFF";
            string falxBleedState = Config.EnableFalxBleedBonus ? "ON" : "OFF";
            string falxRangeState = Config.EnableFalxRangeBoost ? "ON" : "OFF";
            string stealthState = Config.EnableStealthMechanics ? "ON" : "OFF";
            int opacityPercent = (int)Math.Round(Config.XRayColorA * 100f);

            string msg = $"[Bleed & Hunt v1.1.0] Settings & Status:\n" +
                         $"• Hunter's Scent: {xrayState} (Targeting: {Config.XRayTargetFilter}, Range: {Config.XRayMaxDistance}m, Opacity: {opacityPercent}%)\n" +
                         $"• Realistic Stealth: {stealthState} (Cone: {Config.StealthFovDegrees}°, Crouch Hearing: {Config.SneakHearingRadius}m, Raycast: {(Config.EnableLineOfSightRaycast ? "ON" : "OFF")})\n" +
                         $"• Melee Sweep: {rangeState} (Assist Angle: {Config.BetterRangeSweepAngle}°)\n" +
                         $"• Falx Synergy: Bleed Bonus: {falxBleedState}, Reach Boost: {falxRangeState}\n" +
                         $"• Bleed Damage: {bleedState}\n\n" +
                         $"Commands:\n" +
                         $"  /bnh stealth [on|off]  (or /bnh sneak) - Toggle realistic stealth & animal FOV\n" +
                         $"  /bnh xray [animals|monsters|all] [on|off]  (or /bnh scent) - Hunter's scent tracking\n" +
                         $"  /bnh betterrange [on|off]  (or /bnh sweep) - Melee attack assist cone\n" +
                         $"  /bnh falx [bleed|range|both] [on|off] - Configure falx weapon bonuses\n" +
                         $"  /bnh bleeding [default|subtle|bloodlust|on|off] - Adjust bleed intensity\n" +
                         $"  /bnh opacity [5-100] - Adjust scent aura visibility";

            return TextCommandResult.Success(msg);
        }

        private TextCommandResult OnBnhXRay(TextCommandCallingArgs args)
        {
            string? arg1 = args.RawArgs.PopWord()?.ToLowerInvariant();
            string? arg2 = args.RawArgs.PopWord()?.ToLowerInvariant();

            if (string.IsNullOrEmpty(arg1))
            {
                Config.EnableXRay = !Config.EnableXRay;
            }
            else
            {
                if (arg1 == "on" || arg1 == "true") Config.EnableXRay = true;
                else if (arg1 == "off" || arg1 == "false") Config.EnableXRay = false;
                else if (arg1 == "animals" || arg1 == "monsters" || arg1 == "all")
                {
                    Config.XRayTargetFilter = arg1;
                    if (arg2 == "on" || arg2 == "true") Config.EnableXRay = true;
                    else if (arg2 == "off" || arg2 == "false") Config.EnableXRay = false;
                }
                else
                {
                    return TextCommandResult.Error("Usage: /bnh xray [animals|monsters|all] [on|off]");
                }
            }

            SaveConfig();
            return TextCommandResult.Success($"[Bleed & Hunt] Hunter's scent is now {(Config.EnableXRay ? "ENABLED" : "DISABLED")} (Target filter: {Config.XRayTargetFilter}).");
        }

        private TextCommandResult OnBnhBetterRange(TextCommandCallingArgs args)
        {
            string? arg = args.RawArgs.PopWord()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(arg))
            {
                Config.EnableBetterRange = !Config.EnableBetterRange;
            }
            else if (arg == "on" || arg == "true")
            {
                Config.EnableBetterRange = true;
            }
            else if (arg == "off" || arg == "false")
            {
                Config.EnableBetterRange = false;
            }
            else
            {
                return TextCommandResult.Error("Usage: /bnh betterrange [on|off]");
            }

            SaveConfig();
            return TextCommandResult.Success($"[Bleed & Hunt] Melee sweep assist is now {(Config.EnableBetterRange ? "ENABLED" : "DISABLED")}.");
        }

        private TextCommandResult OnBnhBleeding(TextCommandCallingArgs args)
        {
            string? arg = args.RawArgs.PopWord()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(arg))
            {
                return TextCommandResult.Error("Usage: /bnh bleeding [default|subtle|bloodlust|on|off]");
            }

            switch (arg)
            {
                case "off":
                case "false":
                    Config.EnableBleeding = false;
                    break;
                case "on":
                case "true":
                case "default":
                    Config.EnableBleeding = true;
                    Config.BleedingPreset = "default";
                    break;
                case "subtle":
                    Config.EnableBleeding = true;
                    Config.BleedingPreset = "subtle";
                    break;
                case "bloodlust":
                    Config.EnableBleeding = true;
                    Config.BleedingPreset = "bloodlust";
                    break;
                default:
                    return TextCommandResult.Error("Invalid preset. Choose: default, subtle, bloodlust, on, or off.");
            }

            SaveConfig();
            string status = Config.EnableBleeding ? $"ENABLED ({Config.BleedingPreset})" : "DISABLED";
            return TextCommandResult.Success($"[Bleed & Hunt] Bleeding is now {status}.");
        }

        private TextCommandResult OnBnhOpacity(TextCommandCallingArgs args)
        {
            string? raw = args.RawArgs.PopWord();
            if (string.IsNullOrEmpty(raw))
            {
                return TextCommandResult.Error("Usage: /bnh opacity [5-100] (e.g. /bnh opacity 20)");
            }

            raw = raw.TrimEnd('%');
            if (!int.TryParse(raw, out int val))
            {
                return TextCommandResult.Error("Usage: /bnh opacity [5-100] (e.g. /bnh opacity 20)");
            }

            float alpha = Math.Clamp(val / 100f, 0.05f, 1.0f);
            Config.XRayColorA = alpha;
            SaveConfig();

            return TextCommandResult.Success($"[Bleed & Hunt] Scent outline opacity set to {(int)Math.Round(alpha * 100f)}%.");
        }

        private TextCommandResult OnBnhFalx(TextCommandCallingArgs args)
        {
            string? arg1 = args.RawArgs.PopWord()?.ToLowerInvariant();
            string? arg2 = args.RawArgs.PopWord()?.ToLowerInvariant();

            if (string.IsNullOrEmpty(arg1))
            {
                Config.EnableFalxBleedBonus = !Config.EnableFalxBleedBonus;
                SaveConfig();
                string state = Config.EnableFalxBleedBonus ? "ENABLED" : "DISABLED";
                return TextCommandResult.Success($"[Bleed & Hunt] Falx bleed bonus is now {state}. (Reach boost: {(Config.EnableFalxRangeBoost ? "ENABLED" : "DISABLED")})");
            }

            if (arg1 == "on" || arg1 == "true")
            {
                Config.EnableFalxBleedBonus = true;
                SaveConfig();
                return TextCommandResult.Success("[Bleed & Hunt] Falx bleed bonus is now ENABLED.");
            }
            if (arg1 == "off" || arg1 == "false")
            {
                Config.EnableFalxBleedBonus = false;
                SaveConfig();
                return TextCommandResult.Success("[Bleed & Hunt] Falx bleed bonus is now DISABLED.");
            }

            if (arg1 == "bleed")
            {
                if (arg2 == "on" || arg2 == "true") Config.EnableFalxBleedBonus = true;
                else if (arg2 == "off" || arg2 == "false") Config.EnableFalxBleedBonus = false;
                else Config.EnableFalxBleedBonus = !Config.EnableFalxBleedBonus;

                SaveConfig();
                return TextCommandResult.Success($"[Bleed & Hunt] Falx bleed bonus is now {(Config.EnableFalxBleedBonus ? "ENABLED" : "DISABLED")}.");
            }

            if (arg1 == "range" || arg1 == "reach")
            {
                if (arg2 == "on" || arg2 == "true") Config.EnableFalxRangeBoost = true;
                else if (arg2 == "off" || arg2 == "false") Config.EnableFalxRangeBoost = false;
                else Config.EnableFalxRangeBoost = !Config.EnableFalxRangeBoost;

                SaveConfig();
                return TextCommandResult.Success($"[Bleed & Hunt] Falx reach boost is now {(Config.EnableFalxRangeBoost ? "ENABLED" : "DISABLED")}. (Note: Game world reload may be required for reach change)");
            }

            if (arg1 == "both" || arg1 == "all")
            {
                bool target = (arg2 == "on" || arg2 == "true") ? true : ((arg2 == "off" || arg2 == "false") ? false : !Config.EnableFalxBleedBonus);
                Config.EnableFalxBleedBonus = target;
                Config.EnableFalxRangeBoost = target;
                SaveConfig();
                return TextCommandResult.Success($"[Bleed & Hunt] All Falx bonuses are now {(target ? "ENABLED" : "DISABLED")}.");
            }

            return TextCommandResult.Error("Usage: /bnh falx [on|off] or /bnh falx [bleed|range|both] [on|off]");
        }

        private TextCommandResult OnBnhStealth(TextCommandCallingArgs args)
        {
            string? arg = args.RawArgs.PopWord()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(arg))
            {
                Config.EnableStealthMechanics = !Config.EnableStealthMechanics;
            }
            else if (arg == "on" || arg == "true")
            {
                Config.EnableStealthMechanics = true;
            }
            else if (arg == "off" || arg == "false")
            {
                Config.EnableStealthMechanics = false;
            }
            else
            {
                return TextCommandResult.Error("Usage: /bnh stealth [on|off]");
            }

            SaveConfig();
            string state = Config.EnableStealthMechanics ? "ENABLED" : "DISABLED";
            return TextCommandResult.Success($"[Bleed & Hunt] Realistic stealth & animal FOV mechanics are now {state}.");
        }

        public static void SaveConfig()
        {
            try
            {
                Instance?.coreApi?.StoreModConfig(Config, "BleedAndHuntConfig.json");
            }
            catch (Exception ex)
            {
                Instance?.coreApi?.Logger.Warning("[Bleed & Hunt] Could not save BleedAndHuntConfig.json: " + ex.Message);
            }
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            // 1. Apply falx weapon reach boost to all falx variants (blade-falx-*)
            if (Config.EnableFalxRangeBoost && api.World.Items != null)
            {
                int patchedCount = 0;
                foreach (var item in api.World.Items)
                {
                    if (item?.Code == null) continue;
                    if (item.Code.Path.StartsWith("blade-falx-"))
                    {
                        item.AttackRange = Config.FalxAttackRange;
                        patchedCount++;
                    }
                }

                if (patchedCount > 0 && api.Side == EnumAppSide.Server)
                {
                    api.Logger.Notification($"[Bleed & Hunt] Extended attack reach for {patchedCount} falx variants to {Config.FalxAttackRange}m");
                }
            }

            if (api.Side != EnumAppSide.Server) return;

            // 2. Attach behavior to eligible EntityTypes definitions on server
            try
            {
                foreach (var entityType in api.World.EntityTypes)
                {
                    if (entityType?.Server?.BehaviorsAsJsonObj == null) continue;
                    string code = entityType.Code?.Path?.ToLowerInvariant() ?? "";

                    // Exclude inanimate objects and mechanical entities
                    if (code.StartsWith("boat-") || code.StartsWith("projectile-") ||
                        code.StartsWith("strawdummy") || code.StartsWith("mannequin-") ||
                        code.StartsWith("fallingblock") || code.StartsWith("elevator"))
                    {
                        continue;
                    }

                    bool hasHealth = entityType.Server.BehaviorsAsJsonObj.Any(b => b["code"]?.AsString() == "health");
                    if (hasHealth)
                    {
                        bool alreadyHas = entityType.Server.BehaviorsAsJsonObj.Any(b => b["code"]?.AsString() == "bleedandlimp");
                        if (!alreadyHas)
                        {
                            var list = entityType.Server.BehaviorsAsJsonObj.ToList();
                            list.Add(new JsonObject(JObject.Parse("{\"code\": \"bleedandlimp\"}")));
                            entityType.Server.BehaviorsAsJsonObj = list.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[Bleed & Hunt] Error attaching behavior to entity types: " + ex.Message);
            }
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            base.StartServerSide(sapi);

            // Apply Harmony patches for realistic stealth & FOV mechanics
            try
            {
                harmony = new Harmony("bleedandhunt");
                StealthPatch.ApplyPatches(harmony, sapi.Logger);
            }
            catch (Exception ex)
            {
                sapi.Logger.Error("[Bleed & Hunt] Failed to initialize Harmony stealth patches: " + ex.Message);
            }

            // Fallback safety: dynamically attach behavior when entities are loaded or spawned
            sapi.Event.OnEntityLoaded += EnsureBehaviorAttached;
            sapi.Event.OnEntitySpawn += EnsureBehaviorAttached;
        }

        private void EnsureBehaviorAttached(Entity entity)
        {
            if (entity == null) return;
            if (entity.GetBehavior<EntityBehaviorHealth>() != null && entity.GetBehavior<EntityBehaviorBleedAndLimp>() == null)
            {
                string code = entity.Code?.Path?.ToLowerInvariant() ?? "";
                if (code.StartsWith("boat-") || code.StartsWith("projectile-") ||
                    code.StartsWith("strawdummy") || code.StartsWith("mannequin-"))
                {
                    return;
                }

                var behavior = new EntityBehaviorBleedAndLimp(entity);
                behavior.Initialize(entity.Properties, new JsonObject(new JObject()));
                entity.AddBehavior(behavior);
            }
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            base.StartClientSide(capi);

            highlightRenderer = new BleedHighlightRenderer(capi);
            capi.Event.RegisterRenderer(highlightRenderer, EnumRenderStage.AfterFinalComposition, "bleedxray");

            weaponSweep = new WeaponSweepSystem(capi);
        }

        public override void Dispose()
        {
            if (highlightRenderer != null)
            {
                if (coreApi is ICoreClientAPI capi)
                {
                    capi.Event.UnregisterRenderer(highlightRenderer, EnumRenderStage.AfterFinalComposition);
                }
                highlightRenderer.Dispose();
                highlightRenderer = null;
            }

            weaponSweep?.Dispose();
            weaponSweep = null;

            try
            {
                harmony?.UnpatchAll("bleedandhunt");
                harmony = null;
            }
            catch {}

            base.Dispose();
        }
    }
}
