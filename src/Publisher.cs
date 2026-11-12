using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using ResoniteModLoader;

namespace SpeechToText;

/// <summary>
/// Publishes the STT state as DynamicValueVariables ("User/STT.*", SPEC §5) on the local user's root, per world.
/// A 4 Hz timer decides which worlds should have them and posts a reconcile to each world's update thread; all engine
/// objects are only touched there.
/// </summary>
internal static class Publisher
{
    public const string ContainerName = "SpeechToText";
    public const string TextImpulseTag = "STT.Text";

    private sealed class WorldState
    {
        public World World = null!;
        public UserRoot Root = null!;
        public SyncFieldEvent<bool>? TriggerHandler;   // subscribed to Trigger.Value.OnValueChange
        public int LastImpulseSequence; // starts at the current sequence so old text doesn't fire an impulse after (re)creation
        public volatile bool TriggerAccepted;           // Trigger is true AND the policy accepted who wrote it
        public Slot Slot = null!;
        public DynamicVariableSpace? OwnedSpace; // set only when we had to add the "User" space ourselves
        public DynamicValueVariable<string> Text = null!, Partial = null!, Mode = null!, Language = null!;
        public DynamicValueVariable<int> Sequence = null!;
        public DynamicValueVariable<bool> Listening = null!, Speaking = null!, Trigger = null!;
    }

    private static readonly Dictionary<World, WorldState> states = new();
    private static readonly object snapLock = new();
    private static Snapshot snap = Snapshot.Empty;
    private static Timer? timer;
    private static long lastErrorTicks;

    public static Snapshot Current { get { lock (snapLock) return snap; } }

    public static void Update(Func<Snapshot, Snapshot> change)
    {
        lock (snapLock) snap = change(snap);
    }

    public static void Start() => timer ??= new Timer(_ => Tick(), null, 1000, 250);

    /// <summary>Run a pass now (used when the mode changes) instead of waiting for the next timer tick.</summary>
    public static void Poke() => ThreadPool.QueueUserWorkItem(_ => Tick());

    private static void Tick()
    {
        try
        {
            var engine = Engine.Current;
            var wm = engine?.WorldManager;
            if (wm == null) return;

            SttMode mode = SpeechToTextMod.Get(SpeechToTextMod.modeKey);
            bool publishAll = SpeechToTextMod.Get(SpeechToTextMod.publishAllKey);
            World? focused = wm.FocusedWorld;
            World[] worlds;
            try { worlds = wm.Worlds.ToArray(); }
            catch (InvalidOperationException) { return; } // list changed under us; next tick

            Snapshot s = Current;
            foreach (World w in worlds)
            {
                if (w == null || w.IsDestroyed || w.State != World.WorldState.Running || w.IsUserspace()) continue;
                bool hasState; lock (states) hasState = states.ContainsKey(w);
                bool live = w == focused || publishAll;
                if (mode == SttMode.Off ? !hasState : (!hasState && !live)) continue;

                World world = w;
                bool isLive = live;
                world.RunSynchronously(() => Reconcile(world, mode, isLive, s));
            }

            lock (states)
                foreach (World dead in states.Keys.Where(k => k.IsDestroyed).ToList())
                    states.Remove(dead);

            RefreshGate(); // world focus may have changed since the last trigger write
        }
        catch (Exception e) { LogThrottled("tick", e); }
    }

    // ---- trigger (SPEC section 6.2) -----------------------------------------------------------------------------

    /// <summary>
    /// Is the trigger open in a world we are listening for (the focused one, or any with PublishToAllWorlds)?
    /// Only writes the policy accepted count.
    /// </summary>
    private static bool ComputeTriggerOpen()
    {
        World? focused = Engine.Current?.WorldManager?.FocusedWorld;
        bool all = SpeechToTextMod.Get(SpeechToTextMod.publishAllKey);
        lock (states)
            foreach (var kv in states)
                if ((kv.Key == focused || all) && kv.Value.TriggerAccepted) return true;
        return false;
    }

    private static void RefreshGate() => SttController.SetTrigger(ComputeTriggerOpen());

