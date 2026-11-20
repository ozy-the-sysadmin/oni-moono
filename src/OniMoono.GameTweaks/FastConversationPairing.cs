using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-conversation-pairing`. Five times a game second
    /// ConversationManager.Sim200ms first updates the running conversations (loop 1), then, for
    /// every free Duplicant of the colony, scans every other Duplicant for a conversation to join
    /// or a partner to start one with (loop 2): the number of Duplicants squared, with a
    /// GetComponent, a tag test and a cooldown lookup per pair.
    ///
    /// The tweak runs loop 1 as the game's code (transcribed, in the method's fingerprint) and
    /// plans loop 2 with ConversationPlanner, which finds the same first partner for each
    /// Duplicant by looking only at conversations and at Duplicants standing nearby (see its
    /// comment for why the decisions are the same). Each Duplicant's ValidMinionTags,
    /// MinionOnCooldown and position are read once, in list order, only for valid ones (as the
    /// game reads them before using them). The decisions are then applied with the game's own
    /// operations in the game's order, so UnityEngine.Random.Range runs once per new conversation
    /// in the same order and the same conversation types are made. Constructors run after the
    /// plan instead of during the scan, which is the same only for the game's three
    /// conversation types (their constructors set a name): with another type in the list, or
    /// duplicate entries in the live list, the transcribed loop 2 runs.
    ///
    /// With ONI_TWEAK_VERIFY=1 every call plans, then runs the transcribed loop 2 with its
    /// decisions noted and compares them with the plan; the counts are logged every 256 checks.
    ///
    /// Off for the session once another Harmony owner patches Sim200ms or a method whose calls it
    /// changes (GetCentroid, ValidMinionTags, MinionOnCooldown, MinionCloseEnoughToConvo; checked
    /// at the first call, then every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void ConversationManager::Sim200ms(System.Single)", "e1eb2460d7eccd78 win-764025")]
    [GameMember(GameUse.Mirrors, "UnityEngine.Vector3 ConversationManager::GetCentroid(Conversation)", "ee2d56a285de9093 win-764025")]
    [GameMember(GameUse.Uses, "System.Boolean ConversationManager::ValidMinionTags(MinionIdentity)")]
    [GameMember(GameUse.Uses, "System.Boolean ConversationManager::MinionOnCooldown(MinionIdentity)")]
    [GameMember(GameUse.Uses, "System.Boolean ConversationManager::MinionCloseEnoughToConvo(MinionIdentity,Conversation)")]
    public static class FastConversationPairing
    {
        public const string Name = "fast-conversation-pairing";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase target;
        static MethodBase[] guarded;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt, ONI_FRAME_STATS_AB): true runs the game's method.
        public static bool Bypass;
        /// Counts since the start: calls, calls planned, decisions taken; verify mode's checks and
        /// mismatches.
        public static long Calls, Planned, Decisions, Wrong;
        static long verified;

        public static void Counters(Action<string, long> counter)
        {
            counter("calls", Calls);
            counter("planned", Planned);
            counter("ran-game", Calls - Planned);
            counter("decisions", Decisions);
            counter("verified", verified);
            counter("wrong", Wrong);
            counter("stood-aside", off ? 1 : 0);
        }

        static readonly ConversationPlanner planner = new ConversationPlanner();
        static readonly List<ConversationPlanner.Decision> plan = new List<ConversationPlanner.Decision>();
        static readonly List<ConversationPlanner.Decision> noted = new List<ConversationPlanner.Decision>();
        static readonly Dictionary<MinionIdentity, int> indexOf = new Dictionary<MinionIdentity, int>();
        static readonly Dictionary<Conversation, int> numberOf = new Dictionary<Conversation, int>();
        static readonly List<Conversation> byNumber = new List<Conversation>();
        static readonly Predicate<MinionIdentity> isCommunal = match => match.HasTag(GameTags.CommunalDining);
        static readonly Predicate<MinionIdentity> isNotPartying = match => !match.HasTag(GameTags.Partying);

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            target = guarded[0];
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FastConversationPairing), nameof(Sim200ms)) { priority = Priority.Last });
        }

        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(ConversationManager), nameof(ConversationManager.Sim200ms), new[] { typeof(float) }),
            AccessTools.Method(typeof(ConversationManager), nameof(ConversationManager.GetCentroid), new[] { typeof(Conversation) }),
            AccessTools.Method(typeof(ConversationManager), nameof(ConversationManager.ValidMinionTags), new[] { typeof(MinionIdentity) }),
            AccessTools.Method(typeof(ConversationManager), nameof(ConversationManager.MinionOnCooldown), new[] { typeof(MinionIdentity) }),
            AccessTools.Method(typeof(ConversationManager), nameof(ConversationManager.MinionCloseEnoughToConvo), new[] { typeof(MinionIdentity), typeof(Conversation) }),
        ];

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        /// ConversationManager.Sim200ms: loop 1 as the game's, loop 2 planned.
        static bool Sim200ms(ConversationManager __instance, bool __runOriginal)
        {
            if (!__runOriginal || off || Bypass) return __runOriginal;
            if ((Calls++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return true;
            }
            // From here the game's state changes: the game's method must not run as well.
            Loop1(__instance);
            bool planned = Inputs(__instance) && planner.Plan(TuningData<ConversationManager.Tuning>.Get().maxDistance, TuningData<ConversationManager.Tuning>.Get().maxDupesPerConvo, plan);
            if (planned && verify)
            {
                noted.Clear();
                Loop2(__instance, noted);
                verified++;
                if (!Same(plan, noted)) global::Debug.Log($"{Name}: a plan is wrong ({++Wrong}): planned [{string.Join(", ", plan)}], the game's loop [{string.Join(", ", noted)}]");
                if ((verified & 0xFF) == 1) global::Debug.Log($"{Name}: {Calls} calls, {verified} checked, {Wrong} wrong");
            }
            else if (planned)
            {
                Planned++;
                Apply(__instance);
            }
            else Loop2(__instance, null);
            __instance.minionConversations.Clear();
            byNumber.Clear();
            numberOf.Clear();
            indexOf.Clear();
            return false;
        }

        static bool Same(List<ConversationPlanner.Decision> a, List<ConversationPlanner.Decision> b)
        {
            if (a.Count != b.Count) return false;
            for (int k = 0; k < a.Count; k++)
                if (a[k].Kind != b[k].Kind || a[k].I != b[k].I || a[k].J != b[k].J) return false;
            return true;
        }

        /// The planner's input, read from the game; false when the plan would not be the
        /// game's: another conversation type, duplicates in the live list.
        static bool Inputs(ConversationManager cm)
        {
            var types = cm.convoTypes;
            for (int t = 0; t < types.Count; t++)
            {
                var type = types[t];
                if (type != typeof(RecentThingConversation) && type != typeof(AmountStateConversation) && type != typeof(CurrentJobConversation)) return false;
            }
            var items = Components.LiveMinionIdentities.Items;
            int n = items.Count;
            // The conversations as loop 1 left them: those the game's table maps a Duplicant to.
            foreach (var kv in cm.minionConversations)
                if (!numberOf.ContainsKey(kv.Value))
                {
                    numberOf[kv.Value] = byNumber.Count;
                    byNumber.Add(kv.Value);
                }
            planner.Begin(n, byNumber.Count);
            for (int c = 0; c < byNumber.Count; c++)
                foreach (var minion in byNumber[c].minions)
                    planner.AddMember(c, minion == null ? null : minion.transform.GetPosition());
            for (int i = 0; i < n; i++)
            {
                var item = items[i];
                if (!cm.ValidMinionTags(item)) continue;
                if (indexOf.ContainsKey(item)) return false;
                indexOf[item] = i;
                planner.Valid[i] = true;
                planner.Cool[i] = cm.MinionOnCooldown(item);
                planner.Position[i] = item.transform.GetPosition();
                if (cm.minionConversations.TryGetValue(item, out var conversation)) planner.ConversationOf[i] = numberOf[conversation];
            }
            return true;
        }

        /// The plan's decisions, with the game's operations in the game's order.
        static void Apply(ConversationManager cm)
        {
            var items = Components.LiveMinionIdentities.Items;
            foreach (var d in plan)
            {
                var item = items[d.I];
                if (d.Kind == ConversationPlanner.Join)
                {
                    var value = byNumber[d.Conversation];
                    value.minions.Add(item);
                    cm.minionConversations[item] = value;
                }
                else
                {
                    // Created conversations are numbered after the inputs, in creation order.
                    byNumber.Add(Start(cm, item, items[d.J]));
                }
                Decisions++;
            }
        }

        /// The game's "start a conversation" block (part of Sim200ms' fingerprint).
        static Conversation Start(ConversationManager cm, MinionIdentity item, MinionIdentity item2)
        {
            var value = new Conversation();
            value.minions.Add(item);
            value.minions.Add(item2);
            Type type = cm.convoTypes[UnityEngine.Random.Range(0, cm.convoTypes.Count)];
            value.conversationType = (ConversationType)Activator.CreateInstance(type);
            value.lastTalkedTime = GameClock.Instance.GetTime();
            cm.conversations.Add(value);
            cm.minionConversations[item] = value;
            cm.minionConversations[item2] = value;
            return value;
        }

        /// Loop 1 of ConversationManager.Sim200ms, transcribed (part of its fingerprint).
        static void Loop1(ConversationManager cm)
        {
            var conversations = cm.conversations;
            for (int num = conversations.Count - 1; num >= 0; num--)
            {
                Conversation conversation = conversations[num];
                for (int num2 = conversation.minions.Count - 1; num2 >= 0; num2--)
                {
                    MinionIdentity minionIdentity = conversation.minions[num2];
                    if (!cm.ValidMinionTags(minionIdentity) || !cm.MinionCloseEnoughToConvo(minionIdentity, conversation))
                    {
                        conversation.minions.RemoveAt(num2);
                        if (conversation.lastTalked == minionIdentity) conversation.lastTalked = null;
                    }
                    else cm.minionConversations[minionIdentity] = conversation;
                }
                if (conversation.minions.Count <= 1) conversations.RemoveAt(num);
                else if (!(conversation.lastTalked != null) || !conversation.lastTalked.GetComponent<KPrefabID>().HasTag(GameTags.DoNotInterruptMe))
                {
                    bool communal = conversation.minions.Find(isCommunal) != null;
                    bool flag = true;
                    if (!communal && conversation.numUtterances >= TuningData<ConversationManager.Tuning>.Get().maxUtterances) flag = false;
                    else
                    {
                        bool flag2 = conversation.numUtterances == 0;
                        bool allPartying = conversation.minions.Find(isNotPartying) == null;
                        float num5 = flag2 ? TuningData<ConversationManager.Tuning>.Get().delayBeforeStart : TuningData<ConversationManager.Tuning>.Get().delayBetweenUtterances;
                        if (allPartying) num5 = 0f;
                        float num6 = flag2 ? 0f : TuningData<ConversationManager.Tuning>.Get().speakTime;
                        if (allPartying) num6 /= 4f;
                        num6 += num5;
                        if (GameClock.Instance.GetTime() > conversation.lastTalkedTime + num6) flag = cm.TryContinueConversation(conversation, flag2);
                    }
                    if (!flag) conversations.RemoveAt(num);
                }
            }
        }

        /// Loop 2 of ConversationManager.Sim200ms, transcribed (part of its fingerprint): the
        /// fallback, and verify mode's reference (its decisions noted in `log`).
        static void Loop2(ConversationManager cm, List<ConversationPlanner.Decision> log)
        {
            var items = Components.LiveMinionIdentities.Items;
            for (int i = 0; i < items.Count; i++)
            {
                MinionIdentity item = items[i];
                if (!cm.ValidMinionTags(item) || cm.minionConversations.ContainsKey(item) || cm.MinionOnCooldown(item)) continue;
                for (int j = 0; j < items.Count; j++)
                {
                    MinionIdentity item2 = items[j];
                    if (item2 == item || !cm.ValidMinionTags(item2)) continue;
                    if (cm.minionConversations.TryGetValue(item2, out var value))
                    {
                        if (value.minions.Count < TuningData<ConversationManager.Tuning>.Get().maxDupesPerConvo
                            && !((cm.GetCentroid(value) - item.transform.GetPosition()).magnitude >= TuningData<ConversationManager.Tuning>.Get().maxDistance * 0.5f))
                        {
                            value.minions.Add(item);
                            cm.minionConversations[item] = value;
                            log?.Add(new ConversationPlanner.Decision { Kind = ConversationPlanner.Join, I = i, J = j });
                            break;
                        }
                    }
                    else if (!cm.MinionOnCooldown(item2) && !((item2.transform.GetPosition() - item.transform.GetPosition()).magnitude >= TuningData<ConversationManager.Tuning>.Get().maxDistance))
                    {
                        Start(cm, item, item2);
                        log?.Add(new ConversationPlanner.Decision { Kind = ConversationPlanner.Create, I = i, J = j });
                        break;
                    }
                }
            }
        }
    }
}
