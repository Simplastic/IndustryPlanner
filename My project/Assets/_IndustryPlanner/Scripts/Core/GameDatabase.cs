using System;
using System.Collections.Generic;
using UnityEngine;

namespace IndustryPlanner.Core
{
    // ---------------------------------------------------------------------------------------------
    // Serializable definitions, mirroring Resources/Games/<id>.json. All rates are per minute.
    // ---------------------------------------------------------------------------------------------

    [Serializable] public class ItemDef { public string id, name; public bool fluid; public int sinkPoints, tier; }

    [Serializable] public class ItemRateDef { public string item; public float perMin; }

    [Serializable]
    public class RecipeDef
    {
        public string id, name, building;
        public bool alternate;
        public float time;
        public ItemRateDef[] inputs, outputs;
        [Tooltip("Average MW for variable-power recipes; 0 means use the building's power.")]
        public float power;
        public int tier;               // Progression level that unlocks it (see GameDef.progression).
    }

    [Serializable]
    public class FuelDef { public string item; public float perMin, waterPerMin; public string byproduct; public float byproductPerMin; }

    /// <summary>What a node does. Logistics nodes (input/output/bus) are virtual, they don't exist in game.</summary>
    /// <remarks>Production: a whole saved production line used as one node ("bigger picture" planning).</remarks>
    /// <remarks>Splitter / Merger / Bus pass items through: a splitter shares evenly between up to 3 outputs (overflow
    /// goes to outputs with room), a merger joins up to 3 inputs, a bus takes any number of links.</remarks>
    public enum BuildingKind { Producer, Extractor, Generator, Input, Output, Bus, Production, Splitter, Merger }

    /// <summary>A belt or pipe tier with its throughput per minute.</summary>
    [Serializable] public class TransportDef { public string id, name; public float rate; public bool fluid; public int tier; }

    [Serializable]
    public class BuildingDef
    {
        public string id, name, category, kind;
        [Tooltip("MW consumed at 100% clock. Negative for generators (MW produced).")]
        public float power, powerExponent;
        public float extractRate;      // Extractors: items/min on a normal node at 100%.
        public string[] resources;     // Extractors: what they can mine.
        public FuelDef[] fuels;        // Generators: accepted fuels.
        public int tier;               // Progression level that unlocks it.
        public int amplifierSlots;     // Production amplifier slots (Somersloops); 0 = none.
        public int maxIn, maxOut;      // Max links on the input / output pin; 0 = unlimited.

        [NonSerialized] public BuildingKind Kind;
    }

    [Serializable] public class HotbarSlotDef { public string key, label; public string[] buildings; }

    /// <summary>One step of a game's progression, e.g. "Tier 4" / "Phase 1 delivered".</summary>
    [Serializable] public class ProgressionLevel { public int value; public string name, detail; }

    [Serializable]
    public class GameDef
    {
        public string id, name, version, themeClass, logo, tagline;
        public string progressionLabel;     // "Tier"; empty when the game has no progression.
        public ProgressionLevel[] progression;
        public TransportDef[] transport;    // Belt and pipe tiers; empty means links have no capacity limit.
        public string amplifierName;        // "Somersloops"; empty when the game has no amplifiers.
        public string boostName;            // "Power shards": each one raises the clock limit by boostStep.
        public float boostStep = 0.5f, maxClock = 2.5f;
        public ItemDef[] items;
        public BuildingDef[] buildings;
        public RecipeDef[] recipes;
        public HotbarSlotDef[] hotbar;
    }

    /// <summary>A pin on a node: one item flowing in or out at a nominal rate.</summary>
    public readonly struct PortSpec
    {
        public readonly string Item;
        public readonly float PerMin;   // Nominal rate for the whole node (count and clock applied).
        public readonly bool IsOutput;
        public readonly bool Unbounded; // Output/bus inputs accept any amount.

        public PortSpec(string item, float perMin, bool isOutput, bool unbounded = false)
        {
            Item = item; PerMin = perMin; IsOutput = isOutput; Unbounded = unbounded;
        }
    }

    /// <summary>One game's static data plus lookups. Load every game with <see cref="LoadAll"/>.</summary>
    public class GameDatabase
    {
        public static readonly float[] PurityMultipliers = { 0.5f, 1f, 2f };
        public static readonly string[] PurityNames = { "Impure", "Normal", "Pure" };

        public GameDef Def { get; }
        public string Id => Def.id;
        public string Name => Def.name;

        readonly Dictionary<string, ItemDef> m_Items = new();
        readonly Dictionary<string, BuildingDef> m_Buildings = new();
        readonly Dictionary<string, RecipeDef> m_Recipes = new();
        readonly Dictionary<string, List<RecipeDef>> m_RecipesByBuilding = new();

        static Dictionary<string, GameDatabase> s_Games;

        /// <summary>All games found in Resources/Games, keyed by id. Loaded once.</summary>
        public static IReadOnlyDictionary<string, GameDatabase> LoadAll()
        {
            if (s_Games != null) return s_Games;
            s_Games = new Dictionary<string, GameDatabase>();
            foreach (TextAsset asset in Resources.LoadAll<TextAsset>("Games"))
            {
                var db = new GameDatabase(JsonUtility.FromJson<GameDef>(asset.text));
                s_Games[db.Id] = db;
            }
            return s_Games;
        }

