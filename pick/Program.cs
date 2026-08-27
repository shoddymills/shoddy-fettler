using System.Text;
using Picker.Cli;
using Picker.Core;
using Picker.Mcp;

namespace Pick;

/// <summary>
/// The whole executable: read argv, hand it to one of the two front
/// ends in Picker, write what comes back, return the exit code.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        // D2: the managed network path, everywhere. macOS and Linux only
        // have the managed implementation; this makes Windows match, so
        // one network stack ships on three OSes and the publish stays
        // single-file with nothing extracted beside it. Set before any
        // connection can possibly open, and also pinned in the csproj as
        // a runtime host option so neither path can miss it.
        AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);

        // A BOM on stdout corrupts the first JSON-RPC message of a
        // session; explicit UTF-8 on stdin keeps a payload's non-ASCII
        // intact where the console's code page would mangle it - both
        // are fettle's hard-won lessons, kept.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using TextReader stdin =
            new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Second Ctrl-C gets the default behaviour: a server that
            // will not stop when asked twice deserves to be killed.
            e.Cancel = !stopping.IsCancellationRequested;
            if (!stopping.IsCancellationRequested) stopping.Cancel();
        };

        if (argv.Length > 0 && argv[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
            return await Serve(argv, stdin, stopping.Token).ConfigureAwait(false);

        CliResult result = await Command
            .RunAsync(argv, stdin, stopping.Token).ConfigureAwait(false);

        // The complete result, failures included, is already in Stdout
        // when --json was asked for; stderr carries the human diagnostic
        // and nothing a script is expected to parse.
        if (result.Stdout.Length > 0) Console.Out.Write(result.Stdout);
        if (result.Stderr.Length > 0) Console.Error.Write(result.Stderr);

        await Console.Out.FlushAsync().ConfigureAwait(false);
        return result.ExitCode;
    }

    static async Task<int> Serve(string[] argv, TextReader stdin, CancellationToken cancel)
    {
        Arguments args = Arguments.Parse(argv);

        string? path = args.Value("config");
        Result<Boundary> boundary = path is not null
            ? ConfigFile.Open(path)
            : ConfigFile.Find(Directory.GetCurrentDirectory());

        if (!boundary.IsOk)
        {
            // A boundary that cannot be read is a startup failure on
            // stderr, because there is no protocol session yet to carry
            // it.
            Console.Error.WriteLine($"pick serve: {boundary.Failure!.Message}"
                + (boundary.Failure.Subject is null ? "" : $" ({boundary.Failure.Subject})"));
            return ExitCodes.Of(boundary.Failure.Outcome);
        }

        // The boundary is re-read from its file before every request, so
        // an edit binds without a restart.
        string origin = boundary.Value.Origin;
        Func<Result<Boundary>> reload = () => ConfigFile.Open(origin);

        using var bench = new Bench(boundary.Value);
        using var server = new McpServer(bench, stdin, Console.Out, reload);

        await server.RunAsync(cancel).ConfigureAwait(false);
        return ExitCodes.Ok;
    }
}
