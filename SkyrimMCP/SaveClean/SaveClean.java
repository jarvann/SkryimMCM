// FEAT-267: cleans a COPY of a Skyrim SE/AE save with ReSaver's own library (FallrimTools by Mark Fairchild, Apache-2.0),
// headless. SkyLink's server runs it with the JRE bundled with an installed ReSaver; it never writes over its input.
//
// Usage: SaveClean <in.ess> <out.ess> [--undefined] [--unattached] [--stack Script.Function]...
//   --undefined   remove script data whose script file is missing and terminate threads running it (ReSaver: Remove Undefined)
//   --unattached  remove script instances bound to no object (ReSaver: Remove Unattached Instances)
//   --stack X     terminate and delete every thread that started in X (Script.Function / Script.Event), and delete the queued
//                 calls (suspended stacks) for X, the pile-ups that eat script time. The game re-runs the handler on its next event.
// Prints "key: value" lines; exit code 0 = written and re-read, 1 = error (nothing written, or the output didn't re-read).
//
// Build: SkyrimMCP/SaveClean/build.sh (Java 8 bytecode, so ReSaver's bundled JRE runs it).
import java.nio.file.*;
import java.util.*;
import resaver.ProgressModel;
import resaver.ess.*;
import resaver.ess.papyrus.*;

public class SaveClean {
    public static void main(String[] args) {
        try {
            run(args);
        } catch (Throwable ex) {
            System.out.println("error: " + ex);
            System.exit(1);
        }
    }

    static void run(String[] args) throws Exception {
        if (args.length < 2) throw new IllegalArgumentException("usage: SaveClean <in.ess> <out.ess> [options]");
        Path in = Paths.get(args[0]), out = Paths.get(args[1]);
        if (in.toAbsolutePath().normalize().equals(out.toAbsolutePath().normalize())) throw new IllegalArgumentException("output must differ from input");
        if (Files.exists(out)) throw new IllegalArgumentException("output already exists: " + out);
        boolean undefined = false, unattached = false;
        Set<String> stacks = new HashSet<>();
        for (int i = 2; i < args.length; i++) {
            switch (args[i]) {
                case "--undefined": undefined = true; break;
                case "--unattached": unattached = true; break;
                case "--stack": stacks.add(args[++i].toLowerCase(Locale.ROOT)); break;
                default: throw new IllegalArgumentException("unknown option " + args[i]);
            }
        }

        ESS ess = ESS.readESS(in, new ModelBuilder(new ProgressModel())).ESS;
        if (ess.isBroken() || ess.isTruncated()) throw new IllegalStateException("the save is broken or truncated; ReSaver won't write it");
        Papyrus papyrus = ess.getPapyrus();
        System.out.println("read: " + in.getFileName());

        if (undefined) {
            Set<ActiveScript> terminated = papyrus.terminateUndefinedThreads();
            Set<PapyrusElement> removed = papyrus.removeUndefinedElements();
            System.out.println("undefinedThreadsTerminated: " + terminated.size());
            System.out.println("undefinedElementsRemoved: " + removed.size());
        }
        if (unattached) {
            System.out.println("unattachedRemoved: " + papyrus.removeUnattachedInstances().size());
        }
        if (!stacks.isEmpty()) {
            List<PapyrusElement> threads = new ArrayList<>();
            List<PapyrusElement> queued = new ArrayList<>();
            Map<String, Integer> perName = new TreeMap<>();
            for (ActiveScript t : papyrus.getActiveScripts().values()) {
                List<StackFrame> frames = t.getStackFrames();
                if (frames == null || frames.isEmpty()) continue;
                String entry = (frames.get(0).getScriptName() + "." + frames.get(0).getEvent()).toLowerCase(Locale.ROOT);
                if (stacks.contains(entry)) { threads.add(t); perName.merge(entry + " (running)", 1, Integer::sum); }
            }
            for (Map<EID, SuspendedStack> table : Arrays.asList(papyrus.getSuspendedStacks1(), papyrus.getSuspendedStacks2())) {
                for (SuspendedStack s : table.values()) {
                    FunctionMessageData m = s.getMessage();
                    if (m == null) continue;
                    String name = (m.getScriptName() + "." + m.getEvent()).toLowerCase(Locale.ROOT);
                    if (!stacks.contains(name)) continue;
                    queued.add(s);
                    perName.merge(name + " (queued)", 1, Integer::sum);
                    ActiveScript owner = s.getThread();
                    if (owner != null && !threads.contains(owner)) threads.add(owner);
                }
            }
            // A removed thread's own suspended stack (one waiting with no pending call, e.g. in a native call) must go too, or the
            // save keeps a stack pointing at a thread that no longer exists.
            Set<PapyrusElement> threadSet = new HashSet<>(threads);
            for (Map<EID, SuspendedStack> table : Arrays.asList(papyrus.getSuspendedStacks1(), papyrus.getSuspendedStacks2())) {
                for (SuspendedStack s : table.values()) {
                    if (s.getThread() != null && threadSet.contains(s.getThread()) && !queued.contains(s)) queued.add(s);
                }
            }
            // A thread with a pending function message stays (terminated, not deleted): ReSaver can't delete the message, and a
            // message pointing at a missing thread could stop the save loading.
            Set<EID> withMessage = new HashSet<>();
            for (FunctionMessage fm : papyrus.getFunctionMessages()) if (fm.getID() != null) withMessage.add(fm.getID());
            List<PapyrusElement> keep = new ArrayList<>();
            for (PapyrusElement t : threads) if (withMessage.contains(((ActiveScript) t).getID())) keep.add(t);
            for (PapyrusElement t : threads) ((ActiveScript) t).zero();
            threads.removeAll(keep);
            System.out.println("threadsTerminatedOnly: " + keep.size());
            int removedQueued = papyrus.removeElements(queued).size();
            int removedThreads = papyrus.removeElements(threads).size();
            for (Map.Entry<String, Integer> e : perName.entrySet()) System.out.println("stack: " + e.getKey() + " = " + e.getValue());
            System.out.println("threadsRemoved: " + removedThreads);
            System.out.println("queuedRemoved: " + removedQueued);
        }

        ESS.writeESS(ess, out, false);
        System.out.println("written: " + out.getFileName());

        // Re-read what was written: a cleaned save that doesn't parse is reported as a failure.
        ESS check = ESS.readESS(out, new ModelBuilder(new ProgressModel())).ESS;
        if (check.isBroken() || check.isTruncated() || check.getPapyrus() == null) throw new IllegalStateException("the written save didn't re-read cleanly");
        Papyrus p = check.getPapyrus();
        System.out.println("verify: activeScripts " + p.getActiveScripts().size() + " suspended " + (p.getSuspendedStacks1().size() + p.getSuspendedStacks2().size())
                + " instances " + p.getScriptInstances().size());
    }
}
