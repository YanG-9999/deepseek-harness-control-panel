using System;
using System.Reflection;
using System.Threading;

public static class DiagnoseBalance
{
    public static int Main(string[] args)
    {
        string node = args.Length > 0 ? args[0] : "node.exe";
        bool forceNode = args.Length > 1 && args[1] == "node";
        try
        {
            Console.WriteLine("Fetching via " + node + (forceNode ? " (node transport forced)" : ""));
            BalanceResult result;
            if (forceNode)
            {
                // The fallback used when the local .NET TLS stack cannot connect.
                MethodInfo method = typeof(DeepSeekBalanceClient).GetMethod("FetchNodeAsync", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) throw new InvalidOperationException("FetchNodeAsync was not found.");
                var pending = method.Invoke(null, new object[] { "sk-synthetic-invalid-key", CancellationToken.None, node }) as System.Threading.Tasks.Task<BalanceResult>;
                if (pending == null) throw new InvalidOperationException("FetchNodeAsync did not return a task.");
                result = pending.GetAwaiter().GetResult();
            }
            else
            {
                result = DeepSeekBalanceClient.FetchAsync("sk-synthetic-invalid-key", CancellationToken.None, node).GetAwaiter().GetResult();
            }
            Console.WriteLine("OK " + result.Amount);
            return 0;
        }
        catch (Exception error)
        {
            try { Console.WriteLine("CAUGHT " + error.GetType().FullName + ": " + error.Message); }
            catch (Exception) { Console.WriteLine("CAUGHT an exception whose ToString is broken"); }
            return 1;
        }
    }
}
