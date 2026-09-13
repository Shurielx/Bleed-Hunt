using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace BleedAndHunt
{
#pragma warning disable CS0618
    public static class EntityHelper
    {
        public static bool IsBoss(Entity? entity)
        {
            if (entity == null) return false;
            if (entity.HasTags("boss")) return true;

            string path = entity.Code?.Path?.ToLowerInvariant() ?? "";
            var config = BleedAndHuntModSystem.Config;
            if (config?.BossCodes != null && config.BossCodes.Any(b => path.Contains(b)))
            {
                return true;
            }

            return false;
        }

        public static bool IsAnimal(Entity? entity)
        {
            if (entity == null || entity is EntityPlayer) return false;
            if (entity is not EntityAgent) return false;
            if (IsBoss(entity)) return false;

            string path = entity.Code?.Path?.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(path)) return false;

            // Exclude inanimate objects / mechanical / utility entities
            if (path.StartsWith("boat") || path.StartsWith("projectile") ||
                path.StartsWith("strawdummy") || path.StartsWith("mannequin") ||
                path.StartsWith("fallingblock") || path.StartsWith("elevator") ||
                path.StartsWith("echochamber") || path.StartsWith("bobber"))
            {
                return false;
            }

            // Check tags
            if (entity.HasTags("animal") || entity.HasTags("huntable"))
            {
                return true;
            }

            // Check known animal code substrings
            if (path.StartsWith("chicken") || path.StartsWith("hare") || path.StartsWith("goat") ||
                path.StartsWith("sheep") || path.StartsWith("bighorn") || path.StartsWith("deer") ||
                path.StartsWith("pig") || path.StartsWith("boar") || path.StartsWith("fox") ||
                path.StartsWith("wolf") || path.StartsWith("hyena") || path.StartsWith("bear") ||
                path.StartsWith("raccoon") || path.StartsWith("gazelle") || path.StartsWith("moose") ||
                path.StartsWith("elk") || path.StartsWith("fish") || path.StartsWith("beemob") ||
                path.StartsWith("semitamed") || path.StartsWith("tamed") || path.StartsWith("animalbot"))
            {
                return true;
            }

            // Check typical animal behaviors (mammals/birds in Vintage Story)
            if (entity.HasBehavior("multiply") || entity.HasBehavior("grow") || entity.HasBehavior("tameable"))
            {
                return true;
            }

            return false;
        }
    }
}
