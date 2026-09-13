using System;
using System.Reflection;
using Vintagestory.API.Common;

namespace BleedAndHunt
{
    /// <summary>
    /// Optional integration module for the ConfigKit mod (https://mods.vintagestory.at/configkit).
    /// Provides an in-game graphical settings screen (GUI) when ConfigKit is installed.
    ///
    /// [INDEPENDENT MODULE]
    /// This module uses reflection only (no hard dependencies). If you do not want ConfigKit
    /// support, you can safely delete this file without modifying any other part of the codebase.
    /// </summary>
    public class ConfigKitIntegrationSystem : ModSystem
    {
        // Execute slightly after ConfigKit (0.01) and BleedAndHuntModSystem (0.015)
        public override double ExecuteOrder() => 0.02;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            TryRegisterWithConfigKit(api);
        }

        private void TryRegisterWithConfigKit(ICoreAPI api)
        {
            try
            {
                if (api.ModLoader == null) return;

                // 1. Locate ConfigKitModSystem among loaded systems
                object? configKitSystem = null;
                foreach (var system in api.ModLoader.Systems)
                {
                    if (system == null) continue;
                    string typeName = system.GetType().FullName ?? "";
                    if (typeName == "ConfigKit.ConfigKitModSystem" || typeName.EndsWith(".ConfigKitModSystem"))
                    {
                        configKitSystem = system;
                        break;
                    }
                }

                if (configKitSystem == null)
                {
                    // ConfigKit is not loaded; integration silently sleeps
                    return;
                }

                // 2. Find RegisterManagedConfig method
                MethodInfo? registerMethod = configKitSystem.GetType().GetMethod("RegisterManagedConfig");
                if (registerMethod == null)
                {
                    api.Logger.VerboseDebug("[Bleed & Hunt] ConfigKit found, but RegisterManagedConfig was not found.");
                    return;
                }

                // 3. Define event callbacks for GUI updates
                Action onSyncedFromServer = () =>
                {
                    api.Logger.Notification("[Bleed & Hunt] Config synchronized from server via ConfigKit.");
                };

                Action<string> onSettingChanged = (settingName) =>
                {
                    api.Logger.Notification($"[Bleed & Hunt] Config setting '{settingName}' changed via ConfigKit GUI.");
                };

                Action onConfigSaved = () =>
                {
                    api.Logger.Notification("[Bleed & Hunt] Settings saved via ConfigKit GUI.");
                    BleedAndHuntModSystem.SaveConfig();
                };

                // 4. Register Bleed & Hunt configuration with ConfigKit
                // Signature: RegisterManagedConfig(string domain, object configObject, string path, Action onSyncedFromServer, Action<string> onSettingChanged, Action onConfigSaved)
                registerMethod.Invoke(configKitSystem, new object?[]
                {
                    "bleedandhunt",
                    BleedAndHuntModSystem.Config,
                    "BleedAndHuntConfig.json",
                    onSyncedFromServer,
                    onSettingChanged,
                    onConfigSaved
                });

                api.Logger.Notification("[Bleed & Hunt] ConfigKit GUI integration active: Settings are configurable in-game via ConfigKit!");
            }
            catch (Exception ex)
            {
                // Never crash the game or mod if optional integration fails
                api.Logger.VerboseDebug($"[Bleed & Hunt] Optional ConfigKit integration skipped: {ex.Message}");
            }
        }
    }
}