        public static GameDatabase Get(string id) => LoadAll().TryGetValue(id ?? "", out var db) ? db : null;

        GameDatabase(GameDef def)
        {
            Def = def;
            foreach (ItemDef i in def.items) m_Items[i.id] = i;
            foreach (BuildingDef b in def.buildings)
            {
                b.Kind = Enum.TryParse(b.kind, true, out BuildingKind k) ? k : BuildingKind.Producer;
                m_Buildings[b.id] = b;
            }
            foreach (RecipeDef r in def.recipes)
            {
                m_Recipes[r.id] = r;
                if (!m_RecipesByBuilding.TryGetValue(r.building, out var list))
                    m_RecipesByBuilding[r.building] = list = new List<RecipeDef>();
                list.Add(r);
            }
        }

        public bool HasProgression => Def.progression is { Length: > 0 };

        public static bool IsPassThrough(BuildingKind k) => k is BuildingKind.Bus or BuildingKind.Splitter or BuildingKind.Merger;

        /// <summary>Pin item of an empty splitter/merger/bus: it accepts any item until the first link sets it.</summary>
        public const string AnyItem = "*";

        /// <summary>The belt or pipe a link uses: the one chosen, else the fastest unlocked of the right kind.</summary>
        public TransportDef Transport(GraphLink l, ProductionGraph g)
        {
            if (Def.transport is not { Length: > 0 }) return null;
            bool fluid = Item(l.item) is { fluid: true };
            TransportDef chosen = Array.Find(Def.transport, t => t.id == l.transport);
            if (chosen != null) return chosen;
            TransportDef best = null;
            foreach (TransportDef t in Def.transport)
                if (t.fluid == fluid && (best == null || (t.tier <= g.tier && t.rate > best.rate))) best = t;
            return best;
        }

        public IEnumerable<TransportDef> TransportsFor(GraphLink l, ProductionGraph g)
        {
            bool fluid = Item(l.item) is { fluid: true };
            foreach (TransportDef t in Def.transport ?? Array.Empty<TransportDef>())
                if (t.fluid == fluid && (!HasProgression || t.tier <= g.tier)) yield return t;
        }

        /// <summary>Items per minute a link can carry: tier rate times parallel lines.</summary>
        public float Capacity(GraphLink l, ProductionGraph g) =>
            Transport(l, g) is { } t ? t.rate * Mathf.Max(1, l.lanes) : float.MaxValue;
        public int MaxLevel => HasProgression ? Def.progression[Def.progression.Length - 1].value : 0;

        public string LevelName(int level) =>
            HasProgression ? Array.Find(Def.progression, p => p.value == level)?.name ?? $"{Def.progressionLabel} {level}" : "";

        // Unlock checks against a production's progression level and its alternate-recipe setting.
        public bool IsUnlocked(BuildingDef b, ProductionGraph g) => !HasProgression || b.tier <= g.tier;
        public bool IsUnlocked(ItemDef i, ProductionGraph g) => !HasProgression || i == null || i.tier <= g.tier;
        public bool IsUnlocked(RecipeDef r, ProductionGraph g) =>
            !HasProgression || (r.tier <= g.tier && (!r.alternate || g.alternates) && IsUnlocked(Building(r.building), g));

        /// <summary>Is everything this node uses available at the production's level?</summary>
        public bool IsUnlocked(GraphNode n, ProductionGraph g)
        {
            BuildingDef b = Building(n.building);
            if (b == null || !IsUnlocked(b, g)) return false;
            return b.Kind switch
            {
                BuildingKind.Producer => Recipe(n.selection) is not { } r || IsUnlocked(r, g),
                BuildingKind.Production => true,
                _ => IsUnlocked(Item(n.selection), g)
            };
        }

        /// <summary>Boost items needed for a clock speed (e.g. power shards: one per 50% above 100%).</summary>
        public int BoostsNeeded(float clock) =>
            string.IsNullOrEmpty(Def.boostName) || clock <= 1f ? 0 : Mathf.CeilToInt((clock - 1f) / Def.boostStep - 0.0001f);

        /// <summary>Output multiplier from filled amplifier slots: a full set doubles output.</summary>
        public float Amplification(GraphNode n) =>
            Building(n.building) is { amplifierSlots: > 0 } b ? 1f + Mathf.Clamp(n.amplifiers, 0, b.amplifierSlots) / (float)b.amplifierSlots : 1f;

        public ItemDef Item(string id) => id != null && m_Items.TryGetValue(id, out var i) ? i : null;
        public BuildingDef Building(string id) => id != null && m_Buildings.TryGetValue(id, out var b) ? b : null;
        public RecipeDef Recipe(string id) => id != null && m_Recipes.TryGetValue(id, out var r) ? r : null;
        public string ItemName(string id) => id == AnyItem ? "Any item" : Item(id)?.name ?? id;

