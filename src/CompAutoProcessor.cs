using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace _3DPrinters
{
    // ==================== JobDefs ====================
    [DefOf]
    public static class _3DPrintersJobDefOf
    {
        public static JobDef Operate3DPrinter;
    }

    // ==================== WorkGiver ====================
    public class WorkGiver_Operate3DPrinter : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => 
            ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial);
        
        public override PathEndMode PathEndMode => PathEndMode.Touch;
        public override bool Prioritized => true;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (var building in pawn.Map.listerBuildings.allBuildingsColonist)
            {
                var comp = building.TryGetComp<CompAutoProcessor>();
                if (comp != null && comp.ShouldBeWorkedOn(pawn))
                {
                    yield return building;
                }
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t.IsBurning()) return false;
            if (t.IsForbidden(pawn)) return false;
            if (!pawn.CanReserve(t, 1, -1, null, forced)) return false;
            
            var comp = t.TryGetComp<CompAutoProcessor>();
            if (comp == null) return false;
            
            return comp.ShouldBeWorkedOn(pawn);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var comp = t.TryGetComp<CompAutoProcessor>();
            if (comp == null) return null;
            
            return comp.GetJobFor(pawn, t);
        }
    }

    // ==================== JobDriver ====================
    public class JobDriver_Operate3DPrinter : JobDriver
    {
        private Building_AutoProcessor Printer => (Building_AutoProcessor)job.targetA.Thing;
        private CompAutoProcessor Processor => Printer?.Processor;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);

            // Подойти к принтеру
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            // Запустить принтер
            var operateToil = new Toil
            {
                initAction = () =>
                {
                    if (Processor != null)
                    {
                        Processor.TryStartMachine(pawn);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return operateToil;
        }
    }

    // ==================== Building Class ====================
    public class Building_AutoProcessor : Building
    {
        private CompAutoProcessor processorComp;

        public CompAutoProcessor Processor
        {
            get
            {
                if (processorComp == null)
                    processorComp = GetComp<CompAutoProcessor>();
                return processorComp;
            }
        }

        public override void Tick()
        {
            base.Tick();
            Processor?.CompTick();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            if (Processor != null)
            {
                foreach (var gizmo in Processor.GetGizmos())
                {
                    yield return gizmo;
                }
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (Processor != null)
            {
                string extra = Processor.CompInspectStringExtra();
                if (!extra.NullOrEmpty())
                {
                    if (!text.NullOrEmpty())
                        text += "\n";
                    text += extra;
                }
            }
            return text;
        }

        public override void ExposeData()
        {
            base.ExposeData();
        }
    }

    // ==================== Printer States ====================
    public enum PrinterState
    {
        Idle,
        NoRecipe,
        WaitingForIngredients,
        Processing,
        Paused,
        Stopped
    }

    // ==================== CompAutoProcessor ====================
    public class CompAutoProcessor : ThingComp
    {
        public CompProperties_AutoProcessor Props => (CompProperties_AutoProcessor)props;

        private PrinterState state = PrinterState.NoRecipe;
        private RecipeDef selectedRecipe;
        private int progress;
        private int ticksToComplete;
        private const int INGREDIENT_SEARCH_RADIUS = 8;

        public PrinterState CurrentState => state;
        public RecipeDef SelectedRecipe => selectedRecipe;
        public int Progress => progress;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            
            if (Props.supportedRecipes != null && Props.supportedRecipes.Count > 0)
            {
                state = PrinterState.NoRecipe;
            }
        }

        public override void CompTick()
        {
            base.CompTick();

            if (state == PrinterState.Stopped || state == PrinterState.Idle)
                return;

            var powerComp = parent.GetComp<CompPowerTrader>();
            bool hasPower = powerComp == null || powerComp.PowerOn;

            if (!hasPower)
            {
                if (state == PrinterState.Processing)
                    state = PrinterState.Paused;
                return;
            }

            if (state == PrinterState.Paused && hasPower)
            {
                state = PrinterState.Processing;
            }

            if (state == PrinterState.Processing)
            {
                progress++;
                if (progress >= ticksToComplete)
                {
                    FinishProcessing();
                }
            }
        }

        private void FinishProcessing()
        {
            if (selectedRecipe == null) return;

            var map = parent.Map;
            var pos = parent.Position;

            foreach (var product in selectedRecipe.products)
            {
                int count = product.count;
                for (int i = 0; i < count; i++)
                {
                    Thing thing = ThingMaker.MakeThing(product.thingDef);
                    GenPlace.TryPlaceThing(thing, pos, map, ThingPlaceMode.Near);
                }
            }

            // После завершения снова ждём ингредиенты
            state = PrinterState.WaitingForIngredients;
            progress = 0;
        }

        public bool ShouldBeWorkedOn(Pawn pawn)
        {
            // Только если ждём ингредиенты и выбран рецепт
            if (selectedRecipe == null) return false;
            if (state != PrinterState.WaitingForIngredients) return false;
            
            var powerComp = parent.GetComp<CompPowerTrader>();
            if (powerComp != null && !powerComp.PowerOn) return false;
            
            // Проверяем наличие ингредиентов
            return HasIngredientsAvailable(pawn);
        }

        private bool HasIngredientsAvailable(Pawn pawn)
        {
            if (selectedRecipe == null) return false;

            var map = parent.Map;
            var pos = parent.Position;

            foreach (var ingredientDef in selectedRecipe.ingredients)
            {
                float required = ingredientDef.GetBaseCount();
                float found = 0;

                // Ищем в радиусе от принтера
                var cells = GenRadial.RadialCellsAround(pos, INGREDIENT_SEARCH_RADIUS, true);
                foreach (var cell in cells)
                {
                    if (found >= required) break;

                    var things = cell.GetThingList(map);
                    foreach (var thing in things)
                    {
                        if (found >= required) break;
                        if (!ingredientDef.filter.Allows(thing.def)) continue;
                        if (thing.IsForbidden(pawn)) continue;
                        if (!pawn.CanReserve(thing)) continue;

                        found += thing.stackCount;
                    }
                }

                if (found < required)
                    return false;
            }

            return true;
        }

        public void TryStartMachine(Pawn pawn)
        {
            if (selectedRecipe == null) return;
            if (state != PrinterState.WaitingForIngredients) return;

            // Потребляем ингредиенты
            if (!ConsumeIngredients(pawn))
            {
                return;
            }

            state = PrinterState.Processing;
            progress = 0;
            ticksToComplete = Props.GetWorkAmountForRecipe(selectedRecipe);
        }

        private bool ConsumeIngredients(Pawn pawn)
        {
            if (selectedRecipe == null) return false;

            var map = parent.Map;
            var pos = parent.Position;

            foreach (var ingredientDef in selectedRecipe.ingredients)
            {
                float required = ingredientDef.GetBaseCount();
                float consumed = 0;

                var cells = GenRadial.RadialCellsAround(pos, INGREDIENT_SEARCH_RADIUS, true);
                foreach (var cell in cells)
                {
                    if (consumed >= required) break;

                    var things = cell.GetThingList(map).ToList();
                    foreach (var thing in things)
                    {
                        if (consumed >= required) break;
                        if (!ingredientDef.filter.Allows(thing.def)) continue;
                        if (thing.IsForbidden(pawn)) continue;

                        float toConsume = Mathf.Min(required - consumed, thing.stackCount);
                        if (toConsume >= thing.stackCount)
                        {
                            consumed += thing.stackCount;
                            thing.Destroy(DestroyMode.Vanish);
                        }
                        else
                        {
                            thing.stackCount -= Mathf.CeilToInt(toConsume);
                            consumed += toConsume;
                        }
                    }
                }

                if (consumed < required)
                    return false;
            }

            return true;
        }

        public Job GetJobFor(Pawn pawn, Thing target)
        {
            return JobMaker.MakeJob(_3DPrintersJobDefOf.Operate3DPrinter, target);
        }

        public void SelectRecipe(RecipeDef recipe)
        {
            selectedRecipe = recipe;
            state = PrinterState.WaitingForIngredients;  // ВАЖНО: сразу WaitingForIngredients
            progress = 0;
            ticksToComplete = Props.GetWorkAmountForRecipe(recipe);
        }

        public IEnumerable<Gizmo> GetGizmos()
        {
            if (Props == null || Props.supportedRecipes == null || Props.supportedRecipes.Count == 0)
            {
                yield break;
            }

            // Кнопка выбора рецепта
            string currentLabel = "Select recipe";
            if (selectedRecipe != null)
            {
                currentLabel = selectedRecipe.label ?? selectedRecipe.defName;
            }
            
            var recipeOptions = new List<FloatMenuOption>();
            
            foreach (var recipe in Props.supportedRecipes)
            {
                string recipeName = recipe.label ?? recipe.defName;
                
                recipeOptions.Add(new FloatMenuOption(
                    recipeName,
                    () => SelectRecipe(recipe),
                    TexCommand.DesirePower,
                    Color.white
                ));
            }
            
            yield return new Command_Action
            {
                defaultLabel = "Recipe: " + currentLabel,
                defaultDesc = "Click to select recipe.",
                icon = TexCommand.DesirePower,
                action = () =>
                {
                    Find.WindowStack.Add(new FloatMenu(recipeOptions));
                }
            };

            // Кнопка остановки
            if (state == PrinterState.Processing || state == PrinterState.WaitingForIngredients || state == PrinterState.Paused)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Stop production",
                    defaultDesc = "Stop the production process.",
                    icon = TexCommand.ForbidOn,
                    action = () =>
                    {
                        state = PrinterState.Stopped;
                        progress = 0;
                    }
                };
            }
            // Кнопка возобновления
            else if (state == PrinterState.Stopped)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Resume production",
                    defaultDesc = "Resume the production process.",
                    icon = TexCommand.ForbidOff,
                    action = () =>
                    {
                        if (selectedRecipe != null)
                        {
                            state = PrinterState.WaitingForIngredients;
                        }
                        else
                        {
                            state = PrinterState.NoRecipe;
                        }
                        progress = 0;
                    }
                };
            }
        }

        public override string CompInspectStringExtra()
        {
            // Возвращаем только дополнительную информацию
            // Базовая GetInspectString покажет состояние из самого Building
            if (selectedRecipe != null)
            {
                string recipeLabel = selectedRecipe.label ?? selectedRecipe.defName;
                string statusText = "";
                
                switch (state)
                {
                    case PrinterState.WaitingForIngredients:
                        statusText = "Waiting for ingredients";
                        break;
                    case PrinterState.Processing:
                        float pct = (float)progress / ticksToComplete * 100f;
                        statusText = $"Processing: {pct:F0}%";
                        break;
                    case PrinterState.Paused:
                        statusText = "Paused (no power)";
                        break;
                    case PrinterState.Stopped:
                        statusText = "Stopped";
                        break;
                }
                
                if (!string.IsNullOrEmpty(statusText))
                    return $"{statusText}\nRecipe: {recipeLabel}";
                else
                    return $"Recipe: {recipeLabel}";
            }
            
            return null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref state, "state", PrinterState.NoRecipe);
            Scribe_Defs.Look(ref selectedRecipe, "selectedRecipe");
            Scribe_Values.Look(ref progress, "progress", 0);
            Scribe_Values.Look(ref ticksToComplete, "ticksToComplete", 0);
        }
    }

    // ==================== CompProperties ====================
    public class CompProperties_AutoProcessor : CompProperties
    {
        public int baseWorkAmount = 500;
        public bool autoStartOnLoad = true;
        public List<RecipeDef> supportedRecipes;

        public CompProperties_AutoProcessor()
        {
            compClass = typeof(CompAutoProcessor);
        }

        public int GetWorkAmountForRecipe(RecipeDef recipe)
        {
            if (recipe.workAmount > 0)
                return Mathf.CeilToInt(recipe.workAmount);
            return baseWorkAmount;
        }
    }
}