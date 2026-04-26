using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace _3DPrinters
{
    public class CompAutoProcessor : ThingComp
    {
        public CompProperties_AutoProcessor Props => (CompProperties_AutoProcessor)props;

        private RecipeDef currentRecipe;
        private int progress;
        private List<Thing> inputItems;
        private bool isProcessing = false;

        public bool IsProcessing => isProcessing;
        public RecipeDef CurrentRecipe => currentRecipe;
        public int Progress => progress;

        public override void CompTick()
        {
            base.CompTick();
            
            if (!isProcessing || currentRecipe == null) return;
            
            progress++;
            
            if (progress >= Props.GetWorkAmountForRecipe(currentRecipe))
            {
                FinishProcessing();
            }
        }

        private void FinishProcessing()
        {
            foreach (var product in currentRecipe.products)
            {
                for (int i = 0; i < product.count; i++)
                {
                    Thing thing = ThingMaker.MakeThing(product.thingDef);
                    GenSpawn.Spawn(thing, parent.Position, parent.Map);
                }
            }
            
            isProcessing = false;
            currentRecipe = null;
            progress = 0;
            inputItems = null;
        }

        public bool TryStartJob(RecipeDef recipe, Pawn worker, List<Thing> ingredients)
        {
            if (isProcessing) return false;
            if (!Props.CanProcessRecipe(recipe)) return false;
            if (!Props.HasRequiredSkills(worker, recipe)) return false;
            
            currentRecipe = recipe;
            inputItems = ingredients;
            
            foreach (var ingredient in ingredients)
            {
                ingredient.Destroy();
            }
            
            isProcessing = true;
            progress = 0;
            
            return true;
        }
        
        public bool HasEnoughIngredients(RecipeDef recipe, Map map, IntVec3 position)
        {
            return true;
        }

        public override string CompInspectStringExtra()
        {
            if (!isProcessing) return null;
            
            if (currentRecipe != null)
            {
                int workAmount = Props.GetWorkAmountForRecipe(currentRecipe);
                int ticksLeft = workAmount - progress;
                int hoursLeft = ticksLeft / 2500;
                
                string productName = currentRecipe.products[0].thingDef.label;
                
                return "_3DPrinters.ProcessingStatus".Translate(productName, hoursLeft.ToString());
            }
            
            return "_3DPrinters.ProcessingGeneric".Translate();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Defs.Look(ref currentRecipe, "currentRecipe");
            Scribe_Values.Look(ref progress, "progress", 0);
            Scribe_Values.Look(ref isProcessing, "isProcessing", false);
            Scribe_Collections.Look(ref inputItems, "inputItems", LookMode.Reference);
        }
    }

    public class CompProperties_AutoProcessor : CompProperties
    {
        public int baseWorkAmount = 500;
        public bool autoStartOnLoad = true;
        public List<RecipeDef> supportedRecipes;
        public SkillDef requiredSkill;
        public int minSkillLevel = 0;
        
        public CompProperties_AutoProcessor()
        {
            compClass = typeof(CompAutoProcessor);
        }
        
        public int GetWorkAmountForRecipe(RecipeDef recipe)
        {
            // Явное преобразование в int
            if (recipe.workAmount > 0)
                return (int)recipe.workAmount;
            return (int)baseWorkAmount;
        }
        
        public bool CanProcessRecipe(RecipeDef recipe)
        {
            if (supportedRecipes == null || supportedRecipes.Count == 0)
                return true;
            return supportedRecipes.Contains(recipe);
        }
        
        public bool HasRequiredSkills(Pawn pawn, RecipeDef recipe)
        {
            if (requiredSkill == null) return true;
            var skill = pawn.skills.GetSkill(requiredSkill);
            if (skill == null) return false;
            return skill.Level >= minSkillLevel;
        }
    }
}