    /// <summary>Auto-off (and any other "close it now" need): writes false to every trigger that is currently true.</summary>
    public static void ForceTriggerOff()
    {
        List<WorldState> open;
        lock (states) open = states.Values.Where(st => st.TriggerAccepted).ToList();
        foreach (var st in open)
        {
            var s = st;
            s.World.RunSynchronously(() =>
            {
                if (!s.Slot.IsRemoved && s.Trigger.Value.Value) s.Trigger.Value.Value = false;
            });
        }
    }

    /// <summary>
    /// Works out whether the trigger's current value counts, and rejects (reverts) it when it was written by someone the
    /// policy doesn't allow. World thread only.
    /// </summary>
    private static void EvaluateTrigger(WorldState st)
    {
        var field = st.Trigger.Value;
        if (!field.Value) { st.TriggerAccepted = false; return; }

        var who = field.LastModifyingUser;
        bool local = who == null || who.IsLocalUser; // never written means it is our own initial value
        if (TriggerPolicy.Accept(local, SpeechToTextMod.Get(SpeechToTextMod.triggerFromKey)))
        {
            st.TriggerAccepted = true;
            return;
        }

        st.TriggerAccepted = false;
        SpeechToTextMod.Msg($"[trigger] ignored a write by {who?.UserName ?? "another user"} in '{st.World.Name}' (Trigger from: only me)");
        Notify.Warn("Someone else tried to switch your speech to text on. Ignored (the Trigger from setting is Only me).");
        // Reverting inside OnValueChange isn't allowed (the field is mid-change), so do it next update.
        st.World.RunSynchronously(() =>
        {
            if (!st.Slot.IsRemoved && st.Trigger.Value.Value && !st.TriggerAccepted) st.Trigger.Value.Value = false;
        });
    }

    private static void OnTriggerChanged(WorldState st)
    {
        try
        {
            EvaluateTrigger(st);
            RefreshGate(); // the gate moves in the same frame as the write
        }
        catch (Exception e) { LogThrottled("trigger", e); }
    }

    // ---- world thread -------------------------------------------------------------------------------------------

    private static void Reconcile(World world, SttMode mode, bool live, Snapshot s)
    {
        try
        {
            WorldState? st; lock (states) states.TryGetValue(world, out st);

            if (mode == SttMode.Off)
            {
                if (st != null) { Teardown(st); lock (states) states.Remove(world); }
                return;
            }

            UserRoot? root = world.LocalUser?.Root;
            if (root == null || root.IsRemoved) return; // no avatar yet / respawning: keep state, retry next tick

            if (st == null || st.Root != root || st.Slot.IsRemoved)
            {
                if (st != null) { Teardown(st); lock (states) states.Remove(world); }
                st = Build(world, root);
                lock (states) states[world] = st;
            }
            Write(st, mode, live, s);
        }
        catch (Exception e) { LogThrottled("reconcile", e); }
    }

    private static WorldState Build(World world, UserRoot root)
    {
        var st = new WorldState { World = world, Root = root, LastImpulseSequence = Current.Sequence };
        Slot rootSlot = root.Slot;

        // Stock avatars already have the "User" space (CommonAvatarBuilder). Add it only if missing.
        if (rootSlot.GetComponent<DynamicVariableSpace>(sp => sp.SpaceName.Value == "User") == null)
        {
            var space = rootSlot.AttachComponent<DynamicVariableSpace>();
            space.SpaceName.Value = "User";
            space.OnlyDirectBinding.Value = true;
            st.OwnedSpace = space;
        }

        Slot slot = rootSlot.FindChild(ContainerName) ?? rootSlot.AddSlot(ContainerName, persistent: false);
        slot.Tag = ContainerName;
        st.Slot = slot;

        st.Text = Var<string>(slot, "User/STT.Text");
        st.Partial = Var<string>(slot, "User/STT.Partial");
        st.Sequence = Var<int>(slot, "User/STT.Sequence");
        st.Listening = Var<bool>(slot, "User/STT.Listening");
        st.Speaking = Var<bool>(slot, "User/STT.Speaking");
        st.Mode = Var<string>(slot, "User/STT.Mode");
        st.Language = Var<string>(slot, "User/STT.Language");
        st.Trigger = Var<bool>(slot, "User/STT.Trigger");
        st.TriggerHandler = _ => OnTriggerChanged(st);
        st.Trigger.Value.OnValueChange += st.TriggerHandler;
        EvaluateTrigger(st); // a reused container may already hold a value

        SpeechToTextMod.Msg($"[publisher] created STT variables in '{world.Name}' on {root.Slot.Name}");
        return st;
    }

