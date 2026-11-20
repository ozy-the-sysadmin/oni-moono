using System;
using System.Collections.Generic;
using OniMoono.GameTweaks;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The fast-conversation-pairing game tweak's planner (src/OniMoono.GameTweaks/
    /// ConversationPairingCore.cs) against the game's loop 2 of ConversationManager.Sim200ms,
    /// written here as the game has it (every Duplicant scanned in order, the first that passes
    /// taken), on random colonies: the same decisions in the same order.
    public class ConversationPairingTests
    {
        /// A conversation as the game's loop sees it: its member list (null for a destroyed
        /// member; others by position).
        sealed class Convo
        {
            public readonly List<Vector3?> Members = new List<Vector3?>();
        }

        sealed class Colony
        {
            public int N;
            public bool[] Valid, Cool;
            public Vector3[] Pos;
            /// The conversation the game's table maps each Duplicant to (-1: none).
            public int[] Of;
            public List<Convo> Convos = new List<Convo>();
        }

        /// The game's loop 2 on a copy of the colony.
        static List<ConversationPlanner.Decision> GameLoop(Colony c, float maxDistance, int maxPer)
        {
            var convos = new List<Convo>();
            foreach (var x in c.Convos)
            {
                var copy = new Convo();
                copy.Members.AddRange(x.Members);
                convos.Add(copy);
            }
            var of = (int[])c.Of.Clone();
            var result = new List<ConversationPlanner.Decision>();
            Vector3 Centroid(Convo x)
            {
                Vector3 zero = Vector3.zero;
                foreach (var m in x.Members)
                    if (m is { } p) zero += p;
                return zero / x.Members.Count;
            }
            for (int i = 0; i < c.N; i++)
            {
                if (!c.Valid[i] || of[i] >= 0 || c.Cool[i]) continue;
                for (int j = 0; j < c.N; j++)
                {
                    if (j == i || !c.Valid[j]) continue;
                    if (of[j] >= 0)
                    {
                        var value = convos[of[j]];
                        if (value.Members.Count < maxPer && !((Centroid(value) - c.Pos[i]).magnitude >= maxDistance * 0.5f))
                        {
                            value.Members.Add(c.Pos[i]);
                            of[i] = of[j];
                            result.Add(new ConversationPlanner.Decision { Kind = ConversationPlanner.Join, I = i, J = j, Conversation = of[j] });
                            break;
                        }
                    }
                    else if (!c.Cool[j] && !((c.Pos[j] - c.Pos[i]).magnitude >= maxDistance))
                    {
                        var made = new Convo();
                        made.Members.Add(c.Pos[i]);
                        made.Members.Add(c.Pos[j]);
                        convos.Add(made);
                        of[i] = of[j] = convos.Count - 1;
                        result.Add(new ConversationPlanner.Decision { Kind = ConversationPlanner.Create, I = i, J = j, Conversation = convos.Count - 1 });
                        break;
                    }
                }
            }
            return result;
        }

        static List<ConversationPlanner.Decision> Planned(ConversationPlanner planner, Colony c, float maxDistance, int maxPer)
        {
            planner.Begin(c.N, c.Convos.Count);
            for (int k = 0; k < c.Convos.Count; k++)
                foreach (var m in c.Convos[k].Members) planner.AddMember(k, m);
            for (int i = 0; i < c.N; i++)
            {
                if (!c.Valid[i]) continue;
                planner.Valid[i] = true;
                planner.Cool[i] = c.Cool[i];
                planner.Position[i] = c.Pos[i];
                planner.ConversationOf[i] = c.Of[i];
            }
            var decisions = new List<ConversationPlanner.Decision>();
            Assert.True(planner.Plan(maxDistance, maxPer, decisions));
            return decisions;
        }

        static Colony Random(System.Random rng, int n, float spread)
        {
            var c = new Colony { N = n, Valid = new bool[n], Cool = new bool[n], Pos = new Vector3[n], Of = new int[n] };
            int convos = rng.Next(0, 4);
            for (int k = 0; k < convos; k++) c.Convos.Add(new Convo());
            for (int i = 0; i < n; i++)
            {
                c.Valid[i] = rng.NextDouble() < 0.85;
                c.Cool[i] = rng.NextDouble() < 0.3;
                float x = (float)(rng.NextDouble() * spread), y = (float)(rng.NextDouble() * spread * 0.4);
                // Positions on half cells, as Duplicants stand, and a few odd ones.
                if (rng.NextDouble() < 0.7) { x = MathF.Round(x * 2) / 2; y = MathF.Round(y); }
                double odd = rng.NextDouble();
                if (odd < 0.02) x = float.NaN;
                else if (odd < 0.03) y = float.PositiveInfinity;
                else if (odd < 0.04) x = 3e7f;
                float z = odd < 0.05 && odd >= 0.04 ? float.NaN : -0.1f * (i % 3);
                c.Pos[i] = new Vector3(x, y, z);
                c.Of[i] = -1;
                if (c.Valid[i] && convos > 0 && rng.NextDouble() < 0.25)
                {
                    int k = rng.Next(convos);
                    c.Of[i] = k;
                    c.Convos[k].Members.Add(c.Pos[i]);
                }
            }
            // Members the live list doesn't have (gone or destroyed), as loop 1 can leave them.
            foreach (var x in c.Convos)
            {
                if (rng.NextDouble() < 0.3) x.Members.Add(null);
                if (rng.NextDouble() < 0.3) x.Members.Add(new Vector3((float)(rng.NextDouble() * spread), 3f, 0f));
            }
            return c;
        }

        [Fact]
        public void MakesTheGamesDecisionsOnRandomColonies()
        {
            var planner = new ConversationPlanner();
            var rng = new System.Random(4021);
            int decisions = 0, joins = 0;
            for (int run = 0; run < 3000; run++)
            {
                int n = rng.Next(0, 70);
                float spread = run % 3 == 0 ? 12f : run % 3 == 1 ? 40f : 200f;
                int maxPer = rng.Next(2, 6);
                float maxDistance = run % 5 == 0 ? 3.5f : 7f;
                var colony = Random(rng, n, spread);
                var expected = GameLoop(colony, maxDistance, maxPer);
                var got = Planned(planner, colony, maxDistance, maxPer);
                Assert.Equal(string.Join(", ", expected), string.Join(", ", got));
                decisions += expected.Count;
                foreach (var d in expected) if (d.Kind == ConversationPlanner.Join) joins++;
            }
            // The colonies are dense enough to take many decisions of both kinds.
            Assert.True(decisions > 5000 && joins > 500 && decisions - joins > 500, $"{decisions} decisions, {joins} joins");
        }

        [Fact]
        public void FindsAPartnerJustInsideTheDistanceAcrossACellEdge()
        {
            var planner = new ConversationPlanner();
            // maxDistance 7: the planner's cells are 7.5 wide; these pairs straddle edges.
            foreach (var (a, b) in new[] { (7.49f, 14.48f), (-0.01f, 6.98f), (-7.6f, -0.62f), (7.5f, 0.51f) })
            {
                var colony = new Colony { N = 2, Valid = new[] { true, true }, Cool = new bool[2], Pos = new[] { new Vector3(a, 1f, 0f), new Vector3(b, 1f, 0f) }, Of = new[] { -1, -1 } };
                Assert.Equal(string.Join(", ", GameLoop(colony, 7f, 4)), string.Join(", ", Planned(planner, colony, 7f, 4)));
                Assert.Single(Planned(planner, colony, 7f, 4));
            }
        }

        [Fact]
        public void RefusesADistanceItCannotGrid()
        {
            var planner = new ConversationPlanner();
            planner.Begin(0, 0);
            var decisions = new List<ConversationPlanner.Decision>();
            Assert.False(planner.Plan(float.NaN, 4, decisions));
            Assert.False(planner.Plan(0f, 4, decisions));
            Assert.False(planner.Plan(float.PositiveInfinity, 4, decisions));
        }
    }
}
