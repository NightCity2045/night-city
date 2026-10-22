using Robust.Client;

namespace Content.Client
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // NC-Edit-Start
#if TOOLS && NC_DIAGNOSTICS
            if (System.Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_TOKEN") is not { Length: >= 32 and <= 256 } ||
                string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_PIPE")))
                throw new System.InvalidOperationException("This trusted diagnostic build requires NC_DIAGNOSTICS_TOKEN and NC_DIAGNOSTICS_PIPE. Use an ordinary build for normal play.");
            ContentStart.StartLibrary(args, new GameControllerOptions { Sandboxing = false });
#else
            ContentStart.Start(args);
#endif
            // NC-Edit-End
        }
    }
}