    private static DynamicValueVariable<T> Var<T>(Slot slot, string name)
    {
        var existing = slot.GetComponent<DynamicValueVariable<T>>(v => v.VariableName.Value == name);
        if (existing != null) return existing;
        var v = slot.AttachComponent<DynamicValueVariable<T>>();
        v.VariableName.Value = name;
        return v;
    }

    private static void Write(WorldState st, SttMode mode, bool live, Snapshot s)
    {
        Set(st.Mode, mode.ToString());
        EvaluateTrigger(st);
        if (!live)
        {
            // Not the focused world: show that nothing is being captured here, keep the last text.
            Set(st.Listening, false); Set(st.Speaking, false); Set(st.Partial, "");
            return;
        }
        Set(st.Text, s.Text);
        Set(st.Sequence, s.Sequence);
        Set(st.Language, s.Language);
        Set(st.Listening, s.Listening);
        Set(st.Speaking, s.Speaking);
        Set(st.Partial, SpeechToTextMod.Get(SpeechToTextMod.partialsKey) ? s.Partial : "");

        // Tell ProtoFlux a new final text is ready. Sent after every variable above is written, so a
        // DynamicImpulseReceiver("STT.Text") can read User/STT.Text immediately.
        if (s.Sequence != st.LastImpulseSequence)
        {
            st.LastImpulseSequence = s.Sequence;
            if (s.Sequence > 0 && !string.IsNullOrEmpty(s.Text) && !st.Root.IsRemoved)
            {
                // One update later: the dynamic variable components push their new values into the space on the next
                // update, and a receiver that reads User/STT.Text right away would still see the old text.
                // Don't wait for the engine's own change pass (its order against our callback varies, hence the
                // inconsistency): push the new values into the space now. UpdateLinking is what OnChanges runs.
                st.Text.UpdateLinking(); st.Sequence.UpdateLinking(); st.Language.UpdateLinking();
                var root = st.Root;
                st.World.RunInUpdates(1, () =>
                {
                    try { if (!root.IsRemoved) ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(root.Slot, TextImpulseTag, true); }
                    catch (Exception e) { LogThrottled("impulse", e); }
                });
            }
        }
    }

    private static void Set<T>(DynamicValueVariable<T> v, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(v.Value.Value, value)) v.Value.Value = value;
    }

    private static void Teardown(WorldState st)
    {
        try
        {
            st.TriggerAccepted = false;
            if (st.TriggerHandler != null && !st.Trigger.IsRemoved) st.Trigger.Value.OnValueChange -= st.TriggerHandler;
            if (!st.Slot.IsRemoved) st.Slot.Destroy();
            if (st.OwnedSpace != null && !st.OwnedSpace.IsRemoved) st.OwnedSpace.Destroy();
        }
        catch (Exception e) { LogThrottled("teardown", e); }
    }

    private static void LogThrottled(string what, Exception e)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastErrorTicks) < 30_000) return;
        Interlocked.Exchange(ref lastErrorTicks, now);
        SpeechToTextMod.Error($"[publisher] {what} failed: {e}");
    }
}

/// <summary>M2 test source: writes fake utterances so the variable plumbing can be verified without the STT pipeline.</summary>
internal static class DummyFeed
{
    private static Timer? timer;
    private static int n;

    public static void Start() => timer ??= new Timer(_ => Step(), null, 3000, 3000);

    private static void Step()
    {
        try
        {
            if (!SpeechToTextMod.Get(SpeechToTextMod.dummyKey) || SpeechToTextMod.Get(SpeechToTextMod.modeKey) == SttMode.Off) return;
            int i = Interlocked.Increment(ref n);
            Publisher.Update(s => s with
            {
                Text = $"Test utterance {i}",
                Partial = "",
                Sequence = i,
                Listening = true,
                Speaking = i % 2 == 0,
                Language = "en",
            });
        }
        catch (Exception e) { SpeechToTextMod.Error("[dummy] " + e); }
    }
}