        public IReadOnlyList<RecipeDef> RecipesFor(string buildingId) =>
            m_RecipesByBuilding.TryGetValue(buildingId, out var l) ? l : (IReadOnlyList<RecipeDef>)Array.Empty<RecipeDef>();

        /// <summary>Every recipe that produces (or consumes) the item, used when a link is dropped on empty canvas.</summary>
        public IEnumerable<RecipeDef> RecipesWith(string itemId, bool asOutput)
        {
            foreach (RecipeDef r in Def.recipes)
                foreach (ItemRateDef x in asOutput ? r.outputs : r.inputs)
                    if (x.item == itemId) { yield return r; break; }
        }

        /// <summary>Nominal pins of a node, with machine count and clock speed applied.</summary>
        public List<PortSpec> Ports(GraphNode node)
        {
            var ports = new List<PortSpec>();
            BuildingDef b = Building(node.building);
            if (b == null) return ports;
            if (string.IsNullOrEmpty(node.selection))
            {
                // Pass-through nodes take whatever is connected first.
                if (IsPassThrough(b.Kind))
                {
                    ports.Add(new PortSpec(AnyItem, 0, false, unbounded: true));
                    ports.Add(new PortSpec(AnyItem, 0, true));
                }
                return ports;
            }
            float scale = node.count * node.clock;

            switch (b.Kind)
            {
                case BuildingKind.Producer:
                    RecipeDef r = Recipe(node.selection);
                    if (r == null) break;
                    foreach (ItemRateDef x in r.inputs) ports.Add(new PortSpec(x.item, x.perMin * scale, false));
                    float amp = Amplification(node);
                    foreach (ItemRateDef x in r.outputs) ports.Add(new PortSpec(x.item, x.perMin * scale * amp, true));
                    break;
                case BuildingKind.Extractor:
                    ports.Add(new PortSpec(node.selection, b.extractRate * PurityMultipliers[Mathf.Clamp(node.purity, 0, 2)] * scale, true));
                    break;
                case BuildingKind.Generator:
                    FuelDef f = Fuel(b, node.selection);
                    if (f == null) break;
                    ports.Add(new PortSpec(f.item, f.perMin * scale, false));
                    if (f.waterPerMin > 0) ports.Add(new PortSpec("Desc_Water_C", f.waterPerMin * scale, false));
                    if (!string.IsNullOrEmpty(f.byproduct)) ports.Add(new PortSpec(f.byproduct, f.byproductPerMin * scale, true));
                    break;
                case BuildingKind.Input:
                    ports.Add(new PortSpec(node.selection, node.rate, true));
                    break;
                case BuildingKind.Output:
                    ports.Add(new PortSpec(node.selection, 0, false, unbounded: true));
                    break;
                case BuildingKind.Bus or BuildingKind.Splitter or BuildingKind.Merger:
                    ports.Add(new PortSpec(node.selection, 0, false, unbounded: true));
                    ports.Add(new PortSpec(node.selection, 0, true)); // Rate set by the solver from what arrives.
                    break;
                case BuildingKind.Production:
                    // A production line needs what it takes from outside and delivers its final outputs, per copy.
                    GraphAnalysis sub = ProductionStore.AnalysisFor(node.selection);
                    if (sub == null) break;
                    foreach (var x in sub.ExternalInputs) ports.Add(new PortSpec(x.Key, x.Value * node.count, false));
                    foreach (var x in sub.FinalOutputs) ports.Add(new PortSpec(x.Key, x.Value * node.count, true));
                    break;
            }
            return ports;
        }

        /// <summary>MW at full efficiency: positive = consumed, negative = produced.</summary>
        public float Power(GraphNode node)
        {
            BuildingDef b = Building(node.building);
            if (b == null || string.IsNullOrEmpty(node.selection)) return 0;
            if (b.Kind == BuildingKind.Generator) return b.power * node.count * node.clock;
            if (b.Kind == BuildingKind.Production)
                return ProductionStore.AnalysisFor(node.selection) is { } sub ? (sub.PowerUsed - sub.PowerProduced) * node.count : 0f;
            float basePower = b.Kind == BuildingKind.Producer && Recipe(node.selection) is { power: > 0 } r ? r.power : b.power;
            float amp = Amplification(node);
            return basePower * node.count * Mathf.Pow(node.clock, b.powerExponent) * amp * amp; // Amplified machines use amp² power.
        }

        /// <summary>Short title for a node: recipe name, mined resource, fuel, or logistics item.</summary>
        public string NodeTitle(GraphNode node)
        {
            BuildingDef b = Building(node.building);
            if (b == null) return node.building;
            if (string.IsNullOrEmpty(node.selection)) return b.name;
            return b.Kind switch
            {
                BuildingKind.Producer => Recipe(node.selection)?.name ?? node.selection,
                BuildingKind.Production => ProductionStore.NameOf(node.selection) ?? "Missing production",
                _ => ItemName(node.selection)
            };
        }

        public static FuelDef Fuel(BuildingDef b, string item)
        {
            foreach (FuelDef f in b.fuels) if (f.item == item) return f;
            return null;
        }
    }
}
