using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace IndustryPlanner.Core
{
    /// <summary>What a home-screen card shows about a saved production.</summary>
    public class ProductionSummary
    {
        public ProductionGraph Graph;
        public (string item, float perMin)[] Inputs, Outputs;
        public float PowerMW;
    }

    /// <summary>Saves productions as JSON files in Application.persistentDataPath/Productions.</summary>
    public static class ProductionStore
    {
        static string Folder => Path.Combine(Application.persistentDataPath, "Productions");
        static string BlueprintFolder => Path.Combine(Application.persistentDataPath, "Blueprints");
        static string PathFor(string id) => Path.Combine(Folder, id + ".json");

        public static List<ProductionGraph> LoadAll()
        {
            bool firstRun = !Directory.Exists(Folder);
            Directory.CreateDirectory(Folder);
            if (firstRun) Save(CreateExample(), touch: false);

            var graphs = new List<ProductionGraph>();
            foreach (string file in Directory.GetFiles(Folder, "*.json"))
            {
                try { graphs.Add(JsonUtility.FromJson<ProductionGraph>(File.ReadAllText(file))); }
                catch (Exception e) { Debug.LogWarning($"Skipped unreadable production {file}: {e.Message}"); }
            }
            return graphs.OrderByDescending(g => g.lastEditedTicks).ToList();
        }

        // ---------------------------------------------------------------- production lines used as nodes

        static readonly Dictionary<string, (DateTime stamp, ProductionGraph graph, GraphAnalysis analysis)> s_Cache = new();
        static readonly HashSet<string> s_Solving = new(); // Guards against productions that contain each other.

        /// <summary>Solved analysis of a saved production, cached until its file changes. Null if missing or cyclic.</summary>
        public static GraphAnalysis AnalysisFor(string id) => Load(id).analysis;

        public static string NameOf(string id) => Load(id).graph?.name;

        static (ProductionGraph graph, GraphAnalysis analysis) Load(string id)
        {
            string path = PathFor(id ?? "");
            if (string.IsNullOrEmpty(id) || !File.Exists(path) || s_Solving.Contains(id)) return (null, null);
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (s_Cache.TryGetValue(id, out var hit) && hit.stamp == stamp) return (hit.graph, hit.analysis);

            s_Solving.Add(id);
            try
            {
                var graph = JsonUtility.FromJson<ProductionGraph>(File.ReadAllText(path));
                GameDatabase db = GameDatabase.Get(graph.gameId);
                GraphAnalysis analysis = db != null ? GraphSolver.Solve(db, graph) : null;
                s_Cache[id] = (stamp, graph, analysis);
                return (graph, analysis);
            }
            finally { s_Solving.Remove(id); }
        }

        /// <summary>A fresh copy of a saved production, safe to edit.</summary>
        public static ProductionGraph LoadGraph(string id) =>
            File.Exists(PathFor(id ?? "")) ? JsonUtility.FromJson<ProductionGraph>(File.ReadAllText(PathFor(id))) : null;

        public static void Save(ProductionGraph graph, bool touch = true)
        {
            if (touch) graph.lastEditedTicks = DateTime.Now.Ticks;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(graph.id), JsonUtility.ToJson(graph, true));
        }

        public static void Delete(string id)
        {
            if (File.Exists(PathFor(id))) File.Delete(PathFor(id));
        }

        // ---------------------------------------------------------------- blueprints

        public static List<Blueprint> LoadBlueprints(string gameId)
        {
            Directory.CreateDirectory(BlueprintFolder);
            var list = new List<Blueprint>();
            foreach (string file in Directory.GetFiles(BlueprintFolder, "*.json"))
            {
                try
                {
                    var bp = JsonUtility.FromJson<Blueprint>(File.ReadAllText(file));
                    if (bp.gameId == gameId) list.Add(bp);
                }
                catch (Exception e) { Debug.LogWarning($"Skipped unreadable blueprint {file}: {e.Message}"); }
            }
            return list.OrderBy(b => b.name).ToList();
        }

        public static void SaveBlueprint(Blueprint bp)
        {
            Directory.CreateDirectory(BlueprintFolder);
            File.WriteAllText(Path.Combine(BlueprintFolder, bp.id + ".json"), JsonUtility.ToJson(bp, true));
        }

        public static void DeleteBlueprint(string id)
        {
            string path = Path.Combine(BlueprintFolder, id + ".json");
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>The level used by the most recent production of a game, as the default for a new one.</summary>
        public static int LastTier(string gameId) =>
            LoadAll().FirstOrDefault(g => g.gameId == gameId && g.tier < 99)?.tier ?? 0;

        public static ProductionGraph Duplicate(ProductionGraph graph)
        {
            ProductionGraph copy = JsonUtility.FromJson<ProductionGraph>(JsonUtility.ToJson(graph));
            copy.id = Guid.NewGuid().ToString("N");
            copy.name = graph.name + " copy";
            Save(copy);
            return copy;
        }

        public static ProductionSummary Summarize(ProductionGraph graph)
        {
            var summary = new ProductionSummary { Graph = graph, Inputs = Array.Empty<(string, float)>(), Outputs = Array.Empty<(string, float)>() };
            GameDatabase db = GameDatabase.Get(graph.gameId);
            if (db == null) return summary;

            GraphAnalysis a = GraphSolver.Solve(db, graph);
            summary.Inputs = a.RawInputs.OrderByDescending(p => p.Value).Select(p => (db.ItemName(p.Key), p.Value)).ToArray();
            summary.Outputs = a.FinalOutputs.OrderByDescending(p => p.Value).Select(p => (db.ItemName(p.Key), p.Value)).ToArray();
            summary.PowerMW = a.PowerUsed - a.PowerProduced;
            return summary;
        }

        /// <summary>A small working graph so the first launch isn't empty: iron ore to reinforced plates.</summary>
        static ProductionGraph CreateExample()
        {
            var g = new ProductionGraph { gameId = "satisfactory", name = "Reinforced plates (example)", lastEditedTicks = DateTime.Now.Ticks };
            GraphNode miner = g.AddNode("Desc_MinerMk1_C", "Desc_OreIron_C", new Vector2(0, 160));
            GraphNode smelter = g.AddNode("Desc_SmelterMk1_C", "Recipe_IngotIron_C", new Vector2(320, 160));
            GraphNode plates = g.AddNode("Desc_ConstructorMk1_C", "Recipe_IronPlate_C", new Vector2(640, 40));
            GraphNode screws = g.AddNode("Desc_ConstructorMk1_C", "Recipe_Screw_C", new Vector2(640, 300));
            GraphNode rods = g.AddNode("Desc_ConstructorMk1_C", "Recipe_IronRod_C", new Vector2(320, 380));
            GraphNode rip = g.AddNode("Desc_AssemblerMk1_C", "Recipe_IronPlateReinforced_C", new Vector2(960, 160));
            smelter.count = 2; plates.count = 1.5f; screws.count = 1.5f; rods.count = 1;
            g.links.Add(new GraphLink(miner.id, smelter.id, "Desc_OreIron_C"));
            g.links.Add(new GraphLink(smelter.id, plates.id, "Desc_IronIngot_C"));
            g.links.Add(new GraphLink(smelter.id, rods.id, "Desc_IronIngot_C"));
            g.links.Add(new GraphLink(rods.id, screws.id, "Desc_IronRod_C"));
            g.links.Add(new GraphLink(plates.id, rip.id, "Desc_IronPlate_C"));
            g.links.Add(new GraphLink(screws.id, rip.id, "Desc_IronScrew_C"));
            return g;
        }
    }
}
