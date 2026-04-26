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
            if (pawn.workSettings == null)
                yield break;

            foreach (var building in pawn.Map.listerBuildings.allBuildingsColonist)
            {
                var comp = building.TryGetComp<CompAutoProcessor>();
                if (comp == null) continue;

                WorkTypeDef requiredWork = comp.Props.GetWorkTypeDef();
                if (requiredWork == null) continue;

                if (!pawn.workSettings.WorkIsActive(requiredWork))
                    continue;

                if (comp.NeedsWorkNow(pawn))
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

            WorkTypeDef requiredWork = comp.Props.GetWorkTypeDef();
            if (requiredWork == null) return false;
            if (pawn.workSettings != null && !pawn.workSettings.WorkIsActive(requiredWork) && !forced)
                return false;

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

            if (comp.CurrentState == PrinterState.WaitingForIngredients && comp.SelectedRecipe != null)
            {
                TaggedString forceTag = "_3DPrinters.ForceOperateFloat".Translate();
                string forceLabel = forceTag.ToString();
                yield return new FloatMenuOption(forceLabel, () =>
                {
                    Job job = JobMaker.MakeJob(_3DPrintersJobDefOf.Operate3DPrinter, this);
                    selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                });
            }

            if (comp.CurrentState == PrinterState.Processing || comp.CurrentState == PrinterState.WaitingForIngredients)
            {
                TaggedString stopTag = "_3DPrinters.StopFloat".Translate();
                string stopLabel = stopTag.ToString();
                yield return new FloatMenuOption(stopLabel, () => comp.StopMachine());
            }

            if (comp.CurrentState == PrinterState.Stopped)
            {
                TaggedString resumeTag = "_3DPrinters.ResumeFloat".Translate();
                string resumeLabel = resumeTag.ToString();
                yield return new FloatMenuOption(resumeLabel, () => comp.ResumeMachine());
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
                {
                    if (text.NullOrEmpty())
                        text = extra;
                    else
                        text = text + "\n" + extra;
                }
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
        private List<Thing> loadedIngredients = new List<Thing>();

        public PrinterState CurrentState => state;
        public RecipeDef SelectedRecipe => selectedRecipe;

        private Texture2D GetProductIcon()
        {
            if (selectedRecipe != null && selectedRecipe.products.Count > 0)
            {
                var product = selectedRecipe.products[0].thingDef;
                if (product != null && product.uiIcon != null)
                    return product.uiIcon;
            }
            return TexCommand.DesirePower;
        }

        public override void CompTick()
        {
            base.CompTick();

            // Проверка что processing и есть работа
            if (state != PrinterState.Processing) return;
            if (workNeeded <= 0) return;

            var power = parent.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return;

            workDone++;
            if (workDone >= workNeeded)
            {
                FinishWork();
            }
        }

        // Сбрасываем состояние при выходе из Processing
        private void ResetProcessingState()
        {
            state = PrinterState.WaitingForIngredients;
            workDone = 0;
            workNeeded = 0;
            if (loadedIngredients != null)
                loadedIngredients.Clear();
        }

        private void FinishWork()
        {
            if (selectedRecipe == null)
            {
                ResetProcessingState();
                return;
            }

            var map = parent.Map;
            if (map == null)
            {
                ResetProcessingState();
                return;
            }

            var pos = parent.Position;

            foreach (var product in selectedRecipe.products)
            {
                Thing thing = ThingMaker.MakeThing(product.thingDef);
                thing.stackCount = product.count;
                GenSpawn.Spawn(thing, pos, map);
            }

            // Очищаем и переходим в ожидание
            ResetProcessingState();
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
            if (map == null) return false;

            foreach (var ingredientDef in selectedRecipe.ingredients)
            {
                float required = ingredientDef.GetBaseCount();
                float found = 0f;
                HashSet<Thing> checkedThings = new HashSet<Thing>();

                foreach (var cell in GenRadial.RadialCellsAround(parent.Position, 8, true))
                {
                    if (found >= required) break;

                    foreach (var thing in cell.GetThingList(map))
                    {
                        if (found >= required) break;
                        if (checkedThings.Contains(thing)) continue;
                        if (!ingredientDef.filter.Allows(thing.def)) continue;
                        if (thing.IsForbidden(pawn)) continue;
                        if (!pawn.CanReserve(thing)) continue;

                        checkedThings.Add(thing);
                        found += thing.stackCount;
                    }
                }

                if (found < required)
                    return false;
            }

            return true;
        }

        public void StartWorking(Pawn pawn)
        {
            if (state != PrinterState.WaitingForIngredients || selectedRecipe == null) return;

            if (loadedIngredients == null)
                loadedIngredients = new List<Thing>();
            else
                loadedIngredients.Clear();

            if (!ConsumeIngredients(pawn))
            {
                loadedIngredients.Clear();
                return;
            }

            workNeeded = selectedRecipe.workAmount > 0 ? Mathf.CeilToInt(selectedRecipe.workAmount) : Props.baseWorkAmount;
            workDone = 0;
            state = PrinterState.Processing;
        }

        private bool ConsumeIngredients(Pawn pawn)
        {
            var map = parent.Map;
            if (map == null) return false;

            foreach (var ingredientDef in selectedRecipe.ingredients)
            {
                float required = ingredientDef.GetBaseCount();
                float consumed = 0f;

                foreach (var cell in GenRadial.RadialCellsAround(parent.Position, 8, true))
                {
                    if (consumed >= required) break;

                    var things = cell.GetThingList(map).ToList();
                    foreach (var thing in things)
                    {
                        if (consumed >= required) break;
                        if (!ingredientDef.filter.Allows(thing.def)) continue;
                        if (thing.IsForbidden(pawn)) continue;

                        float toConsume = required - consumed;
                        int itemsToTake = Mathf.CeilToInt(toConsume);

                        if (itemsToTake >= thing.stackCount)
                        {
                            AddToLoadedIngredients(thing.def, thing.stackCount);
                            consumed += thing.stackCount;
                            thing.Destroy(DestroyMode.Vanish);
                        }
                        else
                        {
                            AddToLoadedIngredients(thing.def, itemsToTake);
                            consumed += itemsToTake;
                            thing.stackCount -= itemsToTake;
                        }
                    }
                }

                if (consumed < required)
                {
                    ReturnStoredIngredients();
                    return false;
                }
            }

            return true;
        }

        private void AddToLoadedIngredients(ThingDef def, int count)
        {
            if (def == null || count <= 0) return;

            if (loadedIngredients == null)
                loadedIngredients = new List<Thing>();

            // Ищем существующий предмет с таким же def
            var existing = loadedIngredients.FirstOrDefault(t => t != null && t.def == def);
            if (existing != null)
            {
                // Увеличиваем количество в существующей записи
                existing.stackCount += count;
            }
            else
            {
                // Создаём новую запись с правильным количеством
                Thing tempThing = ThingMaker.MakeThing(def);
                if (tempThing != null)
                {
                    tempThing.stackCount = count;
                    loadedIngredients.Add(tempThing);
                }
            }
        }

        private void ReturnStoredIngredients()
        {
            if (loadedIngredients == null || loadedIngredients.Count == 0) return;

            var map = parent.Map;
            if (map == null) return;
            var pos = parent.Position;

            foreach (var ingredient in loadedIngredients)
            {
                if (ingredient == null || ingredient.def == null || ingredient.stackCount <= 0) continue;

                // Создаём новые предметы для возврата
                int remaining = ingredient.stackCount;
                int maxStack = ingredient.def.stackLimit;

                while (remaining > 0)
                {
                    int stackSize = Mathf.Min(remaining, maxStack);

                    Thing returnedThing = ThingMaker.MakeThing(ingredient.def);
                    if (returnedThing != null)
                    {
                        returnedThing.stackCount = stackSize;
                        GenSpawn.Spawn(returnedThing, pos, map);
                    }

                    remaining -= stackSize;
                }
            }

            loadedIngredients.Clear();

            Messages.Message("_3DPrinters.IngredientsReturned".Translate(),
                new TargetInfo(parent.Position, map), MessageTypeDefOf.NeutralEvent);
        }

        public void SelectRecipe(RecipeDef recipe)
        {
            selectedRecipe = recipe;
            state = PrinterState.WaitingForIngredients;
            workDone = 0;
            workNeeded = 0;
        }

        public void StopMachine()
        {
            // Возвращаем ингредиенты если есть
            if (loadedIngredients != null && loadedIngredients.Count > 0)
            {
                ReturnStoredIngredients();
            }

            state = PrinterState.Stopped;
            if (loadedIngredients != null)
                loadedIngredients.Clear();
            workDone = 0;
            workNeeded = 0;
        }

        public void ResumeMachine()
        {
            if (loadedIngredients == null)
                loadedIngredients = new List<Thing>();
            else
                loadedIngredients.Clear();

            workDone = 0;
            workNeeded = 0;

            if (selectedRecipe != null)
                state = PrinterState.WaitingForIngredients;
            else
                state = PrinterState.NoRecipe;
        }

        private bool HasWorkerAvailable()
        {
            WorkTypeDef requiredWork = Props.GetWorkTypeDef();
            if (requiredWork == null) return false;

            var map = parent.Map;
            if (map == null) return false;

            foreach (var pawn in map.mapPawns.FreeColonists)
            {
                if (pawn.workSettings != null && pawn.workSettings.WorkIsActive(requiredWork))
                    return true;
            }
            return false;
        }

        public IEnumerable<Gizmo> GetGizmos()
        {
            if (Props == null || Props.supportedRecipes == null || Props.supportedRecipes.Count == 0)
                yield break;

            bool hasWorker = HasWorkerAvailable();
            WorkTypeDef requiredWork = Props.GetWorkTypeDef();

            string currentLabel;
            if (selectedRecipe != null)
                currentLabel = selectedRecipe.label;
            else
            {
                TaggedString selectTag = "_3DPrinters.SelectRecipeGizmo".Translate();
                currentLabel = selectTag.ToString();
            }

            var options = new List<FloatMenuOption>();
            foreach (var recipe in Props.supportedRecipes)
            {
                var r = recipe;
                Texture2D icon = recipe.products.Count > 0 ? recipe.products[0].thingDef?.uiIcon : null;
                if (icon == null) icon = TexCommand.DesirePower;
                options.Add(new FloatMenuOption(r.label, () => SelectRecipe(r), icon, Color.white));
            }

            TaggedString recipeTag = "_3DPrinters.RecipeGizmo".Translate(currentLabel);
            TaggedString recipeDescTag;

            if (!hasWorker && selectedRecipe != null)
            {
                string workTypeLabel = requiredWork != null ? requiredWork.label : "Unknown";
                TaggedString noWorkerTag = "_3DPrinters.NoWorkerDesc".Translate(workTypeLabel);
                recipeDescTag = noWorkerTag;
            }
            else
            {
                TaggedString normalDescTag = "_3DPrinters.RecipeGizmoDesc".Translate();
                recipeDescTag = normalDescTag;
            }

            Texture2D buttonIcon = GetProductIcon();

            yield return new Command_Action
            {
                defaultLabel = recipeTag.ToString(),
                defaultDesc = recipeDescTag.ToString(),
                icon = buttonIcon,
                action = () => Find.WindowStack.Add(new FloatMenu(options))
            };

            if (state == PrinterState.Processing || state == PrinterState.WaitingForIngredients)
            {
                TaggedString stopTag = "_3DPrinters.StopGizmo".Translate();
                TaggedString stopDescTag = "_3DPrinters.StopGizmoDesc".Translate();
                yield return new Command_Action
                {
                    defaultLabel = stopTag.ToString(),
                    defaultDesc = stopDescTag.ToString(),
                    icon = TexCommand.ForbidOn,
                    action = () => StopMachine()
                };
            }
            else if (state == PrinterState.Stopped)
            {
                TaggedString resumeTag = "_3DPrinters.ResumeGizmo".Translate();
                TaggedString resumeDescTag = "_3DPrinters.ResumeGizmoDesc".Translate();
                yield return new Command_Action
                {
                    defaultLabel = resumeTag.ToString(),
                    defaultDesc = resumeDescTag.ToString(),
                    icon = TexCommand.ForbidOff,
                    action = () => ResumeMachine()
                };
            }
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
                    if (workNeeded > 0)
                    {
                        int pct = (int)((float)workDone / workNeeded * 100f);
                        TaggedString processing = "_3DPrinters.ProcessingStatus".Translate(pct.ToString());
                        status = processing.ToString();
                    }
                    else
                    {
                        TaggedString processing = "_3DPrinters.ProcessingStatus".Translate("0");
                        status = processing.ToString();
                    }
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
            Scribe_Collections.Look(ref loadedIngredients, "loadedIngredients", LookMode.Deep);

            if (loadedIngredients == null)
                loadedIngredients = new List<Thing>();
        }
    }

    public class CompProperties_AutoProcessor : CompProperties
    {
        public int baseWorkAmount = 500;
        public string requiredWorkType = "Crafting";
        public List<RecipeDef> supportedRecipes;

        public CompProperties_AutoProcessor() => compClass = typeof(CompAutoProcessor);

        public WorkTypeDef GetWorkTypeDef()
        {
            return DefDatabase<WorkTypeDef>.GetNamed(requiredWorkType, false);
        }
    }
}