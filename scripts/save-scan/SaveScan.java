// Scans a Skyrim save with ReSaver's own library (FallrimTools), without its GUI: reports whether the Papyrus section parsed,
// undefined scripts (script data in the save with no matching .pex), unattached script instances and undefined elements,
// grouped by script name. Read-only: it never writes the save.
//
// Build (WSL, JDK 17+):  scripts/save-scan/build.sh
// Run (Windows Java 8 from the ReSaver install):  see scripts/save-scan/run.sh
import java.nio.file.*;
import java.util.*;
import resaver.ProgressModel;
import resaver.ess.*;
import resaver.ess.papyrus.*;

public class SaveScan {
    public static void main(String[] args) throws Exception {
        Path save = Paths.get(args[0]);
        ESS.Result r = ESS.readESS(save, new ModelBuilder(new ProgressModel()));
        ESS ess = r.ESS;
        System.out.println("save: " + save.getFileName() + "  game: " + r.GAME + "  size MB: " + r.SIZE_MB);
        System.out.println("broken: " + ess.isBroken() + "  truncated: " + ess.isTruncated());
        Papyrus papyrus = ess.getPapyrus();
        if (papyrus == null) { System.out.println("no Papyrus section parsed"); return; }
        int[] undefined = papyrus.countUndefinedElements();
        System.out.println("counts: strings " + papyrus.getStringTable().size() + "  scripts " + papyrus.getScripts().size()
                + "  instances " + papyrus.getScriptInstances().size() + "  references " + papyrus.getReferences().size()
                + "  arrays " + papyrus.getArrays().size() + "  activeScripts " + papyrus.getActiveScripts().size()
                + "  functionMessages " + papyrus.getFunctionMessages().size() + "  suspended1 " + papyrus.getSuspendedStacks1().size()
                + "  suspended2 " + papyrus.getSuspendedStacks2().size() + "  changeForms " + ess.getChangeForms().size());
        System.out.println("unattached instances: " + papyrus.countUnattachedInstances() + "  undefined elements: " + Arrays.toString(undefined));

        List<String> undefScripts = new ArrayList<>();
        for (Script s : papyrus.getScripts().values()) if (s.isUndefined()) undefScripts.add(s.getName().toString());
        Collections.sort(undefScripts);
        System.out.println("\nundefined scripts (" + undefScripts.size() + "): data in the save, no script file installed");
        for (String n : undefScripts) System.out.println("  " + n);

        Map<String, Integer> unattached = new TreeMap<>(), undefInst = new TreeMap<>();
        for (ScriptInstance i : papyrus.getScriptInstances().values()) {
            String n = String.valueOf(i.getScriptName());
            if (i.isUnattached()) unattached.merge(n, 1, Integer::sum);
            if (i.isUndefined()) undefInst.merge(n, 1, Integer::sum);
        }
        System.out.println("\nunattached instances by script (" + unattached.size() + " scripts):");
        unattached.forEach((k, v) -> System.out.println("  " + v + "  " + k));
        System.out.println("\nundefined instances by script (" + undefInst.size() + " scripts):");
        undefInst.forEach((k, v) -> System.out.println("  " + v + "  " + k));
    }
}
