using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IliyianCustomMod
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        internal static readonly MethodInfo RenderMouseAttachments =
            AccessTools.Method(typeof(DeepResourceGrid), "RenderMouseAttachments");

        static Bootstrap()
        {
            var harmony = new Harmony("iliyian.custommod");

            // Feature 1: deep drill selection shows the deep resource overlay
            // (vanilla 1.6 gates this behind AnyActiveDeepScannersOnMap).
            harmony.Patch(
                AccessTools.Method(typeof(ThingWithComps), "DrawExtraSelectionOverlays"),
                postfix: new HarmonyMethod(typeof(DeepDrillOverlayPatches), nameof(DeepDrillOverlayPatches.DrawExtraSelectionOverlays_Postfix)));

            harmony.Patch(
                AccessTools.Method(typeof(DeepResourceGrid), "DeepResourcesOnGUI"),
                postfix: new HarmonyMethod(typeof(DeepDrillOverlayPatches), nameof(DeepDrillOverlayPatches.DeepResourcesOnGUI_Postfix)));

            // Feature 2: turrets shoot predators that entered the hunting state
            // (JobDefOf.PredatorHunt) targeting a pawn of the turret's faction.
            // Vanilla never puts such predators into AttackTargetsCache.
            harmony.Patch(
                AccessTools.Method(typeof(Building_TurretGun), "TryFindNewTarget"),
                postfix: new HarmonyMethod(typeof(TurretPredatorPatches), nameof(TurretPredatorPatches.TryFindNewTarget_Postfix)));

            // Feature 3: headgear / hair visibility modes (vanilla hides hair when
            // worn headgear covers UpperHead/FullHead; see PawnRenderTree.AdjustParms).
            harmony.Patch(
                AccessTools.Method(typeof(PawnRenderTree), "AdjustParms"),
                postfix: new HarmonyMethod(typeof(HeadgearRenderPatches), nameof(HeadgearRenderPatches.AdjustParms_Postfix)));

            harmony.Patch(
                AccessTools.Method(typeof(PawnRenderNodeWorker_Apparel_Head), "CanDrawNow"),
                postfix: new HarmonyMethod(typeof(HeadgearRenderPatches), nameof(HeadgearRenderPatches.HeadCanDrawNow_Postfix)));

            // Feature 4: editable mechanitor bandwidth.
            harmony.Patch(
                AccessTools.PropertyGetter(typeof(Pawn_MechanitorTracker), "TotalBandwidth"),
                postfix: new HarmonyMethod(typeof(BandwidthPatches), nameof(BandwidthPatches.TotalBandwidth_Postfix)));

            harmony.Patch(
                AccessTools.Method(typeof(Pawn_MechanitorTracker), "GetGizmos"),
                postfix: new HarmonyMethod(typeof(BandwidthPatches), nameof(BandwidthPatches.GetGizmos_Postfix)));

            // Feature 5: remove the mechanitor's 24.9-cell command range limit on mechs.
            harmony.Patch(
                AccessTools.Method(typeof(Pawn_MechanitorTracker), "CanCommandTo"),
                postfix: new HarmonyMethod(typeof(MechControlRangePatches), nameof(MechControlRangePatches.CanCommandTo_Postfix)));

            Log.Message("[IliyianCustomMod] loaded: drill overlay, turrets vs predators, headgear modes, editable bandwidth, unlimited mech control range.");
        }
    }

    // =====================================================================
    // Feature 1: deep drill overlay
    // =====================================================================
    public static class DeepDrillOverlayPatches
    {
        public static void DrawExtraSelectionOverlays_Postfix(ThingWithComps __instance)
        {
            if (__instance == null || !__instance.Spawned || __instance.Map != Find.CurrentMap)
                return;
            if (__instance.HasComp<CompDeepDrill>())
                __instance.Map.deepResourceGrid.MarkForDraw();
        }

        public static void DeepResourcesOnGUI_Postfix(DeepResourceGrid __instance)
        {
            Thing selected = Find.Selector.SingleSelectedThing;
            if (selected == null || !selected.Spawned || selected.TryGetComp<CompDeepDrill>() == null)
                return;
            if (__instance.AnyActiveDeepScannersOnMap())
                return; // vanilla already rendered the label this frame
            Bootstrap.RenderMouseAttachments?.Invoke(__instance, null);
        }
    }

    // =====================================================================
    // Feature 2: turrets shoot hunting predators
    // =====================================================================
    public static class TurretPredatorPatches
    {
        public static void TryFindNewTarget_Postfix(Building_TurretGun __instance, ref LocalTargetInfo __result)
        {
            if (__result.IsValid || __instance == null || !__instance.Spawned)
                return;

            Faction fac = __instance.Faction;
            if (fac == null)
                return;

            Verb verb = __instance.AttackVerb;
            if (verb == null)
                return;

            float range = verb.EffectiveRange;
            if (range <= 0f)
                return;

            bool fliesOverhead = verb.ProjectileFliesOverhead();
            Map map = __instance.Map;
            Pawn best = null;
            float bestDist = float.MaxValue;

            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (!p.RaceProps.predator || p.Faction == fac || p.Downed || p.Fogged())
                    continue;

                if (!IsHuntingFactionPawn(p, fac))
                    continue;

                float distSq = p.Position.DistanceToSquared(__instance.Position);
                if (distSq > range * range || distSq >= bestDist)
                    continue;

                if (!fliesOverhead && !GenSight.LineOfSight(__instance.Position, p.Position, map, skipFirstCell: true))
                    continue;

                best = p;
                bestDist = distSq;
            }

            if (best != null)
                __result = best;
        }

        private static bool IsHuntingFactionPawn(Pawn predator, Faction fac)
        {
            Job job = predator.CurJob;
            if (job == null || job.def != JobDefOf.PredatorHunt)
                return false;
            if (predator.jobs.curDriver == null || predator.jobs.curDriver.ended)
                return false;
            return job.GetTarget(TargetIndex.A).Thing is Pawn prey && !prey.Dead && prey.Faction == fac;
        }
    }

    // =====================================================================
    // Feature 3: headgear / hair visibility modes
    // =====================================================================
    public enum HeadgearMode
    {
        GearOnly,   // vanilla: headgear covers hair
        HideHeadgear, // headgear not drawn, hair shows normally
        ShowBoth    // headgear and hair drawn together
    }

    public static class HeadgearRenderPatches
    {
        // Hair is culled via parms.skipFlags computed in AdjustParms. Depending on
        // the mode, re-enable hair (and, when headgear is hidden entirely, the
        // beard/eyes flags that gear would also set).
        public static void AdjustParms_Postfix(PawnRenderTree __instance, ref PawnDrawParms parms)
        {
            HeadgearMode mode = IliyianCustomModMod.Settings?.HeadgearMode ?? HeadgearMode.GearOnly;
            if (mode == HeadgearMode.GearOnly)
                return;

            Pawn pawn = __instance.pawn;
            if (pawn?.apparel == null || !pawn.RaceProps.Humanlike)
                return;

            bool hasHeadGear = false;
            foreach (Apparel item in pawn.apparel.WornApparel)
            {
                if (item.def.apparel != null && (item.def.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.UpperHead)
                    || item.def.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead)
                    || (item.def.apparel.renderSkipFlags != null && item.def.apparel.renderSkipFlags.Count > 0)))
                {
                    hasHeadGear = true;
                    break;
                }
            }
            if (!hasHeadGear)
                return;

            parms.skipFlags &= ~(ulong)RenderSkipFlagDefOf.Hair;
            if (mode == HeadgearMode.HideHeadgear)
            {
                parms.skipFlags &= ~(ulong)RenderSkipFlagDefOf.Beard;
                parms.skipFlags &= ~(ulong)RenderSkipFlagDefOf.Eyes;
            }
        }

        // HideHeadgear mode: don't draw head-apparel graphics at all.
        public static void HeadCanDrawNow_Postfix(PawnRenderNode n, PawnDrawParms parms, ref bool __result)
        {
            if (__result && IliyianCustomModMod.Settings?.HeadgearMode == HeadgearMode.HideHeadgear)
                __result = false;
        }

        // Vanilla draws zoomed-out pawns from a texture atlas cache that only
        // re-bakes on state changes, so a mode change wouldn't show until then.
        // Mark every pawn's cached frame dirty to force immediate re-render.
        public static void RefreshAllPawnCaches()
        {
            if (Find.Maps == null)
                return;
            foreach (Map map in Find.Maps)
            {
                foreach (Pawn p in map.mapPawns.AllPawns)
                    GlobalTextureAtlasManager.TryMarkPawnFrameSetDirty(p);
            }
            // inspect pane / colonist bar portraits live in a separate cache
            PortraitsCache.Clear();
        }
    }

    // =====================================================================
    // Feature 4: editable mechanitor bandwidth
    // =====================================================================
    public static class BandwidthPatches
    {
        // Per-pawn override of TotalBandwidth.
        public static void TotalBandwidth_Postfix(Pawn_MechanitorTracker __instance, ref int __result)
        {
            int? bw = BandwidthOverridesComponent.GetOverride(__instance.Pawn);
            if (bw.HasValue)
                __result = bw.Value;
        }

        public static void GetGizmos_Postfix(Pawn_MechanitorTracker __instance, ref IEnumerable<Gizmo> __result)
        {
            Pawn pawn = __instance.Pawn;
            if (pawn == null || !pawn.Spawned || Find.GameInitData != null)
                return;

            var original = __result;
            __result = WithEditGizmo(original, pawn);
        }

        private static IEnumerable<Gizmo> WithEditGizmo(IEnumerable<Gizmo> original, Pawn pawn)
        {
            foreach (Gizmo g in original)
                yield return g;

            var cmd = new Command_Action
            {
                defaultLabel = "编辑机械带宽",
                defaultDesc = "打开窗口直接设置该机械师的机械带宽（会覆盖原版由插件/基因计算出的数值）。",
                icon = TexButton.Plus,
                action = delegate { Find.WindowStack.Add(new Dialog_EditBandwidth(pawn)); }
            };
            yield return cmd;
        }
    }

    public class BandwidthOverridesComponent : GameComponent
    {
        private static Dictionary<string, int> overrides = new Dictionary<string, int>();

        public BandwidthOverridesComponent(Game game) { }

        public static int? GetOverride(Pawn pawn)
        {
            if (pawn == null || overrides.Count == 0)
                return null;
            if (overrides.TryGetValue(pawn.ThingID, out int val))
                return val;
            return null;
        }

        public static void SetOverride(Pawn pawn, int? val)
        {
            if (pawn == null)
                return;
            if (val.HasValue)
                overrides[pawn.ThingID] = val.Value;
            else
                overrides.Remove(pawn.ThingID);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref overrides, "bandwidthOverrides", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && overrides == null)
                overrides = new Dictionary<string, int>();
        }
    }

    public class Dialog_EditBandwidth : Window
    {
        private readonly Pawn pawn;
        private readonly int vanillaValue;
        private int value;
        private string buffer;

        public override Vector2 InitialSize => new Vector2(430f, 200f);

        public Dialog_EditBandwidth(Pawn pawn)
        {
            this.pawn = pawn;
            vanillaValue = Mathf.RoundToInt(pawn.GetStatValue(StatDefOf.MechBandwidth));
            value = BandwidthOverridesComponent.GetOverride(pawn) ?? vanillaValue;
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "编辑机械带宽 - " + pawn.LabelShortCap);
            Text.Font = GameFont.Small;

            float y = 44f;
            Widgets.Label(new Rect(0f, y, 200f, 24f), "原版带宽: " + vanillaValue);
            Widgets.Label(new Rect(210f, y, inRect.width - 200f, 24f),
                BandwidthOverridesComponent.GetOverride(pawn).HasValue
                    ? "当前: 覆盖为 " + BandwidthOverridesComponent.GetOverride(pawn)
                    : "当前: 原版值");
            y += 32f;

            Widgets.Label(new Rect(0f, y, 90f, 24f), "带宽:");
            Widgets.TextFieldNumeric(new Rect(95f, y - 3f, 110f, 28f), ref value, ref buffer, 0f, 200f);

            Rect minusRect = new Rect(215f, y - 3f, 28f, 28f);
            Rect plusRect = new Rect(248f, y - 3f, 28f, 28f);
            if (Widgets.ButtonImage(minusRect, TexButton.Minus))
                value = Mathf.Max(0, value - 1);
            if (Widgets.ButtonImage(plusRect, TexButton.Plus))
                value = value + 1;

            y += 40f;
            float btnW = (inRect.width - 16f) / 3f;
            if (Widgets.ButtonText(new Rect(0f, y, btnW, 30f), "保存"))
            {
                BandwidthOverridesComponent.SetOverride(pawn, value == vanillaValue ? (int?)null : value);
                Close();
            }
            if (Widgets.ButtonText(new Rect(btnW + 8f, y, btnW, 30f), "恢复原版值"))
            {
                BandwidthOverridesComponent.SetOverride(pawn, null);
                Close();
            }
            if (Widgets.ButtonText(new Rect(btnW * 2f + 16f, y, btnW, 30f), "取消"))
                Close();
        }
    }

    // =====================================================================
    // Feature 5: no mech control range limit
    // =====================================================================
    public static class MechControlRangePatches
    {
        // Vanilla hardcodes the 24.9-cell command ring in
        // Pawn_MechanitorTracker.CanCommandTo (DistanceToSquared < 620.01f).
        // Every drafted-command gate on mechs (move / attack / multi-goto /
        // float menu, all via MechanitorUtility.InMechanitorCommandRange)
        // routes through this single method, so one postfix lifts them all.
        public static void CanCommandTo_Postfix(ref bool __result)
        {
            if (IliyianCustomModMod.Settings?.UnlimitedMechControlRange == true)
                __result = true;
        }
    }

    // =====================================================================
    // Mod settings
    // =====================================================================
    public class IliyianCustomModMod : Mod
    {
        private IliyianCustomModSettings settings;

        public static IliyianCustomModSettings Settings =>
            LoadedModManager.GetMod<IliyianCustomModMod>()?.settings;

        public IliyianCustomModMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<IliyianCustomModSettings>();
        }

        public override string SettingsCategory() => "Iliyian Custom Mod";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            HeadgearMode old = settings.HeadgearMode;
            listing.Label("鼠族头部装备显示模式 (对所有人形种族生效):");
            if (listing.RadioButton("只显示装备 (原版默认: 装备遮住头发)", settings.HeadgearMode == HeadgearMode.GearOnly))
                settings.HeadgearMode = HeadgearMode.GearOnly;
            if (listing.RadioButton("不显示装备, 正常显示头发", settings.HeadgearMode == HeadgearMode.HideHeadgear))
                settings.HeadgearMode = HeadgearMode.HideHeadgear;
            if (listing.RadioButton("装备和头发同时显示", settings.HeadgearMode == HeadgearMode.ShowBoth))
                settings.HeadgearMode = HeadgearMode.ShowBoth;
            if (settings.HeadgearMode != old)
                HeadgearRenderPatches.RefreshAllPawnCaches();

            listing.Gap(8f);
            listing.CheckboxLabeled("取消机械师对机械体的征召区域限制 (原版 24.9 格指挥圈)", ref settings.UnlimitedMechControlRange);

            listing.Gap(8f);
            listing.Label("提示: 机械师选中后可用「编辑机械带宽」gizmo 直接改带宽。");

            listing.End();
            base.DoSettingsWindowContents(inRect);
        }
    }

    public class IliyianCustomModSettings : ModSettings
    {
        public HeadgearMode HeadgearMode = HeadgearMode.GearOnly;
        public bool UnlimitedMechControlRange = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref HeadgearMode, "headgearMode", HeadgearMode.GearOnly);
            Scribe_Values.Look(ref UnlimitedMechControlRange, "unlimitedMechControlRange", false);
        }
    }
}
