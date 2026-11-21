#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OniMoono.Host.GameTweaks
{
    /// One translatable string of the Moono Tweaks screen: its key (STRINGS.MOONO.TWEAKS....),
    /// its English and a note for translators on where it shows.
    internal sealed record TweakString(string Key, string English, string Note);

    /// Every string the Moono Tweaks screen shows, keyed as the game keys its (STRINGS.…),
    /// for translations in the game's .po format: the screen's own labels (Ui) and the catalog's
    /// text (GameTweakNames: groups, rows, measured gains, choices). English is the catalog's;
    /// translations/moono.pot is generated from All (GameTweakCatalogTests,
    /// `ONI_UPDATE_DOCS=1` rewrites it) and translations/CODE.po holds a language. Launch
    /// options and paths are not translated. Plain .NET with no game types (the tests compile it
    /// in); the game side (OniMoono.GameTweaks.MoonoTweaksMenu) reads the .po for the game's
    /// language and resolves it with Resolve.
    internal static class TweakStrings
    {
        public const string Prefix = "STRINGS.MOONO.TWEAKS.";

        /// The screen's own labels: the last part of each key (STRINGS.MOONO.TWEAKS.UI.NAME, as
        /// the screen names them), its English and where it shows. `{0}`, `{1}`: what the screen
        /// puts there.
        public static readonly (string Name, string English, string Note)[] Ui =
        [
            ("TITLE", "MOONO TWEAKS", "The main menu's button and the screen's title"),
            ("CHANGES_APPLY_ON_RESTART", "Changes apply when the game restarts.", "The footer, left of the buttons (one line)"),
            ("RESET_TO_DEFAULTS", "RESET TO DEFAULTS", "Footer button"),
            ("RESTART_TO_APPLY", "RESTART TO APPLY", "Footer button"),
            ("CHANGES_GAMEPLAY_NOTE", "These trade a little exactness for speed. Most are off by default.", "Under the Changes gameplay heading"),
            ("BUG_FIXES_NOTE", "These fix bugs in the game, so they change what the original does.", "Under the Bug fixes heading"),
            ("NO_MATCHES", "No tweaks match the search.", "In the list when the search box matches no row"),
            ("LAUNCH_OPTION", "LAUNCH OPTION", "Beside a row the launch options set (one line)"),
            ("LAUNCH_OPTION_LINE", "Launch option: {0}", "A row's tooltip, last line; {0}: the launch option"),
            ("LOCKED_ON", "Set by the launch options (on)", "Tooltip of a toggle the launch options set"),
            ("LOCKED_OFF", "Set by the launch options (off)", "Tooltip of a toggle the launch options set"),
            ("NOT_AVAILABLE", "NOT AVAILABLE", "Beside a row whose tweak this game version can't run (one line)"),
            ("NOT_AVAILABLE_TIP", "Not available on this game version.", "Tooltip of such a row's toggle, and its tooltip's first line"),
            ("LOCKED_CHOICE", "Set by the launch options ({0})", "Tooltip of a choice button the launch options set; {0}: the choice"),
            ("NEXT_CHOICE", "Click for the next choice: {0}", "Tooltip of a choice button; {0}: the choices, LIST_SEPARATOR between them"),
            ("LIST_SEPARATOR", ", ", "Between the choices in NEXT_CHOICE"),
            ("SAVE_FAILED", "The settings could not be saved to {0}:\n{1}", "Dialog; {0}: the settings file, {1}: the error"),
            ("SAVED_RESTART", "Your changes are saved and apply when the game restarts.", "Dialog on closing with changes"),
            ("LATER", "LATER", "The dialog's button beside Restart"),
            ("UPDATE_HEADLINE", "Moo! An update is available", "The main menu's update badge, first line"),
            ("UPDATE_MORE", "…more on the release page", "The update badge's tooltip, last line, when the notes are cut"),
            ("OVERLAP_REWRITES", "{0} also patches {1} (it rewrites the game's code).", "A row's tooltip: a mod's transpiler on a method the tweak changes; {0}: the mod, {1}: the methods"),
            ("OVERLAP_SKIPS", "{0} also patches {1} (it can skip the game's code).", "A row's tooltip: a mod's prefix that can skip a method the tweak replaces; {0}: the mod, {1}: the methods"),
            ("OVERLAP_PATCHES", "{0} also patches {1}.", "A row's tooltip: a mod's other patches of methods the tweak changes; {0}: the mod, {1}: the methods"),
            ("OVERLAP_STEPS_ASIDE", "Off while {0} is loaded, as it patches {1}.", "A row's tooltip: the tweak turns itself off for the mod; {0}: the mod, {1}: the methods"),
            ("OVERLAP_ADVICE", "If something looks wrong, turn this tweak off.", "A row's tooltip, after the mods that patch the same methods"),
            ("OVERLAP_MORE_METHODS", "{0} and {1} more", "In a row's tooltip; {0}: the first methods, LIST_SEPARATOR between them, {1}: how many more"),
            ("OVERLAPS_ONE", "1 overlap", "The main menu's badge beside the Moono Tweaks button: one tweak overlaps a mod"),
            ("OVERLAPS_MANY", "{0} overlaps", "The main menu's badge; {0}: how many tweaks and mods overlap"),
            ("OVERLAPS_TIP", "Some mods patch the same code as Moono's tweaks. Open Moono Tweaks to see which.", "The main menu's overlap badge's tooltip"),
            ("PERF_FPS", "fps", "The frame rate's unit, after the number (the counter, the window)"),
            ("PERF_MS", "ms", "Milliseconds, after a number (the performance window)"),
            ("PERF_S", "s", "Seconds, after a number (the performance window)"),
            ("PERF_GB", "GB", "Gigabytes, after a number (the performance window)"),
            ("PERF_KB", "KB", "Kilobytes, after a number (the performance window)"),
            ("PERF_WINDOW_TITLE", "PERFORMANCE", "The performance window's title (click the frame rate counter)"),
            ("PERF_TAB_COLONY", "COLONY", "The performance window's first tab (for players)"),
            ("PERF_TAB_FRAME", "FRAME", "The performance window's second tab (the frame's parts, for developers)"),
            ("PERF_TAB_HITCHES", "HITCHES", "The performance window's third tab (stutters and freezes)"),
            ("PERF_VERDICT_GOOD", "Running well", "Colony tab, the verdict's heading (green light)"),
            ("PERF_VERDICT_GOOD_TEXT", "{0} fps and the game keeps up with speed {1}. You don't need to change anything.", "Colony tab, the verdict; {0}: frames a second, {1}: the speed setting (1 to 3)"),
            ("PERF_VERDICT_STUTTERING", "Stuttering", "Colony tab, the verdict's heading (yellow light)"),
            ("PERF_VERDICT_STUTTERING_TEXT", "{0} fps, but the picture stops for a moment {1} times a minute.", "Colony tab, the verdict; {0}: frames a second, {1}: stutters a minute"),
            ("PERF_VERDICT_BEHIND", "Not keeping up", "Colony tab, the verdict's heading (red light)"),
            ("PERF_VERDICT_BEHIND_TEXT", "The game runs at {0}× instead of the {1}× you chose: your colony's time passes more slowly than it should.", "Colony tab, the verdict; {0}: the speed reached, {1}: the speed chosen"),
            ("PERF_VERDICT_SCREEN_TEXT", "{0} fps, as fast as your {1} Hz screen can show, and the game keeps up with speed {2}.", "Colony tab, the verdict when vertical sync holds the frame rate at the screen's refresh rate; {0}: frames a second, {1}: the screen's refresh rate, {2}: the speed setting"),
            ("PERF_VERDICT_SCREEN_ROOM_TEXT", "{0} fps, as fast as your {1} Hz screen can show, and the game keeps up with speed {2}. There is room to spare: the game's own work takes about {3}% of each frame.", "Colony tab, the same with the main thread's measured share; {3}: percent of the time the main thread is busy"),
            ("PERF_VERDICT_REMEASURING_TEXT", "Measuring again since you pressed Scan again: {0} s of play so far.", "Colony tab, the verdict's text in the first seconds after Scan again; {0}: seconds of unpaused play measured"),
            ("PERF_VERDICT_PAUSED", "Paused", "Colony tab, the verdict's heading while paused"),
            ("PERF_VERDICT_PAUSED_TEXT", "The game is paused. Unpause it for a few seconds to see how it runs.", "Colony tab, the verdict while paused"),
            ("PERF_VERDICT_NO_COLONY", "No colony loaded", "Colony tab, the verdict's heading in the menus"),
            ("PERF_VERDICT_NO_COLONY_TEXT", "Load a colony to see how it runs and what you can do about it.", "Colony tab, the verdict in the menus"),
            ("PERF_ADVICE_HEADING", "What you can do, biggest gain first", "Colony tab, the advice's heading (shown in capitals)"),
            ("PERF_ADVICE_NONE", "No change we have measured would make this colony run better.", "Colony tab, when no advice applies"),
            ("PERF_ADVICE_NONE_STUTTERS", "No change we have measured would remove these stutters: the game reports no cause for {0} of the {1} a minute.", "Colony tab, when no advice applies while it stutters; {0}: stutters a minute with no known cause, {1}: all stutters a minute"),
            ("PERF_ADVICE_NOT_YET", "Nothing to advise yet: let the game run unpaused for half a minute.", "Colony tab, before enough play was measured"),
            ("PERF_ADVICE_SIM_STEP_PACE", "Turn on \"Simulation at its own pace\" in Moono Tweaks", "Colony tab, an advice card's title (the sim-step-pace row's title in quotes)"),
            ("PERF_ADVICE_SIM_STEP_PACE_GAIN", "Would remove most of the {0} stutters a minute that come from waiting for the simulation (in our tests: {1} a minute down to about {2}).", "Colony tab, the card's gain; {0}: stutters a minute now, {1} and {2}: measured before and after"),
            ("PERF_ADVICE_SIM_STEP_PACE_WHY", "Dug and built tiles appear up to 0.2 s later. Off by default.", "Colony tab, the card's side effect"),
            ("PERF_NOTHING_HEADING", "Nothing to gain here", "Colony tab, heading (shown in capitals)"),
            ("PERF_NOTHING_NONE", "No other check has a measured gain yet.", "Colony tab, under Nothing to gain here when no other rule has a line"),
            ("PERF_NOTHING_SIM_STEP_PACE_ON", "Waiting for the simulation step: \"Simulation at its own pace\" is already on.", "Colony tab, under Nothing to gain here"),
            ("PERF_NOTHING_VSYNC", "Frame rate: vertical sync holds it at your screen's {0} Hz, the most the screen can show. To see how fast the game could run, set \"Vertical sync\" to Off in Moono Tweaks (main menu); a {0} Hz screen still shows only {0} pictures a second.", "Colony tab, under Nothing to gain here while vertical sync holds the frame rate; {0}: the screen's refresh rate"),
            ("PERF_NOTHING_SIM_WAITS_FEW", "Waiting for the simulation step: {0} of the {1} stutters a minute, too few for \"Simulation at its own pace\" to help.", "Colony tab, under Nothing to gain here; {0}: stutters a minute from waiting for the simulation step, {1}: all stutters a minute"),
            ("PERF_NOW_HEADING", "Right now", "Colony tab, panel heading (shown in capitals)"),
            ("PERF_NOW_FRAME_RATE", "Frame rate", "Colony tab, a line of Right now"),
            ("PERF_NOW_KEEPS_UP", "Keeps up with speed {0}", "Colony tab, a line of Right now; {0}: the speed setting (1 to 3)"),
            ("PERF_YES", "Yes", "Colony tab, the answer to Keeps up with speed"),
            ("PERF_NO_AT", "No, {0}×", "Colony tab, the answer to Keeps up with speed; {0}: the speed reached"),
            ("PERF_NOW_STUTTERS", "Small stutters", "Colony tab, a line of Right now"),
            ("PERF_PER_MINUTE", "{0} a minute", "A count a minute; {0}: the count"),
            ("PERF_NOW_AUTOSAVE", "Autosave pause", "Colony tab, a line of Right now (the last save's freeze)"),
            ("PERF_NONE_YET", "None yet", "A value not measured yet (no save yet)"),
            ("PERF_NOW_MEMORY", "Memory used", "Colony tab, a line of Right now"),
            ("PERF_GB_OF", "{0} of {1} GB", "Memory used; {0}: used, {1}: installed"),
            ("PERF_NOW_HELD_BACK", "Held back by", "Colony tab, a line of Right now (what limits the frame rate)"),
            ("PERF_HELD_PROCESSOR", "Processor", "Held back by: the processor"),
            ("PERF_HELD_GRAPHICS", "Graphics", "Held back by: the graphics card"),
            ("PERF_HELD_LIMIT", "Frame rate limit", "Held back by: a frame rate cap or vsync"),
            ("PERF_HELD_SCREEN", "Your screen ({0} Hz)", "Held back by: vertical sync holds the frame rate at the screen's refresh rate; {0}: the refresh rate"),
            ("PERF_COLONY_HEADING", "Your colony", "Colony tab, panel heading (shown in capitals)"),
            ("PERF_COLONY_HEADING_CYCLE", "Your colony, cycle {0}", "Colony tab, panel heading (shown in capitals); {0}: the cycle"),
            ("PERF_COLONY_DUPLICANTS", "Duplicants", "Colony tab, a count"),
            ("PERF_COLONY_CRITTERS", "Critters", "Colony tab, a count"),
            ("PERF_COLONY_LOOSE", "Loose items", "Colony tab, a count (items in no storage)"),
            ("PERF_COLONY_BUILDINGS", "Buildings", "Colony tab, a count"),
            ("PERF_COLONY_UTILITIES", "Pipes, wires, rails", "Colony tab, a count"),
            ("PERF_COLONY_ASTEROIDS", "Asteroids discovered", "Colony tab, a count (the asteroids of the planetoid list, rockets left out)"),
            ("PERF_COLONY_FOOT", "Advice is shown only where the gain was measured.", "Colony tab, the footer's note"),
            ("PERF_SCAN_AGAIN", "SCAN AGAIN", "Colony tab, footer button (counts the colony again and measures the stutters a minute from now on)"),
            ("PERF_SCANNING", "SCANNING", "Colony tab, the Scan again button while it counts"),
            ("PERF_SCANNED", "SCANNED", "Colony tab, the Scan again button just after it counted"),
            ("PERF_COLONY_FOOT_SCANNED", "Counted {0}; stutters and advice measured from then on. Advice is shown only where the gain was measured.", "Colony tab, the footer's note after Scan again; {0}: when (PERF_JUST_NOW or \"5 s ago\")"),
            ("PERF_JUST_NOW", "just now", "When something happened less than 2 seconds ago"),
            ("PERF_KV_AVERAGE", "Average", "Frame and Hitches tabs, headline box"),
            ("PERF_KV_SLOWEST", "Slowest 1%", "Frame and Hitches tabs, headline box (the 99th percentile frame)"),
            ("PERF_KV_WORST", "Worst frame", "Frame and Hitches tabs, headline box"),
            ("PERF_KV_SPEED", "Game speed", "Frame tab, headline box (the speed reached)"),
            ("PERF_KV_STUTTERS", "Stutters", "Hitches tab, headline box"),
            ("PERF_PER_MIN", "/ min", "After a count a minute in a headline box"),
            ("PERF_MEASURING", "Measuring…", "The performance window, before its first second is measured"),
            ("PERF_ROW_UPDATE", "Game update", "Frame tab, a part of the frame (scripts' Update, the simulation's step)"),
            ("PERF_ROW_LATE", "Animation, late update", "Frame tab, a part of the frame (animators, LateUpdate)"),
            ("PERF_ROW_DRAWING", "Drawing", "Frame tab, a part of the frame (cameras, present)"),
            ("PERF_ROW_INTERFACE", "Interface", "Frame tab, a part of the frame (canvases, OnGUI)"),
            ("PERF_ROW_ELSE", "Everything else", "Frame tab, a part of the frame (input, particles, between frames)"),
            ("PERF_PHASE_RENDER", "Drawing the scene", "Frame tab, inside Drawing"),
            ("PERF_PHASE_GUI", "Debug interface", "Frame tab, inside Interface (OnGUI)"),
            ("PERF_PHASE_END_OF_FRAME", "End of frame", "Frame tab, inside Drawing (WaitForEndOfFrame)"),
            ("PERF_PHASE_PRESENT", "Showing the picture", "Frame tab, inside Drawing (present)"),
            ("PERF_PHASE_EARLY", "Input and physics", "Frame tab, inside Everything else"),
            ("PERF_PHASE_POST_LATE", "Interface layout", "Frame tab, inside Interface (canvases)"),
            ("PERF_PHASE_PARTICLES", "Particles", "Frame tab, inside Everything else"),
            ("PERF_PHASE_OUTSIDE", "Between frames", "Frame tab, inside Everything else (input polling, frame cap)"),
            ("PERF_ROW_WAIT_SCREEN", "Waiting for the screen (vsync)", "Frame tab, a part of the frame: the main thread waiting for its picture to be shown, while vertical sync holds the frame rate"),
            ("PERF_ROW_WAIT_GPU", "Waiting for the graphics card", "Frame tab, a part of the frame: the main thread waiting for the graphics thread to take its frame (no vertical sync)"),
            ("PERF_ROW_WAIT_EITHER", "Waiting for the screen or graphics card", "Frame tab, a part of the frame: the same wait with vertical sync on but frames slower than the screen"),
            ("PERF_WAIT_DEVICE_FRAME", "Graphics thread taking the next frame", "Frame tab, inside the waiting row"),
            ("PERF_WAIT_QUEUED", "Pictures queued for the screen", "Frame tab, inside the waiting row (vertical sync's pacing)"),
            ("PERF_SUB_WAIT_GRAPHICS", "Waiting for graphics results", "Frame tab, a share of the rows above (the main thread waiting for answers from the graphics thread)"),
            ("PERF_SUB_GC", "Memory clean-up", "Frame tab, a share of the rows above (garbage collection pauses)"),
            ("PERF_BUCKETS_NOTE", "Measuring what is inside Game update costs some frame rate while the row is open.", "Frame tab, a note under the rows while Game update is open"),
            ("PERF_DRILL_INSIDE", "Inside \"{0}\"", "Frame tab, the open row's table heading; {0}: the row's name"),
            ("PERF_DRILL_CALLS", "calls", "Frame tab, column of the open row's table"),
            ("PERF_DRILL_OBJECTS", "objects", "Frame tab, column of the open row's table"),
            ("PERF_DRILL_WORST", "worst", "Frame tab, column of the open row's table (the longest call, ms)"),
            ("PERF_DRILL_SMALLER", "{0} smaller entries", "Frame tab, the open row's last line; {0}: how many"),
            ("PERF_DRILL_NOT_IN_BUCKETS", "The rest (simulation step, other scripts)", "Frame tab, Game update's time outside the state machine buckets"),
            ("PERF_SPREAD_HEADING", "Frame time spread", "Frame tab, panel heading (shown in capitals)"),
            ("PERF_SPREAD_MEDIAN", "Typical (median)", "Frame tab, frame time spread"),
            ("PERF_SPREAD_NINE_IN_TEN", "9 frames in 10 under", "Frame tab, frame time spread (90th percentile)"),
            ("PERF_SPREAD_NINETY_NINE", "99 in 100 under", "Frame tab, frame time spread (99th percentile)"),
            ("PERF_SPREAD_LONGEST", "Longest", "Frame tab, frame time spread"),
            ("PERF_THREADS_HEADING", "Threads", "Frame tab, panel heading (shown in capitals)"),
            ("PERF_THREADS_MAIN", "Main thread busy", "Frame tab, threads"),
            ("PERF_THREADS_WAIT_GRAPHICS", "Main thread waiting for graphics", "Frame tab, threads"),
            ("PERF_THREADS_WAIT_SCREEN", "Main thread waiting for the screen", "Frame tab, threads, while vertical sync holds the frame rate"),
            ("PERF_THREADS_VSYNC", "Vertical sync", "Frame tab, threads"),
            ("PERF_VSYNC_OFF", "Off", "Frame tab, vertical sync off"),
            ("PERF_VSYNC_ON", "On", "Frame tab, vertical sync on (the screen's refresh rate not known)"),
            ("PERF_VSYNC_ON_HZ", "On, {0} Hz", "Frame tab, vertical sync on; {0}: the screen's refresh rate"),
            ("PERF_THREADS_SIM", "Simulation (SimDLL) on the main thread", "Frame tab, threads (with frame stats on)"),
            ("PERF_THREADS_WORKERS", "Game's worker threads", "Frame tab, threads"),
            ("PERF_OF_N", "{0} of {1}", "A count of a total; {0}: the count, {1}: the total"),
            ("PERF_MEMORY_HEADING", "Memory", "Frame tab, panel heading (shown in capitals)"),
            ("PERF_MEMORY_IN_USE", "In use", "Frame tab, memory"),
            ("PERF_MEMORY_PER_FRAME", "New memory per frame", "Frame tab, memory"),
            ("PERF_MEMORY_CLEANUPS", "Clean-ups per minute", "Frame tab, memory (garbage collections)"),
            ("PERF_CLEANUPS_VALUE", "{0} small · {1} full", "Frame tab, memory; {0}: young collections, {1}: full ones"),
            ("PERF_MEMORY_LONGEST", "Longest clean-up pause", "Frame tab, memory"),
            ("PERF_TWEAKS_HEADING", "Tweaks", "Frame tab, panel heading (shown in capitals)"),
            ("PERF_TWEAKS_ACTIVE", "Active", "Frame tab, tweaks"),
            ("PERF_TWEAKS_STEPPED", "Adjusted for your mods", "Frame tab, tweaks: speed-ups that leave a part of the game as it was because a mod changes it (click for the list)"),
            ("PERF_TWEAKS_OFF", "Switched off by you", "Frame tab, tweaks (click for the list)"),
            ("PERF_TWEAKS_ADJUSTED_NOTE", "This is normal: where a mod changes the same part of the game, Moono's speed-up leaves that part as the original game has it, so the mod keeps working.", "Frame tab, the first line of the Adjusted for your mods list"),
            ("PERF_TWEAKS_ADJUSTED_ITEM", "{0}: {1} runs as in the original game because {2} changes it. {3}", "Frame tab, a case of Adjusted for your mods; {0}: the speed-up's name, {1}: the game's method, {2}: the mod, {3}: its effect (PERF_EFFECT_…)"),
            ("PERF_TWEAKS_OFF_ITEM", "{0}: {1}", "Frame tab, a tweak switched off by the player; {0}: its name, {1}: its effect (PERF_EFFECT_…)"),
            ("PERF_EFFECT_NOT_MEASURED", "Effect not measured.", "Frame tab, a tweak's effect when it was not measured"),
            ("PERF_EFFECT_GAIN", "measured gain when on: {0}.", "Frame tab, a switched-off tweak's measured gain; {0}: the gain as Moono Tweaks shows it"),
            ("PERF_EFFECT_COLONY_SUMMARY", "Effect: the colony summary is written during the autosave pause again, about 0.1 s more per save on a late-game colony (measured 100 to 126 ms).", "Frame tab, the effect of background saving adjusted for a mod that changes the colony summary"),
            ("PERF_COMPARE", "Compared with your last recording ({0}): {1}.", "Frame tab, the footer's note; {0}: how long ago, {1}: the rows that changed most"),
            ("PERF_COMPARE_NONE", "No earlier recording to compare with: Record 60 s makes one.", "Frame tab, the footer's note without a recording"),
            ("PERF_AGO_S", "{0} s ago", "A time in the past; {0}: seconds"),
            ("PERF_AGO_MIN", "{0} min ago", "A time in the past; {0}: minutes"),
            ("PERF_AGO_H", "{0} h ago", "A time in the past; {0}: hours"),
            ("PERF_AGO_DAYS", "{0} days ago", "A time in the past; {0}: days"),
            ("PERF_COPY_REPORT", "COPY REPORT", "Frame tab, footer button (the numbers as text to the clipboard)"),
            ("PERF_COPIED", "COPIED", "Frame tab, the copy button just after a copy"),
            ("PERF_RECORD", "RECORD 60 S", "Footer button: saves 60 seconds of measurements to a file"),
            ("PERF_RECORDING_LEFT", "RECORDING {0} S", "The record button while a recording runs; {0}: seconds left"),
            ("PERF_RECORD_SAVED", "SAVED", "The record button for a few seconds after a recording was saved"),
            ("PERF_CAUSE_LOAD", "Loading", "A freeze's cause"),
            ("PERF_CAUSE_FIRST_FRAME", "First frames after loading", "A freeze's cause (the first frame with game time after a load, and the first that runs the 200 ms simulation step)"),
            ("PERF_CAUSE_AUTOSAVE", "Autosave", "A freeze's cause"),
            ("PERF_CAUSE_SAVE", "Save", "A freeze's cause (a save the player made)"),
            ("PERF_CAUSE_TIMELAPSE", "Timelapse picture", "A freeze's cause"),
            ("PERF_CAUSE_GC", "Memory clean-up", "A freeze's cause (garbage collection)"),
            ("PERF_CAUSE_SIM_WAIT", "Waiting for the simulation step", "A stutter's cause"),
            ("PERF_CAUSE_SIM_WAIT_SHORT", "Waiting for simulation", "Hitches tab, the by-cause table's short name for waiting for the simulation step"),
            ("PERF_CAUSE_JIT", "Compiling code (first use)", "A freeze's cause (the runtime compiling code the first time it runs)"),
            ("PERF_CAUSE_WORLD", "Switching asteroid", "A freeze's cause (the view changed to another asteroid)"),
            ("PERF_WHILE_PAUSED", "{0}, paused", "Hitches tab, a freeze that happened while the game was paused; {0}: its cause"),
            ("PERF_CAUSE_UNKNOWN", "Unknown", "A freeze's cause when the game reports none"),
            ("PERF_RANGE_TWO_MIN", "2 min", "Hitches tab, the graph's range"),
            ("PERF_RANGE_TEN_MIN", "10 min", "Hitches tab, the graph's range"),
            ("PERF_RANGE_SESSION", "This session", "Hitches tab, the graph's range"),
            ("PERF_LEGEND_FRAME", "Frame time", "Hitches tab, the graph's legend"),
            ("PERF_LEGEND_STUTTER", "Stutter", "Hitches tab, the graph's legend (20 to 100 ms)"),
            ("PERF_LEGEND_FREEZE", "Freeze", "Hitches tab, the graph's legend (100 ms or more)"),
            ("PERF_NO_FREEZES", "No freezes in this range.", "Hitches tab, when the list is empty"),
            ("PERF_ONGOING", "Ongoing", "Hitches tab, in the list's time column for repeated stutters"),
            ("PERF_SIM_WAIT_FIX", "Can be removed with \"Simulation at its own pace\" in Moono Tweaks.", "Hitches tab, under the waiting for the simulation line"),
            ("PERF_SELECTED_HEADING", "The selected freeze: {0}, {1}", "Hitches tab, heading (shown in capitals); {0}: the cause, {1}: how long ago"),
            ("PERF_PART_SAVE_DATA", "Collecting and writing the colony's data", "Hitches tab, a part of a save's freeze"),
            ("PERF_PART_FRAME", "The rest of the frame", "Hitches tab, a part of a freeze"),
            ("PERF_PART_GC", "Memory clean-up", "Hitches tab, a part of a freeze"),
            ("PERF_PART_JIT", "Compiling code (first time only)", "Hitches tab, a part of a freeze"),
            ("PERF_PART_PREVIEW", "Preview picture", "Hitches tab, a part of a save's freeze"),
            ("PERF_PART_BACKGROUND_WRITE", "Compressing and writing the file (in the background, no freeze)", "Hitches tab, a part of a save done after its freeze"),
            ("PERF_BY_CAUSE_HEADING", "By cause, this session ({0} min)", "Hitches tab, panel heading (shown in capitals); {0}: minutes in a colony"),
            ("PERF_CAUSE_COLUMN", "Cause", "Hitches tab, column heading"),
            ("PERF_TIMES_COLUMN", "Times", "Hitches tab, column heading"),
            ("PERF_WORST_COLUMN", "Worst", "Hitches tab, column heading"),
            ("PERF_TOTAL_COLUMN", "Total", "Hitches tab, column heading"),
            ("PERF_NO_CAUSES", "No stutters or freezes yet.", "Hitches tab, the by-cause table when empty"),
            ("PERF_LOST_HEADING", "Time lost to freezes", "Hitches tab, panel heading (shown in capitals)"),
            ("PERF_LOST_FREEZES", "Freezes over 0.1 s", "Hitches tab, time lost"),
            ("PERF_LOST_STUTTERS", "Stutters over 20 ms", "Hitches tab, time lost"),
            ("PERF_S_PER_HOUR", "{0} s per hour", "Hitches tab, time lost; {0}: seconds"),
            ("PERF_MIN_PER_HOUR", "{0} min per hour", "Hitches tab, time lost; {0}: minutes"),
            ("PERF_LOADING_HEADING", "Loading", "Hitches tab, panel heading (shown in capitals)"),
            ("PERF_LOADING_MENU", "Game start to menu", "Hitches tab, loading"),
            ("PERF_LOADING_COLONY", "Loading this colony", "Hitches tab, loading (until the colony's first frames come at a normal pace)"),
            ("PERF_LOADING_FIRST_FRAME", "First frames after loading", "Hitches tab, loading (the first frame with game time and the first that runs the 200 ms simulation step, together)"),
            ("PERF_LOADING_FIRST_SAVE", "First save", "Hitches tab, loading (the first save after the load)"),
            ("PERF_HITCHES_FOOT", "A cause is named only when the game reports it. Click a red bar or a freeze to see what it was made of.", "Hitches tab, the footer's note"),
            ("RESET_POSITION", "RESET POSITION", "The Frame rate counter row's button: puts the counter back at the top centre"),
            ("POSITION_RESET", "POSITION RESET", "The same button just after a click"),
            ("RESET_POSITION_TIP", "Puts the frame rate counter back at the top centre of the screen.", "Tooltip of the Reset position button"),
        ];

        public static string GroupKey(TweakGroup group) => Prefix + "GROUPS." + group.Key;
        public static string RowKey(TweakInfo row, string part) => Prefix + "ROWS." + Name(row.Id) + "." + part;
        public static string GainKey(TweakGain gain) => Prefix + "GAINS." + gain.Key;
        public static string ChoiceKey(TweakInfo row, string choice) => Prefix + "CHOICES." + Name(row.Id) + "." + Name(choice);
        static string Name(string id) => id.ToUpperInvariant().Replace('-', '_');
        /// A choice's English: its value with spaces for hyphens ("low-memory": "low memory").
        public static string ChoiceEnglish(string choice) => choice.Replace('-', ' ');

        /// Every string, in the screen's order: its labels, then each group, its rows (title,
        /// one-liner, details, choices) and the gains as their tooltips show them; then the
        /// Workshop loader mod's (MoonoLoader.LoaderStrings) and the messages before the game loads
        /// (Launch.StartText).
        public static readonly TweakString[] All = Collect();

        static TweakString[] Collect()
        {
            var all = Ui.Select(u => new TweakString(Prefix + "UI." + u.Name, u.English, u.Note)).ToList();
            var gains = new HashSet<TweakGain>();
            foreach (var group in GameTweakNames.Groups)
            {
                all.Add(new(GroupKey(group), group.Title, "Section heading (shown in capitals)"));
                foreach (var row in GameTweakNames.Rows.Where(r => r.Group == group))
                {
                    all.Add(new(RowKey(row, "TITLE"), row.Title, $"Row title ({row.Id})"));
                    all.Add(new(RowKey(row, "EFFECT"), row.Effect, $"The line under the title ({row.Id})"));
                    if (row.Detail.Length > 0) all.Add(new(RowKey(row, "DETAIL"), row.Detail, $"Tooltip: how it works ({row.Id})"));
                    foreach (var choice in row.Choices ?? [])
                        all.Add(new(ChoiceKey(row, choice), ChoiceEnglish(choice), $"A choice on the row's button (shown in capitals) and in its tooltip ({row.Id})"));
                    if (GameTweakNames.GainText(row) is { } gain && gains.Add(gain.Gain))
                        all.Add(new(GainKey(gain.Gain), gain.Text, $"Tooltip: the measured gain ({(row.Gain != null ? row.Id : "every " + group.Title + " row")})"));
                }
            }
            // The Workshop item's loader mod, which reads the same .po files from the item.
            foreach (var (name, english, note) in MoonoLoader.LoaderStrings.All)
                all.Add(new(MoonoLoader.LoaderStrings.Prefix + name, english, note));
            // The player's messages before the game loads (Launch.StartText).
            foreach (var (name, english, note) in Launch.StartText.All)
                all.Add(new(Launch.StartText.Prefix + name, english, note));
            return all.ToArray();
        }

        /// Each of All's strings in a language: `translated` (key → text, as the game's
        /// Localization.LoadStringsFile reads a .po) where it has the key with a text that keeps
        /// the English's placeholders and tags (SameMarkup), else the English.
        public static string[] Resolve(IReadOnlyDictionary<string, string>? translated) =>
            All.Select(s => translated != null && translated.TryGetValue(s.Key, out var text) && !string.IsNullOrEmpty(text)
                && SameMarkup(s.English, text) ? text : s.English).ToArray();

        static readonly Regex Markup = new(@"\{[^{}]*\}|<[^<>]*>", RegexOptions.CultureInvariant);

        /// The format placeholders (`{0}`) and rich-text tags (`<color=…>`) of a string, sorted:
        /// a translation must have the same ones as the English.
        public static string[] MarkupOf(string text) => Markup.Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

        public static bool SameMarkup(string english, string translated) => MarkupOf(english).SequenceEqual(MarkupOf(translated));

        /// A string as a .po file quotes it: `"` and line breaks escaped. (The game's reader
        /// unescapes only those two: a backslash stays as written, so the catalog has none.)
        public static string Quote(string text) => "\"" + text.Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

        /// translations/moono.pot: every string with its key as msgctxt, its English as msgid and
        /// an empty msgstr.
        public static string Pot()
        {
            var pot = new StringBuilder();
            pot.Append("# Moono's strings (the Moono Tweaks screen, the Workshop loader mod and the messages before the game starts): the template for a translation.\n");
            pot.Append("# Generated from src/OniMoono.Host/GameTweaks/TweakStrings.cs; ONI_UPDATE_DOCS=1 with the unit tests rewrites it.\n");
            pot.Append("# Copy it to CODE.po (the game's language code: zh, ko, ru, ...) and fill in each msgstr (docs/TWEAKS.md).\n");
            pot.Append("msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n");
            foreach (var s in All)
            {
                pot.Append("\n#. ").Append(s.Note).Append('\n');
                pot.Append("msgctxt ").Append(Quote(s.Key)).Append('\n');
                pot.Append("msgid ").Append(Quote(s.English)).Append('\n');
                pot.Append("msgstr \"\"\n");
            }
            return pot.ToString();
        }
    }
}
