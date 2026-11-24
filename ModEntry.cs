using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Tools;
using StardewValley.TerrainFeatures;
using StardewValley.Objects;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace AutoAnimationCancel
{
    public class ModConfig
    {
        public KeybindList CancelKey { get; set; } = KeybindList.Parse("Space");

        /// <summary>Extra milliseconds to keep the post-hit pose before cancelling (0 = cancel on the tick right after the hit).</summary>
        public int PostHitDelayMs { get; set; } = 0;

        /// <summary>Queue a press made mid-swing so the next swing starts the moment the cancel happens.</summary>
        public bool BufferInput { get; set; } = true;

        /// <summary>Only let the key swing when the targeted tile has something that tool can actually work on.</summary>
        public bool GuardMisclicks { get; set; } = true;

        public bool EnableAxe { get; set; } = true;
        public bool EnablePickaxe { get; set; } = true;
        public bool EnableHoe { get; set; } = true;
        public bool EnableWateringCan { get; set; } = true;

        /// <summary>Melee weapons: key acts as a normal attack click (vanilla combo/cancel rules apply).</summary>
        public bool EnableMeleeWeapons { get; set; } = true;
    }

    public class ModEntry : Mod
    {
        private ModConfig Config = null!;

        // Set by the Harmony postfix when the local player's tool actually hits (Farmer.useTool frame).
        private static bool hitThisSwing;
        private static double hitTime;

        // True while a swing we started is in progress.
        private bool ourSwing;
        private bool queuedPress;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();

            var harmony = new Harmony(ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.useTool)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(UseTool_Postfix))
            );

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.Input.ButtonsChanged += OnButtonsChanged;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Player.Warped += (_, _) => ResetState();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetState();
        }

        private static void UseTool_Postfix(Farmer who)
        {
            if (who != null && who.IsLocalPlayer)
            {
                hitThisSwing = true;
                hitTime = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
            }
        }

        private void ResetState()
        {
            ourSwing = false;
            queuedPress = false;
            hitThisSwing = false;
        }

        // ---------------------------------------------------------------- input

        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            Farmer player = Game1.player;
            Tool? tool = player.CurrentTool;

            if (Config.CancelKey.JustPressed())
            {
                if (!IsSupported(tool) || player.ActiveObject != null)
                    return;

                if (Context.IsPlayerFree || player.UsingTool)
                    Helper.Input.SuppressActiveKeybinds(Config.CancelKey);

                if (tool is MeleeWeapon weapon)
                {
                    HandleMeleePress(player, weapon);
                    return;
                }

                if (CanStartSwing(player))
                    StartSwing(player);
                else if (Config.BufferInput && player.UsingTool && ourSwing)
                    queuedPress = true;
            }
        }

        // ---------------------------------------------------------------- tick

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            Farmer player = Game1.player;

            if (!ourSwing)
            {
                hitThisSwing = false;
                return;
            }

            // Swing ended on its own (or was interrupted by something else).
            if (!player.UsingTool)
            {
                ourSwing = false;
                hitThisSwing = false;
                TryConsumeQueue(player);
                return;
            }

            if (hitThisSwing)
            {
                double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? hitTime;
                if (now - hitTime >= Config.PostHitDelayMs)
                {
                    CancelFollowThrough(player);
                    ourSwing = false;
                    hitThisSwing = false;
                    TryConsumeQueue(player);
                }
            }
        }

        private void TryConsumeQueue(Farmer player)
        {
            if (!queuedPress)
                return;
            queuedPress = false;
            if (IsSupported(player.CurrentTool) && player.CurrentTool is not MeleeWeapon && CanStartSwing(player))
                StartSwing(player);
        }

        // ---------------------------------------------------------------- logic

        private bool IsSupported(Tool? tool)
        {
            return tool switch
            {
                Axe => Config.EnableAxe,
                Pickaxe => Config.EnablePickaxe,
                Hoe => Config.EnableHoe,
                WateringCan => Config.EnableWateringCan,
                MeleeWeapon w => Config.EnableMeleeWeapons && !w.isScythe(),
                _ => false
            };
        }

        /// <summary>Mirror of the checks Game1 performs before letting the tool button start a tool.</summary>
        private static bool CanStartSwing(Farmer player)
        {
            return Context.IsPlayerFree
                && !Game1.fadeToBlack
                && player.CanMove
                && !player.UsingTool
                && !player.isEating
                && !player.canOnlyWalk
                && !player.IsSitting()
                && !player.isRidingHorse()
                && !player.onBridge.Value
                && player.ActiveObject == null
                && player.CurrentTool != null
                && Game1.farmEvent == null
                && !Game1.dialogueUp;
        }

        /// <summary>Target position exactly like Game1.pressUseToolButton: mouse if visible, else tile in front.</summary>
        private static Vector2 GetTargetPosition(Farmer player)
        {
            return Game1.wasMouseVisibleThisFrame
                ? new Vector2(Game1.getOldMouseX() + Game1.viewport.X, Game1.getOldMouseY() + Game1.viewport.Y)
                : player.GetToolLocation();
        }

        /// <summary>Same code path as the tail of Game1.pressUseToolButton, so facing, targeting and tool behaviour are vanilla.</summary>
        private void StartSwing(Farmer player)
        {
            Vector2 position = GetTargetPosition(player);

            // Don't swing at a villager in front of us; vanilla would talk instead.
            if (player.currentLocation.doesPositionCollideWithCharacter(Utility.getRectangleCenteredAt(player.GetToolLocation(position), 64), ignoreMonsters: true)?.IsVillager ?? false)
                return;

            if (Config.GuardMisclicks)
            {
                Vector2 hit = player.GetToolLocation(position);
                Vector2 tile = new Vector2((int)(hit.X / 64f), (int)(hit.Y / 64f));
                if (!HasValidTarget(player.currentLocation, tile, player.CurrentTool))
                    return;
            }

            player.FireTool();
            player.toolPower.Value = 0;
            player.toolHold.Value = 0;
            hitThisSwing = false;

            int oldFacing = player.FacingDirection;
            Vector2 toolLocation = player.GetToolLocation(position);
            player.FacingDirection = player.getGeneralDirectionTowards(new Vector2((int)toolLocation.X, (int)toolLocation.Y));
            player.lastClick = new Vector2((int)position.X, (int)position.Y);
            player.BeginUsingTool();

            if (!player.UsingTool)
            {
                player.FacingDirection = oldFacing;
                return;
            }

            if (player.FarmerSprite.IsPlayingBasicAnimation(oldFacing, carrying: true) || player.FarmerSprite.IsPlayingBasicAnimation(oldFacing, carrying: false))
                player.FarmerSprite.StopAnimation();

            ourSwing = true;

            // Upgraded axe/hoe/can enter a charge state. The key never charges: release at once for a level-1 hit.
            // Charging stays on the normal tool button.
            if (player.canReleaseTool)
                ReleaseCharge(player);
        }

        private static void ReleaseCharge(Farmer player)
        {
            if (player.canReleaseTool && player.UsingTool && player.CurrentTool != null)
                player.EndUsingTool();
        }

        /// <summary>
        /// Stop the swing after the hit landed. Deliberately does NOT call EndUsingTool / completelyStopAnimatingOrDoingAction:
        /// for axe/hoe those start a fresh swing animation, which is what causes ghost swings.
        /// </summary>
        private static void CancelFollowThrough(Farmer player)
        {
            player.FarmerSprite.StopAnimation();
            Farmer.canMoveNow(player); // CanMove, UsingTool=false, PauseForSingleAnimation=false, velocities
            player.canReleaseTool = false;
            player.toolPower.Value = 0;
            player.toolHold.Value = 0;
            player.stopJittering();
            player.Halt();
        }

        private void HandleMeleePress(Farmer player, MeleeWeapon weapon)
        {
            if (player.UsingTool)
            {
                // Vanilla: clicking mid-swing restarts/combos the swing (MeleeWeapon.leftClick).
                player.FireTool();
                return;
            }

            if (!CanStartSwing(player))
                return;

            Vector2 position = GetTargetPosition(player);
            player.FireTool();
            int oldFacing = player.FacingDirection;
            Vector2 toolLocation = player.GetToolLocation(position);
            player.FacingDirection = player.getGeneralDirectionTowards(new Vector2((int)toolLocation.X, (int)toolLocation.Y));
            player.lastClick = new Vector2((int)position.X, (int)position.Y);
            player.BeginUsingTool();
            if (!player.UsingTool)
                player.FacingDirection = oldFacing;
            else if (player.FarmerSprite.IsPlayingBasicAnimation(oldFacing, carrying: true) || player.FarmerSprite.IsPlayingBasicAnimation(oldFacing, carrying: false))
                player.FarmerSprite.StopAnimation();
        }

        // ---------------------------------------------------------------- misclick guard

        private static bool HasValidTarget(GameLocation loc, Vector2 tile, Tool tool)
        {
            return tool switch
            {
                Axe axe => AxeTarget(loc, tile, axe),
                Pickaxe pick => PickaxeTarget(loc, tile, pick),
                Hoe => HoeTarget(loc, tile),
                WateringCan can => CanTarget(loc, tile, can),
                _ => true
            };
        }

        private static ResourceClump? ClumpAt(GameLocation loc, Vector2 tile)
        {
            foreach (ResourceClump clump in loc.resourceClumps)
                if (clump.occupiesTile((int)tile.X, (int)tile.Y))
                    return clump;
            return null;
        }

        private static bool AxeTarget(GameLocation loc, Vector2 tile, Axe axe)
        {
            if (loc.terrainFeatures.TryGetValue(tile, out TerrainFeature tf))
            {
                // Dying trees stay in terrainFeatures while the fall/stump-break animation plays: health -100 = stump gone.
                if (tf is Tree tree)
                    return tree.health.Value > -99f && !tree.destroy.Value; // stump is hittable while trunk falls (vanilla)
                if (tf is FruitTree fruit)
                    return fruit.health.Value > -99f && !fruit.destroy;
                if (tf is Bush b && b.isDestroyable())
                    return true;
            }
            foreach (LargeTerrainFeature ltf in loc.largeTerrainFeatures)
                if (ltf is Bush bush && bush.isDestroyable() && bush.getBoundingBox().Contains((int)tile.X * 64 + 32, (int)tile.Y * 64 + 32))
                    return true;

            if (loc.objects.TryGetValue(tile, out StardewValley.Object obj))
                return obj.IsTwig() || obj.IsWeeds() || obj is BreakableContainer; // no placed objects

            ResourceClump? clump = ClumpAt(loc, tile);
            if (clump is GiantCrop)
                return true;
            if (clump != null)
            {
                int id = clump.parentSheetIndex.Value;
                if (id == 600) return axe.UpgradeLevel >= 1; // stump: copper axe+
                if (id == 602) return axe.UpgradeLevel >= 2; // hollow log: steel axe+
            }
            return false;
        }

        private static bool PickaxeTarget(GameLocation loc, Vector2 tile, Pickaxe pick)
        {
            if (loc.objects.TryGetValue(tile, out StardewValley.Object obj))
                return obj.IsBreakableStone() || obj.IsWeeds() || obj is BreakableContainer; // natural only: no machines/chests

            if (loc.terrainFeatures.TryGetValue(tile, out TerrainFeature tf))
                return tf is HoeDirt dirt && dirt.crop == null; // clears empty tilled soil

            ResourceClump? clump = ClumpAt(loc, tile);
            if (clump != null && clump is not GiantCrop)
            {
                int id = clump.parentSheetIndex.Value;
                if (id == 600 || id == 602) return false;                   // stumps/logs: axe only
                if (id == 148 || id == 622) return pick.UpgradeLevel >= 3; // meteorite: gold pickaxe+
                if (id == 672) return pick.UpgradeLevel >= 2;              // boulder: steel pickaxe+
                return true;                                                // mine rocks etc.
            }
            return false;
        }

        private static bool HoeTarget(GameLocation loc, Vector2 tile)
        {
            if (loc.objects.TryGetValue(tile, out StardewValley.Object obj))
                return obj.QualifiedItemId == "(O)590" || obj.QualifiedItemId == "(O)SeedSpot"; // artifact / seed spot

            if (loc.terrainFeatures.TryGetValue(tile, out TerrainFeature tf))
                return tf is HoeDirt dirt && dirt.crop != null && dirt.crop.forageCrop.Value; // ginger

            if (ClumpAt(loc, tile) != null)
                return false;
            if (loc.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back") == null)
                return false;
            if (loc is MineShaft)
                return true;
            return loc.isTilePassable(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport);
        }

        private static bool CanTarget(GameLocation loc, Vector2 tile, WateringCan can)
        {
            if (loc.CanRefillWateringCanOnTile((int)tile.X, (int)tile.Y))
                return can.IsBottomless || can.WaterLeft < can.waterCanMax;

            if (!can.IsBottomless && can.WaterLeft <= 0)
                return false;

            if (loc.terrainFeatures.TryGetValue(tile, out TerrainFeature tf) && tf is HoeDirt dirt)
                return dirt.state.Value != 1;
            if (loc.objects.TryGetValue(tile, out StardewValley.Object obj) && obj is IndoorPot pot)
                return pot.hoeDirt.Value != null && pot.hoeDirt.Value.state.Value != 1;
            if (loc.getBuildingAt(tile) is PetBowl bowl)
                return !bowl.watered.Value;
            return false;
        }

        // ---------------------------------------------------------------- GMCM

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api == null)
                return;

            api.Register(ModManifest, () => Config = new ModConfig(), () => Helper.WriteConfig(Config));
            api.AddKeybindList(ModManifest, () => Config.CancelKey, v => Config.CancelKey = v, () => "Swing + cancel key");
            api.AddNumberOption(ModManifest, () => Config.PostHitDelayMs, v => Config.PostHitDelayMs = v, () => "Post-hit delay (ms)",
                () => "Time to hold the impact pose before cancelling. 0 = fastest.", 0, 300);
            api.AddBoolOption(ModManifest, () => Config.BufferInput, v => Config.BufferInput = v, () => "Buffer presses mid-swing");
            api.AddBoolOption(ModManifest, () => Config.GuardMisclicks, v => Config.GuardMisclicks = v, () => "Guard misclicks",
                () => "Key only swings when the aimed tile has something the tool can work on. Use the normal tool button for anything else.");
            api.AddBoolOption(ModManifest, () => Config.EnableAxe, v => Config.EnableAxe = v, () => "Axe");
            api.AddBoolOption(ModManifest, () => Config.EnablePickaxe, v => Config.EnablePickaxe = v, () => "Pickaxe");
            api.AddBoolOption(ModManifest, () => Config.EnableHoe, v => Config.EnableHoe = v, () => "Hoe");
            api.AddBoolOption(ModManifest, () => Config.EnableWateringCan, v => Config.EnableWateringCan = v, () => "Watering can");
            api.AddBoolOption(ModManifest, () => Config.EnableMeleeWeapons, v => Config.EnableMeleeWeapons = v, () => "Melee weapons (acts as attack click)");
        }
    }

    public interface IGenericModConfigMenuApi
    {
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
        void AddKeybindList(IManifest mod, Func<KeybindList> getValue, Action<KeybindList> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
        void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
        void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    }
}
