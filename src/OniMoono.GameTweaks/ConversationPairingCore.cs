using System;
using System.Collections.Generic;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// fast-conversation-pairing's planner without game types (unit-tested against the game's
    /// loop, ConversationPairingTests). The second loop of ConversationManager.Sim200ms takes
    /// each Duplicant `i` of the live list, in order, that is valid (ValidMinionTags), in no
    /// conversation and not on cooldown, and scans every `j` of the list in order for the first
    /// one that is valid, not `i`, and either (a) in a conversation C with fewer than
    /// maxDupesPerConvo members whose centroid is closer than maxDistance × 0.5 to `i`: `i` joins
    /// C; or (b) in no conversation, not on cooldown and closer than maxDistance to `i`: the two
    /// start a conversation. Every test reads state the loop does not change, but for who is in
    /// which conversation, which changes only by a decision, and a decision ends `i`'s scan. So
    /// `i`'s decision is the smallest `j` that passes, and any search that finds it decides the
    /// same.
    ///
    /// The planner finds it without scanning everyone: (a) per conversation, its first valid
    /// member by live index (a conversation passes or fails as a whole), with the centroid
    /// computed as GetCentroid does (the non-null members' positions summed in member order,
    /// divided by the member count, nulls included); (b) among the free Duplicants in the 3 × 3
    /// cells around `i` of a grid whose cell is larger than maxDistance (a superset of those
    /// closer than maxDistance; the game's distance test then decides), plus those whose
    /// position is not finite or very far out, which are checked for every `i`. Worlds are not
    /// used: the game's test ignores them, and two worlds can be closer than maxDistance.
    /// Distances are the game's expressions on Vector3: (a − b).magnitude.
    public sealed class ConversationPlanner
    {
        public const byte Join = 1, Create = 2;

        public struct Decision
        {
            public byte Kind;
            /// The live indices: `I` joins `J`'s conversation, or starts one with `J`.
            public int I, J;
            /// The conversation joined (an input conversation's index, or Inputs + the index of
            /// one this plan creates, in creation order).
            public int Conversation;
            public override string ToString() => (Kind == Join ? "join" : "create") + $"(i={I}, j={J}, c={Conversation})";
        }

        /// A conversation as the loop sees it: member count (nulls included), the non-null
        /// members' positions in member order, and the live indices of the Duplicants the game's
        /// table maps to it, ascending.
        sealed class Model
        {
            public int Count;
            public readonly List<Vector3> Positions = new List<Vector3>(4);
            public readonly List<int> Keys = new List<int>(4);
            public int CentroidCount = -1;
            public Vector3 Centroid;

            public void Reset()
            {
                Count = 0;
                Positions.Clear();
                Keys.Clear();
                CentroidCount = -1;
                Centroid = default;
            }

            /// ConversationManager.GetCentroid's arithmetic.
            [MirrorsGame("UnityEngine.Vector3 ConversationManager::GetCentroid(Conversation)")]
            public Vector3 GetCentroid()
            {
                if (CentroidCount == Count) return Centroid;
                Vector3 zero = Vector3.zero;
                for (int i = 0; i < Positions.Count; i++) zero += Positions[i];
                Centroid = zero / Count;
                CentroidCount = Count;
                return Centroid;
            }
        }

        readonly List<Model> models = new List<Model>();
        readonly Stack<Model> pool = new Stack<Model>();
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        readonly Stack<List<int>> lists = new Stack<List<int>>();
        readonly List<int> odd = new List<int>();
        readonly List<int> allFree = new List<int>();
        int[] member = new int[64];
        int inputs;

        /// The input: `n` Duplicants of the live list with, per index, ValidMinionTags (`valid`),
        /// MinionOnCooldown and position (read only for valid ones), and the conversation the
        /// game's table maps it to (`conversationOf`, -1 for none; set only for valid ones);
        /// `conversations` input conversations, each with its member count and its non-null
        /// members' positions in order (`AddMember`).
        public int N;
        public bool[] Valid = new bool[64], Cool = new bool[64];
        public Vector3[] Position = new Vector3[64];
        public int[] ConversationOf = new int[64];

        /// Starts an input of `n` Duplicants and `conversations` conversations.
        public void Begin(int n, int conversations)
        {
            N = n;
            if (Valid.Length < n)
            {
                int size = Math.Max(n, Valid.Length * 2);
                Valid = new bool[size];
                Cool = new bool[size];
                Position = new Vector3[size];
                ConversationOf = new int[size];
                member = new int[size];
            }
            Array.Clear(Valid, 0, n);
            Array.Clear(Cool, 0, n);
            for (int i = 0; i < n; i++) ConversationOf[i] = -1;
            foreach (var m in models) pool.Push(m);
            models.Clear();
            for (int c = 0; c < conversations; c++) NewModel();
            inputs = conversations;
        }

        /// One member of input conversation `c`, in member order: its position, or none for a
        /// null member (it counts, as in GetCentroid's division).
        public void AddMember(int c, Vector3? position)
        {
            var m = models[c];
            m.Count++;
            if (position is { } p) m.Positions.Add(p);
        }

        Model NewModel()
        {
            var m = pool.Count > 0 ? pool.Pop() : new Model();
            m.Reset();
            models.Add(m);
            return m;
        }

        static long CellKey(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        static void InsertSorted(List<int> list, int v)
        {
            int k = list.Count;
            list.Add(v);
            while (k > 0 && list[k - 1] > v)
            {
                list[k] = list[k - 1];
                k--;
            }
            list[k] = v;
        }

        // A NaN z makes the game's distance NaN, which passes its test at any x and y.
        static bool Finite(Vector3 p) => !(float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y) || float.IsNaN(p.z)
            || Math.Abs(p.x) > 1e7f || Math.Abs(p.y) > 1e7f);

        /// Fills `decisions` with the decisions the game's loop takes on this input, in order.
        /// False (decisions empty) when the input is outside what it handles: maxDistance not a
        /// positive finite number.
        public bool Plan(float maxDistance, int maxDupesPerConvo, List<Decision> decisions)
        {
            decisions.Clear();
            if (!(maxDistance > 0f) || float.IsInfinity(maxDistance)) return false;
            float half = maxDistance * 0.5f;
            int n = N;
            foreach (var kv in cells)
            {
                kv.Value.Clear();
                lists.Push(kv.Value);
            }
            cells.Clear();
            odd.Clear();
            allFree.Clear();
            for (int i = 0; i < n; i++)
            {
                member[i] = Valid[i] ? ConversationOf[i] : -1;
                if (member[i] >= 0) InsertSorted(models[member[i]].Keys, i);
            }
            // A cell larger than maxDistance: whoever is closer than maxDistance to `i` is in
            // the 3 × 3 cells around `i`'s.
            float cellSize = maxDistance * 1.0625f + 0.0625f;
            for (int i = 0; i < n; i++)
            {
                if (!Valid[i] || Cool[i] || member[i] >= 0) continue;
                allFree.Add(i);
                if (!Finite(Position[i]))
                {
                    odd.Add(i);
                    continue;
                }
                long key = CellKey((int)Math.Floor(Position[i].x / cellSize), (int)Math.Floor(Position[i].y / cellSize));
                if (!cells.TryGetValue(key, out var list))
                {
                    list = lists.Count > 0 ? lists.Pop() : new List<int>();
                    cells[key] = list;
                }
                list.Add(i);
            }
            for (int i = 0; i < n; i++)
            {
                if (!Valid[i] || member[i] >= 0 || Cool[i]) continue;
                int best = int.MaxValue, bestModel = -1;
                Vector3 pi = Position[i];
                // (a) join: per conversation, its first valid member other than `i`.
                for (int c = 0; c < models.Count; c++)
                {
                    var model = models[c];
                    if (model.Count >= maxDupesPerConvo) continue;
                    if ((model.GetCentroid() - pi).magnitude >= half) continue;
                    var keys = model.Keys;
                    for (int q = 0; q < keys.Count; q++)
                    {
                        int k = keys[q];
                        if (k == i) continue;
                        if (k < best)
                        {
                            best = k;
                            bestModel = c;
                        }
                        break;
                    }
                }
                // (b) start: the first free Duplicant closer than maxDistance.
                bool create = false;
                if (Finite(pi))
                {
                    int cx = (int)Math.Floor(pi.x / cellSize), cy = (int)Math.Floor(pi.y / cellSize);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            if (cells.TryGetValue(CellKey(cx + dx, cy + dy), out var list) && FirstFree(list, i, pi, maxDistance, ref best)) create = true;
                    if (FirstFree(odd, i, pi, maxDistance, ref best)) create = true;
                }
                else if (FirstFree(allFree, i, pi, maxDistance, ref best)) create = true;
                if (best == int.MaxValue) continue;
                if (create && member[best] < 0)
                {
                    var model = NewModel();
                    int c = models.Count - 1;
                    model.Count = 2;
                    model.Positions.Add(pi);
                    model.Positions.Add(Position[best]);
                    model.Keys.Add(Math.Min(i, best));
                    model.Keys.Add(Math.Max(i, best));
                    member[i] = c;
                    member[best] = c;
                    decisions.Add(new Decision { Kind = Create, I = i, J = best, Conversation = c });
                }
                else
                {
                    var model = models[bestModel];
                    model.Count++;
                    model.Positions.Add(pi);
                    InsertSorted(model.Keys, i);
                    member[i] = bestModel;
                    decisions.Add(new Decision { Kind = Join, I = i, J = best, Conversation = bestModel });
                }
            }
            return true;
        }

        /// Lowers `best` to the first index of `list` (ascending) below it that the game's start
        /// test accepts (free now, not `i`, closer than maxDistance); true if it did.
        bool FirstFree(List<int> list, int i, Vector3 pi, float maxDistance, ref int best)
        {
            for (int q = 0; q < list.Count; q++)
            {
                int j = list[q];
                if (j >= best) return false;
                if (j == i || member[j] >= 0) continue;
                if ((Position[j] - pi).magnitude >= maxDistance) continue;
                best = j;
                return true;
            }
            return false;
        }

        /// The number of input conversations (decisions' Conversation at or above it are created).
        public int Inputs => inputs;
    }
}
