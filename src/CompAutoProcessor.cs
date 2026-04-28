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
        public static JobDef HaulToPrinter;
    }

    // WorkGiver для переноски ингредиентов к принтеру
    public class WorkGiver_HaulToPrinter : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest =>
            ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial);
        public override PathEndMode PathEndMode => PathEndMode.Touch;
        public override bool Prioritized => false;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            if (pawn.workSettings == null || !pawn.workSettings.WorkIsActive(WorkTypeDefOf.Hauling))
                yield break;

            foreach (var building in pawn.Map.listerBuildings.allBuildingsColonist)
            {
                var comp = building.TryGetComp<CompAutoProcessor>();
                if (comp == null) continue;

                if (comp.NeedsHauling(pawn))
                    yield return building;
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (forced) return false;
            if (pawn.workSettings == null || !pawn.workSettings.WorkIsActive(WorkTypeDefOf.Hauling))
                return false;
            if (t.IsBurning()) return false;
            if (t.IsForbidden(pawn)) return false;

            var comp = t.TryGetComp<CompAutoProcessor>();
            if (comp == null) return false;

            return comp.NeedsHauling(pawn);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var comp = t.TryGetComp<CompAutoProcessor>();
            if (comp == null) return null;

            return comp.CreateHaulJob(pawn, t);
        }
    }

    // WorkGiver для работы на принтере (запуск)
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
            if (forced) return false;
            if (t.IsBurning()) return false;
            if (t.IsForbidden(pawn)) return false;

            var comp = t.TryGetComp<CompAutoProcessor>();
            if (comp == null) return false;

            WorkTypeDef requiredWork = comp.Props.GetWorkTypeDef();
            if (requiredWork == null) return false;
            if (pawn.workSettings != null && !pawn.workSettings.WorkIsActive(requiredWork))
                return false;

            return comp.NeedsWorkNow(pawn);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return JobMaker.MakeJob(_3DPrintersJobDefOf.Operate3DPrinter, t);
        }
    }

    // JobDriver для переноски ингредиентов
    public class JobDriver_HaulToPrinter : JobDriver
    {
        private Building_AutoProcessor Printer => (Building_AutoProcessor)job.targetA.Thing;
        private CompAutoProcessor Processor => Printer?.GetComp<CompAutoProcessor>();
        private Thing Ingredient => job.targetB.Thing;
        private int AmountToTake => job.count;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed))
                return false;
            if (Ingredient != null && !Ingredient.Destroyed && Ingredient.Spawned && !pawn.Reserve(Ingredient, job, 1, -1, null, errorOnFailed))
                return false;
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);

            // Toil 1: Подойти к ингредиенту (если есть) или к принтеру
            var gotoToil = new Toil();
            gotoToil.initAction = () =>
            {
                if (Ingredient != null && !Ingredient.Destroyed && Ingredient.Spawned)
                {
                    // Идём к ингредиенту
                    pawn.pather.StartPath(Ingredient, PathEndMode.ClosestTouch);
                }
                else
                {
                    // Ингредиента нет — идём к принтеру и завершаем
                    pawn.pather.StartPath(job.targetA.Thing, PathEndMode.Touch);
                }
            };
            gotoToil.defaultCompleteMode = ToilCompleteMode.PatherArrival;
            yield return gotoToil;

            // Toil 2: Взять ингредиент или завершить если нет
            var takeToil = new Toil
            {
                initAction = () =>
                {
                    if (Ingredient == null || Ingredient.Destroyed || !Ingredient.Spawned)
                    {
                        // Нечего брать — идём к принтеру
                        return;
                    }

                    int toTake = Mathf.Min(AmountToTake, Ingredient.stackCount);

                    if (toTake >= Ingredient.stackCount)
                    {
                        int stackCount = Ingredient.stackCount;
                        pawn.carryTracker.TryStartCarry(Ingredient, stackCount);
                        if (Ingredient.Spawned)
                        {
                            Ingredient.DeSpawn();
                        }
                    }
                    else
                    {
                        Thing splitThing = Ingredient.SplitOff(toTake);
                        if (splitThing != null)
                        {
                            pawn.carryTracker.TryStartCarry(splitThing, splitThing.stackCount);
                        }
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return takeToil;

            // Toil 3: Отнести к принтеру
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            // Toil 4: Загрузить в принтер
            var loadToil = new Toil
            {
                initAction = () =>
                {
                    if (Processor == null) return;

                    Thing carried = pawn.carryTracker.CarriedThing;
                    if (carried != null && !carried.Destroyed)
                    {
                        int count = carried.stackCount;
                        Processor.AddIngredient(carried.def, count);
                        carried.Destroy(DestroyMode.Vanish);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return loadToil;
        }
    }

    // JobDriver для запуска принтера
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

    // КОМБИНИРОВАННЫЙ JobDriver: переноска + запуск
    public class JobDriver_OperatePrinterWithHaul : JobDriver
    {
        private Building_AutoProcessor Printer => (Building_AutoProcessor)job.targetA.Thing;
        private CompAutoProcessor Processor => Printer?.GetComp<CompAutoProcessor>();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);

            var checkToil = new Toil
            {
                initAction = () =>
                {
                    if (Processor == null)
                    {
                        this.EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    if (Processor.NeedsWorkNow(pawn))
                    {
                        this.ReadyForNextToil();
                        return;
                    }

                    var missingIngredient = Processor.GetMissingIngredientForJob(pawn);
                    if (missingIngredient == null || missingIngredient.Destroyed || !missingIngredient.Spawned)
                    {
                        Messages.Message("_3DPrinters.NoIngredientsAvailable".Translate(),
                            MessageTypeDefOf.RejectInput);
                        this.EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    job.targetB = missingIngredient;
                    job.count = Processor.GetAmountNeeded(missingIngredient);
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return checkToil;

            var gotoIngredient = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch);
            gotoIngredient.FailOnDestroyedOrNull(TargetIndex.B);
            yield return gotoIngredient;

            var takeToil = new Toil
            {
                initAction = () =>
                {
                    var ingredient = job.targetB.Thing;
                    if (ingredient == null || ingredient.Destroyed || !ingredient.Spawned)
                    {
                        this.EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    int toTake = Mathf.Min(job.count, ingredient.stackCount);

                    if (toTake >= ingredient.stackCount)
                    {
                        int stackCount = ingredient.stackCount;
                        pawn.carryTracker.TryStartCarry(ingredient, stackCount);
                        if (ingredient.Spawned)
                        {
                            ingredient.DeSpawn();
                        }
                    }
                    else
                    {
                        Thing splitThing = ingredient.SplitOff(toTake);
                        if (splitThing != null)
                        {
                            pawn.carryTracker.TryStartCarry(splitThing, splitThing.stackCount);
                        }
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return takeToil;

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            var loadToil = new Toil
            {
                initAction = () =>
                {
                    if (Processor == null) return;

                    Thing carried = pawn.carryTracker.CarriedThing;
                    if (carried != null && !carried.Destroyed)
                    {
                        Processor.AddIngredient(carried.def, carried.stackCount);
                        carried.Destroy(DestroyMode.Vanish);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return loadToil;

            var operateToil = new Toil
            {
                initAction = () =>
                {
                    if (Processor != null && Processor.NeedsWorkNow(pawn))
                    {
                        Processor.StartWorking(pawn);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return operateToil;
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

            bool canHaul = selPawn.workSettings != null && selPawn.workSettings.WorkIsActive(WorkTypeDefOf.Hauling);

            // Пункт: загрузить ингредиенты
            if (comp.CurrentState == PrinterState.WaitingForIngredients && comp.SelectedRecipe != null && comp.NeedsHauling(selPawn))
            {
                TaggedString haulTag = "_3DPrinters.HaulToPrinterFloat".Translate();
                string haulLabel = haulTag.ToString();

                if (!canHaul)
                {
                    TaggedString disabledTag = "_3DPrinters.HaulToPrinterDisabled".Translate();
                    yield return new FloatMenuOption(haulLabel, null)
                    {
                        Disabled = true,
                        Label = haulLabel + " (" + disabledTag.ToString() + ")"
                    };
                }
                else
                {
                    yield return new FloatMenuOption(haulLabel, () =>
                    {
                        Job job = comp.CreateHaulJob(selPawn, this);
                        if (job != null)
                            selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    });
                }
            }

            // Пункт: обслужить принтер
            if (comp.CurrentState == PrinterState.WaitingForIngredients && comp.SelectedRecipe != null)
            {
                TaggedString forceTag = "_3DPrinters.ForceOperateFloat".Translate();
                string forceLabel = forceTag.ToString();

                bool hasEnough = comp.NeedsWorkNow(selPawn);
                bool canOperate = comp.Props.GetWorkTypeDef() != null &&
                                  selPawn.workSettings != null &&
                                  selPawn.workSettings.WorkIsActive(comp.Props.GetWorkTypeDef());

                if (!canOperate)
                {
                    string workLabel = comp.Props.GetWorkTypeDef()?.label ?? "Unknown";
                    TaggedString disabledTag = "_3DPrinters.ForceOperateDisabled".Translate(workLabel);
                    yield return new FloatMenuOption(forceLabel, null)
                    {
                        Disabled = true,
                        Label = forceLabel + " (" + disabledTag.ToString() + ")"
                    };
                }
                else if (!hasEnough)
                {
                    TaggedString missingTag = "_3DPrinters.ForceOperateMissingIngredients".Translate();
                    yield return new FloatMenuOption(forceLabel, null)
                    {
                        Disabled = true,
                        Label = forceLabel + " (" + missingTag.ToString() + ")"
                    };
                }
                else
                {
                    yield return new FloatMenuOption(forceLabel, () =>
                    {
                        Job job = comp.NeedsWorkNow(selPawn)
                            ? JobMaker.MakeJob(_3DPrintersJobDefOf.Operate3DPrinter, this)
                            : comp.CreateCombinedJob(selPawn, this);
                        if (job != null)
                            selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    });
                }
            }

            // Остановить
            if (comp.CurrentState == PrinterState.Processing || comp.CurrentState == PrinterState.WaitingForIngredients)
            {
                TaggedString stopTag = "_3DPrinters.StopFloat".Translate();
                string stopLabel = stopTag.ToString();
                yield return new FloatMenuOption(stopLabel, () => comp.StopMachine());
            }

            // Возобновить
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
            if (state != PrinterState.Processing) return;
            if (workNeeded <= 0) return;

            var power = parent.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return;

            workDone++;
            if (workDone >= workNeeded) FinishWork();
        }

        private void ResetProcessingState()
        {
            state = PrinterState.WaitingForIngredients;
            workDone = 0;
            workNeeded = 0;
            if (loadedIngredients != null) loadedIngredients.Clear();
        }

        private void FinishWork()
        {
            if (selectedRecipe == null) { ResetProcessingState(); return; }
            var map = parent.Map;
            if (map == null) { ResetProcessingState(); return; }
            var pos = parent.Position;

            foreach (var product in selectedRecipe.products)
            {
                Thing thing = ThingMaker.MakeThing(product.thingDef);
                thing.stackCount = product.count;
                GenSpawn.Spawn(thing, pos, map);
            }
            ResetProcessingState();
        }

        public void AddIngredient(ThingDef def, int count)
        {
            if (def == null || count <= 0) return;
            if (loadedIngredients == null) loadedIngredients = new List<Thing>();

            var existing = loadedIngredients.FirstOrDefault(t => t != null && t.def == def);
            if (existing != null)
                existing.stackCount += count;
            else
            {
                Thing tempThing = ThingMaker.MakeThing(def);
                if (tempThing != null)
                {
                    tempThing.stackCount = count;
                    loadedIngredients.Add(tempThing);
                }
            }
        }

        public bool NeedsHauling(Pawn pawn)
        {
            if (state != PrinterState.WaitingForIngredients) return false;
            if (selectedRecipe == null) return false;
            var power = parent.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return false;
            return GetMissingIngredient(pawn) != null;
        }

        public Thing GetMissingIngredientForJob(Pawn pawn)
        {
            return GetMissingIngredient(pawn);
        }

        public int GetAmountNeeded(Thing ingredient)
        {
            if (selectedRecipe == null || ingredient == null) return 0;
            foreach (var ingredientDef in selectedRecipe.ingredients)
            {
                if (!ingredientDef.filter.Allows(ingredient.def)) continue;
                float required = ingredientDef.GetBaseCount();
                float loaded = loadedIngredients.Where(t => t != null && ingredientDef.filter.Allows(t.def)).Sum(t => (float)t.stackCount);
                return Mathf.CeilToInt(required - loaded);
            }
            return 0;
        }

        private Thing GetMissingIngredient(Pawn pawn)
        {
            if (selectedRecipe == null) return null;
            var map = parent.Map;
            if (map == null) return null;

            foreach (var ingredientDef in selectedRecipe.ingredients)
            {
                float required = ingredientDef.GetBaseCount();
                float loaded = loadedIngredients.Where(t => t != null && ingredientDef.filter.Allows(t.def)).Sum(t => (float)t.stackCount);
                float missing = required - loaded;
                if (missing <= 0) continue;

                foreach (var thing in map.listerThings.ThingsMatching(ThingRequest.ForGroup(ThingRequestGroup.HaulableEver)))
                {
                    if (!ingredientDef.filter.Allows(thing.def)) continue;
                    if (thing.IsForbidden(pawn)) continue;
                    if (!pawn.CanReserve(thing)) continue;
                    return thing;
                }
            }
            return null;
        }

        public Job CreateHaulJob(Pawn pawn, Thing printer)
        {
            var ingredient = GetMissingIngredient(pawn);
            if (ingredient == null) return null;

            int toTake = GetAmountNeeded(ingredient);
            toTake = Mathf.Min(toTake, ingredient.stackCount);

            var job = JobMaker.MakeJob(_3DPrintersJobDefOf.HaulToPrinter, printer, ingredient);
            job.count = toTake;
            return job;
        }

        public Job CreateCombinedJob(Pawn pawn, Thing printer)
        {
            return JobMaker.MakeJob(_3DPrintersJobDefOf.Operate3DPrinter, printer);
        }

        public bool NeedsWorkNow(Pawn pawn)
        {
            if (state != PrinterState.WaitingForIngredients) return false;
            if (selectedRecipe == null) return false;
            var power = parent.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return false;

            foreach (var ingredientDef in selectedRecipe.ingredients)
            {
                float required = ingredientDef.GetBaseCount();
                float loaded = loadedIngredients.Where(t => t != null && ingredientDef.filter.Allows(t.def)).Sum(t => (float)t.stackCount);
                if (loaded < required) return false;
            }
            return true;
        }

        public void StartWorking(Pawn pawn)
        {
            if (state != PrinterState.WaitingForIngredients || selectedRecipe == null) return;
            if (!NeedsWorkNow(pawn)) return;
            workNeeded = selectedRecipe.workAmount > 0 ? Mathf.CeilToInt(selectedRecipe.workAmount) : Props.baseWorkAmount;
            workDone = 0;
            state = PrinterState.Processing;
        }

        public string GetInspectStringExtra()
        {
            if (selectedRecipe == null)
                return "_3DPrinters.NoRecipeSelected".Translate().ToString();

            string result = "";
            switch (state)
            {
                case PrinterState.WaitingForIngredients:
                    result = "_3DPrinters.WaitingForIngredients".Translate().ToString();
                    break;
                case PrinterState.Processing:
                    int pct = workNeeded > 0 ? (int)((float)workDone / workNeeded * 100f) : 0;
                    result = "_3DPrinters.ProcessingStatus".Translate(pct.ToString()).ToString();
                    break;
                case PrinterState.Stopped:
                    result = "_3DPrinters.Stopped".Translate().ToString();
                    break;
            }

            result += "\n" + "_3DPrinters.RecipeLabel".Translate(selectedRecipe.label).ToString();

            if (loadedIngredients != null && loadedIngredients.Count > 0)
            {
                result += "\n" + "_3DPrinters.LoadedIngredients".Translate() + ":";
                foreach (var ing in loadedIngredients)
                    if (ing != null && ing.stackCount > 0)
                        result += "\n  " + ing.def.label + " x" + ing.stackCount;
            }

            return result;
        }

        public void SelectRecipe(RecipeDef recipe)
        {
            selectedRecipe = recipe;
            state = PrinterState.WaitingForIngredients;
            workDone = 0;
            workNeeded = 0;
            if (loadedIngredients != null) loadedIngredients.Clear();
        }

        public void StopMachine()
        {
            if (loadedIngredients != null && loadedIngredients.Count > 0) ReturnStoredIngredients();
            state = PrinterState.Stopped;
            if (loadedIngredients != null) loadedIngredients.Clear();
            workDone = 0;
            workNeeded = 0;
        }

        public void ResumeMachine()
        {
            if (loadedIngredients == null) loadedIngredients = new List<Thing>();
            else loadedIngredients.Clear();
            workDone = 0;
            workNeeded = 0;
            state = selectedRecipe != null ? PrinterState.WaitingForIngredients : PrinterState.NoRecipe;
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
            Messages.Message("_3DPrinters.IngredientsReturned".Translate(), new TargetInfo(parent.Position, map), MessageTypeDefOf.NeutralEvent);
        }

        private bool HasWorkerAvailable()
        {
            WorkTypeDef requiredWork = Props.GetWorkTypeDef();
            if (requiredWork == null) return false;
            var map = parent.Map;
            if (map == null) return false;
            foreach (var pawn in map.mapPawns.FreeColonists)
                if (pawn.workSettings != null && pawn.workSettings.WorkIsActive(requiredWork))
                    return true;
            return false;
        }

        public IEnumerable<Gizmo> GetGizmos()
        {
            if (Props == null || Props.supportedRecipes == null || Props.supportedRecipes.Count == 0)
                yield break;

            bool hasWorker = HasWorkerAvailable();
            WorkTypeDef requiredWork = Props.GetWorkTypeDef();

            string currentLabel = selectedRecipe != null ? selectedRecipe.label : "_3DPrinters.SelectRecipeGizmo".Translate().ToString();

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
                recipeDescTag = "_3DPrinters.RecipeGizmoDesc".Translate();
            }

            yield return new Command_Action
            {
                defaultLabel = recipeTag.ToString(),
                defaultDesc = recipeDescTag.ToString(),
                icon = GetProductIcon(),
                action = () => Find.WindowStack.Add(new FloatMenu(options))
            };

            if (state == PrinterState.Processing || state == PrinterState.WaitingForIngredients)
            {
                yield return new Command_Action
                {
                    defaultLabel = "_3DPrinters.StopGizmo".Translate().ToString(),
                    defaultDesc = "_3DPrinters.StopGizmoDesc".Translate().ToString(),
                    icon = TexCommand.ForbidOn,
                    action = () => StopMachine()
                };
            }
            else if (state == PrinterState.Stopped)
            {
                yield return new Command_Action
                {
                    defaultLabel = "_3DPrinters.ResumeGizmo".Translate().ToString(),
                    defaultDesc = "_3DPrinters.ResumeGizmoDesc".Translate().ToString(),
                    icon = TexCommand.ForbidOff,
                    action = () => ResumeMachine()
                };
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state, "state", PrinterState.NoRecipe);
            Scribe_Defs.Look(ref selectedRecipe, "selectedRecipe");
            Scribe_Values.Look(ref workDone, "workDone", 0);
            Scribe_Values.Look(ref workNeeded, "workNeeded", 0);
            Scribe_Collections.Look(ref loadedIngredients, "loadedIngredients", LookMode.Deep);
            if (loadedIngredients == null) loadedIngredients = new List<Thing>();
        }
    }

    public class CompProperties_AutoProcessor : CompProperties
    {
        public int baseWorkAmount = 500;
        public string requiredWorkType = "Crafting";
        public List<RecipeDef> supportedRecipes;
        public CompProperties_AutoProcessor() => compClass = typeof(CompAutoProcessor);
        public WorkTypeDef GetWorkTypeDef() => DefDatabase<WorkTypeDef>.GetNamed(requiredWorkType, false);
    }
}