using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IndustryPlanner.Core
{
    /// <summary>Actual flow through one node after solving.</summary>
    public class NodeResult
    {
        public float Efficiency = 1f;                       // 0..1, limited by the most starved input.
        public readonly Dictionary<string, float> Nominal = new();   // item -> nominal rate (in or out)
        public readonly Dictionary<string, float> Actual = new();    // item -> rate actually consumed/produced
        public readonly HashSet<string> Imported = new();            // inputs with no link: assumed supplied from outside
        public readonly Dictionary<string, float> Received = new();  // linked inputs: what actually arrives over links
        public float Power;                                 // MW, + consumed, - produced
        public bool Underfed => Efficiency < 0.999f;
    }

    public class ItemBalance
    {
        public string Item;
        public float Produced, Needed, Imported, Exported;
        public float Net => Produced + Imported - Needed;
    }

    public class GraphAnalysis
    {
        public readonly Dictionary<string, NodeResult> Nodes = new();
        public readonly Dictionary<GraphLink, float> LinkFlow = new();
        public readonly Dictionary<GraphLink, float> LinkCapacity = new();
        public readonly HashSet<GraphLink> Overloaded = new();            // Would carry more than its belts allow.
        public readonly Dictionary<string, ItemBalance> Items = new();
        public readonly Dictionary<string, float> RawInputs = new();     // extractors, input nodes and imports
        public readonly Dictionary<string, float> FinalOutputs = new();  // output nodes and unlinked surplus
        public readonly Dictionary<string, float> ExternalInputs = new(); // input nodes and imports: what must come from outside
        public float PowerUsed, PowerProduced;
        public int UnderfedCount;
        public int OverloadedCount => Overloaded.Count;

        public ItemBalance Balance(string item) =>
            Items.TryGetValue(item, out var b) ? b : Items[item] = new ItemBalance { Item = item };
    }

    /// <summary>
    /// Pushes flow along links. Each output splits its supply between linked inputs in proportion to their
    /// demand; a machine then runs at the fraction of its most starved input. Unlinked inputs are treated as
    /// imports and unlinked outputs as exports, so half-built graphs still give useful numbers.
    /// Splitters share evenly between outputs (overflowing to those with room); every link is capped by its belt.
    /// </summary>
    public static class GraphSolver
    {
        const int MaxIterations = 40;

        public static GraphAnalysis Solve(GameDatabase db, ProductionGraph graph)
        {
            var result = new GraphAnalysis();
            var ports = new Dictionary<string, List<PortSpec>>();
            var kinds = new Dictionary<string, BuildingKind>();
            var outLinks = new Dictionary<(string, string), List<GraphLink>>();
            var linkedInputs = new HashSet<(string, string)>();

            foreach (GraphNode n in graph.nodes)
            {
                ports[n.id] = db.Ports(n);
                kinds[n.id] = db.Building(n.building)?.Kind ?? BuildingKind.Producer;
                result.Nodes[n.id] = new NodeResult { Power = db.Power(n) };
            }
            foreach (GraphLink l in graph.links)
            {
                if (!ports.ContainsKey(l.from) || !ports.ContainsKey(l.to)) continue;
                var key = (l.from, l.item);
                if (!outLinks.TryGetValue(key, out var list)) outLinks[key] = list = new List<GraphLink>();
                list.Add(l);
                linkedInputs.Add((l.to, l.item));
                result.LinkCapacity[l] = db.Capacity(l, graph);
            }

            var incoming = new Dictionary<(string, string), float>();
            var busIn = new Dictionary<string, float>();

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                incoming.Clear();
                result.LinkFlow.Clear();

                // 1. Distribute every output's supply over its links.
                foreach (GraphNode n in graph.nodes)
                {
                    float eff = result.Nodes[n.id].Efficiency;
                    foreach (PortSpec p in ports[n.id])
                    {
                        if (!p.IsOutput || !outLinks.TryGetValue((n.id, p.Item), out var links)) continue;
                        float available = GameDatabase.IsPassThrough(kinds[n.id]) ? busIn.GetValueOrDefault(n.id) : p.PerMin * eff;
                        Distribute(available, links, ports, incoming, result, even: kinds[n.id] == BuildingKind.Splitter);
                    }
                }

                // 2. Update each node's efficiency from what reached it.
                float maxDelta = 0f;
                foreach (GraphNode n in graph.nodes)
                {
                    NodeResult r = result.Nodes[n.id];
                    float eff = 1f;
                    foreach (PortSpec p in ports[n.id])
                    {
                        if (p.IsOutput || p.Unbounded || p.PerMin <= 0 || !linkedInputs.Contains((n.id, p.Item))) continue;
                        eff = Mathf.Min(eff, incoming.GetValueOrDefault((n.id, p.Item)) / p.PerMin);
                    }
                    if (GameDatabase.IsPassThrough(kinds[n.id]))
                    {
                        float inflow = ports[n.id].Count > 0 ? incoming.GetValueOrDefault((n.id, ports[n.id][0].Item)) : 0f;
                        maxDelta = Mathf.Max(maxDelta, Mathf.Abs(inflow - busIn.GetValueOrDefault(n.id)));
                        busIn[n.id] = inflow;
                    }
                    maxDelta = Mathf.Max(maxDelta, Mathf.Abs(eff - r.Efficiency));
                    r.Efficiency = eff;
                }
                if (maxDelta < 1e-4f && iter > 0) break;
            }

            // 3. Totals for the analysis panel and home cards.
            foreach (GraphNode n in graph.nodes)
            {
                NodeResult r = result.Nodes[n.id];
                BuildingKind kind = kinds[n.id];
                if (r.Underfed) result.UnderfedCount++;
                if (r.Power >= 0) result.PowerUsed += r.Power; else result.PowerProduced -= r.Power * r.Efficiency;

                foreach (PortSpec p in ports[n.id])
                {
                    bool linked = p.IsOutput ? outLinks.ContainsKey((n.id, p.Item)) : linkedInputs.Contains((n.id, p.Item));
                    float actual = kind switch
                    {
                        BuildingKind.Bus or BuildingKind.Splitter or BuildingKind.Merger => busIn.GetValueOrDefault(n.id),
                        BuildingKind.Output => incoming.GetValueOrDefault((n.id, p.Item)),
                        _ => p.PerMin * r.Efficiency
                    };
                    r.Nominal[p.Item] = p.Unbounded ? actual : p.PerMin;
                    r.Actual[p.Item] = actual;
                    if (!p.IsOutput && linked) r.Received[p.Item] = incoming.GetValueOrDefault((n.id, p.Item));
                    if (GameDatabase.IsPassThrough(kind)) continue; // Pass-through: not production.

                    ItemBalance bal = result.Balance(p.Item);
                    if (p.IsOutput)
                    {
                        bal.Produced += actual;
                        if (kind is BuildingKind.Extractor or BuildingKind.Input) Add(result.RawInputs, p.Item, actual);
                        if (kind == BuildingKind.Input) Add(result.ExternalInputs, p.Item, actual);
                        float sent = linked ? outLinks[(n.id, p.Item)].Sum(l => result.LinkFlow.GetValueOrDefault(l)) : 0f;
                        if (actual - sent > 0.001f) { bal.Exported += actual - sent; Add(result.FinalOutputs, p.Item, actual - sent); }
                    }
                    else if (kind == BuildingKind.Output)
                    {
                        Add(result.FinalOutputs, p.Item, actual);
                        bal.Needed += actual;
                    }
                    else
                    {
                        bal.Needed += p.PerMin;
                        if (!linked)
                        {
                            r.Imported.Add(p.Item);
                            bal.Imported += actual;
                            Add(result.RawInputs, p.Item, actual);
                            Add(result.ExternalInputs, p.Item, actual);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Shares an output's supply over its links. Normal outputs give bounded inputs a share in proportion to
        /// their demand, then the leftover to unbounded ones (outputs, splitters...). A splitter (even) gives every
        /// link the same share. Either way a link never takes more than its demand or its belt capacity, and what
        /// it can't take overflows to the others. Links whose demand exceeds their capacity are marked overloaded.
        /// </summary>
        static void Distribute(float available, List<GraphLink> links, Dictionary<string, List<PortSpec>> ports,
            Dictionary<(string, string), float> incoming, GraphAnalysis result, bool even)
        {
            int n = links.Count;
            var demand = new float[n];
            var cap = new float[n];
            var weight = new float[n];
            var unbounded = new bool[n];
            for (int i = 0; i < n; i++)
            {
                PortSpec target = ports[links[i].to].Find(p => !p.IsOutput && p.Item == links[i].item);
                unbounded[i] = target.Unbounded;
                demand[i] = target.Unbounded ? float.MaxValue : target.PerMin;
                cap[i] = result.LinkCapacity.GetValueOrDefault(links[i], float.MaxValue);
                weight[i] = even || target.Unbounded ? 1f : target.PerMin;
            }

            float[] given;
            if (even) given = Fill(available, Limits(demand, cap), weight, null);
            else
            {
                // Bounded inputs first (proportional), then whatever is left to unbounded ones (equal).
                given = Fill(available, Limits(demand, cap), weight, unbounded);
                float left = available;
                foreach (float g in given) left -= g;
                float[] extra = Fill(left, Limits(demand, cap), weight, Invert(unbounded));
                for (int i = 0; i < n; i++) given[i] += extra[i];
            }

            for (int i = 0; i < n; i++)
            {
                GraphLink l = links[i];
                result.LinkFlow[l] = given[i];
                var key = (l.to, l.item);
                incoming[key] = incoming.GetValueOrDefault(key) + given[i];
                // Overloaded: the belt is full while the receiver still wants more and supply had it to give.
                bool wantsMore = given[i] < demand[i] - 0.001f && given[i] >= cap[i] - 0.001f;
                if (wantsMore && available > 0.001f) result.Overloaded.Add(l); else result.Overloaded.Remove(l);
            }
        }

        static float[] Limits(float[] demand, float[] cap)
        {
            var limit = new float[demand.Length];
            for (int i = 0; i < limit.Length; i++) limit[i] = Mathf.Min(demand[i], cap[i]);
            return limit;
        }

        static bool[] Invert(bool[] mask)
        {
            var inverted = new bool[mask.Length];
            for (int i = 0; i < mask.Length; i++) inverted[i] = !mask[i];
            return inverted;
        }

        /// <summary>
        /// Water-filling: splits `amount` by weight, capping each entry at its limit and handing what a capped
        /// entry couldn't take to the rest. Entries with skip[i] true get nothing.
        /// </summary>
        static float[] Fill(float amount, float[] limit, float[] weight, bool[] skip)
        {
            int n = limit.Length;
            var given = new float[n];
            var full = new bool[n];
            for (int i = 0; i < n; i++) full[i] = (skip != null && skip[i]) || limit[i] <= 0f;
            float remaining = Mathf.Max(0f, amount);

            while (remaining > 0.0001f)
            {
                float sumWeight = 0f;
                for (int i = 0; i < n; i++) if (!full[i]) sumWeight += weight[i];
                if (sumWeight <= 0f) break;

                // Fill everyone by weight; anyone who would pass their limit is capped and the loop repeats.
                bool capped = false;
                float pool = remaining;
                for (int i = 0; i < n; i++)
                {
                    if (full[i]) continue;
                    float share = pool * weight[i] / sumWeight;
                    if (given[i] + share >= limit[i])
                    {
                        remaining -= limit[i] - given[i];
                        given[i] = limit[i];
                        full[i] = true;
                        capped = true;
                    }
                }
                if (capped) continue;
                for (int i = 0; i < n; i++) if (!full[i]) given[i] += pool * weight[i] / sumWeight;
                remaining = 0f;
            }
            return given;
        }

        static void Add(Dictionary<string, float> d, string item, float v) => d[item] = d.GetValueOrDefault(item) + v;
    }
}
