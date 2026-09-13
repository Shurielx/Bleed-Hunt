using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace BleedAndHunt
{
#pragma warning disable CS0618
    public class EntityBehaviorBleedAndLimp : EntityBehavior
    {
        private float bleedSecondsRemaining = 0f;
        private float xraySecondsRemaining = 0f;
        private float deadHighlightSecondsRemaining = 0f;
        private float currentBleedDps = 0f;
        private string hunterUid = "";
        private Entity? attackerEntity = null;

        private float tickAccumulator = 0f;
        private float limpCheckAccumulator = 0f;
        private float particleAccumulator = 0f;
        private float currentSlowFactor = 1.0f;
        private float previousSlowFactor = 1.0f;

        private bool isAnimal = false;
        private bool isBoss = false;

        // Pre-allocated reusable particle properties to eliminate GC heap churn
        private SimpleParticleProperties? bloodDropParticle;

        public EntityBehaviorBleedAndLimp(Entity entity) : base(entity)
        {
            isBoss = EntityHelper.IsBoss(entity);
            isAnimal = EntityHelper.IsAnimal(entity);
        }

        public override string PropertyName() => "bleedandlimp";

        public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
        {
            base.Initialize(properties, typeAttributes);
            isBoss = EntityHelper.IsBoss(entity);
            isAnimal = EntityHelper.IsAnimal(entity);

            // Clean up any stale tracking attributes saved to disk from past game sessions
            if (entity.World.Side == EnumAppSide.Server && entity.WatchedAttributes != null)
            {
                if (entity.WatchedAttributes.HasAttribute("bleedHunterUid") &&
                    !string.IsNullOrEmpty(entity.WatchedAttributes.GetString("bleedHunterUid", "")))
                {
                    ClearBleeding();
                }
            }
        }

        public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
        {
            base.OnEntityReceiveDamage(damageSource, ref damage);

            // Bleed and damage logic executes only on server side
            if (entity.World.Side != EnumAppSide.Server) return;

            // Prevent self-inflicted recursive bleed damage loops and avoid re-applying execute bonus to bleed ticks
            if (damageSource.Source == EnumDamageSource.Bleed) return;

            // Execute phase: below 50% health, wounded target receives 1.25x increased damage from direct hits
            if (isAnimal && !isBoss)
            {
                var healthBehavior = entity.GetBehavior<EntityBehaviorHealth>();
                if (healthBehavior != null && healthBehavior.MaxHealth > 0f)
                {
                    float ratio = healthBehavior.Health / healthBehavior.MaxHealth;
                    if (ratio <= 0.50f)
                    {
                        damage *= 1.25f;
                    }
                }
            }

            var config = BleedAndHuntModSystem.Config;
            if (config == null || !config.EnableBleeding) return;

            // Identify attacker (player)
            Entity? cause = damageSource.GetCauseEntity() ?? damageSource.SourceEntity;
            if (cause is not EntityPlayer player)
            {
                // Check if damage originated from a player-fired projectile
                if (damageSource.SourceEntity is EntityProjectile proj && proj.FiredBy is EntityPlayer firedPlayer)
                {
                    player = firedPlayer;
                    cause = firedPlayer;
                }
                else
                {
                    return; // Damage not caused by player
                }
            }

            attackerEntity = cause;
            hunterUid = player.PlayerUID;

            // Inspect weapon in hand
            string weaponCode = "";
            ItemSlot? activeSlot = player.RightHandItemSlot;
            if (activeSlot?.Itemstack?.Collectible != null)
            {
                weaponCode = activeSlot.Itemstack.Collectible.Code?.Path?.ToLowerInvariant() ?? "";
            }

            // Determine weapon material tier
            int tier = DetermineWeaponTier(damageSource, player, weaponCode);

            float dpsForTier = GetDpsForTier(tier, config);
            float durationForTier = GetDurationForTier(tier, config);

            // Falx weapon synergy: +50% bleed DPS (uses standard tier duration)
            if (config.EnableFalxBleedBonus && weaponCode.Contains("falx"))
            {
                dpsForTier *= config.FalxBleedMultiplier;
            }

            // Presets: subtle = 50% (0.50f), default = 100% (1.00f), bloodlust = 150% (1.50f)
            float presetMultiplier = 1.0f;
            string preset = config.BleedingPreset?.ToLowerInvariant() ?? "default";
            if (preset == "subtle") presetMultiplier = 0.50f;
            else if (preset == "bloodlust") presetMultiplier = 1.50f;

            float newDps = dpsForTier * presetMultiplier;
            currentBleedDps = config.EnableBleeding ? Math.Max(currentBleedDps, newDps) : 0f;

            // Bleed duration does not stack infinitely; it refreshes/extends to weapon tier duration (e.g. 4s for T2)
            bleedSecondsRemaining = Math.Max(bleedSecondsRemaining, durationForTier);

            // Reset X-Ray duration to exactly 30 seconds from this strike
            xraySecondsRemaining = config.XRayDurationSeconds;

            // Immediate limping update upon receiving damage
            if (config.EnableLimping && isAnimal && !isBoss)
            {
                UpdateAnimalLimping(config);
            }

            // Synchronize state to client via WatchedAttributes
            entity.WatchedAttributes.SetString("bleedHunterUid", hunterUid);
            entity.WatchedAttributes.SetFloat("bleedSecondsLeft", xraySecondsRemaining);
            entity.WatchedAttributes.SetBool("bleedIsAnimal", isAnimal && !isBoss);
            entity.WatchedAttributes.SetBool("bleedIsDead", false);
            entity.WatchedAttributes.MarkPathDirty("bleedHunterUid");
            entity.WatchedAttributes.MarkPathDirty("bleedSecondsLeft");
            entity.WatchedAttributes.MarkPathDirty("bleedIsAnimal");
            entity.WatchedAttributes.MarkPathDirty("bleedIsDead");
        }

        public override void OnGameTick(float deltaTime)
        {
            base.OnGameTick(deltaTime);

            if (entity.World.Side != EnumAppSide.Server) return;

            // FAST IDLE PATH: If entity is not wounded, not bleeding, not tracked, and not limping, skip all tick work!
            // This eliminates 99.9% of tick overhead for ambient/peaceful mobs in loaded chunks.
            if (bleedSecondsRemaining <= 0f && xraySecondsRemaining <= 0f && deadHighlightSecondsRemaining <= 0f && previousSlowFactor >= 1.0f)
            {
                return;
            }

            var config = BleedAndHuntModSystem.Config;
            if (config == null) return;

            // Handle post-mortem corpse highlighting for hunter (10 seconds after death)
            if (deadHighlightSecondsRemaining > 0f)
            {
                deadHighlightSecondsRemaining -= deltaTime;
                if (deadHighlightSecondsRemaining <= 0f)
                {
                    ClearBleeding();
                }
                return;
            }

            if (!entity.Alive) return;

            // 1. Update animal limping speed penalty (animals only, excluding bosses)
            if (config.EnableLimping && isAnimal && !isBoss)
            {
                limpCheckAccumulator += deltaTime;
                if (limpCheckAccumulator >= 0.25f)
                {
                    limpCheckAccumulator = 0f;
                    UpdateAnimalLimping(config);
                }
            }

            // 2. Update X-Ray tracking duration (lasts 30s from last hit)
            if (xraySecondsRemaining > 0f)
            {
                xraySecondsRemaining -= deltaTime;
                if (xraySecondsRemaining <= 0f)
                {
                    entity.WatchedAttributes.SetFloat("bleedSecondsLeft", 0f);
                    entity.WatchedAttributes.MarkPathDirty("bleedSecondsLeft");
                }
            }

            // 3. Process bleed damage tick & blood trail droplets
            if (bleedSecondsRemaining > 0f)
            {
                // Spawn subtle blood droplets on the floor forming a visible trail
                if (config.EnableBloodParticles && entity.Alive)
                {
                    particleAccumulator += deltaTime;
                    if (particleAccumulator >= 0.75f)
                    {
                        particleAccumulator = 0f;
                        SpawnBloodDroplet();
                    }
                }

                tickAccumulator += deltaTime;
                if (tickAccumulator >= 1.0f)
                {
                    tickAccumulator = 0f;
                    bleedSecondsRemaining -= 1.0f;

                    // Sync seconds left periodically (once per sec) instead of every tick (60/s)
                    if (xraySecondsRemaining > 0f)
                    {
                        entity.WatchedAttributes.SetFloat("bleedSecondsLeft", Math.Max(0f, xraySecondsRemaining));
                        entity.WatchedAttributes.MarkPathDirty("bleedSecondsLeft");
                    }

                    if (currentBleedDps > 0f && entity.Alive)
                    {
                        float tickDmg = currentBleedDps;
                        var health = entity.GetBehavior<EntityBehaviorHealth>();
                        if (health != null && health.MaxHealth > 0f && (health.Health / health.MaxHealth) <= 0.50f)
                        {
                            tickDmg *= 1.25f; // Below 50% health receives 1.25x damage
                        }

                        var bleedDmgSource = new DamageSource
                        {
                            Source = EnumDamageSource.Bleed,
                            Type = EnumDamageType.Injury,
                            SourceEntity = attackerEntity,
                            CauseEntity = attackerEntity,
                            IgnoreInvFrames = true,
                            TicksPerDuration = 2,
                            KnockbackStrength = 0f
                        };

                        entity.ReceiveDamage(bleedDmgSource, tickDmg);
                    }
                }
            }

            // Only clear bleeding if this entity was actually wounded / tracked
            if (xraySecondsRemaining <= 0f && bleedSecondsRemaining <= 0f && !string.IsNullOrEmpty(hunterUid))
            {
                ClearBleeding();
            }
        }

        private void SpawnBloodDroplet()
        {
            var box = entity.SelectionBox ?? entity.CollisionBox;
            double midY = box != null ? box.Y2 * 0.35 : 0.3;
            Vec3d pos = entity.Pos.XYZ.AddCopy(0, midY, 0);

            if (bloodDropParticle == null)
            {
                bloodDropParticle = new SimpleParticleProperties(
                    1f, 2f,
                    ColorUtil.ToRgba(230, 160, 15, 20),
                    pos,
                    new Vec3d(0.2, 0.1, 0.2),
                    new Vec3f(-0.05f, -0.2f, -0.05f),
                    new Vec3f(0.1f, 0.1f, 0.1f),
                    8.0f, // stays on the floor for 8 seconds
                    1.0f, // gravity pulls to floor
                    0.14f, 0.20f,
                    EnumParticleModel.Cube
                )
                {
                    WithTerrainCollision = true,
                    Bounciness = 0f,
                    LightEmission = 1
                };
            }
            else
            {
                bloodDropParticle.MinPos = pos;
            }

            entity.World.SpawnParticles(bloodDropParticle);
        }

        private void UpdateAnimalLimping(ModConfig config)
        {
            if (entity is not EntityAgent agent) return;

            var healthBehavior = entity.GetBehavior<EntityBehaviorHealth>();
            if (healthBehavior == null) return;

            float maxHp = healthBehavior.MaxHealth;
            if (maxHp <= 0f) return;

            float currentHp = healthBehavior.Health;
            float ratio = currentHp / maxHp;

            // Limping penalty based on configurable health thresholds
            if (ratio <= config.LimpThresholdLow)
            {
                currentSlowFactor = config.LimpSpeedLow;
            }
            else if (ratio <= config.LimpThresholdHigh && config.LimpThresholdHigh > config.LimpThresholdLow)
            {
                float t = (ratio - config.LimpThresholdLow) / (config.LimpThresholdHigh - config.LimpThresholdLow);
                currentSlowFactor = config.LimpSpeedLow + t * (config.LimpSpeedHigh - config.LimpSpeedLow);
            }
            else
            {
                currentSlowFactor = 1.0f;
            }

            if (currentSlowFactor < 1.0f)
            {
                agent.Stats.Set("walkspeed", "injuryslow", currentSlowFactor - 1.0f, false);
            }
            else if (previousSlowFactor < 1.0f)
            {
                agent.Stats.Remove("walkspeed", "injuryslow");
            }
            previousSlowFactor = currentSlowFactor;
        }

        private void ClearBleeding()
        {
            currentSlowFactor = 1.0f;
            previousSlowFactor = 1.0f;
            bleedSecondsRemaining = 0f;
            xraySecondsRemaining = 0f;
            deadHighlightSecondsRemaining = 0f;
            currentBleedDps = 0f;
            hunterUid = "";
            attackerEntity = null;

            if (entity is EntityAgent agent)
            {
                agent.Stats.Remove("walkspeed", "injuryslow");
            }

            if (entity?.WatchedAttributes != null)
            {
                entity.WatchedAttributes.SetString("bleedHunterUid", "");
                entity.WatchedAttributes.SetFloat("bleedSecondsLeft", 0f);
                entity.WatchedAttributes.SetFloat("bleedDeadSecondsLeft", 0f);
                entity.WatchedAttributes.SetBool("bleedIsAnimal", false);
                entity.WatchedAttributes.SetBool("bleedIsDead", false);
                entity.WatchedAttributes.MarkPathDirty("bleedHunterUid");
                entity.WatchedAttributes.MarkPathDirty("bleedSecondsLeft");
                entity.WatchedAttributes.MarkPathDirty("bleedDeadSecondsLeft");
                entity.WatchedAttributes.MarkPathDirty("bleedIsAnimal");
                entity.WatchedAttributes.MarkPathDirty("bleedIsDead");
            }
        }

        public override void OnEntityDeath(DamageSource damageSourceForDeath)
        {
            base.OnEntityDeath(damageSourceForDeath);

            currentSlowFactor = 1.0f;
            if (entity is EntityAgent agent)
            {
                agent.Stats.Remove("walkspeed", "injuryslow");
            }

            var config = BleedAndHuntModSystem.Config;
            // If wounded game dies, highlight the corpse for hunter retrieval
            if (isAnimal && !string.IsNullOrEmpty(hunterUid) && config != null && config.EnableCorpseHighlight && config.CorpseHighlightDurationSeconds > 0)
            {
                bleedSecondsRemaining = 0f;
                xraySecondsRemaining = 0f;
                deadHighlightSecondsRemaining = config.CorpseHighlightDurationSeconds;

                if (entity?.WatchedAttributes != null)
                {
                    entity.WatchedAttributes.SetFloat("bleedSecondsLeft", 0f);
                    entity.WatchedAttributes.SetBool("bleedIsDead", true);
                    entity.WatchedAttributes.SetFloat("bleedDeadSecondsLeft", deadHighlightSecondsRemaining);
                    entity.WatchedAttributes.MarkPathDirty("bleedSecondsLeft");
                    entity.WatchedAttributes.MarkPathDirty("bleedIsDead");
                    entity.WatchedAttributes.MarkPathDirty("bleedDeadSecondsLeft");
                }
            }
            else
            {
                ClearBleeding();
            }
        }

        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            base.OnEntityDespawn(despawn);

            currentSlowFactor = 1.0f;
            if (entity is EntityAgent agent)
            {
                agent.Stats.Remove("walkspeed", "injuryslow");
            }
        }

        private int DetermineWeaponTier(DamageSource damageSource, EntityPlayer player, string weaponCode)
        {
            // If engine already provided DamageTier in DamageSource
            if (damageSource.DamageTier > 0)
            {
                return damageSource.DamageTier;
            }

            // Projectile damage (spear / arrow)
            if (damageSource.SourceEntity is EntityProjectile projectile)
            {
                if (projectile.DamageTier > 0) return projectile.DamageTier;
                string projCode = projectile.Code?.Path?.ToLowerInvariant() ?? "";
                return GetTierFromCode(projCode);
            }

            // Melee weapon held in active hand
            if (!string.IsNullOrEmpty(weaponCode))
            {
                return GetTierFromCode(weaponCode);
            }

            return 0;
        }

        private int GetTierFromCode(string code)
        {
            if (code.Contains("steel")) return 4;
            if (code.Contains("iron") || code.Contains("meteoriciron")) return 3;
            if (code.Contains("bronze")) return 2;
            if (code.Contains("copper") || code.Contains("scrap")) return 1;
            return 0; // Flint, stone, wood, bone
        }

        private float GetDpsForTier(int tier, ModConfig config)
        {
            return tier switch
            {
                0 => config.Tier0DamagePerSecond,
                1 => config.Tier1DamagePerSecond,
                2 => config.Tier2DamagePerSecond,
                3 => config.Tier3DamagePerSecond,
                4 => config.Tier4DamagePerSecond,
                _ => config.Tier5DamagePerSecond
            };
        }

        private float GetDurationForTier(int tier, ModConfig config)
        {
            return tier switch
            {
                0 => config.Tier0DurationSeconds,
                1 => config.Tier1DurationSeconds,
                2 => config.Tier2DurationSeconds,
                3 => config.Tier3DurationSeconds,
                4 => config.Tier4DurationSeconds,
                _ => config.Tier5DurationSeconds
            };
        }
    }
}
