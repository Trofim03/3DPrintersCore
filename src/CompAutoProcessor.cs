using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace _3DPrinters
{
    [DefOf]
    public static class _3DPrintersJobDefOf
    {
        public static JobDef Operate3DPrinter;
    }

    public class WorkGiver_Operate3DPrinter : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest =>
            ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial);
        public override PathEndMode PathEndMode => PathEndMode.Touch;
        public override bool Prioritized => true;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            if (pawn.workSettings == null || !pawn.workSettings.WorkIsActive(WorkTypeDefOf.Research))
                yield break;

            foreach (var building in pawn.Map.listerBuildings.allBuildingsColonist)
            {
                var comp = building.TryGetComp<CompAutoProcessor>();
                if (comp != null && comp.NeedsWorkNow(pawn))
                    yield return building;
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t.IsBurning()) return false;
            if (t.IsForbidden(pawn)) return false;
            if (!pawn.CanReserve(t, 1, -1, null, forced)) return false;

            var comp = t.TryGetComp<CompAutoProcessor>();
            if (comp == null) return false;

            return comp.NeedsWorkNow(pawn);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return JobMaker.MakeJob(_3DPrintersJobDefOf.Operate3DPrinter, t);
        }
    }

    public class JobDriver_Operate3DPrinter : JobDriver
    {
        private CompAutoProcessor Processor => job.targetA.Thing?.TryGetComp<CompAutoProcessor>();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            var workToil = new Toil
            {
                initAction = () => Processor?.StartWorking(pawn),
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return workToil;
        }
    }

    public class Building_AutoProcessor : Building
    {
        public override void Tick()
        {
            base.Tick();
            GetComp<CompAutoProcessor>()?.CompTick();
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (var option in base.GetFloatMenuOptions(selPawn))
                yield return option;

            var comp = GetComp<CompAutoProcessor>();
            if (comp == null) yield break;

            if (comp.Props?.supportedRecipes != null && comp.Props.supportedRecipes.Count > 0)
            {
                foreach (var recipe in comp.Props.supportedRecipes)
                {
                    var r = recipe;
                    string label = "_3DPrinters.SelectRecipeFloat".Translate(r.label);
                    yield return new FloatMenuOption(label, () => comp.SelectRecipe(r));
                }
            }

            if (comp.CurrentState == PrinterState.WaitingForIngredients && comp.SelectedRecipe != null)
            {
                yield return new FloatMenuOption("_3DPrinters.ForceOperateFloat".Translate(), () =>
                {
                    Job job = JobMaker.MakeJob(_3DPrintersJobDefOf.Operate3DPrinter, this);
                    selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                });
            }

            if (comp.CurrentState == PrinterState.Processing || comp.CurrentState == PrinterState.WaitingForIngredients)
            {
                yield return new FloatMenuOption("_3DPrinters.StopFloat".Translate(), () => comp.StopMachine());
            }

            if (comp.CurrentState == PrinterState.Stopped)
            {
                yield return new FloatMenuOption("_3DPrinters.ResumeFloat".Translate(), () => comp.ResumeMachine());
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            var comp = GetComp<CompAutoProcessor>();
            if (comp != null)
                foreach (var g in comp.GetGizmos()) yield return g;
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            var comp = GetComp<CompAutoProcessor>();
            if (comp != null)
            {
                string extra = comp.GetInspectStringExtra();
                if (!extra.NullOrEmpty())
                    text = text.NullOrEmpty() ? extra : text + "\n" + extra;
            }
            return text;
        }
    }

    public enum PrinterState
    {
        NoRecipe,
        WaitingForIngredients,
        Processing,
        Stopped
    }

    public class CompAutoProcessor : ThingComp
    {
        public CompProperties_AutoProcessor Props => (CompProperties_AutoProcessor)props;
        private PrinterState state = PrinterState.NoRecipe;
        private RecipeDef selectedRecipe;
        private int workDone;
        private int workNeeded;

        public PrinterState CurrentState => state;
        public RecipeDef SelectedRecipe => selectedRecipe;

        public override void CompTick()
        {
            base.CompTick();
            if (state != PrinterState.Processing) return;

            var power = parent.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return;

            workDone++;
            if (workDone >= workNeeded) FinishWork();
        }

        private void FinishWork()
        {
            if (selectedRecipe == null) return;
            var map = parent.Map;
            var pos = parent.Position;

            foreach (var product in selectedRecipe.products)
            {
                Thing thing = ThingMaker.MakeThing(product.thingDef);
                thing.stackCount = product.count;
                GenSpawn.Spawn(thing, pos, map);
            }
            state = PrinterState.WaitingForIngredients;
            workDone = 0;
        }

        public bool NeedsWorkNow(Pawn pawn)
        {
            if (state != PrinterState.WaitingForIngredients) return false;
            if (selectedRecipe == null) return false;

            var power = parent.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return false;

            return HasEnoughIngredients(pawn);
        }

        private bool HasEnoughIngredients(Pawn pawn)
        {
            if (selectedRecipe == null) return false;
            var map = parent.Map;
            float totalNutrition = 0f;
            HashSet<Thing> checkedThings = new HashSet<Thing>();

            foreach (var cell in GenRadial.RadialCellsAround(parent.Position, 8, true))
            {
                foreach (var thing in cell.GetThingList(map))
                {
                    if (checkedThings.Contains(thing)) continue;
                    if (thing.IsForbidden(pawn)) continue;
                    if (!pawn.CanReserve(thing)) continue;

                    foreach (var ing in selectedRecipe.ingredients)
                    {
                        if (ing.filter.Allows(thing.def))
                        {
                            checkedThings.Add(thing);
                            totalNutrition += thing.GetStatValue(StatDefOf.Nutrition) * thing.stackCount;
                            break;
                        }
                    }
                }
            }

            float required = selectedRecipe.ingredients.Sum(i => i.GetBaseCount());
            return totalNutrition >= required;
        }

        public void StartWorking(Pawn pawn)
        {
            if (state != PrinterState.WaitingForIngredients || selectedRecipe == null) return;
            if (!ConsumeIngredients(pawn)) return;

            workNeeded = selectedRecipe.workAmount > 0 ? Mathf.CeilToInt(selectedRecipe.workAmount) : Props.baseWorkAmount;
            workDone = 0;
            state = PrinterState.Processing;
        }

        private bool ConsumeIngredients(Pawn pawn)
        {
            var map = parent.Map;
            float needed = selectedRecipe.ingredients.Sum(i => i.GetBaseCount());
            float consumed = 0f;

            foreach (var cell in GenRadial.RadialCellsAround(parent.Position, 8, true))
            {
                if (consumed >= needed) break;
                foreach (var thing in cell.GetThingList(map).ToList())
                {
                    if (consumed >= needed) break;
                    if (thing.IsForbidden(pawn)) continue;

                    foreach (var ing in selectedRecipe.ingredients)
                    {
                        if (!ing.filter.Allows(thing.def)) continue;
                        float nutritionPerItem = thing.GetStatValue(StatDefOf.Nutrition);
                        float remaining = needed - consumed;
                        int itemsNeeded = Mathf.CeilToInt(remaining / nutritionPerItem);

                        if (itemsNeeded >= thing.stackCount)
                        {
                            consumed += nutritionPerItem * thing.stackCount;
                            thing.Destroy(DestroyMode.Vanish);
                        }
                        else
                        {
                            consumed += nutritionPerItem * itemsNeeded;
                            thing.stackCount -= itemsNeeded;
                        }
                        break;
                    }
                }
            }
            return consumed >= needed - 0.001f;
        }

        public void SelectRecipe(RecipeDef recipe)
        {
            selectedRecipe = recipe;
            state = PrinterState.WaitingForIngredients;
            workDone = 0;
        }

        public void StopMachine()
        {
            state = PrinterState.Stopped;
        }

        public void ResumeMachine()
        {
            state = selectedRecipe != null ? PrinterState.WaitingForIngredients : PrinterState.NoRecipe;
        }

        public IEnumerable<Gizmo> GetGizmos()
        {
            if (Props?.supportedRecipes == null || Props.supportedRecipes.Count == 0) yield break;

            string currentLabel;
            if (selectedRecipe != null)
                currentLabel = selectedRecipe.label;
            else
            {
                TaggedString selectLabel = "_3DPrinters.SelectRecipeGizmo".Translate();
                currentLabel = selectLabel.ToString();
            }
            var options = new List<FloatMenuOption>();
            foreach (var recipe in Props.supportedRecipes)
            {
                var r = recipe;
                options.Add(new FloatMenuOption(r.label, () => SelectRecipe(r), TexCommand.DesirePower, Color.white));
            }

            yield return new Command_Action
            {
                defaultLabel = "_3DPrinters.RecipeGizmo".Translate(currentLabel),
                defaultDesc = "_3DPrinters.RecipeGizmoDesc".Translate(),
                icon = TexCommand.DesirePower,
                action = () => Find.WindowStack.Add(new FloatMenu(options))
            };

            if (state == PrinterState.Processing || state == PrinterState.WaitingForIngredients)
                yield return new Command_Action
                {
                    defaultLabel = "_3DPrinters.StopGizmo".Translate(),
                    defaultDesc = "_3DPrinters.StopGizmoDesc".Translate(),
                    icon = TexCommand.ForbidOn,
                    action = () => StopMachine()
                };
            else if (state == PrinterState.Stopped)
                yield return new Command_Action
                {
                    defaultLabel = "_3DPrinters.ResumeGizmo".Translate(),
                    defaultDesc = "_3DPrinters.ResumeGizmoDesc".Translate(),
                    icon = TexCommand.ForbidOff,
                    action = () => ResumeMachine()
                };
        }

        public string GetInspectStringExtra()
        {
            if (selectedRecipe == null)
            {
                TaggedString noRecipe = "_3DPrinters.NoRecipeSelected".Translate();
                return noRecipe.ToString();
            }

            string status = "";
            switch (state)
            {
                case PrinterState.WaitingForIngredients:
                    TaggedString waiting = "_3DPrinters.WaitingForIngredients".Translate();
                    status = waiting.ToString();
                    break;
                case PrinterState.Processing:
                    TaggedString processing = "_3DPrinters.ProcessingStatus".Translate(((float)workDone / workNeeded * 100f).ToString("F0"));
                    status = processing.ToString();
                    break;
                case PrinterState.Stopped:
                    TaggedString stopped = "_3DPrinters.Stopped".Translate();
                    status = stopped.ToString();
                    break;
            }

            TaggedString recipeLabel = "_3DPrinters.RecipeLabel".Translate(selectedRecipe.label);
            return status + "\n" + recipeLabel.ToString();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state, "state", PrinterState.NoRecipe);
            Scribe_Defs.Look(ref selectedRecipe, "selectedRecipe");
            Scribe_Values.Look(ref workDone, "workDone", 0);
            Scribe_Values.Look(ref workNeeded, "workNeeded", 0);
        }
    }

    public class CompProperties_AutoProcessor : CompProperties
    {
        public int baseWorkAmount = 500;
        public List<RecipeDef> supportedRecipes;
        public CompProperties_AutoProcessor() => compClass = typeof(CompAutoProcessor);
    }
}