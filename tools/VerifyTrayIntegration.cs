using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Read-only diagnostics: all tests continue after a failure so an environment
// limitation cannot conceal unrelated regressions. No service start/stop/install.
public static class VerifyTrayIntegration
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "balance")
        {
            try
            {
                DeepSeekBalanceClient.FetchAsync("synthetic-invalid-key-for-transport-test", CancellationToken.None, args[1]).GetAwaiter().GetResult();
                Console.WriteLine("Unexpected success with a synthetic key.");
                return 1;
            }
            catch (BalanceQueryException error)
            {
                Console.WriteLine(error.Message);
                return error.Message.Contains("API Key") ? 0 : 1;
            }
        }
        Application.EnableVisualStyles();
        Assembly suite = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StopTargetResolverTests.exe"));
        int failed = 0, passed = 0;
        foreach (Type type in suite.GetTypes().Where(t => t.Name.EndsWith("Tests")))
        {
            MethodInfo run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (run == null) continue;
            try { run.Invoke(null, null); passed++; Console.WriteLine("PASS " + type.Name); }
            catch (Exception error)
            {
                failed++;
                Exception cause = error.GetBaseException();
                Console.WriteLine("FAIL " + type.Name + ": " + cause.GetType().Name + " " + cause.Message);
            }
        }
        Console.WriteLine("Passed=" + passed + " Failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
